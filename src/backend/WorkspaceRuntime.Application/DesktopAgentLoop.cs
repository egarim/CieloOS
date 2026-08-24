using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// Drives a DESKTOP session toward a goal the way ConsoleAgentLoop drives a
// console: observe (AT-SPI element list + screenshot), ask the brain for the next
// action, and submit it as a governed `desktop.*` command through AgentRuntime —
// so ownership, policy, and audit apply to every click/keystroke. The brain
// grounds on element ids (exact centers); raw pixels are the vision fallback for
// what the accessibility tree does not expose.
public sealed class DesktopAgentLoop
{
    private const int MaxStepCeiling = 20;

    private readonly AgentRuntime runtime;
    private readonly IDesktopBackend desktop;

    // Metered and capped like the console loop (issue #14): a desktop run spends
    // on models too, and the vision path spends the most — a screenshot is a
    // large prompt. Named `tokens` because this file already has a local `ledger`
    // meaning the step's input record.
    private readonly ITokenLedger? tokens;

    public DesktopAgentLoop(AgentRuntime runtime, IDesktopBackend desktop, ITokenLedger? tokens = null)
    {
        this.runtime = runtime;
        this.desktop = desktop;
        this.tokens = tokens;
    }

    public async Task<DesktopLoopResult> RunAsync(
        string sessionId,
        string goal,
        int maxSteps,
        RuntimePrincipal principal,
        Guid userId,
        Guid agentId,
        IDesktopAgentBrain brain,
        CancellationToken cancellationToken,
        // Which provider this run bills. Optional, so existing callers and tests
        // are unchanged and simply are not metered.
        ModelIdentity? model = null)
    {
        var steps = new List<DesktopLoopStep>();
        var history = new List<string>();
        // Progress is judged by OBSERVATION change, not action identity: a
        // "(observation, action)" pair we have seen before made no progress.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cap = Math.Clamp(maxSteps, 1, MaxStepCeiling);

        using var accounting = tokens is not null && model is not null
            ? TokenAccountingScope.Begin(userId, agentId, model.ProviderId, model.Model, model.Locality)
            : null;

        for (var step = 1; step <= cap; step++)
        {
            if (tokens is not null && model is not null
                && TokenBudget.Exceeded(tokens, userId, agentId, model.Locality) is { } overspent)
            {
                return new DesktopLoopResult(sessionId, goal, false, overspent, steps);
            }

            // Observe: elements are the grounding source; the screenshot gives the
            // vision model context (and the fallback path). Fail only if BOTH are
            // unavailable — a sparse tree is still a valid observation.
            var elements = await desktop.ElementsAsync(sessionId, cancellationToken);
            var shot = await desktop.ScreenshotAsync(sessionId, cancellationToken);
            if (!elements.Ok && !shot.Ok)
            {
                return new DesktopLoopResult(sessionId, goal, false,
                    $"Desktop unavailable: {shot.Error ?? elements.Error}", steps);
            }

            var els = elements.Ok ? elements.Elements : Array.Empty<DesktopElement>();
            var action = await brain.DecideAsync(
                goal, els, shot.Ok ? shot.Png : null, shot.Width, shot.Height, history, step, cancellationToken);

            // An abort is NOT goal-complete — surface the real reason.
            if (action.Aborted)
            {
                steps.Add(new DesktopLoopStep(step, els.Count, "done", null, null, null, null, null, true, action.Note, "Aborted", action.Note ?? "Aborted."));
                return new DesktopLoopResult(sessionId, goal, false, action.Note ?? "The desktop brain aborted.", steps);
            }

            if (action.Done)
            {
                steps.Add(new DesktopLoopStep(step, els.Count, "done", null, null, null, null, null, true, action.Note, "Done", "Agent reported the goal complete."));
                return new DesktopLoopResult(sessionId, goal, true, "Agent reported the goal complete.", steps);
            }

            var (op, args, ledger, error) = Resolve(action, els, shot.Width, shot.Height, sessionId);
            if (error is not null)
            {
                steps.Add(new DesktopLoopStep(step, els.Count, action.Kind, action.ElementId, action.X, action.Y, action.Text, action.Keysym, false, action.Note, "Stopped", error));
                return new DesktopLoopResult(sessionId, goal, false, error, steps);
            }

            // Anti-loop: trip only when the SAME action is taken on the SAME screen
            // — real progress changes the observation, so legitimate identical
            // repeats (Tab, Tab) on a changing screen are allowed, and longer
            // cycles are still caught because their (observation, action) recurs.
            var progressKey = $"{ObservationHash(els)}|{ledger}";
            if (!seen.Add(progressKey))
            {
                steps.Add(new DesktopLoopStep(step, els.Count, action.Kind, action.ElementId, action.X, action.Y, action.Text, action.Keysym, false, action.Note, "Stopped", "Repeated an action on an unchanged screen — no progress."));
                return new DesktopLoopResult(sessionId, goal, false,
                    "Stopped: the agent repeated an action on an unchanged screen without making progress.", steps);
            }

            var result = await runtime.SubmitAsync(
                new SubmitToolRequestDto(userId, agentId, "desktop", op, args),
                principal,
                cancellationToken);

            steps.Add(new DesktopLoopStep(step, els.Count, action.Kind, action.ElementId, action.X, action.Y, action.Text, action.Keysym, false, action.Note, result.Decision.ToString(), result.Reason));
            history.Add(ledger);

            // Any non-Allow decision stops the loop: a Deny cannot be pushed past,
            // and a RequireApproval (e.g. desktop.type/key) means the action is now
            // waiting on the owner — the autonomous run cannot proceed through it.
            if (result.Decision != PolicyDecision.Allow)
            {
                return new DesktopLoopResult(sessionId, goal, false, $"Stopped at step {step} ({result.Decision}): {result.Reason}", steps);
            }
        }

        return new DesktopLoopResult(sessionId, goal, false, $"Reached the step limit ({cap}) before finishing.", steps);
    }

