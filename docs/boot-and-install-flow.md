# Boot & Install Flow

How a CieloOS machine goes from media to a running command bus, and what happens on every boot. See [architecture-diagram.md](architecture-diagram.md) for the component map.

## Install — from media to an installed system

Two paths, one installer. On an **existing** Ubuntu box you run the release bundle's `install.sh` directly; for a **bare-metal appliance** an autoinstall USB does it unattended. Both end at the same `/opt/cielo` + `cielo-runtime.service`.

- **Release bundle (existing Ubuntu 24.04+):** `bash distro/scripts/build-release.sh linux-x64` → carry `release/cielo-<arch>.tar.gz` to the box → `tar xzf … && sudo ./cielo/install.sh --mode <app|headless|kiosk>`. The bundle is self-contained, so the target needs **no .NET**.
- **Autoinstall USB (bare metal):** `bash distro/scripts/build-usb.sh --iso <ubuntu-live-server> --mode kiosk` remasters the ISO with the bundle + a NoCloud autoinstall seed; boot the USB and Ubuntu + CieloOS install unattended (the diagram below).

```mermaid
sequenceDiagram
    autonumber
    actor Dev as Builder (host)
    participant BR as build-release.sh
    participant USB as build-usb.sh
    participant AI as Ubuntu autoinstall (cloud-init)
    participant Inst as install.sh --offline (in-target)
    participant Disk as target disk

    Dev->>BR: build-release.sh linux-x64
    BR->>BR: dotnet publish Api (self-contained, InvariantGlobalization) + build panel
    BR-->>Dev: release/cielo-linux-x64.tar.gz  (no .NET needed on the target)
    Dev->>USB: build-usb.sh --iso <live-server> --mode kiosk
    USB->>USB: remaster ISO — add /cielo bundle + NoCloud autoinstall seed, patch GRUB
    USB-->>Dev: release/cieloos-usb.iso  (flash to USB with dd — erases it)
    Dev->>AI: boot target from USB
    AI->>Disk: partition, install Ubuntu base + podman
    Note over AI,Inst: late-commands
    AI->>Inst: curtin in-target → install.sh --offline --mode kiosk
    Inst->>Disk: /opt/cielo (runtime + panel + surfaces + config), cielo user, /etc/cielo/cielo.env
    Inst->>Disk: enable cielo-runtime.service (+ cielo-kiosk), write linger marker — no start
    AI->>Disk: reboot into the installed system
```

**What ends up on disk:** the self-contained runtime at `/opt/cielo/bin/WorkspaceRuntime.Api` (which **serves its own panel**), the surface manifests, `config/branding.json`, a provider-free `/etc/cielo/cielo.env`, and one systemd unit — `cielo-runtime.service` (plus `cielo-kiosk.service` in kiosk mode). No .NET, no Postgres — a single **SQLite** DB under `/opt/cielo/.data`.

## Boot — every start

```mermaid
sequenceDiagram
    autonumber
    participant SD as systemd
    participant RT as cielo-runtime.service → WorkspaceRuntime.Api
    participant DB as SQLite (/opt/cielo/.data)
    participant FS as /opt/cielo/.data/secrets
    participant Panel as panel (served by the runtime)

    SD->>RT: start (After network-online.target)
    activate RT
    RT->>DB: apply EF Core migrations
    RT->>DB: seed the spreadsheet singleton (+ demo users only if LUNOS_DEMO)
    RT->>FS: mint token files for any existing identities (*.token)
    RT->>RT: load surface manifests (fail fast on a malformed contract)
    RT->>RT: resolve model providers — provider-free by default (UnconfiguredBrain until you add one)
    RT->>Panel: serve the built panel at / (ahead of the auth gate)
    RT-->>SD: listening on :5148 — ready
    deactivate RT
```

The runtime's **eager bootstrap** (migrations → seed → tokens → surfaces) runs before it accepts a request. A **fresh, provider-free** machine has *no users* — so the first request is the loopback-only **claim** (open `http://127.0.0.1:5148/` on the box, or `cielo-claim` over SSH), which creates the owner + agent and mints their tokens. No brains are registered until you add a provider from the panel's **Models** tab (or a key in `/etc/cielo/cielo.env`). In **kiosk** mode `cielo-kiosk.service` (cage + chromium) opens the panel fullscreen once the runtime is up.

## Sessions — created on demand

Nothing spins a container at boot. A session appears only when a command asks for one — and that command rides the same bus as everything else.

```mermaid
sequenceDiagram
    autonumber
    actor Owner as Human / owning agent
    participant Bus as AgentRuntime.SubmitAsync
    participant Orch as SessionOrchestrator
    participant Pod as podman

    Owner->>Bus: session.create {owner, profile}
    Bus->>Bus: ownership gate + manifest policy + audit
    Bus->>Orch: execute (Allow)
    Orch->>Pod: volume create (per-owner home) if absent
    Orch->>Pod: run container (console: ttyd+tmux | desktop: webtop XFCE+Selkies)
    Pod-->>Orch: viewport port
    Orch-->>Owner: session running → attach the viewport
```

Sessions are **rootless podman** containers run by the `cielo` service user (its subuid/subgid + linger are set up by `install.sh`).

## Dev vs installed (a note)

The diagrams above describe the **installed product**: `cielo-runtime.service` runs the self-contained runtime and serves the panel on port **5148**. In active development, `./scripts/dev.sh` runs the backend (**5148**) and the Vite panel (**5173**) from source, with the panel proxying `/api` to the backend — same binary, same bootstrap, just hand-launched and hot-reloading. Set `LUNOS_DEMO=1` there to seed the `joche`/`yulia` demo identities instead of claiming an owner.
