# CieloOS on Windows via WSL2 (including Windows-on-ARM)

Run CieloOS as an **app** on a Windows machine — including an ARM Surface — with no
bootable USB, no dual-boot, and nothing erased. WSL2 gives you a real Linux kernel
for your CPU architecture, and it forwards `localhost`, so a runtime bound to
`127.0.0.1` inside WSL is reachable from your normal Windows browser.

From zero to the claim wizard is five steps.

> **Architecture matters.** A Surface (and most Copilot+ PCs) is **ARM64** → use the
> `linux-arm64` bundle. An Intel/AMD PC is **x64** → use `linux-x64`. Inside WSL,
> `uname -m` prints `aarch64` (ARM) or `x86_64`.

## 1. Install WSL2 + Ubuntu (in Windows PowerShell)

```powershell
wsl --install -d Ubuntu-24.04
```

Run this in an **Administrator** PowerShell — installing WSL enables Windows optional
features and needs elevation (and usually a reboot).

Reboot if it asks, then open **Ubuntu** from the Start menu and create your Linux
user. Confirm you're on WSL **2** (WSL 1 has no real kernel and won't do):

```powershell
wsl -l -v
```

## 2. Download the release bundle into WSL

The bundle is self-contained — **no .NET needed inside WSL**. Download the release
asset for your architecture:

```bash
case "$(uname -m)" in
  aarch64) ARCH=linux-arm64 ;;
  x86_64)  ARCH=linux-x64 ;;
  *)       echo "Unknown architecture: $(uname -m)" >&2; exit 1 ;;
esac

curl -L -o "cielo-$ARCH.tar.gz" \
  "https://github.com/egarim/CieloOS/releases/latest/download/cielo-$ARCH.tar.gz"
```

Building the bundle yourself is a development activity and is documented in
[local-dev.md](local-dev.md); WSL users should start from the release asset.

## 3. Run it (no root, no systemd)

```bash
tar xzf "cielo-$ARCH.tar.gz"
./cielo/run.sh                 # or: PORT=6000 ./cielo/run.sh
```

`run.sh` stays in the foreground (Ctrl-C stops it) and keeps its **control-plane**
state in `cielo/.data` — the SQLite DB and secrets. Nothing is written to `/opt`, no
service is registered, and it never asks for `sudo`.

> If you later create sessions, podman stores their images and named volumes under
> `~/.local/share/containers`, **outside** the bundle. Deleting the bundle folder
> removes the control plane, not those — clean them up with `podman volume ls` /
> `podman volume rm lunos-home-<owner>` and `podman system prune` if you want the
> disk space back.

## 4. Open the panel in your Windows browser

<http://localhost:5148/> → **Claim this machine**.

The first-owner claim is loopback-only by design, and WSL's localhost forwarding
counts as loopback, so claiming from the Windows browser works exactly like
claiming on the box.

## 5. Add an AI provider

CieloOS ships provider-free. In the panel's **Models** tab, add a provider key — it
takes effect immediately, no restart.

Health check from a second Ubuntu shell while it runs:

```bash
./cielo/cielo-selftest.sh --url http://127.0.0.1:5148
```

## Optional: the chat UI

`install.sh` installs Open WebUI as a service, but `run.sh` has no systemd, so under
WSL you start it yourself. It needs podman (see the next section) and your owner
token — the panel's own token, which `.data/secrets/<you>.token` also holds:

```bash
podman run -d --rm --name cielo-chat --network host \
  -e HOST=127.0.0.1 -e PORT=8080 -e WEBUI_AUTH=False -e WEBUI_NAME="CieloOS Chat" \
  -e OPENAI_API_BASE_URL=http://127.0.0.1:5148/v1/agent \
  -e OPENAI_API_KEY="$(cat ~/cielo/.data/secrets/$USER.token)" \
  -v cielo-chat-data:/app/backend/data ghcr.io/open-webui/open-webui:main
```

Then open `http://localhost:8080/` in Windows, and set `Chat__Url=http://localhost:8080/`
in the environment `run.sh` inherits so the panel links to it. `WEBUI_AUTH=False` means
anyone who reaches the page acts as you, which is why `HOST` pins it to loopback.

## Optional: sessions (console / desktop) need podman

The control plane — panel, claim, Models, spreadsheet, audit — runs with **no
podman at all**. Console and desktop *sessions* are rootless podman containers, so
they simply don't start until podman is installed:

```bash
sudo apt install -y podman uidmap slirp4netns fuse-overlayfs
```

Then build the two session images (`localhost/lunos-console:latest` and
`localhost/lunos-desktop:latest`) as your normal user:

```bash
./cielo/run.sh --prepare-sessions
```

`run.sh --prepare-sessions` exits when it is done. It builds into your user's
rootless podman store, the same store the runtime reads, so do not use `sudo`.
If you skip it, the desk list correctly reports the office desk as not ready.

