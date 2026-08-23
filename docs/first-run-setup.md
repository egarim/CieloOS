# First-Run Setup & Provider-Free Install

*Design plan — 2026-08-23, revised after adversarial review (13 findings). Closes the onboarding gap: today users are hardcoded demo seed (`joche`, `yulia`) authenticated by token files, and there is no step that creates YOUR owner or lets you run the OS with no AI provider.*

> **Status: Phases A + B + C SHIPPED** (2026-08-23). Provider-free install +
> loopback-gated single-winner claim + `UnconfiguredBrain` + `create-owner` CLI
> (Phase A), the panel's first-run wizard (Phase B), and **multi-user + the in-panel
> models surface** (Phase C): add a teammate and add/configure model providers from
> the panel with no restart. 129 backend tests; verified live in the browser. See
> **What shipped** below for the two deviations from this plan (single-owner
> in-process claim lock instead of a marker table; `/api/setup/status` kept public).

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
6. **Honest provider onboarding.** *(Superseded by Phase C.)* Onboarding is no longer restart-based: the **Models** panel tab adds a provider that works immediately. The `UnconfiguredBrain` message now points there first (*"Add one from the Models tab … or set a key in config and restart"*). Setting a key in `config/branding.json`/env + restart still works as the config path.
7. **Multi-user.** *(Delivered in Phase C.)* After claim, an owner adds further users from the panel ("＋ Add teammate") or CLI (`add-user --name … --token …`) — human-only, slug-collision-guarded, each gets their own agent + token. No longer single-owner.
8. **Durability of "claimed."** Anchor on the persistent `owner_claim` marker, not the users rows. Forbid deleting the last owner (future user-mgmt). Treat a DB reset as a **deliberate re-provision** that re-opens ownership — document that operators must protect the DB file perms/backups (wiping the DB is the re-claim path, by design).
9. **Token recovery vs rotation (state the limits).** Recovery of a lost owner token = **restart** (the authenticator rewrites `<slug>.token` deterministically now that the user exists), or re-print via CLI. **Rotation** of a *leaked* token is impossible per-user — it requires rotating the shared `signing.key`, invalidating *all* tokens. Flag as a known release limitation (consistent with release-checklist.md token-storage).
10. **Store parity + seed hygiene.** Gate InMemory seeding on the **same** `LUNOS_DEMO` flag as EF so both stores share the first-run predicate (default demo ON for the test suite via the flag). Fix `RuntimeSeed`'s `InferenceProvider` from the dead `"local-inference"` to a real id (`"local-bonsai"`) or empty.
11. **`/api/setup/status`** returns only `{ claimed: bool }`; ideally loopback/rate-limited while unclaimed so it can't be swept for claimable hosts.

## Surfaces

- **`GET /api/setup/status`** → `{ claimed }`. Public.
- **`POST /api/setup/claim`** → `{ name }`; loopback-only + single-winner while unclaimed; creates owner, mints + writes token, returns `{ slug, token }`; `409` once claimed. Public route, gated in-handler.
- **Web wizard** (panel) — ✅ built: when `!claimed`, render the claim wizard (name only) instead of token-login; store the returned token and enter the app. A loading state covers the status check so neither screen flashes first.
- **CLI** `workspace-setup create-owner --name "…"` (+ `add-user`): on-box, claims via loopback, prints the token. Beside `workspace-installer` (which installs *Ubuntu*).

## Phasing

