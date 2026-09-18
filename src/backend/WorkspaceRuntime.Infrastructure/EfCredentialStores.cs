using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// Sessions and API keys, stored the way a credential should be: the secret is
// generated once, handed over once, and only its hash is kept. A copy of the
// database is then a list of who has access, not a set of usable credentials —
// which is exactly what the deterministic identity token could never offer,
// since it was derivable from the signing key and the slug (issue #9).
public static class SecretHash
{
    // SHA-256, not PBKDF2: these secrets are 256 bits of randomness we generated,
    // so there is nothing to brute-force and every request would otherwise pay a
    // deliberate 100 ms cost.
    public static string Of(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    public static string NewSecret(string prefix = "") =>
        prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

public sealed class EfSessionStore : ISessionStore
{
    private readonly IDbContextFactory<RuntimeDbContext> contextFactory;

    public EfSessionStore(IDbContextFactory<RuntimeDbContext> contextFactory)
    {
        this.contextFactory = contextFactory;
    }

    public (PanelSession Session, string Secret) Create(Guid userId, TimeSpan lifetime)
    {
        var secret = SecretHash.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var row = new SessionRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SecretHash = SecretHash.Of(secret),
            CreatedAt = now,
            CreatedAtTicks = now.UtcTicks,
            ExpiresAt = now.Add(lifetime),
            LastSeenAt = now
        };

        using var context = contextFactory.CreateDbContext();
        context.Sessions.Add(row);
        context.SaveChanges();
        return (ToSession(row), secret);
    }

    public PanelSession? Resolve(string secret, DateTimeOffset now)
    {
        var hash = SecretHash.Of(secret);
        using var context = contextFactory.CreateDbContext();
        var row = context.Sessions.AsNoTracking().FirstOrDefault(candidate => candidate.SecretHash == hash);
        if (row is null)
        {
            return null;
        }

        var session = ToSession(row);
        return session.IsLive(now) ? session : null;
    }

    public void Touch(Guid sessionId, DateTimeOffset now)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Sessions.FirstOrDefault(candidate => candidate.Id == sessionId);
        if (row is null)
        {
            return;
        }

        // Only worth a write once a minute: this runs on every authenticated
        // request, and "last seen" does not need to be exact.
        if (now - row.LastSeenAt < TimeSpan.FromMinutes(1))
        {
            return;
        }

        row.LastSeenAt = now;
        context.SaveChanges();
    }

    public void Revoke(Guid sessionId)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Sessions.FirstOrDefault(candidate => candidate.Id == sessionId);
        if (row is null || row.RevokedAt is not null)
        {
            return;
        }

        row.RevokedAt = DateTimeOffset.UtcNow;
        context.SaveChanges();
    }

    public int RevokeAllFor(Guid userId)
    {
        using var context = contextFactory.CreateDbContext();
        var rows = context.Sessions.Where(row => row.UserId == userId && row.RevokedAt == null).ToList();
        foreach (var row in rows)
        {
            row.RevokedAt = DateTimeOffset.UtcNow;
        }

        context.SaveChanges();
        return rows.Count;
    }

    public IReadOnlyList<PanelSession> For(Guid userId)
    {
        using var context = contextFactory.CreateDbContext();
        return context.Sessions.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderByDescending(row => row.CreatedAtTicks)
            .Take(20)
            .AsEnumerable()
            .Select(ToSession)
            .ToList();
    }

    private static PanelSession ToSession(SessionRow row) =>
        new(row.Id, row.UserId, row.CreatedAt, row.ExpiresAt, row.LastSeenAt, row.RevokedAt);
}

public sealed class EfApiKeyStore : IApiKeyStore
{
    private readonly IDbContextFactory<RuntimeDbContext> contextFactory;

    public EfApiKeyStore(IDbContextFactory<RuntimeDbContext> contextFactory)
    {
        this.contextFactory = contextFactory;
    }

