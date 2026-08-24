# CieloOS

**An operating system built to be operated by AI.**

Not by bolting automation onto a GUI, but by making automation unnecessary: humans and AI agents emit the **same typed, policy-checked commands onto one bus**, and every action — a human click, an agent's API call, a keystroke into a console, a click on a desktop — passes **one policy engine** (`Allow` / `Deny` / `RequireApproval`) and lands on **one audit trail**. The UI is a projection of that contract; software joins CieloOS by speaking the contract, not as an opaque app.

> Thesis: **typed where possible, pixels where necessary, policy everywhere.**

*(“CieloOS” is the product brand; the code uses neutral `WorkspaceRuntime` identifiers so the name can change without refactoring. Branding loads from `config/branding.json`.)*

---

## The problem we solve

AI agents that "use a computer" today drive it through **screenshots and guessed pixel clicks** — unreliable, ungoverned, and unauditable. You can't see what the agent is allowed to do, prove what it did, or stop it typing a command an on-screen popup told it to.

CieloOS makes an agent a **first-class, governed OS citizen**:

- It acts through the **same command bus** a human does — so ownership, policy, and audit apply to *every* action, not just the ones an app chose to expose.
- It grounds on the desktop's **accessibility tree** (exact element boxes) first, using pixels only as a fallback — so clicks are precise, not guessed.
- Dangerous actions (typing, keystrokes) require the owner's consent; a **time-boxed input grant** lets the agent work autonomously under one-time approval, revocable at any moment.

**Who it's for:** anyone who wants an **AI coworker on a real desktop** — doing research, driving apps (spreadsheets, office docs), operating a console — where the human *owns* the agent, *consents* to what it touches, and can *audit* everything it did.

## How it works (the flow)

Everything funnels through one choke point (`AgentRuntime.SubmitAsync`): it binds the acting agent to the requesting user, checks the manifest policy, enforces session ownership, consults input grants, and appends a dual-actor audit event.

- **[docs/architecture-diagram.md](docs/architecture-diagram.md)** — the system map (bus, surfaces, sessions, brains, models, identities).
- **[docs/boot-and-install-flow.md](docs/boot-and-install-flow.md)** — install (autoinstall → services), every-boot bring-up, and session-create on demand.

```text
Human / Agent ─▶ SubmitAsync ─▶ ownership + policy + input-grant ─▶ executor ─▶ audit
                                     │                                 │
                                  surfaces                         session containers
                          (spreadsheet · session ·             (console: ttyd+tmux |
                           console · desktop · session-input)    desktop: XFCE+Selkies)
```

## Features (what works today)

**Identity, policy, audit**
- Human identities each **own** an agent — you **claim** the first owner on the box (provider-free, no hardcoded users). The acting user/agent is derived from a per-identity bearer token, never the request body. An agent may only act as itself; a human only through agents it owns; approvals are human-only.
- **Surfaces** (schema-2 manifests in `surfaces/`): `spreadsheet`, `session`, `console`, `desktop`, `session-input`. One `ManifestPolicyEngine` is the sole policy source; mutating commands are `RequireApproval`, bound to a **hash of the exact previewed request**.
- A full **audit log** with **dual-actor** attribution (`joche → joche-agent`) and an **input ledger** (exact console text, desktop click coordinates, keystrokes).

