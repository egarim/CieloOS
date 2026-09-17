# Brief 01a — `Confidential`, and only `Confidential`

## Why this is now half of what it was

The first version of this brief asked for both predicates in one task. The
delegate implemented it and then said, in `concerns`, that it had just built
something that would leave a headless install with no way to be claimed. It was
right, and the design document says so in bold:

> **So the second conjunct does not ship on its own.** … A headless install with
> the narrowing and no socket has no onboarding path whatsoever, and that is not a
> follow-up commit, it is the same commit. **Sequencing note for whoever builds
> this: the socket is a co-requisite, not hardening.**
> — `docs/tls-and-proxies.md` §5

`cielo-claim` runs `curl http://127.0.0.1:$PORT/...`. On `--mode headless` Kestrel
is bound to `0.0.0.0`, so the *local* endpoint of that connection is a specific
interface address, not loopback. Adding the `IsLoopback(LocalIpAddress)` conjunct
therefore refuses the operator's own on-box claim, with the message *"Setup can
only be claimed from the machine itself"* — told to somebody who is on the
machine. That is the exact failure this project spent a day removing everywhere
else.

So `OnThisMachine` moves to **brief 01b**, together with its co-requisite unix
socket. This brief is now the half that ships safely alone.

## Scope

Implement `Confidential` from `docs/tls-and-proxies.md` §2, §4 and §7. Read that
document as the specification.

**Do not touch the four gates.** They keep calling `IsLoopback` exactly as they do
today: lines 542, 547, 706 and 857 of `src/backend/WorkspaceRuntime.Api/Program.cs`
are out of scope for this task and belong to 01b.

What changes:

| where | today | becomes |
|---|---|---|
| `SessionCookieOptions`, `Secure = context.Request.IsHttps` | true only when *this hop* is HTTPS | `Confidential(context)` |
| both `Cookies.Delete(CredentialFormat.SessionCookie)` call sites | no options passed | matching `CookieOptions`, see below |

`Confidential(connection)` is true when **any** of:

1. Kestrel performed the TLS handshake itself (`ITlsHandshakeFeature`).
2. The connection was accepted on a loopback *local* endpoint.
3. The peer address is one the operator named in `Network__TlsTerminatedBy`.

**Do not add `UseForwardedHeaders`.** Not with an empty allowlist either: an empty
`ForwardedHeadersOptions` still trusts `::1` and `::1/128` without anyone naming
them, and unnamed trust is what this design refuses.

### The cookie deletion, which the last run found

`Cookies.Delete` matches on name, path, domain **and** flags. Both call sites pass
no options today, which was harmless while `Secure` was almost always false and
becomes a sign-out that does not sign you out once it is usually true. Pass
options that match what `SessionCookieOptions` produced. This is in scope
*because* this change makes it bite.

## Configuration

`Network__TlsTerminatedBy`: comma-separated **literal IP addresses**, empty by
default. No CIDR, no hostnames — a range is a set of machines nobody named, and a
hostname is a DNS answer somebody else controls. A malformed entry must fail
startup loudly, naming the entry and the accepted form; a proxy list that
half-parsed is worse than one that failed.

Read it through `IConfiguration` only. Do **not** change `install.sh` or the unit
file; `/etc/cielo/network.env` and its `EnvironmentFile=-` line are a separate
task, and `install.sh` regenerates `/etc/cielo/cielo.env` wholesale on every
install so nothing hand-written there survives. Put that in `followups`.

## The one header read that is allowed

A request from an *un-named* address carrying `X-Forwarded-Proto` logs one warning
naming that address, once per process, with the exact string to paste into the
config. It affects no decision.

The rule, which belongs in a comment: **a header may be read only to reduce
authority or to say something out loud, never to grant it.**

## Tests

In `tests/backend/WorkspaceRuntime.Tests/`.

1. `Confidential` is true for a Kestrel-terminated TLS connection, for a loopback
   local endpoint, and for a named terminator peer.
2. `Confidential` is false for an unnamed non-loopback peer **even when it sends
   `X-Forwarded-Proto: https`**. This is the one that matters.
3. A malformed entry (hostname, CIDR, wildcard, nonsense) is rejected at startup.
4. Populating `Network__TlsTerminatedBy` changes no gate. Assert that the four
   `IsLoopback` call sites behave identically with the list empty and populated —
   the invariant that no configuration value can reach the claim gate. It is
   trivially true in this brief because the gates are untouched, and it is the
   regression test that keeps 01b honest.

The last run correctly objected to the phrase "empty must be byte-for-byte today's
behaviour": with an empty list, `Confidential` is true on loopback where `IsHttps`
was false, so the cookie *gains* `Secure` on `--mode app` and `--mode kiosk`. That
is intended, and §3 calls it a strengthening. The invariant is byte-for-byte
identical **configuration**, not behaviour. Do not try to preserve the old cookie
flag.

## Out of scope

`OnThisMachine` and the four gates (01b). The unix socket (01b). Invitations. The
panel. `install.sh` and the unit file. `AccessLevel.OwnerOnly`, which already
requires a session and does not move.

## What to tell me

If `docs/tls-and-proxies.md` and the code disagree, follow the **code** and put the
discrepancy in `concerns`. That document's line numbers were verified on
2026-09-16 and the tree has moved since — the last run found them all shifted by
roughly 38 lines and located each site by its surrounding expression instead,
which was the right call.
