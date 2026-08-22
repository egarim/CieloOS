# Distro V0.1

This folder is the beginning of the actual Lun.Os Linux distribution work.

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

Lun.Os should not be married to one local model provider. The distro owns a neutral local inference contract; individual models are swappable provider profiles.

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

## Apple Silicon VM milestone

The first bootable target is an ARM64 VM on Apple Silicon. QEMU uses macOS Hypervisor.framework acceleration and a standard UEFI boot path, so the guest architecture matches the M2 instead of slowly emulating an Intel machine.

The VM opens full-screen with QEMU zoom-to-fit enabled. Leaving full-screen from QEMU's View menu produces a resizable window whose guest display scales with it. A large Terminus console font is installed for the text-only setup and recovery screens.

```bash
./distro/scripts/vm-prepare.sh
./distro/scripts/vm-run.sh --install
```

The first command downloads and verifies Ubuntu's official ARM64 installer, caches a checksum-pinned PowerShell ARM64 archive, creates an expandable 64 GB VM disk, publishes the backend for Linux ARM64, and builds two companion ISOs: the autoinstall seed and the runtime profile. The second command opens a visible VM window.

The Ubuntu installer may ask for confirmation before starting unattended installation. This is intentional for the first observable setup milestone. The development username and password are both `workspace`.

Once installation finishes and the VM closes, boot the installed disk without setup media:

```bash
./distro/scripts/vm-run.sh --installed
```

To evaluate the prepared reference system without retaining changes:

```bash
./distro/scripts/vm-run.sh --try
```

Try mode uses QEMU's temporary snapshot support. The reference system disk is not modified.

Host connections are forwarded to:

```text
SSH: ssh -p 2222 workspace@127.0.0.1
API: http://127.0.0.1:5148
```

All generated media, downloads, firmware state, logs, and virtual disks stay in `distro/.vm/` and are not committed.

## Build stance

The first practical target is a repeatable Ubuntu installer plus a Lun.Os runtime payload. This gets us to bootable systems quickly while keeping our provisioning independent from the base ISO:

1. Install Ubuntu.
2. Apply the Lun.Os autoinstall seed.
3. Install neutral runtime services.
4. Enable local inference through the Bonsai profile when hardware permits.
5. Add branding assets and kiosk-console polish (see `docs/ai-native-ui.md`: no desktop environment is planned).

After that works reliably, the same answers and payload become a remastered, branded ISO with a custom graphical setup experience.

## Agent-guided installation

The setup payload includes a neutral `workspace-installer` .NET CLI. The installation agent can directly call its JSON-only `defaults`, `probe`, `plan`, and `validate` commands; MCP is an optional future adapter rather than a requirement. The `approve` command is reserved for the trusted onboarding UI or an authenticated human console session.

Plans are content-hashed. A human must approve the exact plan identifier before a separate trusted worker can perform destructive installation. The hash binds the approval to the plan but is not an authorization secret. The CLI never exposes arbitrary shell execution, and policy denies the approval operation to the agent principal.

## .NET and PowerShell

Lun.Os treats .NET and PowerShell as first-class automation surfaces. The distro profile in `profiles/dotnet-automation.json` installs the SDK/runtime and registers structured tools such as `dotnet.build`, `dotnet.test`, `dotnet.script`, and `powershell.run`.

Agents request these tools through policy and the sandboxed executor; they should not run arbitrary shell commands directly.

Ubuntu 26.04 ARM64 supplies the .NET 10 SDK from its built-in archive. The setup media also carries a checksum-pinned official PowerShell ARM64 archive, so PowerShell installation does not depend on GitHub during first boot. .NET 10 file-based apps provide native single-file C# scripting through `dotnet run --file script.cs`.
