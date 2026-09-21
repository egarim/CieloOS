# TLS, reverse proxies, and what "on this machine" means

Spine: the **refuse** design (prove confidentiality from the socket; never read a header for a security decision; split `Confidential` from `OnThisMachine` so no configuration key can reach the claim gate). Grafted in and marked where they appear: from **explicit**, the narrowing invariant, the refusal to enable `X-Forwarded-For`, and the discovery that `/etc/cielo/cielo.env` is regenerated wholesale on every install; from **self-sufficient**, the finding that the login throttle is a fifth reader of the raw peer, and the diagnosis that the cookie flag is a symptom and `install.sh:78` is the disease. Every finding six attackers marked fatal is either answered below or named in §10 as an open question. Two of them are answered by *not* claiming the thing all three designs claimed.

All line references verified against the working tree at `C:\Users\joche\CieloOS` on 2026-09-16. `docs/invites.md` says its own were verified on 2026-09-15; §12 lists the ones that have since drifted, including the three this document depends on.

---

## 1. WHAT IS BROKEN, AND WHY IT MATTERS

The session cookie is marked `Secure` only when *this hop* is HTTPS (Program.cs:2355-2361):

```csharp
// HttpOnly so a script in the panel's origin cannot read it — the flaw the token
// in localStorage had. Strict so it does not ride along on a cross-site request.
// Secure only under HTTPS, because the default deployment is plain HTTP on
// loopback and a Secure cookie would simply never be sent there.
static CookieOptions SessionCookieOptions(HttpContext context, DateTimeOffset expires) => new()
{
    HttpOnly = true,
    SameSite = SameSiteMode.Strict,
    Secure = context.Request.IsHttps,
    Path = "/",
    Expires = expires
};
```

Two call sites mint a cookie with it: `POST /api/auth/login` (Program.cs:766) and `POST /api/auth/password` (Program.cs:835). Behind a TLS-terminating reverse proxy the hop from proxy to CieloOS is plain HTTP, so `IsHttps` is false and a 14-day session cookie ships without `Secure` — over a connection the browser drew a padlock on. `grep -rn "IsHttps\|ForwardedHeaders\|UseForwardedHeaders\|X-Forwarded\|KnownProxies\|UseHttpsRedirection\|UseHsts" src tests --include=*.cs` returns exactly one line, Program.cs:2359. There is no `ForwardedHeaders` middleware anywhere in the solution; the pipeline is `builder.Build()` (Program.cs:314) → `UseCors` (:330) → static files (:346-347) → the principal middleware (:353) → endpoints, with nothing between Kestrel and CORS.

**The comment justifying the conditional is wrong on its own terms.** `http://127.0.0.1` and `http://localhost` are potentially-trustworthy origins; Chrome and Firefox both set and send `Secure` cookies over them. "A Secure cookie would simply never be sent there" is false for the one deployment the line was written for.

**And the flag is the smaller half.** `distro/install.sh:78` — `if [[ "$MODE" == "headless" ]]; then BIND="http://0.0.0.0:$PORT"; ...` — puts the whole panel on the LAN in cleartext. Every password typed into a headless install crosses the network readable, including the invite redemption `docs/invites.md` is designed around. Fixing the `Secure` flag while the password that produced it travelled in the clear is fixing the label on the box. The flag is what made us look; the bind is what is wrong.

### The larger thing this uncovered, and it is worse than the cookie

**A loopback peer is not a trust boundary on this machine, and has not been since the chat UI shipped.** `distro/install.sh:555-563` runs the chat container as:

```
exec podman run --rm --replace --name cielo-chat \
  --network host \
  ...
  -e WEBUI_AUTH=False \
  -e OPENAI_API_BASE_URL="http://127.0.0.1:${PORT}/v1/agent" \
  -e OPENAI_API_KEY="$chat_credential" \
```

`--network host` is there on purpose — the comment at :553-554 says "so the container reaches a loopback-bound runtime" — and it means the container shares the host's network namespace. Its `127.0.0.1` *is* Kestrel's `127.0.0.1`. `WEBUI_AUTH=False` is also on purpose, and install.sh:466-467 says why: "whoever opens the page the owner". So a third-party web application on a mutable tag (`ghcr.io/open-webui/open-webui:main`, re-pulled on every start), holding the owner's credential, with no login of its own, is a genuine loopback peer to this runtime. Not a forged header — a real TCP peer at `127.0.0.1`.

**Update, 2026-09-21 — the chat is now opt-in (`--chat`), off by default.** That
removes this particular loopback peer from a default install, and it does not
change the conclusion below. The argument never depended on the chat existing; the
chat was the sharpest available proof that a loopback peer can be something the
runtime does not trust. Rootless session containers still run in the host network
namespace, `--chat` still exists and boxes still run it, and the reasoning has to
hold for the machine that turns it on. Read what follows as "a loopback peer may be
untrusted", which is all `OwnerOnly` ever needed.

Every gate that reads the raw peer admits it:

| Program.cs | Route | What loopback authorises |
|---|---|---|
| :504 | `GET /api/setup/status` | discloses the owner's slug |
| :509 | `POST /api/setup/claim` | **becomes the machine owner, permanently** |
| :667 | `POST /api/usage/limits` (OsScope) | sets the machine-wide model budget |
| :818 | `POST /api/auth/password` | **sets a first password** |

