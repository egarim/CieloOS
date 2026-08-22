# Lun.Os V0.1

Lun.Os is the current product brand for a rename-safe workspace runtime prototype. The stable technical implementation uses neutral `WorkspaceRuntime` identifiers so the commercial name can change later without invasive refactoring.

Lun.Os is an operating system built to be operated by AI — not by making its UI automatable, but by making automation unnecessary: it is designed so that humans and agents emit the same typed, policy-checked commands onto one bus, with the visible UI as a projection of that contract. Software joins Lun.Os by speaking the contract (tools and surfaces), not as opaque GUI applications. See `docs/ai-native-ui.md` for the design laws, decisions, and roadmap.

## What is included

- ASP.NET Core backend API
- React + TypeScript + Vite frontend
- Ubuntu-based distro profile under `distro/`
- replaceable local-model provider profiles under `distro/models/providers/`
- .NET/PowerShell automation profile under `distro/profiles/`
- .NET 10 SDK and checksum-pinned PowerShell 7.6.4 ARM64 setup payload
- Rename-safe branding loaded from `config/branding.json`
- Multi-user-aware demo model
- Agent runtime with a pluggable inference provider interface
- Interactive `workspace-agent` CLI for provider-neutral local chat
- Verified `workspace-model` installer for the selected local provider
- Policy decisions: `Allow`, `Deny`, `RequireApproval`
- Structured spreadsheet tool requests
- Sandboxed executor abstraction
- Approval flow
- Audit log
- SQLite persistence by default, PostgreSQL via configuration (see `docs/local-dev.md`)
- Human/agent principal split with file-backed bearer tokens; approvals are human-only
- Surface contracts (`surfaces/*.surface.json`, schema 2): revisioned state, typed commands, dry-run previews, progressive disclosure
- Hash-bound approvals with effect-diff preview cards
- `workspace-agent observe` / `do` verbs driving the same command bus as the panel
- Constrained-decoding pass-through (`response_format`) to the local llama.cpp provider
- Deterministic agent-guided installer CLI
- Non-persistent VM trial mode
- Backend and frontend tests

## Run locally

From the repository root:

```bash
./scripts/dev.sh
```

Then open:

- Backend API: http://127.0.0.1:5148/api/branding
- Frontend: http://127.0.0.1:5173

Run verification:

```bash
./scripts/test.sh
```

## Run the installer on Apple Silicon

The V0.1 VM workflow uses QEMU with Apple's hardware acceleration. It opens a normal ARM64 VM window so the operating-system setup is visible.

The VM opens full-screen with zoom-to-fit enabled. Leave full-screen from QEMU's View menu to use a window; resizing the window scales the guest display. The Linux console uses a larger font for readability.

Prepare the installer, VM disk, autoinstall answers, and runtime payload:

```bash
./distro/scripts/vm-prepare.sh
```

Open the installer:

```bash
./distro/scripts/vm-run.sh --install
```

After setup completes and QEMU closes, boot from the installed disk:

```bash
./distro/scripts/vm-run.sh --installed
```

Try the prepared system without keeping any changes:

```bash
./distro/scripts/vm-run.sh --try
```

Try mode uses a temporary QEMU snapshot. The reference disk remains unchanged when the VM shuts down.

The development account and password are both `workspace`. SSH is available on host port `2222`, and the runtime API is forwarded to host port `5148`. This password is only for the disposable development image and must be replaced before release.

Generated VM media and disks live under `distro/.vm/` and are intentionally ignored by Git. The installer media includes the verified PowerShell archive, so PowerShell setup does not depend on GitHub during first boot.

## Run the local agent

Inside an installed VM, check provider readiness:

```bash
workspace-agent status
```

On a fresh Core installation, install the selected provider runtime and model weights once:

```bash
sudo workspace-model install
```

Then start an interactive session:

```bash
workspace-agent
```

One-shot prompts are also supported with `workspace-agent ask "your question"`. The agent talks only to the stable local runtime API; changing provider profiles does not change the user command.

## Distro direction

The actual Linux distribution work starts in `distro/`. V0.1 uses an Ubuntu autoinstall profile and neutral system services such as `agent-runtime.service`, `agent-policy.service`, `agent-executor.service`, and `local-inference.service`.

Agent-guided setup uses the neutral `workspace-installer` CLI. The agent may probe hardware, apply conservative defaults, create a hashed plan, and validate it. Approval is a separate operation reserved for the trusted onboarding UI or authenticated human console. The agent never receives a free-form installer shell. See `docs/agent-guided-installation.md`.

Bonsai is the default local model profile. The current Core installer does not bundle its weights; `sudo workspace-model install` builds the checksum-pinned Prism runtime and downloads the checksum-pinned 1.07 GB model. The development VM has been verified with a real local Bonsai response. An AI-ready image will prebuild the runtime and bundle the weights for first-boot use.
