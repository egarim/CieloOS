# Release Checklist

What must be true before shipping, split by how far the release goes. A **test
release** (trusted testers, disposable VMs) has a lighter bar than a **public /
multi-tenant** release. Items are marked ✅ done · 🟡 in progress · ⬜ open.

## Security — MUST close before a public release

- ⬜ **Default VM credentials.** `distro/autoinstall/user-data` ships
  `workspace / workspace` (SSH password auth on). Dev-only. Before public release:
  generate a per-image password or require SSH keys, and disable password auth.
  *(Acceptable for a closed test release on disposable VMs — flagged here so it is
  never shipped silently.)*
- ✅ **Session ownership at the choke point.** An agent can only observe/drive a
  session it owns (console, desktop, screenshot, elements, input) — enforced in
  `AgentRuntime.SubmitAsync`, fail-closed, covered by tests.
- ✅ **Desktop input consent.** `desktop.type` / `desktop.key` are RequireApproval;
  the per-session **input grant** upgrades them under one-time, time-boxed,
  revocable consent. keysym allowlist (no chords). Injection-hardened prompt.
- ✅ **Screenshot-egress consent.** Default desktop perception is AT-SPI-only
  (nothing leaves the box). A **cloud** vision fallback is wired only after
  `session-input.grant-vision` (time-boxed, human-only). On-box vision needs none.
- ⬜ **Per-session network egress (nftables).** Deny-by-default outbound per agent
  session (V0.6 doc). Containers share the host network today; genuine isolation
  of mutually-distrusting principals is a **bare-metal + `--vm`** guarantee, not a
  shared-kernel container one — do not claim multi-tenant isolation until tested
  on real hardware.
- ⬜ **Token storage.** Per-user bearer tokens live in `.data/secrets/*.token`
  (file perms). Review for a shared/public deployment (short-lived tokens,
  rotation).
- ✅ **Model keys.** DeepSeek / Azure keys read from `.data/secrets/*.env` (0600),
  never logged, never in audited command args.

## Ops / packaging

- 🟡 **Bake the desktop image.** `xdotool` + `scrot` + `python3-gi` +
  `gir1.2-atspi-2.0` + the `lunos-atspi` reader are in
  `distro/images/desktop/Containerfile`; rebuild `localhost/lunos-desktop:latest`
  so fresh sessions have the agent's hands/eyes without a manual step.
- ⬜ **Bake console + desktop images into the distro** so console/desktop sessions
  work on first boot instead of being built ad-hoc (see backlog in ai-native-ui.md).
- ⬜ **Local model on first boot.** `local-inference.service` pulls Bonsai-4B
  if missing; confirm the pull + sha256 verify path on a clean install, or ship
  the Bonsai ISO edition with weights bundled (model-selection.md).
- ⬜ **Postgres vs SQLite.** Default is SQLite; `agent-policy.service` starts
  `After=postgresql`. Decide the shipped default and document it.

## Docs (for the test release) — ✅

- ✅ README (what we are, use case, flow, features, requirements),
  architecture + boot/install diagrams, model-config plan.

## Known limitations to state plainly in release notes

- Desktop **completion quality** depends on the chat model (a small local model
  grounds correctly but may not always recognize "done"); a cloud model is
  stronger. Grounding itself is deterministic (AT-SPI).
- Vision fallback covers canvas/icon surfaces the accessibility tree can't expose;
  it is **cloud** today (opt-in, consented). A local/remote-self-hosted VLM is a
  provider-config choice, not yet bundled.
- Sessions are **rootless podman containers** (dev-and-single-tenant posture), not
  hardware-isolated VMs.