`/api/setup/status` and `/api/setup/claim` are `Public` at Security.cs:66, so the in-handler loopback check is their *only* guard. The helper they all call is `IsLoopback` at Program.cs:2427-2436.

This document does **not** fix that. It is named here because three separate designs each claimed their new predicate meant "the person at the keyboard, the CLI, or an SSH tunnel", and all three were wrong for the same reason. The honest statement is §5's, and the fix is §10 question 1.

**A fifth reader nobody had counted.** Program.cs:727, the login throttle key:

```csharp
var throttleKey = $"source:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
```

It is already degenerate — the chat container, every on-box helper and the kiosk browser share one `source:127.0.0.1` bucket, so one misbehaving local caller can lock the operator out of their own console for fifteen minutes. Behind a proxy it collapses further: every remote client on earth shares one bucket, and eight failures deny sign-in to everyone. **This is also the reason `X-Forwarded-For` must stay off.** Turning it on to fix the throttle would, in the same line, rewrite the input to all four gates above.

---

## 2. THE DECISION, IN ONE PARAGRAPH

**No header is trusted. `UseForwardedHeaders` is not added. Confidentiality is proven from the socket.** The `Secure` flag stops asking "is this hop HTTPS" and starts asking "can this connection be proven confidential from facts the kernel and Kestrel already hold" — which is true when Kestrel performed the TLS handshake itself, when the connection was accepted on a loopback *local* endpoint, or when the peer address is one the operator named as their own TLS terminator. That last is a list of IP addresses in a config file the operator owns, not a claim any caller can make. In parallel, the question the four gates ask is split away from the question the cookie asks: `Confidential(conn)` decides whether a credential may cross this wire, `OnThisMachine(conn)` decides whether the peer is a process on this box, and **no configuration value in this design can make `OnThisMachine` true where it is false today.** A misconfigured proxy list can cost you a cookie flag. It cannot cost you the machine.

---

## 3. THE DEFAULT, FOR SOMEONE WHO CONFIGURES NOTHING

**Byte-for-byte today's behaviour, and the pipeline is the same shape, not merely the same outcome.** With no config file and no keys set:

- No `ForwardedHeaders` middleware is added. Not added-with-an-empty-allowlist — **not added**. The distinction matters: an empty `ForwardedHeadersOptions` still ships trusting `::1` and the `::1/128` network without anyone naming them, which is exactly the unnamed trust this design refuses.
- `--mode app` and `--mode kiosk` bind `http://127.0.0.1:5148` (install.sh:78), so every connection is accepted on a loopback local endpoint and is confidential by fact. The cookie becomes `Secure = true` where it was `false` — a strengthening, invisible, and correct, because browsers send `Secure` cookies to loopback origins.
- `--mode headless` binds `http://0.0.0.0:5148` and is **not** confidential from the LAN. Today that ships a non-`Secure` cookie over cleartext and says nothing. After this change it still ships the cookie and still says nothing, because §10 question 2 is whether it should refuse — but `install.sh` prints a `degrade()` line naming it, so the last screen of the install says the panel serves passwords in cleartext.
- An SSH tunnel (`ssh -N -L 5148:127.0.0.1:5148 you@box`) is confidential, correctly, and for the right reason: the tunnel terminates on the box, so the peer really is local and the browser really is talking to a loopback origin.
- The four gates behave exactly as they do today, including their existing weakness.

The operator who reads nothing and configures nothing gets a machine that behaves as it does now, one flag stronger on loopback, and one honest sentence at the end of the install.

---

## 4. WHICH HEADERS ARE TRUSTED, AND FROM WHERE

**None. Not one, from any source.** This is the whole answer and it is not an evasion — it is what makes the rest of the document short. Three facts are believed, each a property of the connection rather than of its content:

| Fact | Probe | Why a caller cannot forge it |
|---|---|---|
| Kestrel terminated TLS | `Features.Get<ITlsHandshakeFeature>() is not null` | Set by the connection layer that performed the handshake. Deliberately **not** `Request.IsHttps`, which is `Scheme == "https"`, and `Scheme` is a mutable string any middleware can assign. |
| Accepted on a loopback local endpoint | `IPAddress.IsLoopback(Connection.LocalIpAddress)` | The kernel will not deliver an off-box packet to a loopback local endpoint. This is routing, not a claim. |
| Peer is a named TLS terminator | `Connection.RemoteIpAddress` ∈ `Network__TlsTerminatedBy` | The operator describes their own network in a root-owned file. The caller contributes nothing to the comparison. |

`Connection.LocalIpAddress` rather than `RemoteIpAddress` is the load-bearing substitution. `ForwardedHeadersMiddleware` rewrites `RemoteIpAddress`, `RemotePort`, `Scheme`, `Host` and `PathBase`; it does not touch the local half of the socket. Building the confidentiality predicate on the local half makes it structurally immune to the middleware whose arrival is the fear this document exists to answer — immune by construction, not by a promise that nobody will install it. `docs/invites.md:582` calls adding it "probably right regardless", so someone will.

