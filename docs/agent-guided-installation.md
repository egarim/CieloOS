# Agent-Guided Installation

**Status — superseded as the install path.** CieloOS does not install through the agent-guided `workspace-installer` flow. It ships as a self-contained release bundle you install with `install.sh --mode <app|headless|kiosk>`, plus a bare-metal autoinstall USB built with `build-usb.sh`. Neither needs .NET on the target, and both are provider-free by default — you add an AI provider from the panel's Models tab. The `workspace-installer` CLI still emits plan artifacts (see below), but no executor applies them; the design is kept as a record of the deterministic-CLI boundary the installer is built toward.

## Current install path (shipping)

**Build a bundle** on a machine with the .NET 10 SDK + Node (nothing else is needed on the target):

```bash
bash distro/scripts/build-release.sh linux-x64      # or: linux-arm64
# → release/cielo-linux-x64.tar.gz
```

**Install on an Ubuntu 24.04+ target** (amd64 or arm64; no .NET required — the bundle is self-contained):

```bash
tar xzf cielo-linux-x64.tar.gz
sudo ./cielo/install.sh --mode headless             # or: app | kiosk
```

- `app` — binds loopback; your own machine, panel at `http://127.0.0.1:5148/`.
- `headless` — binds all interfaces; a VPS or old box you reach over the LAN/internet with a token.
- `kiosk` — binds loopback and boots the machine into a fullscreen panel browser.

**Claim the first owner** — loopback-only by design, so you do it on the box:

- app / kiosk: open `http://127.0.0.1:5148/` on the machine → the claim wizard.
- headless: `ssh` in and run `cielo-claim "Your Name"` → it prints your bearer token; then open `http://<host-ip>:5148/` from your laptop and sign in with it.

Add a teammate with `cielo-add-user "Their Name" <owner-token>` (or the panel). Verify a running system anytime with `cielo-selftest` (non-destructive). Add an AI provider from the panel's Models tab (no restart), or set a key in `/etc/cielo/cielo.env` and restart.

**Bare-metal autoinstall USB** — installs Ubuntu + CieloOS unattended and **erases the target disk**:

```bash
bash distro/scripts/build-usb.sh --iso <ubuntu-24.04-live-server-amd64.iso> --mode kiosk
# → release/cieloos-usb.iso ; flash to USB with dd, then boot the target
```

**Requirements** — target: Ubuntu 24.04+ (amd64 or arm64) with podman; no .NET; ~4 GB RAM+ (more only for a local model). Build host: .NET 10 SDK + Node; the USB and VM tooling also need `xorriso` (and `qemu` for the VM test). Test the installer without real hardware: `distro/scripts/test-install.sh` runs the bundle in Docker on real Linux, and `distro/scripts/test-install-vm.sh` runs it in a full-system amd64 QEMU VM — both build the bundle, run `install.sh`, and run `cielo-selftest --claim`.

---

The rest of this page documents the agent-guided design the `workspace-installer` CLI is built toward; it is not the current install procedure. The installation agent is a guide and planner. It does not receive a shell and it cannot write a partition table directly.

## Control Path

```text
User conversation
  -> installation agent
  -> workspace-installer CLI
  -> immutable installation plan
  -> deterministic validation
  -> human approval bound to the plan hash
  -> trusted installer worker
  -> Ubuntu Subiquity/curtin
  -> audit record
```

The core boundary is the deterministic CLI, not a particular agent protocol. A future MCP adapter may expose the same commands, but it must not add broader capabilities.

## CLI Commands

The CLI writes JSON to standard output and errors to standard error. The agent principal may invoke the first four read-only commands:

```bash
workspace-installer defaults
workspace-installer probe
workspace-installer plan --request request.json --probe probe.json
workspace-installer validate --plan plan.json --probe probe.json
```

The trusted onboarding UI or an authenticated human console session invokes the approval command:

```bash
workspace-installer approve --plan plan.json --probe probe.json \
  --token PLAN_ID --approved-by USER
```

`--request -` and other file arguments accept `-` for standard input. Tests may supply a saved probe so plans are reproducible without access to real disks.

## Defaults

- Use the only eligible internal disk; require an explicit choice if more than one exists.
- Use the entire selected disk with encryption enabled.
- Keep SSH disabled.
- Install .NET and PowerShell.
- Select the local Prism Bonsai profile and its weights.
- Disable cloud inference fallback.
- Require a human to approve the exact SHA-256 plan identifier.

The plan identifier binds approval to the exact plan content; it is not itself an authorization secret. Process permissions and the policy layer must deny the `approve` command to the agent principal. Passwords, recovery keys, and provider credentials do not enter the agent conversation or installation plan. A trusted UI collects secrets separately.

## Execution Boundary

The current V0.1 CLI implements probing, default selection, planning, validation, and approval artifacts. Its tool contract marks approval as unavailable to agents. The next installer worker will translate only a validated and approved plan into Subiquity configuration. It will expose fixed operations rather than arbitrary command strings. That worker does not exist yet; today CieloOS installs through `install.sh` and the autoinstall USB — see [Current install path](#current-install-path-shipping).

## Try Mode

For a throwaway trial, run the release bundle in a disposable VM: `distro/scripts/test-install-vm.sh` boots a full-system amd64 QEMU VM on a copy-on-write overlay (the base cloud image is never modified), installs the bundle, runs `cielo-selftest --claim`, and powers off, so nothing persists. `distro/scripts/test-install.sh` does the same on real Linux inside Docker. For a bare-metal trial, build the autoinstall USB (see [Current install path](#current-install-path-shipping)) and boot it in a scratch machine or VM; note that it erases the target disk.

The earlier `vm-prepare.sh` / `vm-run.sh --try` reference-VM flow is superseded by the release bundle and these test harnesses.
