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

    public DesktopAgentLoop(AgentRuntime runtime, IDesktopBackend desktop)
    {
        this.runtime = runtime;
        this.desktop = desktop;
    }

    public async Task<DesktopLoopResult> RunAsync(
        string sessionId,
        string goal,
        int maxSteps,
        RuntimePrincipal principal,
        Guid userId,
        Guid agentId,
        IDesktopAgentBrain brain,
        CancellationToken cancellationToken)
    {
        var steps = new List<DesktopLoopStep>();
        var history = new List<string>();
        var recent = new List<string>();
        var cap = Math.Clamp(maxSteps, 1, MaxStepCeiling);

        for (var step = 1; step <= cap; step++)
        {
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

            if (action.Done)
            {
                steps.Add(new DesktopLoopStep(step, els.Count, "done", null, null, null, null, null, true, action.Note, "Done", "Agent reported the goal complete."));
                return new DesktopLoopResult(sessionId, goal, true, "Agent reported the goal complete.", steps);
            }

            var (op, args, ledger, error) = Resolve(action, els, sessionId);
            if (error is not null)
            {
                steps.Add(new DesktopLoopStep(step, els.Count, action.Kind, action.ElementId, action.X, action.Y, action.Text, action.Keysym, false, action.Note, "Stopped", error));
                return new DesktopLoopResult(sessionId, goal, false, error, steps);
            }

            // Anti-loop: the same resolved action twice running isn't progress.
            if (recent.Contains(ledger))
            {
                steps.Add(new DesktopLoopStep(step, els.Count, action.Kind, action.ElementId, action.X, action.Y, action.Text, action.Keysym, false, action.Note, "Stopped", "Repeated an action already taken."));
                return new DesktopLoopResult(sessionId, goal, false,
                    "Stopped: the agent repeated an action without making progress.", steps);
            }

            var result = await runtime.SubmitAsync(
                new SubmitToolRequestDto(userId, agentId, "desktop", op, args),
                principal,
                cancellationToken);

            steps.Add(new DesktopLoopStep(step, els.Count, action.Kind, action.ElementId, action.X, action.Y, action.Text, action.Keysym, false, action.Note, result.Decision.ToString(), result.Reason));
            history.Add(ledger);
            recent.Add(ledger);
            if (recent.Count > 2)
            {
                recent.RemoveAt(0);
            }

            if (result.Decision != PolicyDecision.Allow)
            {
                return new DesktopLoopResult(sessionId, goal, false, $"Blocked at step {step}: {result.Reason}", steps);
            }
        }

        return new DesktopLoopResult(sessionId, goal, false, $"Reached the step limit ({cap}) before finishing.", steps);
    }

    // Turn a brain action into a (operation, args) for the `desktop` surface,
    // resolving an element id to its EXACT center. Returns an error string if it
    // cannot be resolved (e.g. the element scrolled off screen since observation).
    private static (string Op, Dictionary<string, string> Args, string Ledger, string? Error) Resolve(
        DesktopAgentAction action, IReadOnlyList<DesktopElement> elements, string sessionId)
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
            "No vision-capable provider is configured (set Inference:Azure for gpt-4.1-mini)."));
}