### Why not read `X-Forwarded-Proto` from a named proxy

Because reading it buys one thing and costs three, and all three are silent:

1. **nginx does not strip a client's `X-Forwarded-Proto`.** Without `proxy_set_header X-Forwarded-Proto $scheme` — the single most common nginx omission — the value the app reads is client-controlled, so `X-Forwarded-Proto: http` on a genuine TLS connection produces a non-`Secure` cookie. The header must be *overwritten*, not merely *forwarded*, and nothing in the runtime can detect that it was not.
2. **`ForwardLimit = 1` reads the nearest hop, and for scheme the nearest hop is the wrong one.** `X-Forwarded-Proto: https, http` resolves to `http`. "Nearest hop" is safe for an address and unsafe for a scheme; they are opposite ends of the same list.
3. **A named proxy that sends no header at all is indistinguishable from one that sends `http`,** so the runtime would be deciding a security property on the *absence* of a string.

Naming the terminator's address already carries the operator's assertion — "TLS is terminated in front of this socket" — and it carries it as a fact the network cannot edit. Reading the header re-derives the same assertion from a channel the attacks above own. So the header is not read for any decision.

**The one place a header is read, and the rule that makes it safe.** A request that arrives from an *un-named* address carrying `X-Forwarded-Proto` logs one Warning naming that address — once per process, at startup-log volume, with the exact string to paste into the config file. It affects no decision. The rule: **a header may be read only to reduce authority or to say something out loud, never to grant it.** A hostile caller who forges that header can only get themselves logged. In practice this warning is worth more than any middleware, because it fires on boxes that adopt none of this and are proxied today.

---

## 5. WHAT HAPPENS TO THE CLAIM AND FIRST-PASSWORD GATES

They are the reason this is delicate, and they are the reason `AccessLevel.OwnerOnly` was changed to require a session (Security.cs:299-302, reasoning at Security.cs:275-287, recorded in `docs/invites.md` §3). So the answer has to be exact.

### Nothing in this design can open them

`Network__TlsTerminatedBy` feeds `Confidential` and nothing else. There is no key, and no combination of keys, that is an input to `OnThisMachine`. Stated as the invariant to test:

> For every value of every configuration key, `OnThisMachine(conn)` implies `IsLoopback(conn.RemoteIpAddress)` as computed by Program.cs:2427-2436 today. Configuration can narrow this predicate. It can never widen it.

That is one exhaustive unit test over (peer ∈ {loopback, IPv4-mapped loopback, LAN, null}) × (local endpoint) × (peer is named terminator) × (config present or absent), and it is the only basis on which any of this belongs in the solution.

### Two narrowings, both free

```
OnThisMachine(conn) =
       IsLoopback(conn.RemoteIpAddress)          // today's check, unchanged
    && IsLoopback(conn.LocalIpAddress)           // NEW: not a proxy hop into a public bind
    && conn.RemoteIpAddress is not a named terminator   // NEW: a proxy is never the box
```

The second conjunct is the one that earns its place. A loopback *peer* is only evidence of on-box origin when the connection was also *accepted* on a loopback endpoint. If Kestrel is bound to `0.0.0.0:5148` and the peer is `127.0.0.1`, that is either a genuine on-box caller or a co-located proxy forwarding the internet, and the socket cannot tell them apart — so the gate must close for the ambiguous case rather than guess. It costs nothing on `--mode app` and `--mode kiosk`, which bind loopback, so the local claim wizard keeps working.

**On `--mode headless` it closes the gates for everyone, including `cielo-claim`.** That is the sharp edge of this document and it must not be buried:

- `cielo-claim` (install.sh:359-366) is `curl -fsS -XPOST "http://127.0.0.1:${PORT}/api/setup/claim"`. It crosses the same `0.0.0.0` listener, so its local endpoint is a specific interface address and `OnThisMachine` is false.
- It would then get `ClaimOutcome.Forbidden` (Program.cs:522) carrying `FirstRunSetup.cs:136`: *"Setup can only be claimed from the machine itself (localhost). Open the panel on the box or over an SSH tunnel."* The operator ssh'd in, ran the documented command on the box, and was told to run it on the box.
- The same applies to the first password at Program.cs:818-827 and, through it, to `cielo-add-user` (install.sh:368-398), which signs in before it creates anybody.

**So the second conjunct does not ship on its own.** It ships with an on-box transport that does not cross a network listener at all — a unix domain socket, `/run/cielo/admin.sock`, mode `0660 root:cielo`, `Kestrel.ListenUnixSocket`, and `curl --unix-socket` in the helpers, which already run under `sudo`. The gate becomes a filesystem permission: an authority the operator can read with `ls -l`, that nginx running as `www-data` cannot open, and that the same box already uses for the `0600` identity token files (`Auth:SecretsPath`, Program.cs:74-80). A headless install with the narrowing and no socket has no onboarding path whatsoever, and that is not a follow-up commit, it is the same commit. **Sequencing note for whoever builds this: the socket is a co-requisite, not hardening.**

### What it still does not fix

