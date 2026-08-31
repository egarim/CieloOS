using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// Filtering over the audit trail for the /api/audit-events endpoint. A pure
// function in the Application layer so the endpoint stays thin and the
// filtering is unit-testable. (A session filter will slot in here once audit
// rows carry a SessionId.)
public static class AuditQuery
{
    public static IReadOnlyList<AuditEvent> Filter(
        IEnumerable<AuditEvent> events,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        string? action = null)
    {
        var result = events.AsEnumerable();
        if (since is { } after)
        {
            result = result.Where(ev => ev.OccurredAt >= after);
        }
        if (until is { } before)
        {
            result = result.Where(ev => ev.OccurredAt <= before);
        }
        if (!string.IsNullOrWhiteSpace(action))
        {
            result = result.Where(ev => string.Equals(ev.Action, action, StringComparison.Ordinal));
        }
        return result.ToList();
    }
}