    public (ApiKey Key, string Secret) Create(Guid ownerUserId, string name, TimeSpan? lifetime)
    {
        var secret = SecretHash.NewSecret(CredentialFormat.ApiKeyPrefix);
        var now = DateTimeOffset.UtcNow;
        var row = new ApiKeyRow
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim(),
            SecretHash = SecretHash.Of(secret),
            CreatedAt = now,
            CreatedAtTicks = now.UtcTicks,
            ExpiresAt = lifetime is null ? null : now.Add(lifetime.Value)
        };

        using var context = contextFactory.CreateDbContext();
        context.ApiKeys.Add(row);
        context.SaveChanges();
        return (ToKey(row), secret);
    }

    public ApiKey? Resolve(string secret, DateTimeOffset now)
    {
        var hash = SecretHash.Of(secret);
        using var context = contextFactory.CreateDbContext();
        var row = context.ApiKeys.AsNoTracking().FirstOrDefault(candidate => candidate.SecretHash == hash);
        if (row is null)
        {
            return null;
        }

        var key = ToKey(row);
        return key.IsLive(now) ? key : null;
    }

    public void MarkUsed(Guid keyId, DateTimeOffset now)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.ApiKeys.FirstOrDefault(candidate => candidate.Id == keyId);
        if (row is null || (row.LastUsedAt is not null && now - row.LastUsedAt < TimeSpan.FromMinutes(1)))
        {
            return;
        }

        row.LastUsedAt = now;
        context.SaveChanges();
    }

    public bool Revoke(Guid keyId, Guid ownerUserId)
    {
        using var context = contextFactory.CreateDbContext();
        // Scoped to the owner in the QUERY, so a key belonging to someone else is
        // not found rather than found-and-refused.
        var row = context.ApiKeys.FirstOrDefault(candidate => candidate.Id == keyId && candidate.OwnerUserId == ownerUserId);
        if (row is null || row.RevokedAt is not null)
        {
            return false;
        }

        row.RevokedAt = DateTimeOffset.UtcNow;
        context.SaveChanges();
        return true;
    }

    public int RevokeAllFor(Guid userId)
    {
        using var context = contextFactory.CreateDbContext();
        var rows = context.ApiKeys.Where(row => row.OwnerUserId == userId && row.RevokedAt == null).ToList();
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            row.RevokedAt = now;
        }
        context.SaveChanges();
        return rows.Count;
    }

    public IReadOnlyList<ApiKey> For(Guid ownerUserId)
    {
        using var context = contextFactory.CreateDbContext();
        return context.ApiKeys.AsNoTracking()
            .Where(row => row.OwnerUserId == ownerUserId)
            .OrderByDescending(row => row.CreatedAtTicks)
            .AsEnumerable()
            .Select(ToKey)
            .ToList();
    }

    private static ApiKey ToKey(ApiKeyRow row) =>
        new(row.Id, row.OwnerUserId, row.Name, row.CreatedAt, row.ExpiresAt, row.RevokedAt, row.LastUsedAt);
}

public sealed class EfInviteStore : IInviteStore
{
    private readonly IDbContextFactory<RuntimeDbContext> contextFactory;

    public EfInviteStore(IDbContextFactory<RuntimeDbContext> contextFactory)
    {
        this.contextFactory = contextFactory;
    }

    public (Invite Invite, string Code) Create(Guid userId, Guid invitedBy, TimeSpan lifetime)
    {
        var code = SecretHash.NewSecret(CredentialFormat.InvitePrefix);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(lifetime);
        var row = new InviteRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InvitedByUserId = invitedBy,
            CodeHash = SecretHash.Of(code),
            CreatedAt = now,
            CreatedAtTicks = now.UtcTicks,
            ExpiresAt = expiresAt,
            ExpiresAtTicks = expiresAt.UtcTicks
        };

