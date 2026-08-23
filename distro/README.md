# Distro V0.1

This folder is the beginning of the actual CieloOS Linux distribution work.

The distro should be Ubuntu-based, with the current target set to Ubuntu 26.04 LTS. The visible product name comes from `config/branding.json`; stable internal services use neutral names.

## Shape

```text
distro/
  autoinstall/
    user-data
    meta-data
  packages/
    workspace-runtime/
      install.sh
  services/
    agent-runtime.service
    agent-policy.service
    agent-executor.service
    local-inference.service
  models/
    registry.json
    providers/
      prism-bonsai-4b.json
      qwen3-4b.json
  config/
    local-inference.json
  profiles/
    dotnet-automation.json
  tools/
    dotnet.build.json
    dotnet.test.json
    dotnet.script.json
    powershell.run.json
  scripts/
    check-host.sh
    build-profile.sh
```

## Local Inference

CieloOS should not be married to one local model provider. The distro owns a neutral local inference contract; individual models are swappable provider profiles.

The current default provider profile is:

```text
Model: prism-ml/Ternary-Bonsai-4B-gguf
Vendor: Prism ML, Inc.
Base model: Qwen3-4B
Runtime: llama.cpp
Quantization: GGUF Q2_0 ternary
Approximate model size: 1.07 GB
Default endpoint: http://127.0.0.1:8080/v1
License: Apache-2.0
```

That default can be replaced by changing `distro/models/registry.json` and `distro/config/local-inference.json`. A generic Qwen3 4B fallback profile is included to prove the model-provider boundary.

Provider runtime commands are structured into executable and argument fields. The OS does not execute arbitrary shell strings from provider manifests. The Bonsai profile pins PrismML's required `llama.cpp` fork to a full Git commit and pins the GGUF artifact to a SHA-256 digest.

On a Core installation:

```bash
workspace-agent status
sudo workspace-model install
workspace-agent
```

`workspace-model` reads the active provider profile, validates its metadata, builds the pinned runtime, verifies model weights before activation, and binds inference to localhost. `workspace-agent` remains provider-neutral.

For an AI-ready ISO, model weights should be bundled so the system can act locally on first boot. For a smaller Core ISO, the same profile can be installed without weights and pull the selected model later.

## Getting started (develop from source)

Running from source needs the .NET 10 SDK and Node on your machine.

```bash
./scripts/dev.sh
```

This starts the backend API on `http://127.0.0.1:5148` and the Vite panel on `http://127.0.0.1:5173` (the panel proxies `/api` to the backend). If a VM is already forwarding its API to 5148, pick another port: `BACKEND_PORT=5149 ./scripts/dev.sh`.

The first run is a clean, provider-free machine: open the panel and the "Claim this machine" wizard creates the first owner — there are no hardcoded users. To boot the demo identities instead, set the demo seed: `LUNOS_DEMO=1 ./scripts/dev.sh` (the script then prints their session tokens).

Run the tests:

```bash
./scripts/test.sh
```

That runs `dotnet test`, then the panel's vitest suite.

## Deploying a release bundle (Ubuntu 24.04+)

The shipping path is a self-contained bundle: build it once on a machine that has the .NET SDK + Node, then carry the tarball to any Ubuntu box (VPS, spare machine, appliance) and install it with no .NET on the target.

Build:

```bash
bash distro/scripts/build-release.sh linux-x64      # or: linux-arm64
```

This publishes the self-contained runtime, builds the panel, stages surfaces + config + the installer + service unit + self-test, and writes `release/cielo-linux-x64.tar.gz` (or `release/cielo-linux-arm64.tar.gz`).

Install on the target:

```bash
tar xzf cielo-linux-x64.tar.gz
sudo ./cielo/install.sh --mode headless             # or: app | kiosk
```

The three modes share one runtime and differ only in bind address and whether a kiosk browser is installed:

| Mode | For | Binds |
|------|-----|-------|
| `app` | your own machine — a local browser at `http://127.0.0.1:5148/` | loopback |
| `headless` | a VPS / LAN box — your browser + a token, over the network | all interfaces |
| `kiosk` | an appliance that boots into a fullscreen panel browser | loopback |

Options: `--mode <headless|app|kiosk>` (default `headless`), `--port <5148>`, `--ci` (container-safe install for automated tests — no systemd/linger), and `--offline` (install into a not-yet-running system, e.g. an autoinstall in-target chroot; units are enabled to start on first boot rather than started now).

The first-owner claim is loopback-only by design — nobody on the network can claim your box:

