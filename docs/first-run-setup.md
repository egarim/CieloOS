# First-Run Setup & Provider-Free Install

*Design plan — 2026-08-23. Closes the onboarding gap: today users are hardcoded demo seed (`joche`, `yulia`) authenticated by token files, and there is no step that creates YOUR owner or lets you run the OS with no AI provider.*

## Goals

1. **Install with no AI provider.** Stand up the whole OS — identities, sessions, surfaces, files, console + desktop, the panel — with **no local or cloud model**. Providers are added later; until then the agent's autonomous loops are cleanly disabled, not broken.
2. **A real owner, not demo seed.** A fresh machine has **no users**; a first-run **setup** creates the first owner (and their agent). `joche/yulia` become opt-in demo data.
3. **Simplest possible install.** Boot → open the panel → create your owner → (optionally) add a provider later.

## Current state (what we're changing)

- `RuntimeSeed.People()` seeds `joche` + `yulia` unconditionally on first run (`EnsureCreatedAndSeeded`); the audit logs *"Seeded demo users."*
- Auth is **bearer tokens** minted at startup to `.data/secrets/<slug>.token` — no passwords. A human pastes their token into the panel.
- `local-inference.service` runs Bonsai on boot (pull-if-missing); Phase-1 always registers a `local-bonsai` provider, so with no model running the agent loop errors with a confusing *"model error"*.

## Design

### 1. Claim model (first-run bootstrap)

A machine is **unclaimed** when `IRuntimeStore.Users` is empty. On first boot while unclaimed, the runtime generates a one-time **setup token**, writes it `0600` to `.data/secrets/setup.token`, **and logs it to the console/journal** so only someone with machine (console/SSH) access can read it. The setup token is the bootstrap credential — it proves physical/admin control and prevents a network stranger from claiming the machine.

```text
first boot, no users
  -> generate setup token -> .data/secrets/setup.token (0600) + console log
  -> panel shows "Set up Lun.Os" (unclaimed)
  -> operator reads the token from the console, enters name + token
  -> owner + owner-agent created, owner token minted, setup token destroyed
  -> claimed: normal login from here on
```

### 2. Setup surface (web + CLI)

Two front-ends over one runtime path; both provider-free.

- **`GET /api/setup/status`** (public, unauthenticated): `{ claimed: bool }`. The only unauthenticated endpoint besides branding — reveals nothing sensitive.
- **`POST /api/setup/claim`** (guarded by the setup token, works **only while unclaimed**): `{ name, setupToken }` → validates the token, creates the owner `PlatformUser` + a `Workspace` + an `AgentProfile` (default granted tools, `InferenceProvider` empty), mints the owner's token, deletes `setup.token`, returns `{ slug, token }`. Idempotency: once claimed, always `409`.
- **Web wizard** (panel): when `status.claimed == false`, the panel renders a "Welcome — create your account" screen (name + setup token) instead of the token-login. On success it stores the returned owner token and proceeds. For humans.
- **CLI** `workspace-setup create-owner --name "…"`: runs on the box, reads `.data/secrets/setup.token` locally (no need to copy it), claims, prints the owner token. For headless installs. (Sits beside `workspace-installer`, which handles the *Ubuntu* install.)

### 3. Demo seed becomes opt-in

`RuntimeSeed` runs **only** when `LUNOS_DEMO=1` (env/config). Default install: **no users → unclaimed → setup**. Tests and demos set the flag. `EnsureCreatedAndSeeded` gates the seed; the in-memory store keeps seeding (tests depend on it) or takes the same flag.

### 4. No-provider agent state (clean, not broken)

When no **reachable** chat provider resolves, the agent brain is an explicit **`UnconfiguredBrain`** that returns one clear step — *"No AI provider is configured. Add one in Settings (DeepSeek / Azure key, or a local model)."* — instead of a `model error` or the demo recipe. Concretely:

- `local-bonsai` is registered as a provider **only if the local model is actually enabled/installed** (not merely by default), so a Core install doesn't advertise a brain it can't run.
- If the resolved provider is unreachable at call time, surface the same "add a provider" guidance rather than a raw error.
- `local-inference.service` and the model download are **optional**: the runtime boots and the control plane works with zero providers.

### 5. Providers added later

The owner adds providers when ready via the **models surface** (model-config.md Phase 3): DeepSeek/Azure keys, or a local/remote model. Until then, everything except autonomous agent reasoning works. Auth stays **bearer-token** for this release; passwords/passkeys are a later, separate decision.

## Security notes

- The **setup token** is the crux: without it, a network-reachable panel on a fresh machine could be claimed by a stranger. Requiring a token that is only visible on the machine's console/journal binds "claim" to machine access. Consider *also* binding `/api/setup/claim` to loopback until claimed, as defense in depth.
- `/api/setup/status` is deliberately minimal (`claimed` boolean only).
- Once claimed, `setup.token` is destroyed and `/api/setup/claim` fails closed forever (until the DB is reset).
- This is **separate** from the VM's OS login (`workspace/workspace`, dev-only — see release-checklist.md).

## Phasing

- **A. Runtime core:** gate the demo seed behind `LUNOS_DEMO`; first-run detection; setup-token generation + console log; `/api/setup/status` + `/api/setup/claim`; the `workspace-setup` CLI. **Provider-free end to end.**
- **B. Panel wizard:** the unclaimed → "create your account" screen.
- **C. Unconfigured-provider state:** `UnconfiguredBrain` + make `local-bonsai`/model-service optional + friendlier messaging.

## Open questions

- **Recovery** if the owner token is lost — re-issue via the CLI on the box (machine access), or a reset command? (Proposed: CLI re-mint from the box.)
- **Multiple owners** — is claim strictly the *first* user, with later users added by an owner through a `users` surface (future)? (Proposed: yes.)
- **Should Core still bundle Bonsai** so "add a provider" can be one click to the local model, or stay truly weightless? (Proposed: Core weightless; Bonsai edition bundles it.)
