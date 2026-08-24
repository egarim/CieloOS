namespace WorkspaceRuntime.Application;

// A human's login, a browser session, and an integration's key — the three
// things issue #9 says are missing, kept apart on purpose:
//
//   password  proves a person is who they say they are, once
//   session   carries that proof for a while, and can be ended
//   api key   lets a program act without holding the person's credential
//
// The legacy identity token (slug + HMAC, deterministic, eternal) still exists
// for agents and the CLI. It is a capability, not a login, and nothing here
// pretends otherwise.

public sealed record PanelSession(
    Guid Id,
    Guid UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RevokedAt)
{
    public bool IsLive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

public sealed record ApiKey(
    Guid Id,
    Guid OwnerUserId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt)
{
    public bool IsLive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

// Hashing lives behind an interface so the algorithm can move without touching
// the endpoints — and so a test can use a fast one.
public interface IPasswordHasher
{
    string Hash(string password);

    // Returns false for a wrong password AND for a stored hash this
    // implementation cannot read, so an unreadable hash denies rather than
    // admits.
    bool Verify(string password, string stored);

    // A real hash to verify against when the user does not exist or has no
    // password, so a failed login takes the same time either way and cannot be
    // used to enumerate who has a desk on this machine.
    string DummyHash { get; }
}

public interface ISessionStore
{
    // The secret is returned once and never stored: only its hash is kept, so a
    // stolen database is not a set of live sessions.
    (PanelSession Session, string Secret) Create(Guid userId, TimeSpan lifetime);

    PanelSession? Resolve(string secret, DateTimeOffset now);

    void Touch(Guid sessionId, DateTimeOffset now);

    void Revoke(Guid sessionId);

    // "Sign out everywhere" — the thing a permanent token could never do.
    int RevokeAllFor(Guid userId);

    IReadOnlyList<PanelSession> For(Guid userId);
}

public interface IApiKeyStore
{
    (ApiKey Key, string Secret) Create(Guid ownerUserId, string name, TimeSpan? lifetime);

    ApiKey? Resolve(string secret, DateTimeOffset now);

    void MarkUsed(Guid keyId, DateTimeOffset now);

    bool Revoke(Guid keyId, Guid ownerUserId);

    IReadOnlyList<ApiKey> For(Guid ownerUserId);
}

public static class CredentialFormat
{
    // A visible prefix so a leaked string is identifiable in a log or a paste,
    // and so the auth gate can tell an API key from a legacy identity token
    // without trying both.
    public const string ApiKeyPrefix = "cielo_ak_";

    public const string SessionCookie = "cielo_session";

    // Cookie auth is only honoured when this header is present. A cross-site form
    // post cannot set a custom header without a CORS preflight we never grant, so
    // this is what keeps a cookie from being usable for CSRF. The panel sends it
    // on every request.
    public const string PanelHeader = "X-Cielo-Panel";
}
