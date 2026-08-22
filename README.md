# Lun.Os

Lun.Os is the product brand for a rename-safe workspace runtime prototype. The
implementation uses neutral `WorkspaceRuntime` identifiers so the commercial name
can change later without invasive refactoring; branding is loaded from
`config/branding.json`.

Lun.Os is an **operating system built to be operated by AI** — not by making a
GUI automatable, but by making automation unnecessary: humans and agents emit the
**same typed, policy-checked commands onto one bus**, and the visible UI is a
projection of that contract. Software joins Lun.Os by speaking the contract
(tools and surfaces), not as opaque GUI apps. Every action — a human click, an
agent's API call, a keystroke into a console — passes one policy engine
(`Allow` / `Deny` / `RequireApproval`) and lands on one audit trail.

Thesis: **typed where possible, pixels where necessary, policy everywhere.** See
[`docs/ai-native-ui.md`](docs/ai-native-ui.md) for the design laws, decisions,
and roadmap.

## What works today

- **Identity & ownership.** Real distinct identities (joche, yulia), each
  **owning** an agent. Per-identity file-backed bearer tokens; the acting
  user/agent is derived from the token, not the request body. An agent may only
  act as itself; a human only through agents it owns; approvals are human-only.
- **Surfaces** (schema-2 manifests under `surfaces/`): `spreadsheet`, `session`,
  and `console`. `ManifestPolicyEngine` is the single policy source; commands
  carry `RequireApproval` where they mutate; approvals are **hash-bound** with
  dry-run **effect-diff previews**. Revisioned state + ETag + SSE (`/api/events`).
- **Sessions.** One rootless **podman** container per session over a **per-owner
  persistent home volume** (the home is the primitive; a session is a view of
  it). Two kinds: a **console** (ttyd + tmux) and a **desktop** (webtop XFCE).
  **Inhabiting** — an owner can *shadow* or *become* an owned agent's session,
  recorded with **dual-actor** audit (`joche → joche-agent`).
- **The agent-desk panel.** A left rail of your desks (you + the agents you own),
  each anchored on its home: files, sessions, activity, and pending approvals.
  A **"give the agent a task"** console with a live screen view.
- **Agent-driven console loop.** An agent operates its **own** console —
  observe the screen (`tmux capture-pane`), decide, type (`tmux send-keys`) —
  where every keystroke is a policy-checked, audited `console.type`. The brain is
  **pluggable** (`IConsoleAgentBrain`): a cloud model (DeepSeek, OpenAI-compatible)
  today, local models planned. The console sandbox has real tools (`curl`, `jq`,
  `w3m`, `python3` + `openpyxl`) and a `websearch` command backed by a
  self-hosted **SearXNG** service, so it does real web work (e.g. *"search the
  top 10 posts about El Salvador and make an Excel file"*) with real data.
- **Persistence & audit.** SQLite by default, PostgreSQL via configuration (see
  [`docs/local-dev.md`](docs/local-dev.md)); a full audit log with dual-actor
  attribution.

## Run locally

From the repository root:

```bash
./scripts/dev.sh
```

- Backend API: http://127.0.0.1:5148/api/branding
- Frontend panel: http://127.0.0.1:5173

`dev.sh` prints joche's session token; each identity has its own at
`.data/secrets/<slug>.token`. Run the tests with `./scripts/test.sh`.

## Where it's going

- **Hands and eyes** ([`docs/hands-and-eyes.md`](docs/hands-and-eyes.md)) —
  step 1 (console tools + private search) ships today; step 2 is a **local-only
  desktop-control agent** (AT-SPI-first perception + a vision-model fallback,
  perception→text→decision, a `/dev/uinput` injection daemon, Ollama models) so
  the agent uses a real desktop like a person.
- **Live image** ([`docs/live-image.md`](docs/live-image.md)) — a bootable,
  multi-arch (x86-64 / Raspberry Pi / Apple-Silicon-in-UTM) Lun.Os to run on real
  hardware.
- The production session backend moves from podman to Incus system containers;
  the command shape and policy path are unchanged (only the executor moves).

## Distro (early)

The Linux distribution work lives under `distro/` (an Ubuntu profile, session
image `distro/images/console/`, the SearXNG service `distro/services/searxng/`,
and neutral system services). Agent-guided setup uses the `workspace-installer`
CLI; the agent never receives a free-form installer shell — approval is a
separate operation reserved for the trusted onboarding surface (see
[`docs/agent-guided-installation.md`](docs/agent-guided-installation.md)). This
layer is early and still aspirational relative to the runtime above.
