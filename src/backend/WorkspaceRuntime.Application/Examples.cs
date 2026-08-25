using System.Collections.Concurrent;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// Runnable examples: what this machine can do, as something you press rather than
// something you read.
//
// An example is a SCRIPTED SEQUENCE of ordinary bus commands, not a free-form goal
// handed to a model. Three reasons, and the third is the one that decided it:
//   - progress is real (step 3 of 7), not a spinner pretending;
//   - the run is deterministic, so a failure is a bug on this machine rather than
//     a model having a bad day;
//   - it works on a machine with NO model provider configured, which is exactly
//     the state a fresh installation is in when someone first wants to see what
//     they have bought.
//
// Because the steps are ordinary commands, an example that needs a human's consent
// STOPS and asks — and that is the most honest thing these demos show.
public sealed record ExampleStep(
    string Surface,
    string Operation,
    IReadOnlyDictionary<string, string> Input,
    string Note,
    // "command" rides the bus. "observe" is a gated read (the browser's page text,
    // for instance) — useful in a demo to show what the agent just perceived, and
    // never a mutation.
    string Kind = "command");

public sealed record Example(
    string Id,
    string Title,
    string Summary,
    bool NeedsSession,
    IReadOnlyList<ExampleStep> Steps);

public interface IExampleCatalog
{
    IReadOnlyList<Example> Examples { get; }
    Example? Find(string id);
}

public enum ExampleRunState { Running, AwaitingApproval, Finished, Failed }

public sealed record ExampleStepReport(int Number, string Note, string Outcome, string Detail);

public sealed record ExampleRun(
    string RunId,
    string ExampleId,
    string Title,
    string? SessionId,
    ExampleRunState State,
    int Step,
    int TotalSteps,
    string Message,
    IReadOnlyList<ExampleStepReport> Reports,
    Guid? ApprovalId = null,
    string? ApprovalReason = null,
    string? ApprovalHash = null);

// One run at a time per person. A demo that can be started twice while the first
// is still clicking around their desktop is not a demo, it is a fight.
public sealed class ExampleRunner
{
    private readonly ConcurrentDictionary<string, ExampleRun> runs = new(StringComparer.Ordinal);

    public ExampleRun? Current(string owner) => runs.GetValueOrDefault(owner);

    public bool TryClaim(string owner, ExampleRun run)
    {
        // Read-then-write is not atomic just because the dictionary is: two
        // requests a millisecond apart could both see "nothing running" and both
        // start driving the same desktop. AddOrUpdate runs its factory under the
        // bucket lock, so exactly one of them wins.
        var claimed = false;
        runs.AddOrUpdate(
            owner,
            _ => { claimed = true; return run; },
            (_, existing) =>
            {
                if (existing.State is ExampleRunState.Running or ExampleRunState.AwaitingApproval)
                {
                    return existing;
                }
                claimed = true;
                return run;
            });
        return claimed;
    }

    public void Update(string owner, Func<ExampleRun, ExampleRun> change)
    {
        if (runs.TryGetValue(owner, out var current))
        {
            runs[owner] = change(current);
        }
    }
}

public static class ExampleSubstitution
{
    // Steps name the session as {session} so one catalogue serves every desk.
    public static IReadOnlyDictionary<string, string> Bind(
        IReadOnlyDictionary<string, string> input, string? sessionId)
    {
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            bound[key] = value.Replace("{session}", sessionId ?? "", StringComparison.Ordinal);
        }
        return bound;
    }
}
