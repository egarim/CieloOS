# Brief 02c — suspension, and the thing "sign out everywhere" never did

## Why this comes before the token stops travelling

`docs/invites.md` §5 is blunt about it: without suspension, an invitation
redeemed by the wrong person is **permanent**.

The redeemer's first move is `POST /api/keys` with no expiry, which yields
`ApiKey.ExpiresAt = null` and `IsLive()` true forever. And no route on this
machine lets an owner see or revoke somebody else's keys — `GET /api/keys`
filters `keys.For(caller.Subject)` and `DELETE /api/keys/{id}` scopes to
`caller.Subject`. So the owner watches an account they cannot close.

Shipping an off-box path to a first password while that is true gets the order
backwards, which is why 02e — `POST /api/users` returning an invitation instead
of a permanent token — waits behind this.

## 1. `IApiKeyStore.RevokeAllFor(Guid userId)`

New, mirroring `ISessionStore.RevokeAllFor`. Both stores, EF and in-memory.

**This is worth having on its own merits, and the reason is uncomfortable.**
`POST /api/auth/password` today ends every session and leaves every minted key
alive. So "changing your password signs you out everywhere" — which is what the
comment there says it is for, and what a person expects after losing a laptop —
has never been true. A key minted before the change still authenticates after it.

Fix that too: the password change should revoke keys as well as sessions. If you
think that is wrong — a key is arguably a deliberate, separately-managed
credential rather than a session — say so and argue it rather than silently
picking one. But do not leave the comment claiming something the code does not do.

## 2. `POST /api/users/{slug}/suspend` and `/unsuspend`

Both `OwnerOnly`. Both tabled in `AccessPolicyTests`.

`SuspendedAt` already exists on `UserRow` and in the `Invites` migration from
02a, wired to nothing.

**Suspend:**
- refuses when the target `IsMachineOwner`, for the obvious reason
- writes `SuspendedAt`
- `sessions.RevokeAllFor(user.Id)` and `keys.RevokeAllFor(user.Id)`
- supersedes any live invitation — a suspended person should not be able to
  redeem their way back in
- audits it

**Unsuspend** clears the column and does nothing else. It does **not** restore
sessions or keys: those were revoked and stay revoked, and the person signs in
again with their password. Nothing is deleted anywhere in this.

Suspension is not deletion and not a slug rename. Their home volume, token file,
audit history and spreadsheet all stay exactly where they are, and unsuspending
is one write. That is what makes "an invitation was spent by the wrong person" a
recoverable event rather than a permanent one.

## 3. The check, and where it goes

In the middleware, **immediately after principal resolution**, so a suspended
person is refused whatever they present — session cookie, API key, or identity
token. Not in each handler: a check that has to be remembered in thirty places is
a check that will be missed in one.

Return a message that says what happened. "Unauthorized" tells a suspended person
nothing and tells the owner nothing when they are looking at a support question.

The identity token path matters most here. It is the credential that cannot be
revoked, so the suspension check is the only thing that stops it.

## 4. Tests

1. Both routes are `OwnerOnly` in `AccessPolicy.Required`.
2. Suspending refuses the machine owner.
3. A suspended person is refused with a session cookie, with an API key, **and
   with an identity token** — the third is the one that matters, because nothing
   else can stop it.
4. Suspending revokes sessions AND keys.
5. Suspending supersedes a live invitation.
6. Unsuspending restores access but NOT the old sessions or keys.
7. `/api/auth/password` revokes keys as well as sessions — the test that would
   have failed before this brief.
8. Suspension survives a restart (it is a column, not memory).

## Rules

Build and run the suite until green. Do not commit.

Do not touch `distro/install-quiet.sh`, `distro/scripts/cielo-install-tui.sh`,
`distro/scripts/cielo-install-frames/`, `distro/scripts/build-release.sh`,
`distro/RELEASE-README.md`, `mockups/portal/`.

Final message: new test count, and whether you agree that a password change
should revoke API keys.