The unix socket makes `OnThisMachine` mean "a process that could open a `root:cielo` file", which is a real principal. It does **not** make it mean "the person at the keyboard", and it is not a fix for §1's chat container, which does not need `OnThisMachine` at all on a box where the four gates still read the raw peer. Until the gates move onto the socket exclusively, the honest sentence is: **`OnThisMachine` distinguishes a caller that crossed a network listener from one that did not. It does not distinguish a trusted on-box process from an untrusted one.** §10 question 1.

### `AccessLevel.OwnerOnly` does not move, and its recorded reason needs rewriting

`PrincipalGate.Check` keeps requiring `hasSession` (Security.cs:299-302). Nothing here restores loopback as an owner factor. But the reason recorded at Security.cs:295-296 and `docs/invites.md:596` is *"there is no ForwardedHeaders middleware in this solution"*, and after this document that premise is worth less than it looks — not because the middleware arrives (it does not) but because §1 shows the gate was never protected by its absence. The next reader must not conclude the rejection expired.

The replacement premise is **not** "`OnThisMachine` cannot be forged, but a person at the console still has not proved a password." That sentence is the mistake three designs made. The honest one is:

> `OwnerOnly` requires a session because a password is the only thing on this machine that proves a *person*. Every address-shaped test — raw peer, local endpoint, named terminator, even a unix socket — proves at most a *process*, and this box runs processes it does not trust: a rootless podman container in the host network namespace is a loopback peer, and an unauthenticated chat UI holding the owner's key is one today. A second factor that proves a process is not a second factor.

---

## 6. THE OPERATOR: ONE LAPTOP, A SMALL TEAM, NO PUBLIC DNS

This is the stated case and it is the one where all three designs were thinnest, because no public DNS means no ACME, which means the hard part is not the config key — it is where a certificate comes from. The steps, in order, and **the order is the design**:

**Step 1. Claim the box and set your password before anything is in front of it.**

```bash
sudo ./install.sh --mode headless
ssh in, then:  cielo-claim "Your Name"
               # set your first password, on the box, now
```

This is the whole trick. The claim and the first password are the two operations `OnThisMachine` guards, they each happen exactly once in the life of the machine, and they happen before a terminator exists. Doing them first means a co-located proxy closing those gates afterwards costs you nothing — you already walked through them, and they are gates you never need again. Every painful configuration in every rejected design existed to keep those gates open *after* a proxy was installed. You do not need them open.

**Step 2. Narrow the plaintext listener to loopback.** In `/etc/cielo/cielo.env`:

```
ASPNETCORE_URLS=http://127.0.0.1:5148
```

Now nothing plaintext is on the LAN. This is the actual fix for §1's disease, and it is one line.

**Step 3. Put a terminator in front of it, on the same laptop.** Caddy or nginx to `http://127.0.0.1:5148`. With no public DNS, the certificate is self-signed or from a `mkcert`-style local CA, and there is no third option — see §9 for why the product does not generate one for you and §10 question 4 for whether it should.

**Step 4. Configure nothing.** A terminator that reaches CieloOS on loopback is confidential by fact 2 in §4. `Network__TlsTerminatedBy` stays empty forever. The best configuration is no configuration; the key exists only for the operator whose terminator is on a *different* host and therefore arrives from a LAN address.

**Step 5. Add teammates over the tunnel, not the LAN.** Until §10 question 3 is answered, an invite redemption and a first password are still `OnThisMachine`-gated, so the five people either come to the box or come through `ssh -N -L 5148:127.0.0.1:5148 you@box`. That is honest and it is what `docs/invites.md` §11 question 1 is blocked on.

**What this costs the operator who skips step 1.** A box that is claimed *after* a co-located proxy is in place cannot be claimed at all: the gate is closed and there is no socket yet. That is the one genuinely bricking order, it is entirely recoverable by stopping the proxy for sixty seconds, and it is why `install.sh` must print step 1's instruction before anything else and why the startup log must say, when a terminator is named on an unclaimed box, that the box must be claimed first.

**If you have SSH, you may not need any of this.** The tunnel is already confidential, the peer really is local, and the browser really is talking to a loopback origin. It stays first in the docs. Steps 2-4 are for the person who needs a browser on a phone.

---

## 7. CONFIGURATION

**One key, list-valued, default empty, in a file `install.sh` does not regenerate.**

```
# CieloOS network trust. Nothing is trusted unless you name it here.
#
# The addresses YOUR TLS terminator connects to this box FROM. Comma-separated,
# literal IPs. No CIDR, no hostnames: a range is a set of machines you did not
# name, and a hostname is a DNS answer somebody else controls.
#
# EMPTY (the default) is correct for almost everyone. A terminator that reaches
# CieloOS on loopback needs nothing here.
#
# Naming an address does TWO things and you are agreeing to both:
#   a) session cookies issued over that connection are marked Secure;
#   b) a request from that address is never "on this machine" again — the
#      first-owner claim and the first-password gate close for it. Claim the box
#      and set your password BEFORE you put a terminator in front of it.
# Network__TlsTerminatedBy=10.0.0.5
```

**It lives in a new `/etc/cielo/network.env`, not in `cielo.env`, and this is not tidiness.** `distro/install.sh:321-323` regenerates `/etc/cielo/cielo.env` wholesale on every run:

```bash
sed -e "s#UID_PLACEHOLDER#${CIELO_UID}#" \
    -e "s#^ASPNETCORE_URLS=.*#ASPNETCORE_URLS=${BIND}#" \
    "$BUNDLE/cielo.env.example" > /etc/cielo/cielo.env
```

That is `>`, not a merge. A key added by hand is destroyed by the next `sudo ./install.sh`, and the box comes back with an un-`Secure` cookie and nothing on screen saying so — a security regression caused by a routine upgrade. `/etc/cielo/chat.env` (install.sh:489-502) is the existing precedent: `if [[ ! -f ]]`, created once, `chmod 0644`, never rewritten. `network.env` follows it, at `chmod 0640` to match the adjacent `cielo.env` (install.sh:341) rather than inheriting root's umask by omission.

`distro/services/cielo-runtime.service:11` gains one line under the existing `EnvironmentFile=`:

```
EnvironmentFile=-/etc/cielo/network.env
```

The leading `-` makes it optional, so every existing install upgrades with the file absent and no behaviour change.

**Note the same hazard for step 2 of §6.** `ASPNETCORE_URLS` is rewritten by install.sh:322 on every run, so an operator who narrows the bind by hand has it widened back on the next upgrade. That is a bug this document does not fix and §10 question 5 is about it. `--mode` is the only durable way to set the bind today.

**Both install trees, in the same commit.** `distro/install.sh` (685 lines) and `release/cielo/install.sh` (527 lines) have already diverged — the release copy is missing the `degrade()` mechanism (distro:43-57) and the `/var/lib/cielo` ownership fix (distro:95-101), and the bind line is distro:78 versus release:63. The release tarball is what a laptop operator actually installs. A security config that lands in one tree is a feature that silently does not exist on the distribution channel, and `EnvironmentFile=-` makes its absence not even an error. Both trees and both unit files, or this does not land.

---

## 8. WHAT A MISCONFIGURATION LOOKS LIKE, AND HOW THE OPERATOR IS TOLD

| Operator does | Result | How they find out |
|---|---|---|
| Nothing; `--mode app`/`kiosk` | Confidential by loopback. Cookie gains `Secure`. Gates unchanged. | Nothing to find out. |
| Nothing; `--mode headless`, no TLS | Cleartext panel on the LAN, non-`Secure` cookie. **Unchanged from today.** | A `degrade()` line, printed last per install.sh:52-57, naming it. |
| Terminator → `127.0.0.1`, loopback bind, **no config** (the §6 recommendation) | Confidential. Gates closed to the terminator. Nothing to configure. | Nothing to find out. |
| Terminator on another host, declared | Confidential. Named trust decision logged in plain words at startup. | `journalctl -u cielo-runtime`. |
| **Typo in the address** | Nothing matches. Fails **closed** — cookie stays non-`Secure`, gates unaffected. | The one-time Warning names the address that actually arrived, to paste in. This is the highest-value line in the change. |
| **Terminator declared but it is proxied by something else too** | Only the nearest hop's address is compared. Fails closed. | Same Warning. |
| `Network__TlsTerminatedBy=0.0.0.0` or a CIDR or a hostname | **Refuses to start**, naming the entry. | See below. |
| **Declares a real address that is not actually behind TLS** | Cookies marked `Secure` over cleartext. | Nothing detects it. **The one fail-open.** See below. |
| Claims the box *after* installing a co-located proxy | Cannot claim it at all. | The 403 must say *why*: "this request arrived via 127.0.0.1, which is named in Network__TlsTerminatedBy, so it is not treated as on-box." |

**Refusing to start, and the thing that would have swallowed it.** A half-parsed allowlist is worse than none, because the operator then believes a boundary exists. A wildcard is refused rather than warned: a wildcard socket is by construction also directly reachable, so the assertion it encodes can never be true. A CIDR is refused with a message that names the limitation rather than dropping the entry silently. A hostname is refused because resolving one makes DNS a trust root.

But **`distro/install.sh:352` runs `systemctl restart cielo-runtime.service` and never checks that it came up.** There is no `is-active` test for the runtime anywhere in the installer — the only `degrade()` calls are for images, search and podman — and the closing status at install.sh:654 passes `--lines=0`, so it prints `activating (auto-restart)` with no log lines under a banner reading "CieloOS installed". A fail-fast that lands there is invisible, which is precisely the failure install.sh:44-52 was written to prevent: *"The last screen is the one people read, and it said everything was fine."* So refusing to start must be paired with an `is-active` check and a `degrade()` line, or the mechanism the installer already built is bypassed by the feature that needs it most.

**The one fail-open, stated without hedging.** An operator who names an address that is not behind a TLS terminator gets `Secure` cookies over cleartext, and nothing in the product can detect it — CieloOS cannot see what is in front of it. Three things bound it: it takes a deliberate, specific, non-wildcard address, so there is no plausible accident; it is announced in plain language at startup and is greppable; and it is *only* a confidentiality failure, because the same key cannot reach `OnThisMachine`. Worth knowing that the observed symptom is not "a cookie you can read" but **a silent login loop** — the browser refuses to store a `Secure` cookie from an `http://` origin, `POST /api/auth/login` returns 200 with a user payload, and every subsequent request 401s. A diagnostic that reports "confidential: true" would actively steer the operator away from the cause, which is an argument for that diagnostic naming *which of the three facts* was satisfied rather than returning a boolean.

