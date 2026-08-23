# Release Checklist

What must be true before shipping, split by how far the release goes. A **test
release** (trusted testers, disposable VMs) has a lighter bar than a **public /
multi-tenant** release. Items are marked ✅ done · 🟡 in progress · ⬜ open.

## Chosen target for the FIRST release — CieloOS (2026-08-23)

**Dogfood · bare-metal USB · provider-free.** Audience is just the author, so the
Security items below are documented limitations, not blockers. The product is
**CieloOS**; the runtime is identical across three deploy shapes and only the
"presentation mode" differs — `app` (localhost browser), `headless` (VPS/old
machine, browser + token), `kiosk` (boots into a fullscreen panel browser). The
model story is provider-free (add your own key in the Models tab, no restart).

- ✅ **Runtime self-serves the panel.** `Panel:Path` (default `<root>/panel`) served
  ahead of the auth gate, so a booted machine lands on the first-run wizard with no
  dev machine. Verified: `GET /` → SPA, assets load unauthenticated, API unaffected.
- ✅ **Self-contained release bundle.** `distro/scripts/build-release.sh
  [linux-x64|linux-arm64]` → a ~50M tarball (runtime + panel + surfaces + config;
  no .NET on the target, `InvariantGlobalization` so no libicu). Provider-free,
  SQLite. Verified: real ELF x86-64 output.
- ✅ **Mode-aware installer.** `distro/install.sh --mode <app|headless|kiosk>` (live)
  + `--ci` (container tests) + `--offline` (autoinstall in-target). Creates the
  `cielo` service user (rootless-podman prereqs), runs `cielo-runtime.service`,
  drops `cielo-claim`/`cielo-add-user`/`cielo-selftest`.
- ✅ **Run-as-an-app / WSL2 path.** `run.sh` ships in the bundle: foreground, no root,
  no systemd, state in `<bundle>/.data`. **Verified on a real Windows-on-ARM Surface**
  (2026-08-23): built `linux-arm64` in WSL2 Ubuntu 24.04 (aarch64), ran `./cielo/run.sh`
  as a non-root user, reached the claim wizard from the Windows browser at
  `http://localhost:5148/` (WSL forwards loopback, so the claim gate is satisfied);
  `cielo-selftest` 4/4. See docs/wsl-quickstart.md.
- ⬜ **Session defaults are amd64-only.** Verified on arm64: `Sessions:Image` defaults to
  `accetto/ubuntu-vnc-xfce-g3:latest` (published for amd64 ONLY) and `Sessions:ViewportPort`
  defaults to `6901`, while `distro/images/desktop/Containerfile` builds on webtop, which
  serves Selkies on **3000**. So on any ARM host (WSL2, Apple Silicon) desktop sessions
  cannot start, and the viewport port would be wrong even if they did. Rootless podman
  itself is fine on arm64/WSL (no systemd, no XDG_RUNTIME_DIR needed); with
  `Sessions__Image=lscr.io/linuxserver/webtop:ubuntu-xfce Sessions__ViewportPort=3000` a
  `human-desktop` session starts and serves. Pick a multi-arch default and align the port.
- ✅ **Installer verified on a clean machine (2026-08-24).** Built a bundle from main
  and ran `install.sh` in throwaway `ubuntu:24.04` containers on arm64:
  `--ci` completes all 8 stages, stages the first-boot image builder and correctly
  leaves it disabled; `--offline` defers the image build, writes the `podman-restart`
  wants-symlink for `cielo`, the linger marker, and enables runtime/kiosk/image units;
  the ONLYOFFICE package is selected from the target architecture. An installed runtime
  then booted and passed `cielo-selftest --claim` **10/10** (claim, token auth, add a
  provider, provider becomes the chat default, key never returned).
  Still unverified: the image build itself under `install.sh` (it was exercised by hand
  via `podman build`, not through the installer), and `cielo-session-images.service`
  actually firing on a real first boot.
- ✅ **Automated Linux test.** `distro/scripts/test-install.sh` runs install + the
  full first-run self-test in `ubuntu:24.04` — 10/10 pass on native Linux.
  `distro/scripts/test-install-vm.sh` runs the x86-64 bundle in a full-system qemu
  VM (definitive amd64; Docker's user-mode emulation FailFasts in .NET EH, a QEMU
  artifact — not a defect).
- 🟡 **Autoinstall USB (end goal).** `distro/scripts/build-usb.sh --iso <live-server>`
  remasters the ISO to install Ubuntu + CieloOS unattended (`install.sh --offline`).
  Config + GRUB patch validated; a full install-boot is the heavy end-to-end check.
- ⬜ **Sessions on first boot.** Console + desktop podman images build on first use;
  prebuild/bake still open (rootless-podman-under-systemd validated only on the target).

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

## First-run setup / install

- ✅ **Provider-free install.** The OS boots with **no AI provider** — identities,
  sessions, surfaces, files, panel all work; an unconfigured agent returns an
  honest "set a key and restart" message (`UnconfiguredBrain`), not an error.
  Demo users (`joche`/`yulia`) are opt-in (`Runtime:SeedDemo`/`LUNOS_DEMO`, default off).
- ✅ **First owner claim.** A fresh machine has no users; `POST /api/setup/claim`
  (loopback-only, single-winner) creates the owner + agent + token. CLI:
  `workspace-installer create-owner --name "…"`. See docs/first-run-setup.md.
- ✅ **Panel wizard (Phase B).** The panel checks `/api/setup/status` while signed
  out: unclaimed → a "Claim this machine" wizard (name → claim → straight into the
  app); claimed → token login. Verified end-to-end in the browser.
- ✅ **Add a teammate.** An owner adds another user from the panel (desks rail →
  "+ Add teammate") or CLI (`workspace-installer add-user --name … --token …`);
  human-only, returns the new user's token to hand over. Multi-user, not just single-owner.
- ✅ **In-panel model providers (models surface).** A "Models" tab lists providers
  and adds one (DeepSeek / Azure / OpenAI-compatible / Ollama presets) with base
  URL + model + key; usable immediately, **no restart**. Keys are stored 0600 and
  never returned by the API. OS defaults per capability are set from the panel.

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
