# Brief 02b-1 — the owner's half of invitations

Storage landed in 02a: `IInviteStore`, `Invite`, `InvitePrefix`, both stores, the
`Invites` migration, 15 tests. Nothing calls any of it.

This brief adds the three routes an **owner** uses, and the two places that must
supersede a live invitation as part of their own work. It does **not** touch
`POST /api/users`, `/preview` or `/redeem` — minting a link nobody can redeem is
fine for one commit; changing how teammates are created before redemption exists
is not.

Read `docs/invites.md` §5. It is the specification.

## 1. `AccessPolicy` first, and it is the part to get right

`AccessPolicy.Required` **falls through to `AnyPrincipal`** (`Security.cs`). An
unlisted invitation route is agent-mintable by omission and nothing would fail to
say so. This has bitten this codebase before: `/api/version/{id}/restore` matches
no rule today, which is how an agent can restore its owner's home.

| route | verb | level |
|---|---|---|
| `/api/invites` | POST | `OwnerOnly` |
| `/api/invites` | GET | `OwnerOnly` |
| `/api/invites/{id}/revoke` | POST | `OwnerOnly` |

`OwnerOnly` now also means a session — `PrincipalGate.Check` refuses it without
one — so a leaked identity token cannot mint an invitation. That is the property
`docs/invites.md` §3 says every earlier proposal got wrong; it is true now, and
these routes inherit it for free.

**Write the tests for this first**, in `AccessPolicyTests`, which already tables
route/verb/level triples. A route that falls through to `AnyPrincipal` must be a
test failure, not a code review finding.

## 2. `POST /api/invites`

Body `{ slug }` — the desk to invite. Returns `{ code, expiresAt, slug }`.

- Refuse if that user already has a password. An invitation sets a **first**
  password and never a reset, so the owner can never mint themselves into a
  teammate's account. This is the invariant the whole design rests on; it is
  checked here and again at redeem.
- Refuse if the target `IsMachineOwner`.
- `SupersedeLiveFor(userId)` **before** creating the new one, so re-issuing kills
  the old link rather than leaving two live.
- Lifetime 72 hours.
- Audit it.

**Return the code, not a link.** The machine cannot know its own public URL: a
DNAT rewrites the destination before the packet arrives, so even
`SSH_CONNECTION` reports the post-translation address. `Request.Host` is worse
than useless — Kestrel accepts any `Host` unless `AllowedHosts` says otherwise, so
composing a link from it is password-reset poisoning, and a credential-bearing URL
aimed at an attacker's origin means their JavaScript reads the fragment. The panel
composes the link from `window.location.origin`, which is correct by construction
because the owner is looking at the panel through an address that works.

Say in NOTES that you did this and why, so the next person does not "fix" it.

## 3. `GET /api/invites`

Every invitation, whatever its state, newest first, with `state` from
`Invite.State(now)`. Never the code or its hash — those are gone the moment they
are issued, and a list endpoint that could return one would undo the hashing.

Three rows for one person is the point, not a bug: it is the honest record that
they lost the link twice.

## 4. `POST /api/invites/{id}/revoke`

Writes `RevokedAt`. Idempotent — revoking a dead invitation is not an error, it
is a no-op with the same answer. Audit it.

## 5. The two places that must supersede

Both are existing handlers that gain one call:

- **`POST /api/users/{slug}/organization`** — a move is exactly when the owner
  should be told the old link is dead. Its audit row is written one layer down, in
  `SetUserOrganization`; do not add a second.
- **`POST /api/auth/password`**, the **first-password branch** (`existing is
  null`) — once they have a password, any live invitation is meaningless and must
  not outlive it.

`docs/invites.md` §6 explains why this is a written timestamp rather than a
computed "stale" state: deriving staleness from `user.OrgSlug != OrgSlugAtIssue`
means moving somebody acme→globex→acme **resurrects a dead link**, and nothing in
the row records it was ever dead.

## 6. Tests

1. Each of the three routes is `OwnerOnly` in `AccessPolicy.Required`. Table them.
2. Minting for a user who already has a password is refused.
3. Minting for the machine owner is refused.
4. Minting twice supersedes the first — the old row reads `superseded`, the new
   one `live`, and there is never more than one live invitation per user.
5. `GET /api/invites` never returns the code or the hash. Assert the response
   body does not contain either string.
6. Revoking twice is not an error and does not move `RevokedAt` the second time.
7. Moving a person between organizations supersedes their live invitation.
8. Setting a first password supersedes their live invitation.

## Rules

`Program.cs` is 2,960 lines and `Security.cs` is 330. **EDIT blocks for
`Program.cs`**; `Security.cs` may be a FILE block. FILE blocks for new test files.

Route handlers in `Program.cs` are top-level `app.MapPost(...)` lambdas — match the
surrounding style, including the named-argument `AppendAudit` shape the newer call
sites use.

## One more rule, learned the hard way

**OLD text for an EDIT block comes from the excerpt file, never from
`docs/invites.md`.** That document quotes source code, and its quotes are
historical — it shows `Secure = context.Request.IsHttps` in §5, which was replaced
by a `Confidential()` predicate earlier today. A previous attempt at this brief
anchored five of six edits on the document's code and matched nothing.

The document is the specification for *behaviour*. The excerpt is the source of
truth for *text*.

Nothing in this brief requires touching `SessionCookieOptions`, the logout
handlers, or the login handler. If you find yourself editing those, you are
following §5's prose about what redeem will eventually do, which is 02b-2.