**A real bug this surfaces.** `context.Response.Cookies.Delete(CredentialFormat.SessionCookie)` at Program.cs:782 and :793 passes no options. Once `Secure` is set on more connections than before, a delete whose `Secure`/`Path`/`SameSite` do not match the original may not match and remove it, leaving a server-revoked but browser-resident cookie after sign-out. Both call sites need the matching `CookieOptions`, and that is a prerequisite of this change rather than a footnote to it.

---

## 9. WHAT WAS CONSIDERED AND REJECTED

**`UseForwardedHeaders` with a `KnownProxies` allowlist** — option (b) at `docs/invites.md:582`, and the conventional answer. Rejected. It asks the operator to name *who may write a header* and then believes what they wrote, which re-derives an assertion the operator already made, through a channel three separate attacks own (§4). It also rewrites `Connection.RemoteIpAddress`, the input to all four gates and the throttle at :727 — so the correct version of it requires `ForwardedHeaders.XForwardedProto` only, `KnownNetworks.Clear()` and `KnownProxies.Clear()` before the list (the framework ships trusting `::1` without anyone naming it), `ForwardLimit` set, and a rule that the next person to touch it must not add `XForwardedFor` to fix the throttle. That is four invariants a future commit can break silently, bought for one thing — per-request scheme — that naming the address already gives. The failure of an allowlist is "someone unexpected wrote a header we trusted"; the failure of a declared address is "the operator described their own network wrong". The second is narrower, visible in a log, and cannot be triggered by a remote party.

**`ForwardedHeaders.XForwardedHost` alongside the scheme.** Rejected on sight. Nothing in the tree consumes `Request.Host` for a decision or a generated link, `appsettings.json:8` sets `"AllowedHosts": "*"` so it is unvalidated anyway, and `ForwardedHeadersOptions.AllowedHosts` defaults to allow-any. It is trust nobody named, for a use case nobody has.

**Enabling `X-Forwarded-For` to fix the login throttle** (Program.cs:727). Rejected, and it is the single most tempting wrong move in this area. The throttle is genuinely broken behind a proxy — every remote client shares one bucket, so eight failures deny sign-in to everyone — and the one-line fix for it is the one line that hands `IsLoopback` to the internet at four call sites. It is also a bad fix on its own terms: a rotating forged header turns a lockout throttle into unbounded bucket growth and a free brute-force channel. The throttle needs a different key (source *and* account, or a bucket that cannot be shared), costed as its own change.

**A second loopback port for on-box work** (`Network__OnBoxPort=5149` plus a second bind), so a co-located proxy on 5148 could be named without closing the gates. Rejected twice over. First, it does not work: a container in the host network namespace reaches `127.0.0.1:5149` exactly as easily as `:5148`, so the port distinguishes nothing that matters. Second, half of it lives in `ASPNETCORE_URLS`, which install.sh:322 rewrites on every run, and the other half in `cielo-claim`, which install.sh:359 regenerates with `${PORT}` baked in — so a routine upgrade removes the bind, restores the old port in the helper, and leaves a config key asserting an on-box path that no longer exists. A remedy that a reinstall silently dismantles is worse than no remedy. §6's ordering replaces it and costs nothing.

**A `Cielo__AllowCleartextCookies` escape hatch**, or a `Network__CookieSecure=never` debug value. Rejected. It would be pasted into every headless `cielo.env` within a month and the refusal becomes decoration. Worse, `never` is a documented key whose only guard is prose, it lives in a file created once and never re-examined, and it *weakens* Program.cs:2359 even on a genuine direct TLS connection. The escape hatch is the real deployment fix — terminate on loopback — which is two lines and less typing.

**`Network__CookieSecure=always`**, an operator assertion that every browser reaches this box over TLS, for the terminator whose source address is not enumerable (a load balancer pool, a CDN). Genuinely contested, and it fails closed — over plain HTTP nobody can sign in, because the browser will not send the cookie back. Rejected for v1 anyway, on the grounds that the deployment it serves is a platform team's, the constraint says the operator may be one person on a laptop, and a blanket assertion that cannot be checked against anything is the shape of the one fail-open in §8 with the guard rails removed. If a pool shows up, this is the right answer for it and it should be added then.

**Terminating TLS inside CieloOS** — a Kestrel HTTPS listener alongside the plaintext loopback one, with `--self-signed` generating a leaf and printing its fingerprint. The strongest rejected option, and the one that best serves §6's operator, because it is the only shape where "reachable over HTTPS" needs no second process at all. Rejected for now on four counts, none of them aesthetic: certificate expiry becomes an outage in a product with no runtime degradation channel (`degrade()` at install.sh:43-57 is install-time only), so the person positioned to notice is the person who can no longer sign in to find out why; an IP-SAN leaf rots under DHCP, and every address change is a new fingerprint re-trusted on five laptops and two phones, which is a chore that gets abandoned; the closing banner at install.sh:660-662 and `cielo-selftest.sh:9` both hardcode plaintext loopback and would need rewriting; and the recommended Let's Encrypt path does not work as written, because `cielo-runtime.service:8` runs as `User=cielo` and `/etc/letsencrypt/live` and `/archive` are `0700 root:root`, so the most-documented configuration fails on first start into exactly the invisible restart loop §8 describes. It is the right next design and it is a bigger one. §10 question 4.