Rootless podman works in WSL2 with **no systemd and no `XDG_RUNTIME_DIR` fiddling** —
WSL provides `/run/user/<uid>`, and `useradd` already gave your user subuid/subgid
ranges. Verified: `podman info` reports `rootless=true` on `arm64`.

## Optional: run it as a service instead of a foreground app

`install.sh` installs a systemd unit, and WSL has systemd **off** by default. Turn it
on first:

```bash
sudo tee -a /etc/wsl.conf >/dev/null <<'EOF'
[boot]
systemd=true
EOF
```

Then, from Windows PowerShell, `wsl --shutdown` and reopen Ubuntu. Now:

```bash
sudo ./cielo/install.sh --mode app     # loopback bind, same as run.sh
```

For a throwaway "just show me the thing" run, prefer `run.sh` — it needs neither
root nor systemd, which is why it exists as a separate script rather than a flag on
`install.sh`.

## Maintenance: keep the WSL disk from growing

Every image rebuild leaves the previous image dangling; podman only untags it.
Check how much can be reclaimed:

```bash
podman system df
```

Prune all unused images (this removes the dangling rebuilds, not named volumes):

```bash
podman image prune -a
```

That frees space inside WSL. The WSL virtual disk still looks the same size from
Windows until you tell it to shrink. On current WSL builds:

```powershell
wsl --manage Ubuntu --set-sparse
```

Replace `Ubuntu` with whatever `wsl -l -v` names your distro. If `--manage` is
not available, shut WSL down and compact the VHDX instead:

```powershell
wsl --shutdown
Get-ChildItem "$env:LOCALAPPDATA\Packages" -Recurse -Filter ext4.vhdx |
  Select-Object FullName
Optimize-VHD -Path <the-ext4.vhdx-path-from-above> -Mode Full
```

Run `podman system df` again to confirm `RECLAIMABLE` has dropped.

## Why WSL2 and not a bootable USB

- A Surface is ARM64: a bootable Linux USB would need an **arm64** installer (the
  autoinstall USB we build and test is amd64), and Linux-on-Surface is
  driver/firmware/Secure-Boot fiddly.
- The USB path **erases the target disk** — it turns the machine into an appliance.
  WSL leaves Windows exactly as it was.
- WSL2 runs the same arm64 Linux binary we already test on arm64 Linux, so it's the
  same runtime, not a port.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `wsl` fails with `Wsl/CallMsi/Install/REGDB_E_CLASSNOTREG` | The WSL runtime is not really installed (an old Store package can mask this). Enable the features from an **admin** shell — `dism /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart` and the same for `VirtualMachinePlatform` — **reboot**, then `winget install --id Microsoft.WSL` **elevated**. |
| `WslRegisterDistribution failed with error: 0x8007019e` | Same cause: the WSL feature/runtime is missing. Fix as above, then `ubuntu.exe install --root` registers the distro without the interactive user prompt. |
| `wsl -l -v` shows version 1 | `wsl --set-version Ubuntu-24.04 2` |
| `run.sh: cannot execute: required file not found` | The file has CRLF endings (copied through Windows tooling): `sed -i 's/\r$//' cielo/run.sh` |
| `Exec format error` | Wrong arch bundle. Check `uname -m`: `aarch64` → `linux-arm64`, `x86_64` → `linux-x64`. |
| Browser can't reach `localhost:5148` | Make sure `run.sh` is still running, and that nothing else holds the port. If you switch ports with `PORT=6000 ./cielo/run.sh`, open `http://localhost:6000/` — the port you launch with is the port you browse to. |
| Sessions never start | Install podman (above); the rest of the panel works without it. |

## Status

**Verified end-to-end on a real Windows-on-ARM Surface (2026-08-23)** — Windows 11
26200, WSL 2.7.12 (kernel 6.18.33.2), Ubuntu 24.04 on WSL 2, `aarch64`. Built the
`linux-arm64` bundle in WSL (.NET SDK 10.0.400, Node 22), ran `./cielo/run.sh` as a
non-root user with no systemd, and reached the claim wizard from the Windows browser
at `http://localhost:5148/`. `cielo-selftest.sh` passed 4/4.

Sessions were exercised too: rootless podman 4.9.3, a `human-desktop` session started
and served its XFCE viewport to the Windows browser — but only after overriding the
amd64-only default image and the mismatched viewport port (see the sessions section
above). Agent control of the desktop was not tested (stock webtop lacks the tooling).

## See also

- [distro/RELEASE-README.md](../distro/RELEASE-README.md) — the release bundle, modes, first owner
- [first-run-setup.md](first-run-setup.md) — the claim + provider flow in detail
- [boot-and-install-flow.md](boot-and-install-flow.md) — install, boot, and session-create flows
