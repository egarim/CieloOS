#!/usr/bin/env bash
# Build a CieloOS auto-install USB image: remaster an Ubuntu 24.04 live-server ISO
# so booting it installs Ubuntu unattended and then installs CieloOS into the
# target (install.sh --offline). Flash the result to a USB with `dd` (or Etcher)
# and boot the machine from it. THIS ERASES the target machine's disk.
#
#   distro/scripts/build-usb.sh --iso <ubuntu-24.04-live-server-amd64.iso> \
#       [--mode kiosk|app|headless] [--password <admin-pw>] [--out <cieloos-usb.iso>]
#
# The admin account it creates is the UBUNTU login (for SSH/maintenance) — distinct
# from the CieloOS owner you claim in the panel on first boot.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ISO="" ; MODE="kiosk" ; PASSWORD="cielo" ; OUT="$ROOT/release/cieloos-usb.iso"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --iso) ISO="${2:?}"; shift 2 ;;
    --mode) MODE="${2:?}"; shift 2 ;;
    --password) PASSWORD="${2:?}"; shift 2 ;;
    --out) OUT="${2:?}"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
[[ -f "$ISO" ]] || { echo "Pass --iso <ubuntu live-server amd64 ISO>" >&2; exit 1; }
command -v xorriso >/dev/null || { echo "xorriso missing (brew install xorriso)" >&2; exit 1; }
command -v openssl >/dev/null || { echo "openssl missing" >&2; exit 1; }
case "$MODE" in headless|app|kiosk) ;; *) echo "--mode must be headless|app|kiosk" >&2; exit 2 ;; esac

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
PWHASH="$(openssl passwd -6 "$PASSWORD")"

echo "==> Building amd64 bundle"
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then bash "$ROOT/distro/scripts/build-release.sh" linux-x64 >/dev/null; fi
STAGE="$ROOT/release/cielo"
test -x "$STAGE/bin/WorkspaceRuntime.Api" || { echo "bundle missing at $STAGE" >&2; exit 1; }

echo "==> Autoinstall seed (mode=$MODE)"
mkdir -p "$WORK/seed"
: > "$WORK/seed/meta-data"
cat > "$WORK/seed/user-data" <<EOF
#cloud-config
autoinstall:
  version: 1
  interactive-sections: []
  locale: en_US.UTF-8
  keyboard: {layout: us}
  identity:
    hostname: cielo
    realname: CieloOS Admin
    username: cielo-admin
    password: "${PWHASH}"
  ssh: {install-server: true, allow-pw: true}
  storage:
    layout: {name: direct}
  packages: [podman, uidmap, slirp4netns, fuse-overlayfs, curl, ca-certificates]
  late-commands:
    - mkdir -p /target/root/cielo-bundle
    - cp -a /cdrom/cielo/. /target/root/cielo-bundle/
    - curtin in-target --target=/target -- bash /root/cielo-bundle/install.sh --offline --mode ${MODE}
  shutdown: reboot
EOF

echo "==> Patching GRUB for unattended autoinstall"
xorriso -osirrox on -indev "$ISO" -extract /boot/grub/grub.cfg "$WORK/grub.cfg" >/dev/null 2>&1
# Append the autoinstall kernel args after every /casper/vmlinuz (skip already-patched
# lines), and shorten the menu timeout so the USB installs hands-off. perl for BSD/GNU parity.
perl -pi -e 's{(/casper/vmlinuz)}{$1 autoinstall ds=nocloud;s=/cdrom/autoinstall/}g unless /autoinstall/; s/^set timeout=.*/set timeout=3/;' "$WORK/grub.cfg"
grep -q 'autoinstall ds=nocloud' "$WORK/grub.cfg" || { echo "ERROR: could not patch grub.cfg (no /casper/vmlinuz line?)" >&2; exit 1; }

echo "==> Remastering ISO -> $OUT"
mkdir -p "$(dirname "$OUT")"
# -boot_image any replay carries the original El Torito + EFI boot records so the
# remastered ISO stays bootable; -map adds/replaces our files; commit on exit.
xorriso -indev "$ISO" -outdev "$OUT" \
  -volid CIELO_INSTALL \
  -boot_image any replay \
  -map "$STAGE" /cielo \
  -map "$WORK/seed/user-data" /autoinstall/user-data \
  -map "$WORK/seed/meta-data" /autoinstall/meta-data \
  -map "$WORK/grub.cfg" /boot/grub/grub.cfg

SIZE="$(du -sh "$OUT" | cut -f1)"
echo "==> Done: $OUT ($SIZE), mode=$MODE"
echo "    Flash to USB (ERASES it):  sudo dd if=$OUT of=/dev/rdiskN bs=4m   (macOS: diskutil list, then unmount)"
echo "    Boot the target from USB. It installs Ubuntu + CieloOS unattended, reboots,"
echo "    and (kiosk) opens the panel. Ubuntu login: cielo-admin / <password>."