**Sessions**
- One rootless **podman** container per session over a **persistent per-owner home volume** (+ a shared owner↔agent volume). Two kinds: **console** (ttyd + tmux) and **desktop** (webtop XFCE + Selkies, with ONLYOFFICE and the agent's hands/eyes: `xdotool`, `scrot`, AT-SPI).
- **Inhabiting** — an owner can *shadow* or *become* an owned agent's session, dual-actor audited.

**Agents that do real work**
- **Console loop** — the agent operates its own console (observe `tmux capture-pane` → decide → type `tmux send-keys`), every keystroke a governed `console.type`. Real tools: `curl`, `jq`, `python3`+`openpyxl`, `python-docx/pptx`, and a private **web search** (self-hosted **SearXNG**) — e.g. *"search the top posts about X and make a spreadsheet."*
- **Desktop loop** — the agent uses the GUI: **AT-SPI-first grounding** (exact element boxes → click the element, not a guessed pixel), with a **vision-model fallback** only for what the accessibility tree can't see. Clicks are autonomous; **typing/keys require the owner's consent**.
- **Input grant** — a human leases input on a session for N minutes; while live, the agent **types autonomously**; revocable, time-boxed, audited (the V0.6 consent model).

**Chat**
- **Open WebUI**, installed and started by `install.sh` against the OpenAI-compatible `/v1/agent` endpoint and authenticated as the owner: every message runs the console loop, so the agent uses its tools and operates the OS, and the reply streams back as it works. Loopback-only until there is a login (issue #9) — tunnel to reach it from elsewhere.

**Models — layered & replaceable** ([docs/model-config.md](docs/model-config.md))
- One capability-based registry resolves a provider per capability (**chat / vision / embedding**) through a cascade **agent → user → OS**. Providers are tagged by capability and **locality** (`on-box` / `remote-self-hosted` / `cloud`).
- Ships with **DeepSeek** and **Azure OpenAI gpt-4.1-mini** (cloud) and **local Bonsai-4B** (llama.cpp, on-box). AT-SPI-first means the **default desktop path needs no vision model and nothing leaves the box**.

**The panel** — an agent-desk: a rail of your desks (you + owned agents), each anchored on its home (files, sessions, activity, pending approvals), plus a "give the agent a task" console with a live view. Files the agent produces can be **downloaded** from the browser (byte-for-byte, policed and audited like any other read); binary files say so instead of previewing as noise.

## Requirements

**To run the OS (installed / appliance):**
- Ubuntu 24.04+ (amd64 or arm64), **podman**. **No .NET** — the release bundle is self-contained.
- Or **Windows via WSL2** (including Windows-on-ARM) — see [docs/wsl-quickstart.md](docs/wsl-quickstart.md).
- **Provider-free by default** — bring your own model key and add it from the panel's Models tab.
- **~4 GB RAM** and up (more only if you run a local model, e.g. Bonsai). CPU-only is fine (no GPU required).
- Optional: cloud model keys (DeepSeek / Azure OpenAI) for stronger reasoning/vision — or run **fully local** with Bonsai.

**To develop (or to build a bundle / USB):**
- macOS (Apple Silicon) or Linux, **.NET 10 SDK**, **Node** (for the Vite panel), and for the USB/VM tooling: **QEMU**, `xorriso` (see `distro/scripts/check-host.sh`).

## Run it

**Develop** — backend + panel from source on your machine:

```bash
./scripts/dev.sh
```

- Backend API: http://127.0.0.1:5148/api/branding
- Panel: http://127.0.0.1:5173

`dev.sh` prints joche's token; each identity has one at `.data/secrets/<slug>.token`. Tests: `./scripts/test.sh`.

**Install the release bundle** — self-contained, so the target needs no .NET. Build it on a machine with the dev prerequisites, carry the tarball to any Ubuntu 24.04+ box, then run the bundled `install.sh`:

```bash
bash distro/scripts/build-release.sh linux-x64   # or linux-arm64 → release/cielo-<arch>.tar.gz
# on the target:
tar xzf cielo-linux-x64.tar.gz
sudo ./cielo/install.sh --mode app               # app | headless | kiosk
```

- **app** — your own machine; panel on loopback only.
- **headless** — a VPS / old box; binds all interfaces, reach it with a token.
- **kiosk** — boots into a fullscreen panel browser.

**Run it as an app, no root and no systemd** — the same bundle, foreground, control-plane state in `cielo/.data` (podman keeps any session volumes outside it). This is the WSL2 path (including Windows-on-ARM: build `linux-arm64`):

```bash
tar xzf cielo-linux-<arch>.tar.gz               # linux-arm64 on ARM, linux-x64 on Intel/AMD
./cielo/run.sh                                  # → http://localhost:5148/
```

See [docs/wsl-quickstart.md](docs/wsl-quickstart.md) to go from a stock Windows machine to the claim wizard.

**Autoinstall USB** — a bare-metal appliance that installs Ubuntu + CieloOS unattended (**erases the target disk**):

```bash
bash distro/scripts/build-usb.sh --iso ubuntu-24.04-live-server-amd64.iso --mode kiosk
# → release/cieloos-usb.iso ; flash to USB with dd, boot the target
```

**First run — claim your machine, then add a provider.** CieloOS ships provider-free with no hardcoded users, and the first-owner claim is loopback-only (nobody on the network can claim your box):

- **app / kiosk:** open http://127.0.0.1:5148/ on the box → the claim wizard.
- **headless:** `ssh` in and run `cielo-claim "Your Name"` — it prints your bearer token; then open http://<host-ip>:5148/ from your laptop and sign in with it.

Then add an AI provider from the panel's **Models** tab (works immediately, no restart). Verify anytime with `cielo-selftest` (non-destructive). Full detail: [distro/RELEASE-README.md](distro/RELEASE-README.md).

## Where it's going

Design laws, decisions, and the milestone roadmap live in [docs/ai-native-ui.md](docs/ai-native-ui.md). Near-term: the per-user **models config surface** and a **"screenshot-leaves-the-machine" consent** for cloud vision (model-config.md Phase 3); agent **memory** on the audit substrate; and the move from podman to **Incus** system containers (same command shape, only the executor moves).

## Documentation

| Doc | What |
|---|---|
| [architecture-diagram.md](docs/architecture-diagram.md) | System map — the one bus and everything on it |
| [boot-and-install-flow.md](docs/boot-and-install-flow.md) | Install, boot, and session-create flows |
| [model-config.md](docs/model-config.md) | Layered, capability-based model configuration |
| [ai-native-ui.md](docs/ai-native-ui.md) | Design laws, decisions, roadmap |
| [architecture.md](docs/architecture.md) · [local-dev.md](docs/local-dev.md) | Runtime internals · running & auth |
| [wsl-quickstart.md](docs/wsl-quickstart.md) | Run it as an app on Windows (incl. ARM) via WSL2 |
| [hands-and-eyes.md](docs/hands-and-eyes.md) · [model-selection.md](docs/model-selection.md) | Desktop control · model/provider profiles |