    // Turn a brain action into a (operation, args) for the `desktop` surface,
    // resolving an element id to its EXACT center. Returns an error string if it
    // cannot be resolved (e.g. the element scrolled off screen since observation).
    private static (string Op, Dictionary<string, string> Args, string Ledger, string? Error) Resolve(
        DesktopAgentAction action, IReadOnlyList<DesktopElement> elements, int screenWidth, int screenHeight, string sessionId)
    {
        switch (action.Kind)
        {
            case "click":
            case "double_click":
                int x, y;
                if (action.ElementId is { } id)
                {
                    var element = elements.FirstOrDefault(candidate => candidate.Id == id);
                    if (element is null)
                    {
                        return ("", Empty(), "", $"Element {id} is no longer on screen.");
                    }
                    x = element.CenterX;
                    y = element.CenterY;
                }
                else if (action.X is { } px && action.Y is { } py)
                {
                    x = px;
                    y = py;
                }
                else
                {
                    return ("", Empty(), "", "Click action had neither an element id nor coordinates.");
                }
                // The "exact center" guarantee only holds inside the screen: a
                // partially-scrolled element or a garbage box can resolve off-screen.
                if (screenWidth > 0 && screenHeight > 0 && (x < 0 || y < 0 || x >= screenWidth || y >= screenHeight))
                {
                    return ("", Empty(), "", $"Target ({x},{y}) is outside the {screenWidth}x{screenHeight} screen.");
                }
                return (action.Kind, new Dictionary<string, string>
                {
                    ["id"] = sessionId,
                    ["x"] = x.ToString(),
                    ["y"] = y.ToString(),
                }, $"{action.Kind} ({x},{y})", null);

            case "type":
                var text = action.Text ?? "";
                return ("type", new Dictionary<string, string>
                {
                    ["id"] = sessionId,
                    ["text"] = text,
                }, $"type: {text}", null);

            case "key":
                if (string.IsNullOrWhiteSpace(action.Keysym))
                {
                    return ("", Empty(), "", "Key action had no keysym.");
                }
                return ("key", new Dictionary<string, string>
                {
                    ["id"] = sessionId,
                    ["keysym"] = action.Keysym,
                }, $"key: {action.Keysym}", null);

            default:
                return ("", Empty(), "", $"Unknown action kind '{action.Kind}'.");
        }
    }

    private static Dictionary<string, string> Empty() => new();

    // A cheap fingerprint of what's on screen — the set of actionable elements and
    // their boxes. Two steps with the same fingerprint saw the same screen.
    private static string ObservationHash(IReadOnlyList<DesktopElement> elements)
    {
        if (elements.Count == 0)
        {
            return "empty";
        }
        return string.Join(";", elements.Select(e => $"{e.Role}:{e.Name}:{e.X},{e.Y},{e.W},{e.H}"));
    }
}

// Stands in when no vision-capable provider is configured: the desktop loop needs
// to SEE, so it reports done immediately with a clear reason rather than flailing.
public sealed class NoVisionDesktopBrain : IDesktopAgentBrain
{
    public Task<DesktopAgentAction> DecideAsync(
        string goal, IReadOnlyList<DesktopElement> elements, byte[]? screenshotPng,
        int screenWidth, int screenHeight, IReadOnlyList<string> history, int step,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DesktopAgentAction(
            true, "done", null, null, null, null, null,
            "No vision-capable provider is configured (set Inference:Azure for gpt-4.1-mini).", Aborted: true));
}
