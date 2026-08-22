# Lun.Os live image (parked plan)

Goal: a bootable Lun.Os you can put on a USB (or run in a VM) that comes up as
"the OS" — the runtime running, podman ready, session images present, and the
panel on screen in a kiosk browser. Deferred behind the agent-console loop; this
captures the design so the recon isn't lost.

## Multi-arch — the target matrix

The image must be built **per architecture** (a live image is arch-specific; the
bootloader and rootfs differ). Three targets, one provisioning layer:

| Target | Arch | Boots on | Builder |
|---|---|---|---|
| PC / laptop | `x86-64` | most bare-metal PCs (BIOS+UEFI hybrid ISO) | native x86-64 Ubuntu + root |
| Raspberry Pi 4/5 | `arm64` | Pi (UEFI or Pi firmware + u-boot) | native arm64 Ubuntu + root |
| Apple Silicon (M1…) | `arm64` | UTM / QEMU on the Mac (no bare-metal boot — Apple Silicon won't boot arbitrary USB Linux) | native arm64 Ubuntu + root |

Note the two ARM targets share a rootfs but differ in boot: the Pi needs its
firmware/u-boot path; the UTM target is generic UEFI (`QEMU`/`virt`). Apple
Silicon Macs do **not** boot a Linux USB directly — "run it on my Mac" means UTM.

## Blockers found during recon (2026-08-22)

- Our only Linux box is the dev VM: **arm64, Ubuntu 26.04, no passwordless sudo,
  no build tools, no `/dev/kvm`.** It cannot build x86-64 at all, and cannot
  build *anything* until it has sudo + `apt`.
- Building a bootable image needs **root** (debootstrap / chroot / loop mounts).

## Builder strategy

Build in **CI on native runners**, not on the dev VM:

- **GitHub Actions** has native `x86-64` and `arm64` Ubuntu runners with
  passwordless sudo → one job per arch, each producing an ISO/IMG artifact.
- The build is a **builder-agnostic script** (`scripts/build-image.sh <arch>`)
  that runs on any Ubuntu-with-sudo of that arch; the Actions workflow is a thin
  matrix wrapper. Anyone can also run it on their own box.
- Tool: **live-build** (canonical Ubuntu live ISO, BIOS+UEFI hybrid) for x86-64;
  live-build/mkosi for arm64. Decide per-arch during the build spike.
- Verify each artifact **boots in QEMU** (TCG is fine for a smoke boot to the
  panel) before anyone flashes hardware.

## The Lun.Os provisioning layer (arch-independent — "what makes it Lun.Os")

Reused across all three targets:

- Packages: `podman`, `ca-certificates`, `curl`, a kiosk browser (chromium) + a
  minimal compositor (`cage`/`weston` or openbox+X), fonts.
- The runtime: a **self-contained** publish (`dotnet publish -r <rid>
  --self-contained`, `linux-x64` / `linux-arm64`) dropped at `/opt/lunos`; no
  .NET install needed in the image.
- **Single-origin panel**: the runtime must serve the built React SPA (static +
  SPA fallback) so the kiosk opens one URL. (Prereq — see the runtime today only
  redirects `/` to `/api/branding`.)
- systemd units: `lunos-runtime.service` (the API) + `lunos-kiosk.service` (auto
  login → compositor → `chromium --kiosk http://localhost:<port>`).
- `lunos-firstboot.service` (once): seed identities/tokens, provision home
  volumes, pre-pull or import the session images (`lunos-console`, `webtop`).
- Session images: **bake in** `lunos-console` (~187 MB) for offline boot; the
  `webtop` desktop (~3.3 GB) is baked for offline or pulled on first network —
  decide by image-size budget.
- Autologin + a first-run note showing the panel URL and the printed token.

## Open decisions (resolve at build time)

- Bake the 3.3 GB desktop image in (offline, huge ISO) vs first-boot pull.
- live-build vs mkosi for arm64.
- Pi boot path (UEFI vs firmware+u-boot).
- Persistence: pure live (tmpfs, resets) vs a persistence partition on the USB.
