# Model Configuration & Local Vision Wiring

*Design plan — 2026-08-23. Builds on [model-selection.md](model-selection.md) (provider profiles) and the desktop stack (commits 49ac917 → 68cd63d).*

## Problem

Model selection is fragmented and OS-only:

- **Console brain** picks a provider **per agent** (`runtime_agents.InferenceProvider` → `ConsoleBrainRegistry`: deepseek | gpt-4.1-mini).
- **Desktop vision brain** is a **single global** Azure gpt-4.1-mini config.
- **Local inference** (`LocalInferenceRegistry`, Bonsai-4B) is an **OS-level** registry with provider profiles that already declare capabilities (incl. `vision`) + hardware requirements.

Three systems, three scopes, no shared resolution. And the desktop vision path sends a full screenshot to a cloud model every step (the audit's open finding), which we cannot replace with a local VLM because this hardware (8 GB VM / 16 GB Mac) can't run one well.

## Key insight: AT-SPI-first makes "local vision" mostly "local text"

AT-SPI already yields **exact boxes for app chrome** (menus, buttons, dialogs, toolbars) with no model at all. A screenshot + VLM is only needed as a **fallback** for canvas/icon surfaces. So the **default desktop perception is AT-SPI-only, grounded by a plain text model** (element list → element id) — even a small local model (Bonsai-4B) suffices. No screenshot, no VLM, nothing leaves the box. The VLM becomes an **optional, configurable fallback**, not a requirement. This runs on current hardware *and* closes the screenshot-egress finding by default.

## Decisions (2026-08-23)

- **Config scope: layered — OS → user → agent** (a cascade), not per-OS *or* per-user.
- **Desktop default: AT-SPI-only text grounding**; vision (VLM) is an opt-in fallback.

## Architecture

### Provider profile (capability-tagged)

```text
ProviderProfile {
  id            "deepseek" | "azure-gpt-4.1-mini" | "local-bonsai" | "remote-qwen-vl"
  displayName
  kind          openai-compatible | azure-openai | local-llamacpp | ollama
  baseUrl, model
  capabilities  [ chat, vision, embedding ]      // what it can do
  locality      on-box | remote-self-hosted | cloud
  authSecretRef -> .data/secrets/<...>.env        // null for keyless/local
}
```

### Capability-based resolution, layered

Capabilities: **chat** (reasoning / console brain / AT-SPI grounding), **vision** (screenshot fallback), **embedding** (memory phase-2). Each resolves through a cascade:

```text
resolve(capability, agent) =
     agent override (InferenceProvider)     // finest grain, this run
  ?: user default   (per-user config)       // bring-your-own keys/models
  ?: OS default     (shipped/admin)          // machine baseline, local, no cloud
```

- **OS scope** — distro-shipped profiles + a defaults map `{capability → providerId}`; baseline is local Bonsai for `chat`, **none** for `vision` (AT-SPI-only), none for `embedding`. No cloud out of the box.
- **User scope** — each user registers their own providers (their DeepSeek/Azure keys, their remote VLM) and per-capability defaults, at `.data/models/<user>.json`; keys in `.data/secrets`. This is the "per-user" layer.
- **Agent scope** — the existing `InferenceProvider` field, now "which of my user's providers," falling back user → OS.

`IModelRegistry.Resolve(capability, agent) → ResolvedProvider` (profile + auth loaded from secrets, never logged). All three brains route through it, replacing `ConsoleBrainRegistry`, the global desktop config, and eventually the local-inference selection.

### Config is a surface

A **`models` surface** — `list` (my providers + effective resolution), `set-default {capability, providerId}`, `add-provider {…}` — so config is typed, audited, ownership-scoped, and a user edits **only their own**. Provider **keys** are set through the credential-safe path (a secret write), never as an audited command argument.

### Local vision wiring

The `vision` provider is configurable to point at what the user actually has:

- **remote-self-hosted** — the user's beefier box / home server running Ollama `qwen2.5vl` ("self-hosted, not on *this* machine"),
- **cloud** — gpt-4.1-mini,
- **none → AT-SPI-only** (default).

The desktop brain is a **hybrid**: resolve `chat`, ground on the AT-SPI element list (text, no image); only when the element list is sparse AND a `vision` provider resolves AND egress is permitted, fall back to screenshot + VLM.

### Screenshot-egress permission (closes the audit's last item)

The profile's `locality` decides the gate:

- **on-box / AT-SPI-only** → nothing leaves → no gate.
- **cloud vision** → requires a per-user/session **"screenshot leaves the machine"** consent (same shape as the input grant) before any frame is sent; absent → skip vision, stay AT-SPI-only.

## Phasing

1. **Provider registry v2** — `ProviderProfile` + `IModelRegistry` (OS defaults from config, user config from `.data`, agent override); resolve `chat`; route the console brain + `/v1/agent` through it; migrate existing deepseek/azure into profiles. No behavior change — unified + layered.
2. **AT-SPI-only desktop brain** — new text brain (element list → element id, no screenshot) resolved via `chat`, made the default; keep `ModelDesktopBrain` (vision) as the `vision`-capability fallback. Removes cloud image egress by default.
3. **`models` config surface** + **screenshot-egress permission** + wire the **`embedding`** capability for memory phase-2.

## Non-goals / later

- Not building a model *download/installer* manager here (that's the distro's job; see model-selection.md editions).
- Bonsai's own vision (`vision: false` today) is out of scope; the vision capability is served by a remote/cloud VLM or skipped.