**A local CA (`mkcert`-style) generated by the installer.** Rejected specifically for this product. The CA key would sit alongside the `0600` identity token files that `IdentityTokenAuthenticator` reads (`Auth:SecretsPath`, Program.cs:74-80), on a box that runs rootless podman containers and an unauthenticated chat UI in the host network namespace. A compromise of that box would then mint a trusted certificate for any hostname on the operator's laptop. A single pinned self-signed leaf cannot. Narrower blast radius is worth the clumsier interstitial.

**First-party ACME.** Rejected. HTTP-01 needs port 80 inbound and a public name, which §6's operator has neither of; it would also need `/.well-known/acme-challenge/*` reachable unauthenticated, and `AccessPolicy.Required` falls through to `AnyPrincipal` at Security.cs:221, so the path would have to be named `Public` explicitly or fall into the agent-writable-by-omission case. DNS-01 needs a provider API token per DNS host, which is a plugin ecosystem. Consuming a certificate rather than obtaining one means **this change adds no routes at all**, so Security.cs:221's fall-through stays irrelevant to it.

**Trusting `Request.Host` to infer a public HTTPS origin.** Rejected on sight: `appsettings.json:8` sets `"AllowedHosts": "*"`, so `Host` is attacker-controlled. This is the same reasoning `docs/invites.md` §11 question 2 gives for not composing invite links from it.

**Tailscale, and mesh VPNs generally** — WireGuard, Nebula, ZeroTier, Headscale. Rejected as a design input: **not part of the product's setup; it was a test harness for one box.** Nothing in `distro/install.sh` installs, configures or mentions one, and a design that assumes a mesh assumes the operator has already solved identity and transport somewhere this product cannot see, then quietly inherits that solution's failure modes. It is also the most seductive rejection here, because a mesh genuinely does make the wire confidential and does give the box a stable name — which is exactly why it must not become the answer by default: it converts "CieloOS is secure" into "CieloOS is secure if you also run this other thing", and the constraint says the operator may be one person installing on a laptop. An operator who already runs one gets the benefit without the product knowing: the terminator is on the mesh interface, it reaches CieloOS on loopback, and §6 step 4 applies unchanged. That is the correct relationship — CieloOS does not know, does not care, and does not depend.

**Refusing cleartext sign-in outright on headless.** Genuinely contested and deferred rather than rejected; see §10 question 2. Refusing would break every headless install that signs in over the LAN today, and the install banner at install.sh:660-662 documents that flow as the supported path. Shipping the refusal in the same change as the cookie fix would make a security correction indistinguishable from a regression.

**Deleting the local `IsLoopback` helper** (Program.cs:2427-2436) so nobody can write a fifth gate against the raw peer. Attractive, and rejected for now: the throttle at :727 legitimately wants the raw peer, `OnThisMachine` needs it as its first conjunct, and a deletion that forces every caller through a stashed `context.Items` verdict trades a forgery-proof read for a string-keyed, in-process-writable one. The cheaper version of the same guarantee is a review rule: **after this change `Connection.RemoteIpAddress` appears in Program.cs exactly twice — inside `OnThisMachine` and at the throttle. A third occurrence is a bug**, and that is one grep, runnable by CI.

---

## 10. OPEN QUESTIONS THAT NEED A HUMAN DECISION

