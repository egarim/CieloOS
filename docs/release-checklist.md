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
  `--ci` completes all 9 stages, stages the first-boot image builder and correctly
  leaves it disabled; `--offline` defers the image build, writes the `podman-restart`
  wants-symlink for `cielo`, the linger marker, and enables runtime/kiosk/image units;
  the ONLYOFFICE package is selected from the target architecture. An installed runtime
  then booted and passed `cielo-selftest --claim` **10/10** (claim, token auth, add a
  provider, provider becomes the chat default, key never returned).
  Still unverified: the image build itself under `install.sh` (it was exercised by hand
  via `podman build`, not through the installer), and `cielo-session-images.service`
  actually firing on a real first boot.
- ✅ **Chat ships with the installer (2026-08-24).** Stage 8 installs Open WebUI as
  `cielo-chat.service` against `/v1/agent`, as the owner, on loopback. Verified on
  arm64 hardware: `HOST=127.0.0.1` makes it listen on loopback only and the LAN
  address refuses; the runner refuses an unclaimed box, then resolves the owner and
  its token after a claim; `/api/setup/status` returns the owner to a loopback caller
  and `null` to a remote one, which still cannot claim.
  Still unverified: the service actually starting on a real first boot (the image
  pull happens there), and a full chat round-trip through Open WebUI's own UI —
  the wiring was verified against `/v1/agent/models` with the same token instead.
- ✅ **The desktop looks like CieloOS (2026-08-24).** The session image seeds a
  light, macOS-adjacent look from Ubuntu packages only (Orchis-Light, Papirus-Light,
  Breeze_Light, Inter, Plank) plus a vector wallpaper rasterised at build. Verified
  by screenshot on arm64 across seven builds, and the seed is a first-run default:
  a session whose theme was changed by hand kept that change across a restart.
  Two things this cost, both recorded because they will bite again: the wallpaper
  key is named after the *monitor*, whose name is decided at session start
  (`screen` here, `selkies-primary` in an older config), so it is set at runtime
  from `xrandr` rather than seeded; and Plank reads **dconf**, not the settings
  file it writes, so its theme ships as a dconf system default.
  Still open: a screenshot smoke test, so a webtop base-image update cannot
  silently undo the layout.
- ✅ **Desk profiles (2026-08-24).** A user is created as an office, .NET developer
  or marketing desk; the profile decides the session image, the agent's tool grant
  and the home seeding, and is recorded on the user and in the audit trail.
  Verified on arm64 against the live instance: the existing owner migrated to
  `office`, a new `dotnet` desk started its session from
  `localhost/cielo-desk-dotnet:latest`, and inside it `dotnet new unoapp` produces
  a solution with the Uno extension already in VS Code.
  Two things worth remembering, both invisible until tested: `dotnet new install`
  is **per user**, so templates installed by root at build time were missing for
  the desk user until `DOTNET_CLI_HOME` moved that state somewhere shared; and VS
  Code's launcher refuses to run when it sees WSL in `/proc/version`, which the
  container inherits from the build host.
  A desk is two images — the desktop the person uses and the console their AGENT
  works in — because a .NET desk whose console lacked the SDK would give the
  toolchain to the human and withhold it from the machine.
  Still open: the marketing images have never been built (they exist as layers and
  build on demand), and switching an existing desk's profile is not implemented.
- ✅ **Model spend metered and capped (2026-08-24).** Every provider call is
  recorded with its token counts against (user, agent, provider, model), and
  monthly ceilings per desk / agent / machine stop a run before the call is made.
  Verified against a real DeepSeek key on the live instance: a chat recorded
  1,121 + 61 tokens; a ceiling below that spend produced *"The model budget for
  your desk is used up for this month (1,182 of 500 tokens)"* as the agent's
  reply with spend unchanged — the refused call was never made; raising the
  ceiling restored service; an agent asking to change a ceiling got 403; both
  changes appear in the audit trail.
  Two things worth remembering: SQLite cannot compare a `DateTimeOffset` in a
  query, so the month is stored as an explicit `yyyy-MM` key rather than derived;
  and metering had to be wired at the HTTP handler because the three brains each
  build their own request, so a per-brain hook would be three places to forget.
  The ceiling holds back headroom for one more call rather than reserving spend,
  so it stops slightly early instead of overshooting. It is **not** atomic: two
  runs starting at the same instant can both pass the check, and each may then
  spend one call's worth. A true reservation needs the provider to price a call
  before charging it.
  Still open: cost estimates in money (needs a per-model price table), and a
  rollup if a machine ever makes enough calls for summing to hurt.
