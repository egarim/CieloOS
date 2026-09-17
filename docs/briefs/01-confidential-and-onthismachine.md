# Brief 01 — split `Confidential` from `OnThisMachine`

## Why this comes first

This is a prerequisite for invitations, not a detour. `docs/invites.md` §5 refuses
non-loopback redemption unless the wire is confidential, and §11 question 1 names
that as **the blocking open question**: on a default headless install there is no
TLS, no certificate, and no way for the runtime to know whether there is one in
front of it. `docs/tls-and-proxies.md` answers it. Until that answer is code, an
invitation cannot be redeemed by the remote person it exists for.

Implement `docs/tls-and-proxies.md` §2 through §5 and §7. Read that document as
the specification; this brief only fixes the scope and the acceptance criteria.

## The decision you are implementing, restated so you cannot miss it

**No header is trusted. Do not add `UseForwardedHeaders`.** Not with an empty
allowlist either — an empty `ForwardedHeadersOptions` still trusts `::1` and
`::1/128` without anyone naming them, and unnamed trust is the thing being
refused.

Two predicates replace one:

- `Confidential(connection)` — may a credential cross this wire? True when Kestrel
  performed the TLS handshake itself, or the connection was accepted on a loopback
  *local* endpoint, or the peer address is one the operator named in
  `Network__TlsTerminatedBy`.
- `OnThisMachine(connection)` — is the peer a process on this box? **No
  configuration value may be an input to this.** A misconfigured proxy list costs
  a cookie flag; it must not cost the machine.

## Files and call sites

`src/backend/WorkspaceRuntime.Api/Program.cs`, verified at the current tree:

| line | today | becomes |
|---|---|---|
| 542 | `IsLoopback(...)` — owner disclosure on `/api/setup/status` | `OnThisMachine` |
| 547 | `IsLoopback(...)` — the first-owner claim gate | `OnThisMachine` |
| 706 | `IsLoopback(...)` — machine-wide budget scope | `OnThisMachine` |
| 857 | `IsLoopback(...)` — the first-password gate | `OnThisMachine` |
| 766 | `RemoteIpAddress` as the login throttle key | **unchanged** |
| 2399 | `Secure = context.Request.IsHttps` | `Secure = Confidential(...)` |

Line 766 is deliberately left alone and you should not "fix" it. It is a
rate-limit bucket, not a gate: it spends an attacker's own address rather than a
victim's desk name. Behind a proxy it will bucket every caller together, which is
a real weakness — but it is a *throttling* weakness, and solving it by trusting a
header would hand an attacker the ability to reset their own bucket at will.

`IsLoopback` at 2467 stays as the primitive both predicates are built from.

## Configuration

`Network__TlsTerminatedBy`: comma-separated **literal IP addresses**, empty by
default. No CIDR and no hostnames — a range is a set of machines nobody named, and
a hostname is a DNS answer somebody else controls. Empty must be byte-for-byte
today's behaviour.

Note for you: `/etc/cielo/cielo.env` is regenerated wholesale by
`distro/install.sh` on every install, so anything written there by hand is lost on
upgrade. Do **not** change `install.sh` in this task — say so in `followups`.

## The one header read that is allowed

A request arriving from an *un-named* address carrying `X-Forwarded-Proto` logs
one warning naming that address, once per process, with the exact string to paste
into the config. It affects no decision.

The rule, which belongs in a comment: **a header may be read only to reduce
authority or to say something out loud, never to grant it.**

## Tests

In `tests/backend/WorkspaceRuntime.Tests/`. The invariant matters more than the
cases:

1. **No configuration value makes `OnThisMachine` true where it is false today.**
   Drive it with a populated `Network__TlsTerminatedBy` and assert a non-loopback
   peer is still not on this machine. This is the test that protects the claim
   gate, so write it first.
2. `Confidential` is true for: a Kestrel-terminated TLS connection, a loopback
   local endpoint, and a named terminator address.
3. `Confidential` is false for a plain non-loopback peer that is *not* named, even
   when it sends `X-Forwarded-Proto: https`.
4. An empty or absent `Network__TlsTerminatedBy` reproduces current behaviour
   exactly.
5. A malformed entry in the list (a hostname, a CIDR, a nonsense string) is
   rejected loudly at startup rather than silently ignored — a proxy list that
   half-parsed is worse than one that failed.

## Out of scope

Invitations. The panel. `install.sh`. `AccessLevel.OwnerOnly`, which already
requires a session and does not move. Do not reformat code you are not changing.

## What to tell me

If `docs/tls-and-proxies.md` and the code disagree, follow the **code** and put
the discrepancy in `concerns`. That document records line numbers verified on
2026-09-16 and the tree has moved since.
