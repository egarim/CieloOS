# Brief 02a — invitations: the storage layer only

## Why invitations exist

`docs/invites.md` is the specification. Read §1, §2, §6 and §8; this brief scopes
the work and fixes the acceptance criteria.

The problem in one line: creating a teammate hands the owner a **permanent,
unrevocable, non-single-use identity token** and expects them to email it. That
credential never expires, cannot be revoked, and authenticates its holder as a
full human principal. An invitation is its opposite in every dimension that
matters.

**One thing in that document is now out of date in our favour.** §3 argues that
every previous proposal was wrong because a leaked owner token reaches `OwnerOnly`
routes from any address. That was true when it was written and is fixed:
`PrincipalGate.Check` requires `hasSession` for `OwnerOnly`, and the four
on-machine gates now run on `TransportFacts.OnThisMachine` over a unix socket.
Build against the code, not against that section's threat model.

## Scope: storage and domain. No endpoints, no UI.

This brief stops at "an invitation can be created, resolved, spent, superseded and
revoked, and it survives a restart". Nothing calls it yet. `POST /api/users` is
**not** changed and still returns the identity token — that is 02b, and doing it
here would break every existing caller before there is anything to replace it.

### 1. `Invite`, `IInviteStore`, `InvitePrefix`

In `src/backend/WorkspaceRuntime.Application/Credentials.cs`, beside
`ISessionStore` and `IApiKeyStore`, because that file's stated rule is that
everything touching a secret stays reviewable as one unit.

```csharp
public interface IInviteStore
{
    (Invite Invite, string Code) Create(Guid userId, Guid invitedBy, TimeSpan lifetime);
    Invite? Resolve(string code, DateTimeOffset now);   // ANY state — see below
    bool Spend(Guid inviteId, DateTimeOffset now, string from);
    bool NotePreview(Guid inviteId, DateTimeOffset now, string from);
    int SupersedeLiveFor(Guid userId);
    bool Revoke(Guid inviteId);
    IReadOnlyList<Invite> All();
}

public const string InvitePrefix = "cielo_inv_";
```

`Resolve` returns the row **whatever its state**, deliberately. The handler needs
to tell the person "this link was already used" rather than "no such link", and
hiding dead rows behind `null` collapses *expired* and *never existed* into one
answer at the wrong layer. Refusing to distinguish them **in the reply** is a
different decision, made later, in the handler.

The prefix exists for the reason `ApiKeyPrefix` does, plus one: the auth gate
switches on prefix, and a credential-shaped string with no prefix is a string
somebody eventually presents as a bearer token. Nothing resolves an invite except
the redeem and preview handlers — an invitation is not a way to authenticate, it
is a way to obtain a password.

Two functions on `Invite`, matching the shape `ApiKey.IsLive` and
`PanelSession.IsLive` already have:

```csharp
public bool IsLive(DateTimeOffset now) =>
    RedeemedAt is null && RevokedAt is null && SupersededAt is null && ExpiresAt > now;

// Check order is DISPLAY order: report the first true thing, because "it was
// replaced" is the sentence that tells the person what to do next. A row can be
// both superseded and expired.
public string State(DateTimeOffset now) =>
    RedeemedAt is not null     ? "used"
    : RevokedAt is not null    ? "revoked"
    : SupersededAt is not null ? "superseded"
    : ExpiresAt <= now         ? "expired"
    : "live";
```

**Nothing is ever deleted.** Expiry needs no write — time passes. Spending writes
`RedeemedAt`, re-issuing writes `SupersededAt` on the old row, calling one off
writes `RevokedAt`. There is no sweep job, no DELETE, and **no `Purpose` column**:
there is exactly one purpose, and a column with one legal value is an invitation
to add a second without thinking about it.

### 2. The row and the migration

`InviteRow` in `RuntimeDbContext.cs` beside the other `*Row` classes. The schema is
given verbatim in `docs/invites.md` §8 — follow it, including the paired
`*AtTicks` columns, which exist because every other ordered timestamp in this
schema carries its own ticks for ordering that survives the provider.

Hash the code the way sessions and API keys already are (`SecretHash`,
`EfCredentialStores.cs:13-23`), so a copy of the database is a list of **who was
invited**, not a set of usable links.

**Do not write the migration.** `dotnet ef` is available here and will be run
against your model to generate `Invites`, its designer and the snapshot — 1,345
lines of generated file that would otherwise eat your entire output budget and
arrive subtly wrong. Write the row class and the `OnModelCreating` configuration
so the generated migration matches §8's schema, and say in NOTES if you expect it
to differ.

`DatabaseUpgradeTests.The_model_and_the_migrations_have_not_drifted_apart` calls
`HasPendingModelChanges()`, so the model and the migration are checked against
each other automatically once it is generated.

§8 also adds `SuspendedAt` to `runtime_users`. **Include the column in the
migration and the row class; wire nothing to it.** Suspension is 02c.

### 3. Both stores, not one

`EfInviteStore` and `InMemoryInviteStore`, both in `EfCredentialStores.cs`, which
already ships them in pairs. The in-memory store is **a shipping configuration**
selected by `databaseProvider == "memory"`, not a test fixture, so a missing twin
is a real hole in a real mode.

`Spend` is a compare-and-swap with exactly one winner. In EF that is a conditional
update; in the in-memory store it takes the existing `lock (gate)` and checks the
same full predicate. That is the one place where "it is atomic" is a claim about a
lock rather than about the database.

`ExecuteUpdate` appears nowhere in `src/` today — every store is
read-modify-`SaveChanges` over `IDbContextFactory`. If you use it, it must behave
identically on Npgsql, which `Program.cs` wires against the same migrations. If
you are not confident, use the existing pattern and say so in NOTES; a slower
correct store beats a faster one whose atomicity nobody proved.

### 4. Tests

1. A created invite is live; the code it returns is not what is stored.
2. `Spend` succeeds once. **Race it**: N threads, exactly one winner, N-1 losers.
   This is the test the whole design leans on.
3. Spending, revoking and superseding each move `State` to the right word, and
   `IsLive` to false.
4. Expiry needs no write: an invite whose `ExpiresAt` has passed reports
   `expired` and `IsLive` false without anything having been written.
5. `State` reports the FIRST true thing for a row that is both superseded and
   expired.
6. `Resolve` returns dead rows rather than null.
7. `SupersedeLiveFor` supersedes only live rows for that user, and returns how
   many.
8. Everything survives a round trip through the EF store, not just the in-memory
   one.

## Rules

`Credentials.cs`, `RuntimeDbContext.cs` and `EfCredentialStores.cs` are all small
enough for FILE blocks — but check the sizes in the context below before deciding,
and use EDIT blocks for anything over ~500 lines.

No migration files. They are generated here from your model.
