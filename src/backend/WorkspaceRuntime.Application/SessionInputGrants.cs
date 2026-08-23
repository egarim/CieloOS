namespace WorkspaceRuntime.Application;

// A time-boxed lease authorizing pointer/keyboard INPUT on a desktop session —
// the V0.6 per-session input grant. While a grant is live, AgentRuntime upgrades
// desktop.type/key from RequireApproval to Allow, so the agent can type
// autonomously under ONE-TIME human consent instead of per-keystroke approval.
// In-memory by design: a runtime restart drops all input authority (fail closed).
public sealed record InputGrant(Guid Id, string SessionId, Guid GrantedByUserId, DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt);

public interface ISessionInputGrants
{
    InputGrant Grant(string sessionId, Guid grantedByUserId, TimeSpan duration, DateTimeOffset now);
    bool IsActive(string sessionId, DateTimeOffset now);
    int Revoke(string sessionId);
    IReadOnlyList<InputGrant> Active(DateTimeOffset now);
}

public sealed class InMemorySessionInputGrants : ISessionInputGrants
{
    private readonly object gate = new();
    private readonly List<InputGrant> grants = new();

    public InputGrant Grant(string sessionId, Guid grantedByUserId, TimeSpan duration, DateTimeOffset now)
    {
        var grant = new InputGrant(Guid.NewGuid(), sessionId, grantedByUserId, now, now + duration);
        lock (gate)
        {
            // One active grant per session — a new grant replaces the old.
            grants.RemoveAll(existing => existing.SessionId == sessionId);
            grants.Add(grant);
        }
        return grant;
    }

    public bool IsActive(string sessionId, DateTimeOffset now)
    {
        lock (gate)
        {
            return grants.Any(grant => grant.SessionId == sessionId && grant.ExpiresAt > now);
        }
    }

    public int Revoke(string sessionId)
    {
        lock (gate)
        {
            return grants.RemoveAll(grant => grant.SessionId == sessionId);
        }
    }

    public IReadOnlyList<InputGrant> Active(DateTimeOffset now)
    {
        lock (gate)
        {
            return grants.Where(grant => grant.ExpiresAt > now).ToList();
        }
    }
}
