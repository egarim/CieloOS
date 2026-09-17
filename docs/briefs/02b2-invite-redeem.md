# Brief 02b-2 — `/preview` and `/redeem`, the public pair

This is the security-critical half of invitations. `docs/invites.md` §5 specifies
it and the **order of operations in redeem is the design** — implement the steps
in the order given, not a rearrangement that computes the same answer.

Landed already, do not rebuild: `IInviteStore` with an atomic `Spend`, both
stores, the `Invites` migration, `POST/GET /api/invites` and
`/api/invites/{id}/revoke` (all `OwnerOnly`), and `SupersedeLiveFor` wired into
first-password and organization-move.

**Read the code, not the document, for anything textual.** `docs/invites.md`
quotes source that has since changed — it shows `Secure = context.Request.IsHttps`,
which was replaced this morning by `TransportFacts.Confidential(context, tlsTerminatedBy)`.
The document is the specification for *behaviour* only.

## 1. `AccessPolicy`, first

Both routes are **Public**, added to the Public block at the *top* of
`AccessPolicy.Required` beside `/api/auth/login`, above the `OwnerOnly` rules so
the `/api/invites` exact-match cannot swallow them.

Public for the reason login is public: this is what you use when you have no way
in. Their guard is a 256-bit one-time code.

Table both in `AccessPolicyTests` in the same commit. `Required()` falls through
to `AnyPrincipal`, so an unlisted route is agent-reachable by omission.

## 2. `POST /api/invites/preview`

Body `{ code }`. Tells an invitee who they are about to become — display name,
organization, and whether the link is usable — so they are not asked to choose a
password for an account they cannot identify.

Returns the `state` vocabulary from `Invite.State(now)`: `live`, `used`,
`revoked`, `superseded`, `expired`. An unrecognised code gets the same shape as a
dead one; do not let the reply distinguish "never existed" from "expired", which
would make this an oracle for testing codes.

Calls `NotePreview` so the owner can see the link was opened, and from where.

## 3. `POST /api/invites/redeem`

Body `{ code, password }`. The steps, in this order:

**0. Transport.** Refuse 403 unless `TransportFacts.Confidential(context, tlsTerminatedBy)`.
A first password and the session cookie that follows must not cross a network in
cleartext. The loopback gate on first passwords was incidentally the guarantee
that a never-yet-set password is never typed over a network; this replaces it.
This check will refuse a plain-HTTP headless install, which is intended and is
§11 open question 1.

**1. Throttle**, on its own bucket `invite:{source}` — **never** `source:{source}`.
Sharing the login bucket is a denial-of-sign-in primitive: garbage bodies to a
public endpoint would lock `/api/auth/login` for everyone, refillable forever,
free, and invisible because garbage codes write no audit. Worse here because this
box's own copy says loopback includes "your SSH tunnel", so the whole population
shares one source address. Checked before anything costs CPU, the ordering
`/api/auth/login` already uses.

**2. Password length**, against the request alone, before any lookup. A typo must
not burn somebody's only way onto the machine, and validating the invitation first
would make a four-character password a probe that separates live codes from dead
ones.

**3. Resolve** by hash. **4. Refuse** anything not live, naming the state, in the
preview's vocabulary. Audit a failure **only when a real invitation resolved**; an
unrecognised code writes nothing.

**5. Spend before writing anything.** `IInviteStore.Spend` is already a single
atomic statement — use it and check its return. Do not re-read and re-check.

Spend before the password write, deliberately: a crash between them burns an
invitation and costs a re-issue, while the other order leaves a live invitation
over a credential it has already changed, which a second holder can change again.

**6. Write the hash conditionally.** Add
`bool IRuntimeStore.SetFirstPasswordHash(Guid userId, string hash)` — an
`ExecuteUpdate` predicated on `PasswordHash == ""`, returning whether it landed,
with an in-memory twin. The existing `SetPasswordHash` is unconditional and would
reopen the invariant: the "does this account have a password" read happens before
the spend, and between the two somebody can walk to the box and set one through
`/api/auth/password` at loopback. Check-then-act across two store calls is a
description of an invariant, not the invariant. If it returns false the invitation
is already burned — give the ordinary refusal, fail closed, and audit it.

**7. Session and audit.** `sessions.Create(user.Id, TimeSpan.FromDays(14))` and the
existing `SessionCookieOptions`, so the reply is byte-for-byte what a password
login returns and the portal's signed-in path is one code path. Revoke nothing: an
account with no password had no sessions.

**One audit row, not two**, with named arguments: `Principal = <owner slug>` and
`OnBehalfOf = <invitee slug>`. `AuditHomeSlugs` yields both, and
`/api/audit-events` filters `Ownership.CanAccessHome` over that set, so one row
reaches the owner's Activity feed and the invitee's. The detail carries the source
address — the one fact that turns "that wasn't me" into something actionable.

## 4. Tests

1. Both routes are `Public` in `AccessPolicy.Required`.
2. Redeem over a non-confidential transport is 403, before anything else happens.
3. A short password is refused **without** the invitation being consumed.
4. A dead invitation is refused with its state, and the code is not spent.
5. An unrecognised code is refused and **writes no audit row**.
6. Redeeming twice: the second is refused, and the password is not changed.
7. A successful redeem sets the password, spends the invitation, issues a session
   cookie, and writes exactly **one** audit row carrying both slugs.
8. `SetFirstPasswordHash` returns false when a password already exists, and the
   handler then refuses rather than overwriting.
9. Preview does not distinguish an unknown code from an expired one.

## How to work

Build and test locally until green: `dotnet build` and
`dotnet test tests/backend/WorkspaceRuntime.Tests/WorkspaceRuntime.Tests.csproj`.
The suite is at 558 and must not drop.

Do not commit. Leave the working tree for review.

Do not touch: `distro/install-quiet.sh`, `distro/scripts/cielo-install-tui.sh`,
`distro/scripts/cielo-install-frames/`, `distro/scripts/build-release.sh`,
`distro/RELEASE-README.md`, `mockups/portal/`. Those are somebody else's
uncommitted work in this tree.
