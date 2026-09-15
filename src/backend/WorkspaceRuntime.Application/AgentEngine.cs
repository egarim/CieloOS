using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// What an engine is given. Deliberately small, and deliberately not negotiable:
// an engine is told who it acts for and where, and everything on the other side
// of that — ownership, the policy decision on each action, approvals, the audit
// trail, the bill — stays the host's.
//
// Note what is NOT here. No model, no key, no prompt. Which model a run uses is
// the host's to decide, or the claim that a benchmark pinned one is only a claim
// about a config file somebody could change. See docs/agent-engines.md.
public sealed record EngineRun(
    string SessionId,
    string Goal,
    int MaxSteps,
    RuntimePrincipal Principal,
    Guid UserId,
    Guid AgentId);

// The thing that turns a goal into audited work.
//
// Today there is exactly one implementation and it is ours. The seam exists
// because the OpenClaw benchmark (docs/agent-benchmark.md) found the agent loop
// to be the part of CieloOS with the most competition in it and the least of our
// own value — while the parts around it, which no comparable tool has, were never
// measured because the comparison had nothing to measure them against.
public interface IAgentEngine
{
    // Stable, and recorded with the run. An audit trail that cannot say WHICH
    // engine acted cannot answer the only new question that arises the moment
    // there is more than one of them.
    string Id { get; }

    // ConsoleLoopResult is console-flavoured in its name only: a list of steps,
    // each carrying the policy decision and the reason that let it happen, plus
    // how the run ended. That is exactly what the host needs back from any engine.
    // It is also already the response shape of /api/sessions/{id}/agent-run, so
    // renaming it would change a public contract to gain nothing today.
    Task<ConsoleLoopResult> RunAsync(
        EngineRun run,
        Func<ConsoleLoopStep, Task>? onStep,
        CancellationToken cancellationToken);
}
