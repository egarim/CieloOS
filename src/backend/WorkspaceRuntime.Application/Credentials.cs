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

// An invitation: the ONE thing a person with no password may do, and the only
// credential this product lets out of the building. Hashed like a session secret
// and an API key, so a copy of the database is a list of who was invited, not a
// set of usable links.
//
// Nothing here is ever deleted. An invitation that was used, replaced, called off
// or aged out is a column and a clock — which is also what makes the owner's list
// worth reading: three rows for Dmitri is the honest record that he lost it twice.
public sealed record Invite(
    Guid Id,
    Guid UserId,
    Guid InvitedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RedeemedAt,
    string RedeemedFrom,
    DateTimeOffset? SupersededAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? FirstPreviewedAt,
    string FirstPreviewedFrom)
{
    public bool IsLive(DateTimeOffset now) =>
        RedeemedAt is null && RevokedAt is null && SupersededAt is null && ExpiresAt > now;

    // Check order is DISPLAY order: report the first true thing, because "it was
    // replaced" is the sentence that tells the person what to do next. A row can
    // be both superseded and expired.
    public string State(DateTimeOffset now) =>
        RedeemedAt is not null     ? "used"
        : RevokedAt is not null    ? "revoked"
        : SupersededAt is not null ? "superseded"
        : ExpiresAt <= now         ? "expired"
        : "live";
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

    int RevokeAllFor(Guid userId);

    IReadOnlyList<ApiKey> For(Guid ownerUserId);
}

public interface IInviteStore
{
    (Invite Invite, string Code) Create(Guid userId, Guid invitedBy, TimeSpan lifetime);

    // ANY state, deliberately: the handler needs to tell the person "this link
    // was already used" rather than "no such link", and hiding dead rows behind
    // null collapses expired and never-existed into one answer at the wrong
    // layer. Refusing to distinguish them IN THE REPLY is a different decision,
    // made later, in the handler.
    Invite? Resolve(string code, DateTimeOffset now);

    // The compare-and-swap. Exactly one caller wins.
    bool Spend(Guid inviteId, DateTimeOffset now, string from);

    bool NotePreview(Guid inviteId, DateTimeOffset now, string from);

    int SupersedeLiveFor(Guid userId);

    bool Revoke(Guid inviteId);

    IReadOnlyList<Invite> All();
}

public static class CredentialFormat
{
    // A visible prefix so a leaked string is identifiable in a log or a paste,
    // and so the auth gate can tell an API key from a legacy identity token
    // without trying both.
    public const string ApiKeyPrefix = "cielo_ak_";

    // Invitations carry a visible prefix for the reason API keys do, plus one:
    // the auth gate switches on ApiKeyPrefix, and a credential-shaped string
    // with no prefix is a string somebody will eventually present as a bearer
    // token. Nothing resolves this one except the redeem and preview handlers —
    // an invitation is not a way to authenticate, it is a way to obtain a
    // password.
    public const string InvitePrefix = "cielo_inv_";

    public const string SessionCookie = "cielo_session";

    // Cookie auth is only honoured when this header is present. A cross-site form
    // post cannot set a custom header without a CORS preflight we never grant, so
    // this is what keeps a cookie from being usable for CSRF. The panel sends it
    // on every request.
    public const string PanelHeader = "X-Cielo-Panel";
}