- **A. Provider-free first-run (one releasable unit) — ✅ SHIPPED:** unconditional spreadsheet seed; `LUNOS_DEMO`-gated demo seed (both stores); `CreateOwner` + `Mint`/`IssueToken` on the interfaces + token-file write; `AccessPolicy` whitelist; loopback + single-winner `claim` + `status`; the `create-owner` CLI; **and** `local-bonsai`-optional + `UnconfiguredBrain` with the restart-based message. This is the shippable test-release core. (See **What shipped** for the two deviations.)
- **B. Panel wizard — ✅ SHIPPED:** the panel fetches `/api/setup/status` while signed out; unclaimed renders a "Claim this machine" wizard (name → `POST /api/setup/claim` → store the returned token → straight into the app), claimed renders token login. A 403/409 from the claim maps to a clear message (loopback-only / already-claimed). Verified live: unclaimed → wizard → claim → app → sign out → login.
- **C. Models surface + multi-user — ✅ SHIPPED:** a persistent provider store (`.data/secrets/providers.json`, 0600) the `ModelRegistry` reads LIVE, so a provider added from the panel is usable with no restart (`ConsoleBrainRegistry` builds brains lazily by provider id). Endpoints `GET/POST/DELETE /api/models` + `POST /api/models/defaults` (human-only; API keys stored 0600, never returned — only a `hasKey` flag). A "Models" panel tab adds providers (DeepSeek/Azure/OpenAI-compatible/Ollama presets) and sets OS defaults; the header badge reflects the real chat default (the legacy `/api/inference/status` badge is retired). Multi-user: `IRuntimeStore.AddUser` (slug-collision-guarded) + `SetupService.AddUser` + `POST /api/users` (human-only) + panel "＋ Add teammate" + CLI `add-user --name … --token …`.

## What shipped (Phase A)

Built and verified end-to-end (unit tests + live HTTP + CLI):

- **Seed refactor (both stores).** Demo population (`joche`/`yulia`) is opt-in via
  `Runtime:SeedDemo` / `LUNOS_DEMO` (default **off**). The spreadsheet singleton is
  created **unconditionally** via a per-entity guard (`!Spreadsheets.Any()`), so a
  provider-free machine boots empty *and* survives repeated boots without a PK
  clash. `EfRuntimeStore(factory, seedDemo=true)` default keeps `PersistenceTests`
  green; `InMemoryRuntimeStore(seedDemo=true)` default keeps the 52 unit fixtures green.
- **`IRuntimeStore.CreateOwner`** (both stores) — one-transaction owner+workspace+agent,
  returns `false` if a user already exists (writes an `owner.claim` audit event).
- **`ITokenAuthenticator.Mint` + `IssueToken`** — `IssueToken` mints *and* writes the
  0600 `<slug>.token` file, so a runtime-created owner has a token file immediately.
- **`SetupService`** (`ISetupService`) — `IsClaimed()` = `Users.Any()`; `Claim(name, fromLoopback)`
  slugifies the name, creates the owner + `-agent` (empty `InferenceProvider`, full
  `OwnerDefaults.AgentTools`), issues both token files. Loopback-gated, name-validated.
- **`AccessPolicy`** whitelists `/api/setup/status` + `/api/setup/claim` (Public).
- **Endpoints** `GET /api/setup/status → {claimed}` and `POST /api/setup/claim {name}`
  → `{slug, token}` / 409 / 403 / 400. Loopback detection unwraps IPv4-mapped IPv6.
- **`local-bonsai` gated on `Inference:Local:Enabled`** (default off) — no phantom
  on-box provider that would fail every turn on a machine with no model server.
- **`UnconfiguredBrain`** — the console fallback when no chat provider resolves
  (non-demo image): ends the turn with an honest "set a key / enable local / restart"
  message instead of a connection error. Demo images keep `RecipeConsoleBrain`.
- **CLI** `workspace-installer create-owner --name "…" [--url http://127.0.0.1:5148]` —
  claims over loopback and prints the owner token.

**Two deliberate deviations from the plan above:**

1. **Single-winner via an in-process lock + store recheck, not a persistent
   `owner_claim` UNIQUE marker.** The runtime is a single process (one Kestrel, singleton
   stores), so a `lock` in `SetupService` fully serializes concurrent claims and the
   store's `Users.Any()` recheck is authoritative — verified by a 16-way parallel claim
   test yielding exactly one owner. This avoids an EF migration and matches the
   single-owner test-release posture. A future multi-process deployment would need the
   marker row (or a DB unique constraint) as the plan describes.
2. **`/api/setup/status` kept Public (not loopback-gated).** It returns a single
   boolean (`claimed`), and keeping it public lets a panel reached without an SSH
   tunnel still show the right screen. The claim itself remains loopback-only.

## Known limitations to put in release notes

Single-owner; provider onboarding is restart-based (edit config, restart); no per-user token revocation (rotating a leaked token rotates the shared key → all tokens); DB reset deliberately re-opens ownership (protect the DB file); remote (non-loopback) web claim is unsupported.
