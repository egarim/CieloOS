# First-Run Setup & Provider-Free Install

*Design plan — 2026-08-23, revised after adversarial review (13 findings). Closes the onboarding gap: today users are hardcoded demo seed (`joche`, `yulia`) authenticated by token files, and there is no step that creates YOUR owner or lets you run the OS with no AI provider.*

## Goals

1. **Install/run with no AI provider.** The whole OS control plane — identities, sessions, surfaces, files, console + desktop, the panel — works with **no local or cloud model**. The agent's autonomous loops are cleanly disabled ("no provider configured"), not broken. Providers are added later.
2. **A real owner, not demo seed.** A fresh machine has **no users**; a first-run **claim** creates the first owner (+ their agent). `joche/yulia` become opt-in demo data.
3. **Simplest possible install.** Boot → open the panel (on the box / over an SSH tunnel) → create your owner → add a provider whenever.

## Current state (what we're changing)

- `RuntimeSeed.People()` seeds `joche` + `yulia` unconditionally (`EnsureCreatedAndSeeded` / `InMemoryRuntimeStore` ctor). Auth is **bearer tokens** minted to `.data/secrets/<slug>.token` (no passwords). `Mint(slug) = slug:HMAC(signing.key, slug)` — deterministic.
- `local-bonsai` is registered as a chat provider **unconditionally** and is the default when DeepSeek is absent, so with no model running the agent errors with *"model error"*.

## Claim model — loopback-gated, no setup token

Review conclusion: a "setup token" is ceremony. The CLI already needs local read of `.data/secrets` (which *is* proof of machine access), and for the web path a static token in a cleartext POST is weaker than the structural fix. So:

**While the machine is unclaimed, `POST /api/setup/claim` is accepted only from loopback** (`127.0.0.1`/`::1`). A remote request is refused. Only someone on the box — a local browser, an **SSH tunnel** (how the panel is already reached), or the CLI — can claim. No token to generate, log, rate-limit, or leak.

```text
first boot, no owner marker
  -> panel (served/tunneled to localhost) shows "Set up Lun.Os"
  -> operator enters their name  (or runs: workspace-setup create-owner --name "…")
  -> claim (loopback only, single-winner): create owner + owner-agent + mint token
  -> owner-claimed marker persisted; owner token written 0600 + returned
  -> claimed: normal token login from here on; remote reachable
```

*(If remote web claim over a network is ever required, that is a separate decision needing TLS + a one-shot short-expiry nonce — out of scope here.)*

## Design & implementation requirements

The review verified these are load-bearing — the plan must call them out, not discover them:

