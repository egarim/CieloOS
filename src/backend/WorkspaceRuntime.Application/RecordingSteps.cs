using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// One entry in a recording's step list — the action that ran, when, and what it
// actually did. This is the raw material for a tutorial's chapters.
public sealed record AuditStep(DateTimeOffset At, string Action, string Detail);

// Builds the step list for a recording: the commands the agent ran while it
// recorded. The recorder already writes the wall-clock start and duration; the
// audit trail holds the commands, so the step list is the audit rows inside the
// recording's window. (#19 part 1 — the sidecar next to the MP4.) Without a
// session id on audit rows the window is the divider; when the audit carries a
// session id this can also be narrowed to that session.
public static class RecordingSteps
{
    public static IReadOnlyList<AuditStep> Build(IEnumerable<AuditEvent> events, double startedAtUnix, int seconds)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds((long)startedAtUnix);
        var end = start.AddSeconds(seconds);
        return events
            .Where(ev => ev.Outcome == AuditOutcome.Success && ev.OccurredAt >= start && ev.OccurredAt <= end)
            .OrderBy(ev => ev.OccurredAt)
            .Select(ev => new AuditStep(ev.OccurredAt, ev.Action, ev.Detail))
            .ToList();
    }
}