- ✅ **Automated Linux test.** `distro/scripts/test-install.sh` runs install + the
  full first-run self-test in `ubuntu:24.04` — 12/12 pass on native Linux, and it
  asserts the chat is installed, loopback-bound, follows a moved port on reinstall,
  and is genuinely removed by `--no-chat`.
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
- ✅ **Login, sessions and revocable keys (2026-08-24).** A password (PBKDF2-SHA256,
  per-password salt) proves who a person is; a server-side session in an httpOnly
  SameSite=Strict cookie carries that and can be ended, including everywhere at
  once; named API keys let a program act without holding anyone's own credential,
  and the packaged chat now mints its own instead of using the owner token.
  Cookie auth is only honoured with a panel header, which is what stops it being
  a CSRF hole. Verified on the live instance: identical messages for a wrong
  password and an unknown desk, an httpOnly cookie invisible to JavaScript,
  the same cookie refused without the header, sign-out returning 401 immediately
  after, an agent refused a key (403), and a revoked key dead on the next call.
  Deliberately not done in this pass: passkeys/WebAuthn (needs HTTPS and a
  domain; the default deployment is plain HTTP on loopback), MFA, per-key scopes
  narrower than "acts as this person", and rate limiting on login — a local
  attacker can still guess as fast as PBKDF2 allows (~100 ms per try).
- ⬜ **Identity tokens are still eternal.** They remain how agents authenticate
  and cannot be revoked individually; rotating `signing.key` invalidates every
  identity at once. The login work above removes the need for a person to carry
  one, but does not fix the token itself.
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
- 🟡 **`browser` surface, phase 1 (#16).** `lunos-browser` is in the desktop
  Containerfile, so a rebuilt `localhost/lunos-desktop:latest` carries it; the
  profile images inherit it through `FROM ${BASE}`. Open, and deliberately not
  done in phase 1:
  - **An existing agent does not get the grant.** `OwnerDefaults.AgentTools` gains
    `browser`, but that only applies when an identity is created, so on a machine
    that already exists every agent is denied `browser` until its
    `GrantedToolsJson` is updated. There is no endpoint for this and no
    reconciliation on startup — widening an agent's capabilities during an upgrade
    is the owner's decision, not a migration's.
  - **No egress allowlist yet.** `navigate` is `RequireApproval` for every URL,
    which is safe but means routine browsing asks a human every time. The per-desk
    domain allowlist (phase 2) is what turns the common case back into `Allow`.
  - **Clicks are confined to the current origin.** Every request type is
    intercepted for the duration of a click — `fetch`, XHR, `sendBeacon` and image
    pixels included, not just navigations — and popups cannot be created at all
    (`--block-new-web-contents`), so leaving a site goes through the
    approval-gated `navigate`. `navigate` in turn refuses a cross-origin
    **redirect**: a human approves a destination, and a server must not get to
    pick a different one afterwards.
    Two things this does NOT cover, deliberately:
    - **Same-origin submission.** A click that posts to the site the agent is
      already on is indistinguishable from ordinary use of that site. Read "the
      agent may visit X" as "the agent may post to X".
    - **Anything the page does after the command ends.** This is the structural
      one. The helper is one process per command, so CDP interception is torn down
      when it exits: ambient traffic, a `setTimeout` scheduled by a click, or a
      delayed `location` change all run with nothing watching. Confinement bounds
      what the agent's action DOES, not what the page does afterwards.
- ⬜ **Egress proxy for the agent browser (phase 2, and the real fix).** Launch the
  agent's Chromium behind a local proxy in the session container
  (`--proxy-server=127.0.0.1:<port>`) that enforces the per-desk allowlist for
  every request, including CONNECT for TLS. This is the only enforcement that
  outlives a command, and it subsumes four separate holes review found one at a
  time in the CDP-window approach: cross-origin redirects, popups, delayed
  requests, and ambient page traffic. Until it exists, "navigation is the
  human-approved egress decision" is true *of the agent's commands*, not of the
  browser as a whole — say so in release notes rather than implying more.
  - **No `type` on the web.** Filling a form is phase 2, behind the existing
    `ISessionInputGrants` lease.
  - **No auto-waiting beyond the load event.** The helper waits for
    `Page.loadEventFired` plus a short settle; a client-rendered page that paints
    late can be read a beat early. This is the visible cost of not shipping
    Playwright — revisit with numbers if it bites.
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