        using var context = contextFactory.CreateDbContext();
        context.Invites.Add(row);
        context.SaveChanges();
        return (ToInvite(row), code);
    }

    public Invite? Resolve(string code, DateTimeOffset now)
    {
        var hash = SecretHash.Of(code);
        using var context = contextFactory.CreateDbContext();
        var row = context.Invites.AsNoTracking().FirstOrDefault(candidate => candidate.CodeHash == hash);
        return row is null ? null : ToInvite(row);
    }

    // Read-modify-SaveChanges, matching every other store in this file. ExecuteUpdate
    // would be a single conditional UPDATE and a stronger guarantee, but it appears
    // nowhere in src/ today and its behaviour on Npgsql against these migrations is
    // unproven; a slower correct store beats a faster one whose atomicity nobody
    // proved. The predicate is the full liveness check, so a row that was redeemed,
    // revoked or superseded between the read and the write loses the race.
    // ONE statement, because two is a race and this credential is single-use.
    //
    // The first version read the row, checked liveness in C#, then wrote. Eight
    // threads racing that produced SEVEN winners: every one of them read a live
    // row before any of them wrote, so every one of them believed it had won, and
    // an invitation that may be spent exactly once set a password seven times.
    // That is seven people onto one account.
    //
    // Read-modify-SaveChanges is the pattern every other store in this file uses
    // and it is right for them, because none of them decides who may take over an
    // account. Here the liveness test and the write have to be the same statement,
    // so the database does the deciding:
    //
    //     UPDATE runtime_invites SET RedeemedAt = ... WHERE Id = ... AND <live>
    //
    // Rows-affected is the answer. One winner, and every loser is told no by the
    // same WHERE clause that the winner passed. This is the one place the design
    // leans on atomicity, and ExecuteUpdate is how both SQLite and Npgsql give it
    // to us without a transaction the providers would treat differently.
    public bool Spend(Guid inviteId, DateTimeOffset now, string from)
    {
        using var context = contextFactory.CreateDbContext();
        var affected = context.Invites
            .Where(row => row.Id == inviteId
                && row.RedeemedAt == null
                && row.RevokedAt == null
                && row.SupersededAt == null
                && row.ExpiresAtTicks > now.UtcTicks)
            .ExecuteUpdate(setters => setters
                .SetProperty(row => row.RedeemedAt, now)
                .SetProperty(row => row.RedeemedAtTicks, now.UtcTicks)
                .SetProperty(row => row.RedeemedFrom, from));
        return affected == 1;
    }

    public bool NotePreview(Guid inviteId, DateTimeOffset now, string from)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Invites.FirstOrDefault(candidate => candidate.Id == inviteId);
        if (row is null
            || row.FirstPreviewedAt is not null
            || row.RedeemedAt is not null
            || row.RevokedAt is not null
            || row.SupersededAt is not null
            || row.ExpiresAtTicks <= now.UtcTicks)
        {
            return false;
        }

        row.FirstPreviewedAt = now;
        row.FirstPreviewedFrom = from;
        context.SaveChanges();
        return true;
    }

    public int SupersedeLiveFor(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        using var context = contextFactory.CreateDbContext();
        var rows = context.Invites
            .Where(row => row.UserId == userId
                && row.RedeemedAt == null
                && row.RevokedAt == null
                && row.SupersededAt == null
                && row.ExpiresAtTicks > now.UtcTicks)
            .ToList();
        foreach (var row in rows)
        {
            row.SupersededAt = now;
            row.SupersededAtTicks = now.UtcTicks;
        }

        context.SaveChanges();
        return rows.Count;
    }

    public bool Revoke(Guid inviteId)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Invites.FirstOrDefault(candidate => candidate.Id == inviteId);
        if (row is null || row.RevokedAt is not null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        row.RevokedAt = now;
        row.RevokedAtTicks = now.UtcTicks;
        context.SaveChanges();
        return true;
    }

    public IReadOnlyList<Invite> All()
    {
        using var context = contextFactory.CreateDbContext();
        return context.Invites.AsNoTracking()
            .OrderByDescending(row => row.CreatedAtTicks)
            .AsEnumerable()
            .Select(ToInvite)
            .ToList();
    }

    private static Invite ToInvite(InviteRow row) =>
        new(row.Id, row.UserId, row.InvitedByUserId, row.CreatedAt, row.ExpiresAt,
            row.RedeemedAt, row.RedeemedFrom, row.SupersededAt, row.RevokedAt,
            row.FirstPreviewedAt, row.FirstPreviewedFrom);
}