1. **Seed the spreadsheet singleton unconditionally.** `SpreadsheetRow{Id=1}` is today created *inside* the demo-seed block; `SpreadsheetRevision` reads `.Single(Id==1)` on nearly every mutation/SSE. Gating the seed would crash a non-demo install. Fix: create the sheet row in its own `if (!Spreadsheets.Any())` step **outside** the user-seed block (and keep InMemory's unconditional default). Add a test that boots `LUNOS_DEMO` off and reads the spreadsheet.
2. **Add an identity-write path.** `IRuntimeStore` has no create-user; `Mint` is only on the concrete authenticator. Add `CreateOwner(user, workspace, agent)` to `IRuntimeStore` (both stores, one transaction); promote `Mint(slug)` onto `ITokenAuthenticator` (or inject the concrete one into claim); on claim, **write the owner's 0600 token file**. Recovery/CLI re-mint from `signing.key`, never assume the file exists.
3. **Whitelist the setup routes in the choke point.** `AccessPolicy.Required` (the auth gate) demands a bearer for everything except `/`, `/api/branding`, `/api/inference/status` — so on a fresh box the setup routes 401 *before* their own handler. Add `/api/setup/status` + `/api/setup/claim` to the Public branch; the claim gate (loopback + single-winner) lives in the handler.
4. **Atomic single-winner claim.** "Users empty" is check-then-act (TOCTOU → two owners). Enforce at-most-one-owner with a **persistent UNIQUE constraint** — a dedicated `owner_claim` marker row inserted in the *same transaction* that creates the owner; the second inserter hits a unique-violation → `409`. A process lock alone is insufficient. Do not anchor "claimed" on `Users.Any()`.
5. **Provider-free must include the no-provider brain state (fold Phase C into A).** Register `local-bonsai` **only if the local model is enabled/installed**; otherwise the resolved chat brain is an explicit **`UnconfiguredBrain`** returning one clear step. Without this, a freshly-claimed agent errors on its first message — so it ships *with* claim, not later.
6. **Honest provider onboarding.** There is **no runtime add-provider surface this release** (models surface is model-config.md Phase 3). So the `UnconfiguredBrain` message names the action that works **now**: *"No AI provider configured — set a key in config (`Inference:Deepseek:ApiKey` / `Inference:Azure:*`) or enable the local model, then restart."* Onboarding is **restart-based** until the models surface lands; state this plainly.
7. **Single-owner release.** After claim, a second human cannot be created except by hand. State the test release is **single-owner**; if a second tester principal is needed, add a `workspace-setup add-user --name` (reusing `CreateOwner` + `Mint`) — do not defer the whole multi-user story silently.
8. **Durability of "claimed."** Anchor on the persistent `owner_claim` marker, not the users rows. Forbid deleting the last owner (future user-mgmt). Treat a DB reset as a **deliberate re-provision** that re-opens ownership — document that operators must protect the DB file perms/backups (wiping the DB is the re-claim path, by design).
9. **Token recovery vs rotation (state the limits).** Recovery of a lost owner token = **restart** (the authenticator rewrites `<slug>.token` deterministically now that the user exists), or re-print via CLI. **Rotation** of a *leaked* token is impossible per-user — it requires rotating the shared `signing.key`, invalidating *all* tokens. Flag as a known release limitation (consistent with release-checklist.md token-storage).
10. **Store parity + seed hygiene.** Gate InMemory seeding on the **same** `LUNOS_DEMO` flag as EF so both stores share the first-run predicate (default demo ON for the test suite via the flag). Fix `RuntimeSeed`'s `InferenceProvider` from the dead `"local-inference"` to a real id (`"local-bonsai"`) or empty.
11. **`/api/setup/status`** returns only `{ claimed: bool }`; ideally loopback/rate-limited while unclaimed so it can't be swept for claimable hosts.

## Surfaces

- **`GET /api/setup/status`** → `{ claimed }`. Public.
- **`POST /api/setup/claim`** → `{ name }`; loopback-only + single-winner while unclaimed; creates owner, mints + writes token, returns `{ slug, token }`; `409` once claimed. Public route, gated in-handler.
- **Web wizard** (panel): when `!claimed`, render "create your account" (name only) instead of token-login; store the returned token.
- **CLI** `workspace-setup create-owner --name "…"` (+ `add-user`): on-box, claims via loopback, prints the token. Beside `workspace-installer` (which installs *Ubuntu*).

## Phasing

- **A. Provider-free first-run (one releasable unit):** unconditional spreadsheet seed; `LUNOS_DEMO`-gated demo seed (both stores); `CreateOwner` + `Mint` on the interfaces + token-file write; `AccessPolicy` whitelist; loopback + single-winner `claim` + `status`; the `workspace-setup` CLI; **and** `local-bonsai`-optional + `UnconfiguredBrain` with the restart-based message. This is the shippable test-release core.
- **B. Panel wizard:** the unclaimed → "create your account" screen (+ show the provider-config hint).
- **C. (later) models surface** (model-config.md Phase 3) turns the "restart to add a key" into an in-panel add-provider; then the `UnconfiguredBrain` copy points at Settings for real.

## Known limitations to put in release notes

Single-owner; provider onboarding is restart-based (edit config, restart); no per-user token revocation (rotating a leaked token rotates the shared key → all tokens); DB reset deliberately re-opens ownership (protect the DB file); remote (non-loopback) web claim is unsupported.
