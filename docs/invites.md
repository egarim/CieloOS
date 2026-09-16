# Invitations — how a remote teammate gets their first password

Spine: the **operator** design (the invitation is a credential in its own right, minted with the person, previewable, listed, revocable, never a reset). Grafted in and marked where they appear: the first-password-only invariant and the refusal ordering from **minimal**; `context.Items["session"]` as the test for "this caller proved a password", `IApiKeyStore.RevokeAllFor`, and the fragment-not-query-string rule from **hostile**. Every flaw six judges marked fatal is either fixed below or answered in §12 by name.

All line references verified against the working tree at `C:\Users\joche\CieloOS` on 2026-09-15.

---

## 1. WHAT IS BROKEN, AND WHY IT MATTERS

`POST /api/users` (Program.cs:1024) creates a person, writes their `0600` token file, and returns `{ slug, token }`. That token is the only credential the new person has. `UserRow.PasswordHash` defaults to `""` (RuntimeDbContext.cs:55), so every account starts with no password.

Setting the first one is loopback-only (Program.cs:812-821):

```csharp
else if (!IsLoopback(context.Connection.RemoteIpAddress))
{
    return Results.Json(
        new { error = "Set your first password on the machine itself (localhost or your SSH tunnel)." },
        statusCode: StatusCodes.Status403Forbidden);
}
```

The gate is right and its reason is right — an eternal, derivable, non-revocable identity token must not be convertible into a password from anywhere. But it produces a dead end that nobody designed:

- **The remote teammate cannot set their own first password.** They are not on the box. That is what "remote" means.
- **The owner cannot set it for them.** `/api/auth/password` only ever touches `caller.Subject` (Program.cs:795, 823). There is no route that writes another person's hash.
- **So the only credential that can reach them is the permanent identity token**, emailed — which is precisely what the password work (issue #9) existed to replace. `distro/install.sh:251` and `release/cielo/install.sh:251` make the owner paste it by hand; `main.tsx:1283` and `main.tsx:2447` both render it on screen under "Share this with them".

The result is that the safest path is the least convenient one, so the least safe credential on the machine is the one that travels. An emailed identity token never expires, cannot be revoked, is not single-use, and authenticates its holder as a full human principal on every route. An invitation is the opposite of it in every dimension that matters.

### The larger thing this uncovered

While tracing constraint 7 it became clear the loopback gate was already half-false, and not because of anything invitations add. `AccessLevel.OwnerOnly` is enforced at Program.cs:422-424 as a slug comparison:

```csharp
if (level == AccessLevel.OwnerOnly
    && !store.Users.Any(user => user.IsMachineOwner
        && string.Equals(user.Slug, principal.Slug, StringComparison.Ordinal)))
```

A legacy identity token resolves to `PrincipalKind.Human` (Program.cs:408-411 → `PrincipalResolver.BySlug`, Security.cs:229-233) and sets neither `context.Items["session"]` nor `context.Items["apiKey"]`. It therefore passes the owner check, the human check at :434 and the API-key refusal at :447, **from any source address**. A leaked owner token already creates users, creates organizations, and moves people between organizations remotely. Every one of the three proposals built its invitation mint on `OwnerOnly` and asserted that an identity token could not reach it. All three were wrong about the same line.

That is fixed in §3 as a prerequisite, not as a follow-up, because an invitation route built on today's `OwnerOnly` is a route where a leaked owner token mints a way into any passwordless desk — including, on a fresh install, every desk.

---

## 2. THE SHAPE IN ONE PARAGRAPH

An **invitation** is a 256-bit one-time secret, minted by the machine owner for a person who already exists and has no password, stored only as a SHA-256 hash the way sessions and API keys already are (`SecretHash`, EfCredentialStores.cs:13-23), bound to exactly one `UserId`, valid 72 hours, spendable exactly once. `POST /api/users` stops returning the identity token and returns an invitation instead; the `0600` token file is still written on the box for the agent and the CLI, it just stops travelling. The owner sends the link out of band — this machine has no mailer and will not grow one. The invitee opens `/portal.html#invite=<code>`, a public preview tells them who they are and which organization they are joining, they choose a password, and a single compare-and-swap spends the invitation and writes the hash; they land in the portal on an ordinary 14-day session cookie, the same thing `POST /api/auth/login` returns. An invitation sets a **first** password and never a reset, so the owner can never mint themselves into a teammate's account. `POST /api/auth/password` keeps its loopback gate byte-for-byte. Nothing is deleted: spent, superseded, revoked and expired are four nullable timestamps and a clock, and the owner's Invitations list is the whole history, three links to Dmitri included.

Two things ship alongside it because without them the feature is unrecoverable rather than merely risky: **`OwnerOnly` starts meaning "proved a password"**, and **the owner gains a way to suspend an account** — a state change, never a removal.

---

## 3. PREREQUISITE: `OwnerOnly` MEANS A SESSION

This is one clause in the middleware and it is the load-bearing line of the whole document.

```csharp
// OwnerOnly now means the owner PROVED A PASSWORD, not merely that the caller
// resolved to the owner's slug.
//
// The check above asks who you are. It does not ask what you presented, and the
// legacy identity token answers "the owner" from any address on earth — it is
// deterministic, eternal, derivable from the signing key, and it has been mailed
// out of this machine by hand since install.sh shipped. So an owner-only route
// was an owner-TOKEN-only route, and /api/auth/password's loopback gate (:812)
// was the only thing standing between a leaked token and a credential. Adding an
// invitation mint on top of that would have built a second road to the same
// destination and left the first gate standing beside it, guarding nothing.
//
// context.Items["session"] is written at :377 and nowhere else, inside the cookie
// branch alone. The API-key branch writes a different key (:399) and is refused
// below; the token branch writes neither. So this is exactly "came in on a cookie
// this machine issued after verifying a password."
//
// Deliberately NOT `|| IsLoopback(...)`. IsLoopback reads the raw TCP peer
// (:2418) and there is no ForwardedHeaders middleware in this solution — behind
// any reverse proxy every caller in the world is 127.0.0.1, and the disjunct
// would invert this gate completely and silently. See §11 open question 1.
if (level == AccessLevel.OwnerOnly && !context.Items.ContainsKey("session"))
{
    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    await context.Response.WriteAsJsonAsync(new
    {
        error = "Owner actions need a signed-in session. Sign in with your password first."
    });
    return;
}
```

It goes immediately after the existing `OwnerOnly` slug check at :432 and before the human check at :434, so the two refusals stay distinguishable in the logs.

**What this breaks, stated plainly.** `cielo-add-user` posts to `/api/users` with `Authorization: Bearer <owner-token>` and will now get 403. It has to sign in first, which is three lines of shell and forces the owner to have a password — the property we want:

```bash
jar="$(mktemp)"
curl -fsS -c "$jar" -XPOST "http://127.0.0.1:${PORT}/api/auth/login" \
  -H 'Content-Type: application/json' -d "{\"slug\":\"${owner}\",\"password\":\"${password}\"}" >/dev/null
curl -fsS -b "$jar" -H 'X-Cielo-Panel: 1' -XPOST "http://127.0.0.1:${PORT}/api/users" ...
rm -f "$jar"
```

The owner's own first password is still set at loopback, where they already are when they run this. The sequence becomes claim → set your password on the box → sign in → invite people, and every step of it is something the owner can do.

**The cost worth naming:** an owner on an upgraded install who has no password and no shell on the box can no longer add users. They could never set a first password remotely either, so they were already in that position for half of the owner surface; this makes it all of it. The honest fix is the same as everyone else's — get to loopback once.

This clause also closes the same hole on `POST /api/organizations` and `POST /api/users/{slug}/organization`, which have it today and which no proposal noticed.

---

## 4. THE FLOW, END TO END

**Monday 09:14 — the owner adds six people.** Joche is signed in at the panel with his password. `PeopleView` (main.tsx:2251): name, organization, desk, Add. Each call returns `{ slug, invite: { code, expiresAt } }`. The result state becomes an array so the six accumulate instead of overwriting, each row reading

> **Marco Díaz** · `acme-marco` · Acme
> `https://box.example/portal.html#invite=cielo_inv_7f3a…`
> Works once. Expires Thu 18 Sep. This is not a password and not a token.
> [Copy link]

with one [Copy all six] writing `Name — link` lines to the clipboard. He pastes them into one message to Yulia. **The product sends no mail.** There is no SMTP credential on this box, `PlatformUser.Email` is synthesised (`{slug}@example.test` in the seed), and a mailer would need a new egress path, a delivery-failure mode and a bounce nobody reads. Out-of-band delivery means onboarding security is the owner's messaging app's security, and there is no honest way to claim otherwise.

**09:38 — Marco opens the link.** The fragment never reaches the server, so the code is not in the access log, not in any proxy in front of the box, and never rides a `Referer` header. `portal.html` reads `location.hash` **above every other gate** — above the stored-token check, above `/api/setup/status` — and POSTs the code to `/api/invites/preview`. Back comes `{ state: "live", displayName: "Marco Díaz", slug: "acme-marco", orgDisplayName: "Acme", invitedBy: "Joche", expiresAt }`. The page says who he is, which organization he is joining, that the link works once, and asks for a password twice.

**09:39 — he chooses one.** `POST /api/invites/redeem` with `{ code, password }`. The server throttles, rejects a short password *before* the invitation is touched, resolves the code, spends it with one compare-and-swap, writes the hash conditionally on it still being empty, issues a 14-day session cookie, and audits. He lands in the portal at Chat, already signed in — the same drop-through the first-run claim does. The success banner says **"You're signed in as `acme-marco` — that's the name you'll use next time"**, because otherwise he will never learn his slug and the sign-in screen's Desk field is unanswerable. `history.replaceState` clears the fragment only now, after success, so a refresh mid-form still works.

**Wednesday — Dmitri lost the message.** Joche opens the Invitations panel, clicks [Send a new link] on Dmitri's row. The old row takes `SupersededAt`, a new code appears once. Dmitri still has no password, so this is an ordinary mint. Three rows for Dmitri stay in the list forever; that is the honest record that he lost it twice.

**Thursday — Marco quits before redeeming.** [Call it off] on his row writes `RevokedAt`. Without that button the only way to kill a live code would be to mint a replacement and not send it, which leaves the owner deliberately holding a live credential.

---

## 5. THE ENDPOINTS

| Route | Verb | Level | Why that level |
|---|---|---|---|
| `/api/users` | POST | **OwnerOnly** | unchanged (Security.cs:120-124); now also means a session (§3) |
| `/api/invites` | POST | **OwnerOnly** | minting a credential that sets someone's first password *is* the power to create a person |
| `/api/invites` | GET | **OwnerOnly** | the list of which desks have no password yet is a map of the weakest accounts |
| `/api/invites/{id}/revoke` | POST | **OwnerOnly** | the same power read backwards |
| `/api/invites/preview` | POST | **Public** | the caller has no credential by construction; its guard is the code |
| `/api/invites/redeem` | POST | **Public** | same reason `/api/auth/login` is public |
| `/api/users/{slug}/suspend` | POST | **OwnerOnly** | ending someone's access to the machine |
| `/api/users/{slug}/unsuspend` | POST | **OwnerOnly** | the same |
| `/api/auth/password` | POST | HumanOnly | **unchanged**, gate and comment untouched |

### `AccessPolicy` — the exact edits

`AccessPolicy.Required` falls through to `AnyPrincipal` (Security.cs:221). An unlisted invitation route would be agent-mintable by omission and nothing would fail to say so. Two edits, in this order.

```csharp
// ...added to the Public block at Security.cs:65-74, beside /api/auth/login:

    // The two invitation routes a person with no credential must be able to
    // reach. Public for the reason login is: this is what you use before you
    // have any way in. Their guard is a 256-bit one-time code.
    //
    // These live in the Public block at the TOP of this function precisely so
    // the OwnerOnly prefix rule below cannot swallow them — the same shape
    // /api/projects/mine already uses against the /api/projects/ prefix.
    || path == "/api/invites/preview" || path == "/api/invites/redeem"

// ...and as its own rule, beside the /api/users one at :120:

// Everything else about invitations, on EVERY verb including the read. A prefix
// rule and not a list of three paths, because the fall-through at the end of
// this function is AnyPrincipal: an invitation route added in six months would
// otherwise be agent-mintable by omission.
//
// The READ is owner-only too. GET /api/invites answers "which people on this
// machine have no password yet", which is the question an attacker asks before
// deciding which link is worth intercepting.
if (path == "/api/invites" || path.StartsWith("/api/invites/", StringComparison.Ordinal))
{
    return AccessLevel.OwnerOnly;
}

// Ending and restoring someone's access to this machine. Owner-only for the
// same reason creating them is, and a state change rather than a removal — see
// §6 on why there is no DELETE anywhere near this.
if (path.StartsWith("/api/users/", StringComparison.Ordinal)
    && (path.EndsWith("/suspend", StringComparison.Ordinal)
        || path.EndsWith("/unsuspend", StringComparison.Ordinal)))
{
    return AccessLevel.OwnerOnly;
}
```

The normalisation at Security.cs:47 lowercases the path and trims a trailing slash before any `==` runs, so these comparisons are safe as written and must **not** be "fixed" into `OrdinalIgnoreCase`.

While in this file: delete Security.cs:162-165 (`if (isPost && path == "/api/users") return AccessLevel.HumanOnly;`). It is unreachable — :120-124 already returned `OwnerOnly` for the same path — and it tells an auditor the opposite of what the file does. Nothing asserts the old answer. This change adds two more rules to that function; shipping them while leaving a third one lying is how the next person reads the file wrong.

### `POST /api/users` — the response changes

```csharp
// The identity token is NOT in this response any more.
//
// It used to be, and emailing it was the owner's only way to onboard anybody: a
// permanent, non-revocable, non-expiring bearer credential, sitting in a mailbox
// forever. The token still exists and is still written 0600 on the box, because
// the agent and the CLI authenticate with it. What changed is that it no longer
// leaves the machine to get a person started. An invitation does, and it dies on
// first use.
//
// Minted in THIS call rather than in a second one, because creating a person and
// giving that person a way in is one act, and the two-call version's failure mode
// is a desk nobody can reach that looks exactly like a desk somebody can. The
// half-succeed case is answered honestly rather than by splitting the call: if
// the mint throws, the user is still created and `invite` comes back null with a
// sentence saying to re-issue from the Invitations panel. The caller is never
// told a link exists when it does not.
```

Response: `{ slug, invite: { id, code, expiresAt } }`, or `{ slug, invite: null, note }`. Audits `user.add` as today, plus a separate `invite.create`, so the action vocabulary is identical whether the invitation came with the person or three weeks later.

**Callers that move with it.** `src/backend/WorkspaceRuntime.Setup/Program.cs:172` does `document.RootElement.GetProperty("token").GetString()`; `GetProperty` throws `KeyNotFoundException`, which is not in the catch filter at that file's :87, so `add-user` would die with a stack trace *after* creating the person. `main.tsx:221` + `:1283` (`addTeammate`, the Desks rail) and `main.tsx:2259` + `:2447` (`addPerson`, `PeopleView`) each render `result.token`; both are rewritten. `distro/install.sh:251` and `release/cielo/install.sh:251` are bare `curl … ; echo` and do not parse — they will not throw, they will silently stop printing a token, which is worse than a break, so their usage comments and their login preamble (§3) change together.

### `POST /api/invites`

Body `{ slug }`. The target is looked up in **`store.Users` only** — `PrincipalResolver.BySlug` resolves agents too, and an invitation minted against `acme-marco-agent` would write a password hash keyed to an agent id.

Refusals, each a 403 or 404 with its own message, because every one of them is the owner talking to themselves in their own panel:

- **404** — no such person.
- **403 `IsMachineOwner`** — *the owner's account can never be invited.* This is the single most valuable property in the design. The owner's first password is set on the box, through the existing route, and no mailed link ever reaches the account that can create people. All three proposals' worst attack chains end at "and now they hold the owner's desk"; this closes every one of them.
- **403 `target.Id == caller.Subject`** — belt and braces on the same idea.
- **403 already has a password.** An invitation only ever sets the **first** one. This is the invariant that keeps the owner from minting themselves into a teammate's account: the moment an invitation can reset, every owner's sent-messages folder is a standing account-takeover primitive and the audit trail stops being able to distinguish "Maria wrote this" from "the owner wrote this as Maria". Recovery from a forgotten password is §11 open question 3, and it is a different endpoint with a louder audit line.
- **403 suspended.** A suspended account is not onboarded, it is stopped.

On success: `SupersedeLiveFor(userId)` runs first, so there is never more than one live secret for one person in two mailboxes, and a double-click is a supersede rather than two live links. Then mint `cielo_inv_<64 hex>` via `SecretHash.NewSecret(CredentialFormat.InvitePrefix)`, store `SecretHash.Of(code)` only, `ExpiresAt = now + 72h`. Return `{ id, slug, code, expiresAt }` — the code once, and then never again, because only its hash was kept.

**72 hours, fixed, no knob.** Long enough to cross two timezones and a weekend; short enough that a forgotten message is not a standing door. Rejected: configurable, because every configurable security default becomes the wrong one on some machine, and re-issuing is one click. Rejected: 14 days (the operator proposal's choice, to match the session lifetime) — a session is a credential you are actively using, an unredeemed invitation is a credential nobody is watching, and matching their durations matches the wrong thing.

### `GET /api/invites`

Every invitation ever minted, newest first: `{ id, slug, displayName, orgSlug, state, createdAt, expiresAt, redeemedAt, redeemedFrom, firstPreviewedAt, firstPreviewedFrom, invitedBy }`. **Never a code, and it structurally cannot return one** — only the hash was stored. `state` is the precise one (live / used / superseded / revoked / expired), not the collapsed one the invitee gets.

Deliberately **not** added to `GET /api/users`: a `hasPassword` flag there would put "which of my colleagues is weakest" on a HumanOnly route readable by every signed-in person in the organization (`OrganizationRules.Visible`, Program.cs:922).

### `POST /api/invites/preview`

Body `{ code }` — in the **body**, never a path or query parameter, so it does not reach an access log even if someone builds a link wrong.

Returns `{ state }`, plus for `live` only `{ slug, displayName, orgDisplayName, invitedBy, expiresAt }`. Every other state returns the state word and nothing else, so a spent code forwarded around a company does not stay a name-disclosure oracle forever.

States: `live | used | expired | superseded | revoked | suspended | unknown`. `unknown` is deliberately distinguishable from the rest, because a truncated paste is the single most likely real failure and "check you copied everything after the `#`" is the only message that fixes it. That costs nothing: you cannot reach any of these states without presenting 256 bits.

**One conditional write, once per invitation.** The first successful preview of a live code records `FirstPreviewedAt` and `FirstPreviewedFrom`. A second open by the same person writes nothing. This is the only pre-redemption signal that exists — "this link was opened from 203.0.113.9 at 03:14, an hour before Marco's flight landed" — and without it a stranger who previews a forwarded link is invisible until they spend it. It writes no audit row: an unauthenticated public endpoint that appends a row per request is a remote write amplifier, and `/api/auth/login` already declined to build one (Program.cs:748-752 audits only when the user is real).

### `POST /api/invites/redeem`

Body `{ code, password }`. The order of operations *is* the security design.

**0. Transport.** Refuse with 403 when the request is neither HTTPS nor from a genuinely loopback peer. `SessionCookieOptions` already sets `Secure = context.Request.IsHttps` (Program.cs:2350) and `distro/install.sh:63` binds headless installs to `http://0.0.0.0:$PORT` with no TLS anywhere in the tree — so without this check the code, the chosen password and the returned session cookie all cross the network in cleartext, and every leak argument below is written about the wrong channel. The loopback gate on first passwords was incidentally the guarantee that a never-yet-set password is never typed over a network at all; this is what replaces it. It is the one check in this document that will stop a working deployment on day one, and §11 open question 1 is about that.

**1. Throttle**, on a bucket of its own: `invite:{source}`, never `source:{source}`. Sharing the login bucket is what two proposals did and it is a denial-of-sign-in primitive: eight garbage bodies to a public endpoint would lock `POST /api/auth/login` for everyone for fifteen minutes, refillable forever, free, and — because garbage codes write no audit — invisible. It is worse than it sounds on this box, whose own error copy says loopback includes "your SSH tunnel", so the whole population shares one source address. Checked before anything else costs CPU, the same ordering `/api/auth/login` uses at :719-720 and for the same reason. Against 256 bits of entropy the throttle buys nothing cryptographically; it exists only to stop a stranger spending the machine's PBKDF2 budget, so it is loose.

**2. Password length**, checked against the request alone, before the invitation is looked up. Two reasons: a typo must not cost somebody their only way onto the machine, and validating the invitation first would make a four-character password a probe that separates live links from dead ones.

**3. Resolve** by SHA-256. **4. Refuse** everything that is not live, with the state in the body, matching the preview's vocabulary. A failure is audited only when a real invitation resolved; an unrecognised code writes nothing.

**5. Spend, before writing anything.** One compare-and-swap, not a read followed by a write:

```csharp
// The database decides the winner, not the handler.
//
// Two people can hold the same forwarded message. Read-then-write lets both see
// an unspent row and both write a password, the second silently replacing the
// first — and the honest invitee then finds a link that worked for somebody else.
// Exactly one caller gets 1 back here; the loser is refused before it hashes
// anything.
//
// The predicate carries the WHOLE liveness condition, not just "unspent". One
// proposal checked only RedeemedAt and relied on a Resolve() a moment earlier —
// which means a redemption in flight when the owner clicks [Call it off] still
// succeeds, and revocation, the only containment primitive for a leak, is not
// actually enforced anywhere.
//
// Ticks, not DateTimeOffset, because SQLite cannot compare one in a query. Every
// other ordered timestamp in this schema carries its own ticks and says so
// (RuntimeDbContext.cs:14-16); an invitation's expiry is the first one that is
// compared rather than ordered, and it needs them for the same reason.
var spent = context.Invites
    .Where(row => row.Id == inviteId
        && row.RedeemedAtTicks == 0
        && row.RevokedAtTicks == 0
        && row.SupersededAtTicks == 0
        && row.ExpiresAtTicks > nowTicks)
    .ExecuteUpdate(row => row
        .SetProperty(r => r.RedeemedAtTicks, nowTicks)
        .SetProperty(r => r.RedeemedAt, now)
        .SetProperty(r => r.RedeemedFrom, source));
```

**Spend before the password write, deliberately.** A crash between them burns an invitation and costs a re-issue. The other order leaves a live invitation sitting over a credential it has already changed, which a second holder can change again.

**6. Write the hash conditionally.** A new `bool IRuntimeStore.SetFirstPasswordHash(Guid userId, string hash)` — an `ExecuteUpdate` predicated on `PasswordHash == ""`, returning whether it landed. The plain `SetPasswordHash` is unconditional and would reopen the invariant §5 spent a paragraph defending: the handler's "does this account have a password" read happens before the spend, and between the two, Marco can walk to the box and set one through `/api/auth/password` at loopback. Check-then-act across two store calls is not the invariant, it is a description of it. If this returns false, the invitation is already burned and the caller gets the ordinary refusal — slightly the wrong sentence, fail-closed, and audited so the owner can see what happened.

**7. Session and audit.** `sessions.Create(user.Id, TimeSpan.FromDays(14))` and the existing `SessionCookieOptions`, so the response is byte-for-byte what a password login returns and the portal's signed-in path is one code path. No sessions are revoked: an account with no password had none, because a password is the only way to get one.

**One audit row, not two.** `AuditHomeSlugs` (Program.cs:2308-2326) yields `Principal` **and** `OnBehalfOf` as home slugs, and `/api/audit-events` filters `Ownership.CanAccessHome` over that set (:1162). So a single row written with `Principal = <owner slug>` and `OnBehalfOf = <invitee slug>` reaches both Joche's Activity feed and Marco's, and neither needs a second copy. This is the first `AppendAudit` call in `Program.cs` to set either field — every existing one passes seven positional arguments (e.g. :750, :831, :1037) — so it uses named arguments, and `AuditEvent` (Models.cs:154-165) has `Principal` and `OnBehalfOf` as optional parameters 9 and 10. The detail carries the source address, because that is the one fact that turns "that wasn't me" into something actionable. The client-side `ActivityView` filter (main.tsx:2469) reads only `principal` and `onBehalfOf` and ignores `userId` entirely, which is the other reason the row must carry them.

### `POST /api/users/{slug}/suspend`

Not optional, and not a sequel. Without it a redeemed-by-the-wrong-person invitation is **permanent**: the redeemer's first move is `POST /api/keys` with no expiry, which yields `ApiKey.ExpiresAt = null` and `IsLive()` true forever (Credentials.cs:34), and no route on this machine lets the owner see or revoke another person's keys — `GET /api/keys` filters `keys.For(caller.Subject)` (:852) and `DELETE /api/keys/{id}` scopes to `caller.Subject` (:897). Shipping a new off-box path to a first password while the owner cannot end a compromised account gets the order backwards.

`SuspendedAt` on `UserRow`, checked in the middleware immediately after principal resolution, so a suspended person is refused whatever they present — cookie, API key, or identity token. Suspending calls `sessions.RevokeAllFor(user.Id)` and a **new** `IApiKeyStore.RevokeAllFor(Guid userId)`, mirroring `ISessionStore.RevokeAllFor` (Credentials.cs:67). That method is worth having regardless of invitations: today `/api/auth/password` ends every session and leaves every minted key alive, so "sign out everywhere" has never meant it.

Refuses when the target `IsMachineOwner`, for the obvious reason.

`/unsuspend` clears the column. It does not restore sessions or keys; those were revoked and stay revoked, and the person signs in again with their password. Nothing is deleted anywhere in this.

---

## 6. STORAGE

One new table, `runtime_invites`, and one new column on `runtime_users`.

```csharp
// An invitation: the ONE thing a person with no password may do, and the only
// credential this product lets out of the building. Hashed like a session secret
// and an API key, so a copy of the database is a list of who was invited, not a
// set of usable links.
//
// Nothing here is ever deleted. An invitation that was used, replaced, called off
// or aged out is a column and a clock — which is also what makes the owner's list
// worth reading: three rows for Dmitri is the honest record that he lost it twice.
public sealed class InviteRow
{
    public Guid Id { get; set; }

    // The ONE account this can set a password for, bound at mint time.
    //
    // This is where organization isolation comes from, and it costs nothing: an
    // invitation names no organization and creates nobody. The row it points at was
    // stamped with an OrgSlug by SetupService.AddUser, and redeeming writes exactly
    // one column, PasswordHash. Moving a person stays what it already is — one
    // owner-only route, POST /api/users/{slug}/organization. Constraint 4 holds by
    // construction rather than by a check somebody has to remember.
    public required Guid UserId { get; set; }
    public required Guid InvitedByUserId { get; set; }

    public string CodeHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    // Ticks beside every one of these, because the redemption predicate COMPARES
    // them inside a query and SQLite cannot compare a DateTimeOffset there. The
    // nullable timestamps are what a person reads; the ticks are what the
    // compare-and-swap reads, and 0 means "not yet".
    public long ExpiresAtTicks { get; set; }

    public DateTimeOffset? RedeemedAt { get; set; }
    public long RedeemedAtTicks { get; set; }
    public string RedeemedFrom { get; set; } = "";

    // Two columns rather than one VoidedAt plus a reason string, because the
    // owner's list has to say "replaced by a newer link" and "called off" in
    // different words, and because RevokedAt is already the spelling on sessions
    // and API keys.
    public DateTimeOffset? SupersededAt { get; set; }
    public long SupersededAtTicks { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public long RevokedAtTicks { get; set; }

    // The only signal that exists before a link is spent. Written once, on the
    // first successful preview of a live code, so a benign second open by the same
    // person is silent and a stranger's look is not.
    public DateTimeOffset? FirstPreviewedAt { get; set; }
    public string FirstPreviewedFrom { get; set; } = "";
}
```

Indexes, beside the session and key ones at RuntimeDbContext.cs:294-297:

```csharp
// Looked up by its hash exactly once in its life. Unique for the same reason the
// session and key tables are: a duplicate would mean two invitations sharing one
// secret.
modelBuilder.Entity<InviteRow>().ToTable("runtime_invites")
    .HasIndex(row => row.CodeHash).IsUnique();
// "Has this person got one outstanding" — asked on every mint, and by the
// supersede that precedes it.
modelBuilder.Entity<InviteRow>().HasIndex(row => row.UserId);
```

And on `UserRow`:

```csharp
// Set means this person may not authenticate at all, whatever they present. Not
// a deletion and not a slug rename: their home volume, token file, audit history
// and spreadsheet all stay exactly where they are, and unsuspending is one write.
// This is what an owner does at 03:00 when an invitation was spent by the wrong
// person, and it is the reason that is a recoverable event rather than a
// permanent one.
public DateTimeOffset? SuspendedAt { get; set; }
```

### How a spent or expired invitation is represented without deleting anything

It is never removed. Liveness is a function, the same shape `ApiKey.IsLive` (Credentials.cs:34) and `PanelSession.IsLive` (:22) already have:

```csharp
public bool IsLive(DateTimeOffset now) =>
    RedeemedAt is null && RevokedAt is null && SupersededAt is null && ExpiresAt > now;

// What the owner's list shows. Check order is display order: report the FIRST
// true thing, because "it was replaced" is the sentence that tells the person
// what to do next. A row can be both superseded and expired.
public string State(DateTimeOffset now) =>
    RedeemedAt is not null    ? "used"
    : RevokedAt is not null   ? "revoked"
    : SupersededAt is not null ? "superseded"
    : ExpiresAt <= now        ? "expired"
    : "live";
```

Expiry needs no write at all — time passes. Spending writes `RedeemedAt`. Re-issuing writes `SupersededAt` on the old one. Calling one off writes `RevokedAt`. There is no sweep job, no DELETE endpoint, and no `Purpose` column: there is exactly one purpose, and a column with one legal value is an invitation to add a second one without thinking about it.

**Every state is a written timestamp.** This is deliberate, and it is where one proposal went wrong: it derived a `stale` state at read time from `user.OrgSlug != OrgSlugAtIssue`, which means moving Marco acme→globex→acme *resurrects a dead link* and nothing in the row records that it was ever dead. Two routes therefore supersede a person's live invitations as part of their own work:

- `POST /api/users/{slug}/organization` (Program.cs:1005) — a move is exactly when the owner should be told the old link is dead.

  > **Correction, verified 2026-09-16.** An earlier draft of this section claimed that handler appends no audit row, and that `CommandBusConformanceTests.cs:56` asserted an audit that did not exist. That is wrong, and it is wrong in the way design documents usually are: it read the handler and stopped. The row is written one layer down, inside `SetUserOrganization` (`EfRuntimeStore.cs:726-731`, and `InMemoryRuntimeStore.cs:247` for the test double), carrying the same reasoning the conformance test cites — the slug every other trail is keyed on does not change, so nothing else would record that anything happened. Nothing needs fixing here. The rest of this section stands: a move must still supersede a live invitation.
- `POST /api/auth/password` — when it sets a **first** password (the `existing is null` branch at :833), any live invitation for that user is now meaningless and must not outlive it.

### Application side

`Invite`, `IInviteStore` and `InvitePrefix` go in `src/backend/WorkspaceRuntime.Application/Credentials.cs` beside `ISessionStore` and `IApiKeyStore`, because that file's stated rule is that everything touching a secret stays reviewable as one unit.

```csharp
public interface IInviteStore
{
    (Invite Invite, string Code) Create(Guid userId, Guid invitedBy, TimeSpan lifetime);
    Invite? Resolve(string code, DateTimeOffset now);        // any state, so the caller can name it
    bool Spend(Guid inviteId, DateTimeOffset now, string from);  // the compare-and-swap; single winner
    bool NotePreview(Guid inviteId, DateTimeOffset now, string from);
    int SupersedeLiveFor(Guid userId);
    bool Revoke(Guid inviteId);
    IReadOnlyList<Invite> All();
}

// Invitations carry a visible prefix for the reason API keys do, plus one: the
// auth gate switches on ApiKeyPrefix, and a credential-shaped string with no
// prefix is a string somebody will eventually present as a bearer token. Nothing
// resolves this one except the redeem and preview handlers — an invitation is not
// a way to authenticate, it is a way to obtain a password.
public const string InvitePrefix = "cielo_inv_";
```

`Resolve` returns the row whatever its state, deliberately: the handler needs to name the state back to the person, and hiding dead rows behind `null` would collapse "expired" and "never existed" into one answer at the wrong layer. Refusing to *tell them apart in the reply* is a different decision, and it is made in the handler.

`EfInviteStore` and `InMemoryInviteStore` both go in `EfCredentialStores.cs`, which already ships them in pairs — the in-memory store is a shipping configuration selected by `databaseProvider == "memory"`, not a fixture, so a missing twin is a real hole in a real mode. The in-memory `Spend` takes the existing `lock (gate)` and checks the same full predicate; that is the one place where "it is atomic" is a claim about a lock rather than about the database, and it needs a test that races it.

`ExecuteUpdate` appears nowhere in `src/` today — every store is read-modify-`SaveChanges` over `IDbContextFactory`. It must behave identically on Npgsql, which Program.cs:52-57 wires against the same migrations. Worth proving before relying on it for the single-winner property.

---

## 7. WHEN THE LINK LEAKS

A live, unspent link lets whoever holds it set that one desk's first password. That is real, it is the same exposure every invitation link in every product has, and everything else in this design is about bounding it and detecting it.

**Bounded.** One desk, in one organization, never the machine. The owner's account cannot be invited at all, so no mailed link ever reaches the account that creates people. It crosses no organization boundary, because it sets the password of a user whose `OrgSlug` is already fixed and redeeming writes no other column. It confers no bearer token, no API key and no permanent capability — only a 14-day session. It expires in 72 hours. It works once.

**Opened twice, benignly.** Ana clicks her link, gets distracted, clicks it again. Preview is idempotent after the first write; the form renders again; the fragment is still there because it is only cleared after a successful redeem. Nothing is spent, nothing is audited, the owner's Activity feed stays empty. This is the common case and it must be silent, which is why preview writes no audit row.

**Replayed.** A second redemption of a spent code loses the compare-and-swap and is refused with `used`. Two tabs racing produce exactly one password and one refusal, decided by the database.

**Opened far too late.** Past 72 hours the predicate fails on `ExpiresAtTicks` and preview says `expired` with no name attached. The owner mints a new one; the old row keeps its `SupersededAt` and stays in the list.

**Intercepted and spent by a stranger.** This is the case worth walking.

- *It announces itself.* Marco's link now says `used`, and the portal's copy for that state is deliberately **"This invitation has already been used. If that was you, sign in below. If it wasn't, tell whoever set up this machine"** — not a silent login box. The whole detection story lives in that sentence.
- *The owner can answer "was it Marco?"* `GET /api/invites` shows the row as used, when, and from where; `FirstPreviewedFrom` may show an earlier look from somewhere else; the audit row carries `Principal = joche`, `OnBehalfOf = acme-marco` and the source address, and both of them can read it.
- *Eviction is one click and it is complete.* [Suspend] on Marco's row writes `SuspendedAt`, revokes every session **and every API key** he holds, and the middleware refuses his principal thereafter regardless of what he presents. Without `IApiKeyStore.RevokeAllFor` this step would leave behind a permanent key the owner cannot even see, and the takeover would be irreversible; that is the single biggest thing six judges found and every proposal missed.
- *Then the owner re-onboards Marco properly.* His account has a password that is not his, so it needs the reset that §11 open question 3 is about. Until that exists, the honest answer is: suspend the account and create a new desk for Marco. That is a real cost and it is written down rather than discovered.

**What a leak does not do, checked against constraint 7.** It does not reopen the loopback gate. `/api/auth/password` is untouched. A leaked identity token cannot set a first password off-box, and after §3 it cannot mint an invitation either — `OwnerOnly` now requires a session, and an identity token never produces one. The two credentials are independent, and the invitation is narrower than the token in every dimension: one account, one action, one use, 72 hours, revocable output.

**Not mitigated, plainly.** The link is a bearer secret in a message. Fragments stay out of access logs, proxy logs and `Referer` headers, and the panel clears the fragment after a successful redeem — but they reach browser history and clipboard managers, and whoever reads the owner's messaging app first wins. Single use and 72 hours are the mitigation, not the elimination. If the threat model includes a compromised mailbox, this design does not meet it.

---

## 8. THE EF MIGRATION

One migration, `Invites`. `DatabaseUpgradeTests.The_model_and_the_migrations_have_not_drifted_apart` calls `HasPendingModelChanges()` (:76-85) precisely so that adding a row class without a migration fails in seconds rather than on a customer's box, so the repo will say so immediately.

```sql
CREATE TABLE runtime_invites (
  Id                 TEXT    NOT NULL PRIMARY KEY,
  UserId             TEXT    NOT NULL,
  InvitedByUserId    TEXT    NOT NULL,
  CodeHash           TEXT    NOT NULL,
  CreatedAt          TEXT    NOT NULL,
  CreatedAtTicks     INTEGER NOT NULL,
  ExpiresAt          TEXT    NOT NULL,
  ExpiresAtTicks     INTEGER NOT NULL,
  RedeemedAt         TEXT    NULL,
  RedeemedAtTicks    INTEGER NOT NULL DEFAULT 0,
  RedeemedFrom       TEXT    NOT NULL DEFAULT '',
  SupersededAt       TEXT    NULL,
  SupersededAtTicks  INTEGER NOT NULL DEFAULT 0,
  RevokedAt          TEXT    NULL,
  RevokedAtTicks     INTEGER NOT NULL DEFAULT 0,
  FirstPreviewedAt   TEXT    NULL,
  FirstPreviewedFrom TEXT    NOT NULL DEFAULT ''
);
CREATE UNIQUE INDEX IX_runtime_invites_CodeHash ON runtime_invites (CodeHash);
CREATE INDEX        IX_runtime_invites_UserId   ON runtime_invites (UserId);

ALTER TABLE runtime_users ADD COLUMN SuspendedAt TEXT NULL;
```

Purely additive. No backfill, no existing row touched, nothing to alter — an upgraded install is unaffected until the owner mints the first invitation. `Down()` drops the table and the column, and nothing else references either.

**Rollout on the live box.** Migrations run inside the `EfRuntimeStore` constructor at startup (Program.cs:68), so a migration that throws is a box that does not come back, and this machine has a history of a stale EF migration lock keeping it down silently. Therefore: stop the unit, copy the SQLite file aside, deploy this migration **alone**, start, and confirm the box returns and both existing people still sign in *before* anything else ships in the same window. Never two migrations in one restart — that gives you two suspects.

---

## 9. THE FRONT END

### The invitee's screen is in the **portal**, not the panel

`vite.config.ts` builds two entries: `index.html` → `main.tsx` (the admin panel, 2537 lines of plain CSS, no i18n) and `portal.html` → `src/portal/` (the four-slot nav the teammate was actually hired to use, with `src/frontend/src/i18n/{en,es,ru}.json` behind it). One proposal put redemption in the panel; that drops Marco into "Manage this machine", which is the wrong Home and an untranslated one. The link is `…/portal.html#invite=<code>`, which also needs no `MapFallback` — and there is none in `src/` today, the panel is `UseDefaultFiles` + `UseStaticFiles` only (Program.cs:343-349), so a `/invite` path route would 404.

The hash is read **above every other gate**, before the stored-token check and before `/api/setup/status`. A browser holding a stale `runtime.token` in `localStorage` — a reused kiosk, a shared laptop, the owner checking the link — must still reach the invitation screen, and `history.replaceState` must not have destroyed the fragment by then. Redemption also clears any stored identity token, so a shared machine does not leave the next person holding the last person's permanent credential.

`src/portal/SignIn.tsx:40-59` still offers a full "sign in with an identity token" mode that calls `writeToken()`. It stays, because an install upgraded from before passwords has nothing else — but its copy stops telling people to find a token file and starts telling them to ask for an invitation link. Killing human token authentication outright is §11 open question 5, and it is the change that makes constraint 5 permanent rather than true only for people onboarded after this ships.

New keys go in **all three** i18n files; `shared/i18n-parity.test.ts` fails on a key added to one and not the others.

### The owner's screens are in the panel

`PeopleView` (main.tsx:2251) gains a third card, **Invitations**: rows reading `Marco · live · expires Thu 18 Sep · invited by joche`, with [Send a new link], [Call it off] and [Suspend]. Because superseded rows persist, this panel is also the answer to "did I already send him one?" — which is the question that makes a person give up and mail a token instead. Both `result.token` renders (`main.tsx:1283`, `:2447`) become the link-with-expiry block from §4.

The Security card in `ModelsView` says *"Setting your first password must be done on the machine itself"*, which is about to be true for the owner and confusing for six people who just set theirs through a link. It becomes *"Your first password is set on the machine itself, or through an invitation link. Changing it ends every other session."*

---

## 10. TESTS

Three files fail the moment this lands, and should.

**`AccessPolicyTests`** — an `[InlineData]` row per route, including the Public ones and including deliberate rows for the reads, because in this codebase the rows *are* the record of the decision:

```
("/api/invites", "POST", OwnerOnly)   ("/api/invites", "GET", OwnerOnly)
("/api/invites/<guid>/revoke", "POST", OwnerOnly)
("/api/invites/preview", "POST", Public)   ("/api/invites/redeem", "POST", Public)
("/api/invites/anything", "GET", OwnerOnly)      // pins the fail-closed prefix
("/api/users/acme-marco/suspend", "POST", OwnerOnly)
```

**`CommandBusConformanceTests`** — `AllowedPostPaths` is an exact-match allowlist (`:20-86`) whose matcher normalises `${…}` to `*` (:107). It needs `/api/invites`, `/api/invites/*/revoke`, `/api/invites/preview`, `/api/invites/redeem`, `/api/users/*/suspend`, `/api/users/*/unsuspend`, each with the paragraph its neighbours carry. The short version: *control-plane identity. Owner-only, or public by necessity because the caller has no credential yet by construction. Nothing in any workspace changes — an invitation holds no file, no volume and no path — and none of it is an agent's to do in any form.*

**`StoreReadScopeTests`** — `IsStoreWideRead` (:92-99) matches a hard-coded collection alternation with no invites entry, so the guard will run on `GET /api/invites`, pass, and look at nothing. That is exactly the failure `Program.cs:939-944` already narrates about Organizations. Add an invites shape to the alternation; the `IsMachineOwner` escape hatch (:66) then keeps the owner-wide read legal and *recorded as legal*.

| # | Test | What it catches |
|---|---|---|
| T-1 | `OwnerOnly_needs_a_session` — the owner's identity token POSTs `/api/users`, `/api/organizations`, `/api/invites`; all 403 | **fails today** for the first two. The hole every proposal built on |
| T-2 | `An_invitation_never_sets_a_second_password` — mint, set a password by another route, redeem; refused and the first password still verifies | the check-then-act TOCTOU; `SetFirstPasswordHash` returning false |
| T-3 | `Only_one_redemption_wins` — 20 parallel redeems of one code; exactly one 200, one password, one session. Run against **both** stores | read-then-write; the in-memory twin's lock being a claim rather than a guarantee |
| T-4 | `Revoking_beats_a_redemption_in_flight` — revoke, then redeem; refused | a predicate that checks only `RedeemedAt`, which makes revocation decorative |
| T-5 | `The_owner_can_never_be_invited` — mint for the `IsMachineOwner` slug, and for yourself; both 403 | every proposal's worst chain, which ends at the owner's desk |
| T-6 | `Suspending_ends_every_credential` — redeem, mint an API key with no expiry, suspend; the key, the session and the identity token are all refused | **fails today** for the key. The permanent capability an invitation would otherwise hand out |
| T-7 | `Moving_or_self_setting_kills_a_live_invitation` — move the person's org, and separately set their first password at loopback; the invitation is superseded both times and cannot revive | the resurrecting derived state |
| T-8 | `Refusals_do_not_leak` — unknown / expired / used / superseded / revoked return the state word and, except for `live`, no name, no org, no invitedBy | a spent code staying a name oracle |
| T-9 | `Redemption_refuses_cleartext` — non-loopback + `IsHttps == false` → 403 before the code is resolved | the whole leak analysis being written about the wrong channel |
| T-10 | `Invitation_failures_do_not_lock_out_sign_in` — 20 garbage redeems from one source, then a correct `POST /api/auth/login` from the same source; it succeeds | the shared-throttle denial-of-sign-in primitive |
| T-11 | `The_audit_row_reaches_both_homes` — the owner and the invitee each see the redemption in `/api/audit-events` | `AuditHomeSlugs` / `CanAccessHome`; the reason there is one row and not two |
| T-12 | `Ticks_round_trip` — mint, restart the context, redeem at the boundary; and the same against Npgsql | the `DateTimeOffset`-in-a-query rule, and `ExecuteUpdate` on a second provider |
| T-13 | `Portal_renders_the_invitation_above_every_gate` — a stale `runtime.token` in `localStorage`, `#invite=…` present; the invitation screen renders | the branch placed inside `if (!token && !session)` |
| T-14 | `DatabaseUpgradeTests` — watch `HasPendingModelChanges` go red after the row class and green after the migration | the guard being wired to these tables at all |

---

## 11. OPEN QUESTIONS THAT NEED A HUMAN DECISION

1. **The wire, and it is the blocking one.** `distro/install.sh:63` binds headless installs to `http://0.0.0.0:$PORT`; there is no TLS, no certificate, and no `ForwardedHeaders` middleware anywhere in the solution. §5 step 0 refuses non-loopback redemption without HTTPS, which means **the feature does not work on the default headless install until somebody terminates TLS**. Three options and they are not equivalent: (a) ship the refusal and document TLS as a prerequisite, which is what is written above; (b) add `UseForwardedHeaders` with an explicit `KnownProxies` list, which makes `IsHttps` and `RemoteIpAddress` honest again behind a proxy and is probably right regardless — note it must be an allowlist, because a wide-open `ForwardedHeaders` lets any caller claim any address and would hand `IsLoopback` to the internet; (c) allow cleartext with a loud warning, which is a first password on a plaintext wire and is the option this design refuses. Decide before building, not after.
2. **Link composition.** `Request.Scheme` + `Host` is password-reset poisoning — Kestrel accepts any `Host` unless `AllowedHosts` says otherwise, and a credential-bearing URL aimed at an attacker's origin means their JavaScript reads the fragment. The on-box path is worse than wrong: `cielo-add-user` talks to `http://127.0.0.1:${PORT}`, so a composed link would literally read `http://127.0.0.1:5148/portal.html#invite=…` and be unusable by the remote person it is for. Options: a `Panel:PublicUrl` config key, or return only the fragment and let each caller compose its own origin. This needs an answer *before* `POST /api/users` starts returning a link, because that script is the on-box onboarding story after the token stops travelling.
3. **Forgotten passwords.** A teammate who forgets theirs is stuck: `/api/auth/password` needs the current one, an invitation refuses an account that has one, and nothing is deleted. The only recovery is somebody with a shell. This is a chosen line, not an oversight — an owner who can mint a reset can silently become any person on the machine — but it also means the §7 "spent by the wrong person" story ends at *suspend and create a new desk*. If a reset is wanted, the honest shape is a separate route with its own audit action, its own confirmation, and loopback (or a re-entered owner password) as its gate. Answering this also decides whether the first-password-only invariant is a permanent property of the product or a temporary narrowing.
4. **The owner's own recovery.** They cannot be invited and cannot reset: `/api/auth/password` needs the current password, and `/api/invites` refuses `IsMachineOwner`. That is a hole today rather than one this creates, but shipping invitations makes it conspicuous — everybody else now has a recovery story and the owner does not.
5. **Should a human identity token stop authenticating once that person has a password?** After §3 it can no longer do owner things, but it still authenticates as a full human principal everywhere else, forever, and every install that already emailed some is not healed by this. Killing human token auth (keeping it for agents and the CLI) is the change that makes constraint 5 permanent. It is the natural next commit and it is disruptive enough to be costed as one.
6. **Should a team lead see and re-issue invitations for her own organization?** Constraint 3 keeps *creation* with the owner, but "Dmitri lost his link" is a message that reaches Yulia first and has to be relayed. The natural shape is a read-and-reissue scope over her own organization, which is a different grant from `OwnerOnly` and should be designed rather than bolted on.
7. **The demo seed.** `RuntimeSeed.People()` creates joche and yulia with no password hash at all, so a demo image will show two people the Invitations panel thinks need onboarding — one of whom is the owner, who cannot be invited. Should the seed set passwords, or should the panel say nothing about accounts that predate invitations?

---

## 12. WHAT WAS CONSIDERED AND REJECTED

**Building the mint on today's `OwnerOnly`.** All three proposals did, and all three asserted in prose that a leaked identity token could not reach it. The refusal at Program.cs:422-424 is a slug comparison with no loopback component, and the token branch at :408-411 produces a `Human` principal, so it could — from anywhere, on a fresh install where every account is passwordless. Rejected in favour of §3, which fixes it at the level rather than in one handler. The three routes that already had this property get it fixed for free, which is the argument for a level over a check.

**Requiring `session || IsLoopback` to mint** (the strongest of the three proposals). The session half is exactly right and is kept. The loopback half is rejected: `IsLoopback` reads the raw TCP peer (:2418) and there is no `ForwardedHeaders` middleware, so behind any reverse proxy every caller on earth is `127.0.0.1` and the gate inverts completely and silently. A security check whose failure mode is "passes for everyone, says nothing" is worse than no check, because it is written down as protection.

**A typed second factor — the invitee must type their desk slug.** Rejected. `FirstRunSetup.cs` mints slugs as `$"{orgSlug}-{Slug.Of(displayName)}"`, deterministically; `GET /api/users` hands every slug in the organization to any signed-in teammate; and whoever holds the forwarded message holds the display name and usually the organization. The withheld half is derivable from the same leak, so it buys near zero — while costing a cold, phishing-shaped page, a refusal that cannot be explained to a confused user, and (in the proposal as written) a five-strike auto-revoke wired to the one input the design had decided it would never help anyone get right. That is a remote denial-of-onboarding and a trap a legitimate user springs on themselves.

**Refusing a preview endpoint, to avoid an oracle.** Rejected, and the reasoning is worth keeping even though the conclusion flips. The property being bought — "a code-holder cannot confirm the invitation is real and whose it is" — did not survive contact with the implementation: the proposal's own handler did one indexed lookup for an unknown code and a lookup *plus a synchronous SQLite write* for a live one, which is a live-vs-dead classifier over a LAN regardless of a byte-identical reply. Having paid the UX price and not received the goods, ship the preview. What it discloses to somebody already holding a one-time secret for one account is a name they could learn by spending it.

**Two calls: create the person, then mint separately.** Genuinely contested. The argument for it — the gate would otherwise live in two places, and a half-succeeded handler is worse than two calls the panel makes back to back — loses its first half once `OwnerOnly` itself carries the gate (§3), leaving only the half-succeed, which is answered by returning `invite: null` with a sentence rather than by splitting the call. The argument against two calls is the stronger one: after `token` is removed, a `POST /api/users` that returns only a slug is a route that creates people nobody can reach, and "created a teammate and forgot to invite them" becomes a standing failure mode. One call, plus `POST /api/invites` for every re-issue.

**Letting an invitation reset an existing password.** Rejected, and this is the design's sharpest line. It is friendlier — it would give "I forgot my password" a remote answer, which §11 open question 3 says the product now lacks — and it is strictly weaker: the moment an invitation can reset, one stolen owner session mints for any teammate and takes their desk from anywhere, silently, with no notification path (there is no mailer, and the victim's only signal is an audit feed they have no reason to open). Before this change no route could set another person's password off-box at all. A reset is a different endpoint with a different gate and a louder audit action, and it should be costed on its own.

**Two audit rows per redemption**, one homed on the invitee and one on the owner. Rejected as duplication. `AuditHomeSlugs` (:2308-2326) yields both `Principal` and `OnBehalfOf`, so a single row reaches both feeds. The proposal that argued for two rows diagnosed the underlying scoping problem correctly — the owner really cannot read a row homed only on `acme-marco` — and then missed that the fix was already in the function it was reading.

**Sharing the `LoginThrottle` bucket with sign-in**, argued by two proposals as a security property ("guessing an invitation and guessing a password are one attack and should share one budget"). Rejected: it is a new denial-of-sign-in primitive on the machine's only front door. Eight garbage bodies to a free public endpoint lock out `POST /api/auth/login` for every caller sharing that source address — which, on a box reached through an SSH tunnel, is everybody — refillable every fifteen minutes, costing the attacker nothing, and invisible because garbage codes are deliberately not audited. Separate bucket.

**Mailing the invitation from the box.** Rejected. It would allow a one-hour lifetime and binding to an address, and it needs an SMTP credential on the machine, a new egress path past `EgressAllowlist`, a delivery-failure mode and a bounce nobody reads. Out-of-band delivery is worse at expiry and better at everything else, and it means onboarding security is the owner's messaging provider's security — which is stated here rather than quietly assumed.

**"The owner sets a temporary password and tells them"** — one endpoint, no table, no migration, three lines of handler. Genuinely the right answer for a two-person box, and it is the answer several of the fatal findings push toward. Rejected for three reasons that compound as the machine grows: the owner then knows a live credential for somebody else's desk, nothing forces a change, and the secret still travels over the same channel but with no expiry, no single use, and no way to tell whether the right person used it. It also makes the audit trail permanently unable to distinguish the teammate from the owner acting as them.

**Magic-link sign-in generally** — the link signs you in, no password. Better UX, strictly worse here: the link *is* the credential, so a leak is a full compromise with no second half to withhold, it leaves the person with no password so the next sign-in has the same problem, and it makes every sign-in depend on the mail channel rather than only the first.

**A `DELETE` on anything.** Never considered seriously and named here because it is the constraint most likely to be violated by accident: spent, superseded, revoked and expired are timestamps, `SuspendedAt` is a timestamp, and `DELETE /api/keys/{id}` — the only delete-shaped verb near this code — sets `RevokedAt` and removes nothing (EfCredentialStores.cs:188-202).

---

## 13. WHAT THIS DELIBERATELY DOES NOT DO

- **No password reset, for anyone, including the owner.** §11 questions 3 and 4.
- **No mailer, ever.** The owner copies a link. The product's onboarding security is the owner's messaging app's security and says so out loud.
- **It does not retire the identity token.** It stops mailing it and stops it doing owner work. Every existing token still authenticates its holder as a human on every other route, forever, and any install that already emailed some is not healed by this. §11 question 5 is the change that would be.
- **Invitation rows accumulate and are never compacted.** By design — they are the record of how each person arrived — and an owner who re-issues repeatedly grows a table nothing prunes. Small, and the shape of every "nothing is deleted" decision, so worth naming rather than discovering.
- **Unsuspending does not restore sessions or keys.** They were revoked; the person signs in again. Restoring a revoked credential would make revocation reversible, which is the one property it must not have.
- **No invitation may be presented in an `Authorization` header.** The middleware (Program.cs:353-455) must stay unable to resolve one to a principal. An invitation is not a way to authenticate; it is a way to obtain a password, and the two must not blur.
- **No bulk invite, no CSV, no directory sync.** Six people is six clicks and one [Copy all six].