1. **The chat container, and it is the blocking one.** `distro/install.sh:556` runs an unauthenticated third-party web app on a mutable tag in the host network namespace, holding the owner's credential, and its `127.0.0.1` is Kestrel's. That makes it a genuine loopback peer to `/api/setup/claim` (:509), the first-password gate (:818), the machine-wide budget (:667) and the owner disclosure (:504). It is true today, on a default install, with no proxy and no header involved. Options: (a) move all four gates onto the unix socket exclusively, which is the durable answer and which §5 already builds the socket for; (b) run the chat container on its own network with `host.containers.internal`, which install.sh:553's comment says was tried and rejected because the runtime binds loopback in app/kiosk modes; (c) put a credential on the chat's hop. Nothing in this document is safe to call a trust boundary until this is answered, and **the documentation must not describe `OnThisMachine` as "the person at the keyboard" before it is.**
2. **Whether `--mode headless` should refuse cleartext sign-in.** Today it serves the panel and every password on the LAN in the clear. A refusal is the honest posture and it breaks the flow install.sh:660-662 documents. The middle position — refuse to mint a *first* password or redeem an invitation over cleartext, permit an *existing* sign-in with an audit row recording that the credential crossed a cleartext network — is the line `docs/invites.md` §5 step 0 already draws and is probably right. Decide before building, because it changes whether the cookie fix ships alone or with a refusal.
3. **Whether the session containers are also loopback peers.** `SessionOrchestrator.cs:161` runs sessions with no `--network` flag (rootless default) and `:233-234` publishes viewports as `-p 127.0.0.1::{port}`, while Program.cs:2447-2452 exists specifically to rewrite a host loopback URL to `host.containers.internal` so a session can reach host services. Whether that forwarder presents a loopback source address to Kestrel depends on the podman network backend — slirp4netns versus pasta, and their gateway mapping — and `distro/install.sh:88` pins neither. **This must be answered by a packet-level test on a real install, not by reading.** If it is loopback, every desk and console container is inside the gates and question 1's answer (a) becomes urgent rather than correct.
4. **Where §6's certificate comes from.** No public DNS means no ACME, so the recommended deployment ends at "run a terminator with a self-signed cert" and the product says nothing about how. Options: leave it to the operator and document `mkcert` and Caddy's internal CA concretely in `distro/README.md`; or the rejected in-product Kestrel TLS listener from §9, which is the better UX and a bigger change with a real renewal story attached. Whoever owns the headless install story owns this.
5. **`ASPNETCORE_URLS` is rewritten on every install** (install.sh:322), so §6 step 2 — narrowing the bind to loopback — is undone by the next `sudo ./install.sh` unless the operator re-passes the same `--mode`. That is the same reinstall hazard §7 moved the new key out of `cielo.env` to escape, and it applies to a line that was already there. Either the bind becomes operator-owned config that survives, or the installer learns to preserve it, or `--mode` grows a fourth value meaning "headless, behind a terminator on this box".
6. **Whether the unix socket replaces the loopback gates or joins them.** §5 ships it as an additional confidential transport with the gates still reading `OnThisMachine`. Making the four gates socket-*only* is stricter, is the real answer to question 1, and breaks the app/kiosk claim wizard, because a browser cannot speak to a unix socket. That is a genuine conflict between the two modes and it needs deciding rather than discovering.
7. **Whether an on-box diagnostic should exist at all.** A `GET /api/transport` returning which of §4's three facts held would turn every entry in §8's table into something the operator can read instead of guess — and it is a new public route that echoes the listening interface to anonymous callers, and per Security.cs:221 it must be named explicitly or it falls through to `AnyPrincipal`. The narrower version is `cielo-selftest.sh` growing four lines, which nothing ever runs.

---

## 11. WHAT THIS DELIBERATELY DOES NOT DO

- **It does not make the loopback gates mean what their comments say.** §5 and §10 question 1. The comment at Program.cs:2425-2426 — *"Loopback = the request originates on the box itself (a local browser, the SSH tunnel the panel uses, or the CLI)"* — is not true on a default install and should be corrected in the same commit, whatever else lands.
- **It does not fix the login throttle** (Program.cs:727). Behind any proxy it is one bucket for the world; on any box it is one bucket for every local caller including the chat container. Named, costed separately, and explicitly not fixed by the one line that would fix it.
- **It adds no routes,** so `AccessPolicy.Required`'s fall-through to `AnyPrincipal` (Security.cs:221) stays irrelevant to this change. Any diagnostic added later (§10 question 7) must be named there explicitly.
- **It reads no header for any decision.** The one header that is read is read to log a warning, and a caller who forges it can only get themselves logged.
- **It never rewrites `Connection.RemoteIpAddress`.** Not by middleware, not by a stashed verdict, not for the throttle.
- **Nothing is deleted.** This is configuration and process-lifetime state: no rows, no migration, no `EnsureCreated` implication. `EnvironmentFile=-` means the new file's absence is not an error, so every existing install upgrades with no action and no behaviour change.
- **It does not assume a mesh VPN, a platform team, a public name, or a second machine.** §9, and it is the constraint most likely to be violated by the next design that touches this area.

---

## 12. CITATIONS IN `docs/invites.md` THAT HAVE DRIFTED

That document opens by asserting its line references were verified on 2026-09-15, which is exactly the claim that decays into false confidence. Three of them are load-bearing for this one:

| invites.md | Says | Actually |
|---|---|---|
| :13 | `POST /api/auth/password` gate at Program.cs:812-821 | Program.cs:818-827 |
| :79, :596 | `IsLoopback` at Program.cs:2418 | Program.cs:2427-2436 |
| :255 | `Secure = context.Request.IsHttps` at Program.cs:2350 | Program.cs:2359 |
| :255, :582 | headless bind at `distro/install.sh:63` | `distro/install.sh:78`. `:63` is the line number in `release/cielo/install.sh`, which has drifted 158 lines from it |

**What needs rewriting rather than renumbering.** §11 question 1 at :582 is answered by this document as **(a)** — document TLS as a prerequisite — with the terminator reaching CieloOS on loopback so the refusal has a deployment that satisfies it and no allowlist is needed. Option (b)'s premise, that `UseForwardedHeaders` "makes `IsHttps` and `RemoteIpAddress` honest again", is half right and the dangerous half: this design makes `IsHttps` unnecessary and leaves `RemoteIpAddress` raw forever. §5 step 0 at :255 becomes implementable as one line against `Confidential(conn)`, and T-9 at :571 (`Redemption_refuses_cleartext`) becomes a test rather than a refusal with no deployment that can pass it. §12's rejection of `session || IsLoopback` at :596 keeps its conclusion and needs its reason replaced with §5's, or the next reader will conclude the rejection expired when the middleware question was settled — when in fact the reason it was right got stronger.