- **app / kiosk:** open `http://127.0.0.1:5148/` on the box; the claim wizard creates the owner (or run `cielo-claim "Your Name"`).
- **headless:** `ssh` in and run `cielo-claim "Your Name"` — it prints your bearer token; then open `http://<host-ip>:5148/` from your laptop and sign in with that token.

Add an AI provider from the panel's **Models** tab (works immediately, no restart), or set a key in `/etc/cielo/cielo.env` and restart. Add a teammate with `cielo-add-user "Their Name" <owner-token>` or the panel's add-teammate control.

Verify a running system anytime (non-destructive):

```bash
cielo-selftest
```

`cielo-selftest --claim` also exercises the full first-run flow, so only point that at a throwaway or CI machine. The installer lands the runtime under `/opt/cielo` (single SQLite DB in `/opt/cielo/.data`) as the `cielo` service user; console/desktop sessions run as rootless podman containers whose images build on first use.

## Autoinstall USB appliance (bare metal)

To turn a spare machine into a CieloOS appliance, remaster an Ubuntu 24.04 live-server ISO into an unattended installer. On a build machine with `xorriso` and `openssl`:

```bash
bash distro/scripts/build-usb.sh --iso ubuntu-24.04-live-server-amd64.iso --mode kiosk
```

Options: `--mode <kiosk|app|headless>` (default `kiosk`), `--password <admin-pw>` (default `cielo`), and `--out <path>` (default `release/cieloos-usb.iso`). It builds the amd64 bundle, writes the autoinstall seed, and produces `release/cieloos-usb.iso`.

Flash it to a USB stick — **this ERASES the stick**:

```bash
sudo dd if=release/cieloos-usb.iso of=/dev/rdiskN bs=4m
```

Boot the target from the USB. **This ERASES the target's disk**, installs Ubuntu + CieloOS unattended (`install.sh --offline`), reboots, and in kiosk mode opens the panel. The Ubuntu maintenance login is `cielo-admin` / your `--password` — distinct from the CieloOS owner you claim in the panel on first boot.

## Requirements

- **Install target:** Ubuntu 24.04+ (amd64 or arm64) with `podman`. No .NET needed — the runtime is self-contained. Provider-free by default; add your own key from the Models tab. Budget ~4 GB RAM and up (more only if you run a local model).
- **Develop from source:** .NET 10 SDK + Node.
- **Build a USB appliance:** `xorriso` + `openssl` on the build machine.

## Build stance

The first practical target is a repeatable Ubuntu installer plus a CieloOS runtime payload. This gets us to bootable systems quickly while keeping our provisioning independent from the base ISO:

1. Install Ubuntu.
2. Apply the CieloOS autoinstall seed.
3. Install neutral runtime services.
4. Enable local inference through the Bonsai profile when hardware permits.
5. Add branding assets and kiosk-console polish (see `docs/ai-native-ui.md`: no desktop environment is planned).

After that works reliably, the same answers and payload become a remastered, branded ISO with a custom graphical setup experience.

## Agent-guided installation (superseded)

> The shipping installers are the scripts above: `build-release.sh` + `install.sh` for a bundle, and `build-usb.sh` for a bare-metal appliance. The earlier `workspace-installer` .NET CLI — together with the `vm-prepare.sh` and `vm-run.sh --install` VM flow — is superseded by them.

The neutral setup-payload design still stands as future direction. The idea is that an installation agent calls JSON-only `defaults`, `probe`, `plan`, and `validate` commands, with MCP an optional adapter rather than a requirement, while the `approve` command is reserved for the trusted onboarding UI or an authenticated human console session.

Plans are content-hashed. A human must approve the exact plan identifier before a separate trusted worker can perform destructive installation. The hash binds the approval to the plan but is not an authorization secret. The CLI never exposes arbitrary shell execution, and policy denies the approval operation to the agent principal.

## .NET and PowerShell

CieloOS treats .NET and PowerShell as first-class automation surfaces. The distro profile in `profiles/dotnet-automation.json` installs the SDK/runtime and registers structured tools such as `dotnet.build`, `dotnet.test`, `dotnet.script`, and `powershell.run`.

Agents request these tools through policy and the sandboxed executor; they should not run arbitrary shell commands directly.

Ubuntu 26.04 ARM64 supplies the .NET 10 SDK from its built-in archive. The setup media also carries a checksum-pinned official PowerShell ARM64 archive, so PowerShell installation does not depend on GitHub during first boot. .NET 10 file-based apps provide native single-file C# scripting through `dotnet run --file script.cs`.