// Password storage on the user row, kept here with the other credential code
// rather than in EfRuntimeStore, so everything that touches a secret is in one
// file and reviewable as a unit.
public static class EfPasswords
{
    public static string? Read(RuntimeDbContext context, Guid userId)
    {
        var hash = context.Users.AsNoTracking()
            .Where(row => row.Id == userId)
            .Select(row => row.PasswordHash)
            .FirstOrDefault();
        return string.IsNullOrEmpty(hash) ? null : hash;
    }

    public static void Write(RuntimeDbContext context, Guid userId, string hash)
    {
        var row = context.Users.FirstOrDefault(candidate => candidate.Id == userId);
        if (row is null)
        {
            return;
        }

        row.PasswordHash = hash;
        row.PasswordSetAt = DateTimeOffset.UtcNow;
        context.SaveChanges();
    }

    public static bool WriteFirst(RuntimeDbContext context, Guid userId, string hash)
    {
        var now = DateTimeOffset.UtcNow;
        return context.Users
            .Where(row => row.Id == userId && row.PasswordHash == "")
            .ExecuteUpdate(setters => setters
                .SetProperty(row => row.PasswordHash, hash)
                .SetProperty(row => row.PasswordSetAt, now)) == 1;
    }
}

// The memory-mode versions. Sessions and keys that vanish on restart are the
// right semantics for a runtime whose whole database does.
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, PanelSession> byHash = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public (PanelSession Session, string Secret) Create(Guid userId, TimeSpan lifetime)
    {
        var secret = SecretHash.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var session = new PanelSession(Guid.NewGuid(), userId, now, now.Add(lifetime), now, null);
        lock (gate)
        {
            byHash[SecretHash.Of(secret)] = session;
        }
        return (session, secret);
    }

    public PanelSession? Resolve(string secret, DateTimeOffset now)
    {
        lock (gate)
        {
            return byHash.TryGetValue(SecretHash.Of(secret), out var session) && session.IsLive(now) ? session : null;
        }
    }

    public void Touch(Guid sessionId, DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var (hash, session) in byHash.ToList())
            {
                if (session.Id == sessionId)
                {
                    byHash[hash] = session with { LastSeenAt = now };
                }
            }
        }
    }

    public void Revoke(Guid sessionId)
    {
        lock (gate)
        {
            foreach (var (hash, session) in byHash.ToList())
            {
                if (session.Id == sessionId)
                {
                    byHash[hash] = session with { RevokedAt = DateTimeOffset.UtcNow };
                }
            }
        }
    }

    public int RevokeAllFor(Guid userId)
    {
        lock (gate)
        {
            var revoked = 0;
            foreach (var (hash, session) in byHash.ToList())
            {
                if (session.UserId == userId && session.RevokedAt is null)
                {
                    byHash[hash] = session with { RevokedAt = DateTimeOffset.UtcNow };
                    revoked++;
                }
            }
            return revoked;
        }
    }

    public IReadOnlyList<PanelSession> For(Guid userId)
    {
        lock (gate)
        {
            return byHash.Values.Where(session => session.UserId == userId)
                .OrderByDescending(session => session.CreatedAt)
                .ToList();
        }
    }
}

// The memory-mode twin. A missing twin is a real hole in a real mode, not a test
// fixture: databaseProvider == "memory" is a shipping configuration.
public sealed class InMemoryInviteStore : IInviteStore
{
    private readonly Dictionary<string, Invite> byHash = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public (Invite Invite, string Code) Create(Guid userId, Guid invitedBy, TimeSpan lifetime)
    {
        var code = SecretHash.NewSecret(CredentialFormat.InvitePrefix);
        var now = DateTimeOffset.UtcNow;
        var invite = new Invite(Guid.NewGuid(), userId, invitedBy, now, now.Add(lifetime),
            null, "", null, null, null, "");
        lock (gate)
        {
            byHash[SecretHash.Of(code)] = invite;
        }
        return (invite, code);
    }

    public Invite? Resolve(string code, DateTimeOffset now)
    {
        lock (gate)
        {
            return byHash.TryGetValue(SecretHash.Of(code), out var invite) ? invite : null;
        }
    }

