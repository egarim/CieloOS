using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// The next thing the agent wants to do at the console: either it's done, or it
// types some text (optionally pressing Enter). A brain decides this from the
// goal and the current screen.
public sealed record ConsoleAgentAction(bool Done, string? Text, bool Submit, string? Note);

// One recorded turn of the loop: what the screen showed, what the brain chose,
// and how the policy bus decided on the resulting keystrokes.
public sealed record ConsoleLoopStep(
    int Step,
    string ScreenBefore,
    string? Text,
    bool Submit,
    bool Done,
    string? Note,
    string Decision,
    string Reason);

public sealed record ConsoleLoopResult(
    string SessionId,
    string Goal,
    bool Completed,
    string StopReason,
    IReadOnlyList<ConsoleLoopStep> Steps);

// The pluggable brain. A deterministic recipe stands in for it today; a
// model-backed brain (cloud or local) drops in behind the same seam without the
// loop, the policy bus, or the audit trail changing.
public interface IConsoleAgentBrain
{
    Task<ConsoleAgentAction> DecideAsync(string goal, string screen, IReadOnlyList<string> history, int step, CancellationToken cancellationToken);
}

// A deterministic stand-in for a model: it leaves a durable note in the agent's
// own home, reads it back, then reports done. Enough to exercise the whole loop
// — observe, decide, act-through-the-bus, audit — with no external model.
public sealed class RecipeConsoleBrain : IConsoleAgentBrain
{
    public Task<ConsoleAgentAction> DecideAsync(string goal, string screen, IReadOnlyList<string> history, int step, CancellationToken cancellationToken)
    {
        var safeGoal = Sanitize(goal);
        return Task.FromResult(step switch
        {
            1 => new ConsoleAgentAction(false, $"echo \"lun.os agent handled: {safeGoal}\" > ~/AGENT_LOG.md", true, "record the task in my home"),
            2 => new ConsoleAgentAction(false, "cat ~/AGENT_LOG.md", true, "verify it landed"),
            _ => new ConsoleAgentAction(true, null, false, "task recorded")
        });
    }

    // The text is typed into the agent's own shell; strip characters that would
    // break the demo command (this is not a security boundary — isolation is).
    private static string Sanitize(string value) =>
        value.Replace("\"", "").Replace("$", "").Replace("`", "").Replace("\n", " ").Trim();
}

// The brain used when NO chat provider is configured. It types nothing and takes
// no action — it ends the loop immediately with a single, honest message telling
// the operator how to add a provider. This is what makes a provider-free install
// coherent: the OS runs, the agent is reachable, and asking it to think returns a
// clear "not configured yet" instead of a model-connection error.
public sealed class UnconfiguredBrain : IConsoleAgentBrain
{
    public const string DefaultMessage =
        "No AI provider is configured yet. Add one from the Models tab in the panel " +
        "(it works immediately, no restart) — or set a key in config " +
        "(Inference:Deepseek:ApiKey / Inference:Azure:ApiKey) and restart.";

    private readonly string message;

    public UnconfiguredBrain(string? message = null) => this.message = string.IsNullOrWhiteSpace(message) ? DefaultMessage : message;

    public Task<ConsoleAgentAction> DecideAsync(string goal, string screen, IReadOnlyList<string> history, int step, CancellationToken cancellationToken) =>
        Task.FromResult(new ConsoleAgentAction(true, null, false, message));
}

// Drives a console session toward a goal: observe the screen, ask the brain for
// the next action, and submit each keystroke batch as a `console.type` through
// AgentRuntime — so ownership, policy, and audit apply to every step exactly as
// they would to a human. Read-side observation is a plain capture; the write
// side always goes through the bus.
public sealed class ConsoleAgentLoop
{
    private const int MaxStepCeiling = 20;

    private readonly AgentRuntime runtime;
    private readonly IConsoleBackend console;

    // Model spend is metered and capped here because this is where a run's cost
    // actually accrues: one goal can be twenty model calls (issue #14). Optional,
    // so a runtime without a ledger behaves exactly as before.
    private readonly ITokenLedger? ledger;

