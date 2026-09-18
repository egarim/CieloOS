using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

public enum SuspensionResult
{
    Success,
    NotFound,
    MachineOwner
}

public static class Suspension
{
    public const string RefusalMessage = "This account is suspended. Ask the machine owner to restore access.";

    public static bool IsSuspended(IRuntimeStore store, RuntimePrincipal principal) =>
        store.Users.Any(user => user.Id == principal.Subject && user.SuspendedAt is not null);

    public static SuspensionResult Suspend(
        string slug,
        IRuntimeStore store,
        ISessionStore sessions,
        IApiKeyStore keys,
        IInviteStore invites,
        Guid actorId)
    {
        var user = store.Users.FirstOrDefault(candidate =>
            string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
        if (user is null)
        {
            return SuspensionResult.NotFound;
        }
        if (user.IsMachineOwner)
        {
            return SuspensionResult.MachineOwner;
        }

        store.SetSuspendedAt(user.Id, DateTimeOffset.UtcNow);
        var endedSessions = sessions.RevokeAllFor(user.Id);
        var revokedKeys = keys.RevokeAllFor(user.Id);
        var supersededInvites = invites.SupersedeLiveFor(user.Id);
        store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, actorId, null,
            "user.suspend", AuditOutcome.Success,
            $"Suspended '{user.Slug}', ending {endedSessions} session(s), revoking {revokedKeys} API key(s), and superseding {supersededInvites} invitation(s)."));
        return SuspensionResult.Success;
    }

    public static SuspensionResult Unsuspend(string slug, IRuntimeStore store, Guid actorId)
    {
        var user = store.Users.FirstOrDefault(candidate =>
            string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
        if (user is null)
        {
            return SuspensionResult.NotFound;
        }

        store.SetSuspendedAt(user.Id, null);
        store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, actorId, null,
            "user.unsuspend", AuditOutcome.Success, $"Restored access for '{user.Slug}'."));
        return SuspensionResult.Success;
    }
}

public static class PasswordCredentialRotation
{
    public static (int Sessions, int ApiKeys) RevokeAll(
        Guid userId, ISessionStore sessions, IApiKeyStore keys) =>
        (sessions.RevokeAllFor(userId), keys.RevokeAllFor(userId));
}