    // The one place where "it is atomic" is a claim about a lock rather than about
    // the database. The predicate is the same full liveness check the EF store uses,
    // so the two stores agree on what "spendable" means.
    public bool Spend(Guid inviteId, DateTimeOffset now, string from)
    {
        lock (gate)
        {
            foreach (var (hash, invite) in byHash.ToList())
            {
                if (invite.Id != inviteId)
                {
                    continue;
                }

                if (!invite.IsLive(now))
                {
                    return false;
                }

                byHash[hash] = invite with { RedeemedAt = now, RedeemedFrom = from };
                return true;
            }
            return false;
        }
    }

    public bool NotePreview(Guid inviteId, DateTimeOffset now, string from)
    {
        lock (gate)
        {
            foreach (var (hash, invite) in byHash.ToList())
            {
                if (invite.Id != inviteId)
                {
                    continue;
                }

                if (invite.FirstPreviewedAt is not null || !invite.IsLive(now))
                {
                    return false;
                }

                byHash[hash] = invite with { FirstPreviewedAt = now, FirstPreviewedFrom = from };
                return true;
            }
            return false;
        }
    }

    public int SupersedeLiveFor(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            var superseded = 0;
            foreach (var (hash, invite) in byHash.ToList())
            {
                if (invite.UserId == userId && invite.IsLive(now))
                {
                    byHash[hash] = invite with { SupersededAt = now };
                    superseded++;
                }
            }
            return superseded;
        }
    }

    public bool Revoke(Guid inviteId)
    {
        lock (gate)
        {
            foreach (var (hash, invite) in byHash.ToList())
            {
                if (invite.Id == inviteId && invite.RevokedAt is null)
                {
                    byHash[hash] = invite with { RevokedAt = DateTimeOffset.UtcNow };
                    return true;
                }
            }
            return false;
        }
    }

    public IReadOnlyList<Invite> All()
    {
        lock (gate)
        {
            return byHash.Values.OrderByDescending(invite => invite.CreatedAt).ToList();
        }
    }
}

public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly Dictionary<string, ApiKey> byHash = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public (ApiKey Key, string Secret) Create(Guid ownerUserId, string name, TimeSpan? lifetime)
    {
        var secret = SecretHash.NewSecret(CredentialFormat.ApiKeyPrefix);
        var now = DateTimeOffset.UtcNow;
        var key = new ApiKey(Guid.NewGuid(), ownerUserId, string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim(),
            now, lifetime is null ? null : now.Add(lifetime.Value), null, null);
        lock (gate)
        {
            byHash[SecretHash.Of(secret)] = key;
        }
        return (key, secret);
    }

    public ApiKey? Resolve(string secret, DateTimeOffset now)
    {
        lock (gate)
        {
            return byHash.TryGetValue(SecretHash.Of(secret), out var key) && key.IsLive(now) ? key : null;
        }
    }

    public void MarkUsed(Guid keyId, DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var (hash, key) in byHash.ToList())
            {
                if (key.Id == keyId)
                {
                    byHash[hash] = key with { LastUsedAt = now };
                }
            }
        }
    }

    public bool Revoke(Guid keyId, Guid ownerUserId)
    {
        lock (gate)
        {
            foreach (var (hash, key) in byHash.ToList())
            {
                if (key.Id == keyId && key.OwnerUserId == ownerUserId && key.RevokedAt is null)
                {
                    byHash[hash] = key with { RevokedAt = DateTimeOffset.UtcNow };
                    return true;
                }
            }
            return false;
        }
    }

    public int RevokeAllFor(Guid userId)
    {
        lock (gate)
        {
            var revoked = 0;
            var now = DateTimeOffset.UtcNow;
            foreach (var (hash, key) in byHash.ToList())
            {
                if (key.OwnerUserId == userId && key.RevokedAt is null)
                {
                    byHash[hash] = key with { RevokedAt = now };
                    revoked++;
                }
            }
            return revoked;
        }
    }

    public IReadOnlyList<ApiKey> For(Guid ownerUserId)
    {
        lock (gate)
        {
            return byHash.Values.Where(key => key.OwnerUserId == ownerUserId)
                .OrderByDescending(key => key.CreatedAt)
                .ToList();
        }
    }
}