    public ConsoleAgentLoop(AgentRuntime runtime, IConsoleBackend console, ITokenLedger? ledger = null)
    {
        this.runtime = runtime;
        this.console = console;
        this.ledger = ledger;
    }

    public async Task<ConsoleLoopResult> RunAsync(
        string sessionId,
        string goal,
        int maxSteps,
        RuntimePrincipal principal,
        Guid userId,
        Guid agentId,
        IConsoleAgentBrain brain,
        CancellationToken cancellationToken,
        // Called as each step lands, so a caller can stream progress instead of
        // waiting for the whole loop. Optional: nothing else in the loop changes.
        Func<ConsoleLoopStep, Task>? onStep = null,
        // Which provider is about to be billed. Optional so every existing caller
        // and test keeps working; without it the run is simply not metered.
        ModelIdentity? model = null)
    {
        var steps = new List<ConsoleLoopStep>();
        var history = new List<string>();
        var recent = new List<string>();
        var cap = Math.Clamp(maxSteps, 1, MaxStepCeiling);

        // The whole run bills to one acting pair, so the scope opens once and
        // every model call underneath it is attributed without threading identity
        // through the brains.
        using var accounting = ledger is not null && model is not null
            ? TokenAccountingScope.Begin(userId, agentId, model.ProviderId, model.Model, model.Locality)
            : null;

        for (var step = 1; step <= cap; step++)
        {
            // Checked every step, not just at the start: one goal can be twenty
            // calls, and a budget that is only consulted once is a budget that can
            // be blown through by a single long run.
            if (ledger is not null && model is not null
                && TokenBudget.Exceeded(ledger, userId, agentId, model.Locality) is { } overspent)
            {
                return new ConsoleLoopResult(sessionId, goal, false, overspent, steps);
            }

            var view = await console.CaptureAsync(sessionId, cancellationToken);
            if (!view.Available)
            {
                return new ConsoleLoopResult(sessionId, goal, false, $"Console unavailable: {view.Detail}", steps);
            }

            var action = await brain.DecideAsync(goal, view.Screen, history, step, cancellationToken);
            if (action.Done)
            {
                var doneStep = new ConsoleLoopStep(step, view.Screen, null, false, true, action.Note, "Done", "Agent reported the goal complete.");
                steps.Add(doneStep);
                if (onStep is not null) await onStep(doneStep);
                return new ConsoleLoopResult(sessionId, goal, true, "Agent reported the goal complete.", steps);
            }

            var text = action.Text ?? "";

            // Anti-loop: if the model repeats a command it already ran, it isn't
            // making progress — stop instead of burning the whole step budget
            // (and looking like a crash). The result of the earlier run stands.
            if (!string.IsNullOrWhiteSpace(text) && recent.Contains(text))
            {
                steps.Add(new ConsoleLoopStep(step, view.Screen, text, action.Submit, false, action.Note, "Stopped", "Repeated a command already run."));
                return new ConsoleLoopResult(sessionId, goal, false,
                    "Stopped: the agent repeated a command it had already run without making progress (its earlier result stands — check the home).", steps);
            }

            var result = await runtime.SubmitAsync(
                new SubmitToolRequestDto(userId, agentId, "console", "type", new Dictionary<string, string>
                {
                    ["id"] = sessionId,
                    ["text"] = text,
                    ["submit"] = action.Submit ? "true" : "false"
                }),
                principal,
                cancellationToken);

            var typedStep = new ConsoleLoopStep(step, view.Screen, text, action.Submit, false, action.Note, result.Decision.ToString(), result.Reason);
            steps.Add(typedStep);
            if (onStep is not null) await onStep(typedStep);
            history.Add($"typed: {text}");
            recent.Add(text);
            if (recent.Count > 2)
            {
                recent.RemoveAt(0);
            }

            // A denied keystroke stops the loop — the agent cannot push past the
            // policy that just refused it.
            if (result.Decision != PolicyDecision.Allow)
            {
                return new ConsoleLoopResult(sessionId, goal, false, $"Blocked at step {step}: {result.Reason}", steps);
            }
        }

        return new ConsoleLoopResult(sessionId, goal, false, $"Reached the step limit ({cap}) before finishing.", steps);
    }
}
