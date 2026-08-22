#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:${PATH}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
STATE_DIR="${WR_VM_STATE_DIR:-$ROOT_DIR/distro/.vm}"

# shellcheck source=../vm/defaults.env
source "$ROOT_DIR/distro/vm/defaults.env"

MODE="installed"
for argument in "$@"; do
  case "$argument" in
    --install) MODE="install" ;;
    --installed) MODE="installed" ;;
    --try) MODE="try" ;;
    *) echo "Unknown option: $argument" >&2; exit 2 ;;
  esac
done

QEMU_BIN="$(command -v qemu-system-aarch64 || true)"
if [[ -z "$QEMU_BIN" ]]; then
  echo "QEMU is not installed. Run: brew install qemu" >&2
  exit 1
fi

QEMU_PREFIX="$(cd "$(dirname "$QEMU_BIN")/.." && pwd)"
FIRMWARE_CODE=""
FIRMWARE_VARS=""
for candidate in \
  "$QEMU_PREFIX/share/qemu/edk2-aarch64-code.fd" \
  "$QEMU_PREFIX/share/qemu/edk2-arm-code.fd"; do
  if [[ -f "$candidate" ]]; then
    FIRMWARE_CODE="$candidate"
    break
  fi
done
for candidate in \
  "$QEMU_PREFIX/share/qemu/edk2-arm-vars.fd" \
  "$QEMU_PREFIX/share/qemu/edk2-aarch64-vars.fd"; do
  if [[ -f "$candidate" ]]; then
    FIRMWARE_VARS="$candidate"
    break
  fi
done

if [[ -z "$FIRMWARE_CODE" || -z "$FIRMWARE_VARS" ]]; then
  echo "Could not locate QEMU's ARM64 UEFI firmware and variable template." >&2
  exit 1
fi

DISK_PATH="$STATE_DIR/runtime/system.qcow2"
VARS_PATH="$STATE_DIR/runtime/uefi-vars.fd"
ISO_PATH="$STATE_DIR/downloads/$UBUNTU_ISO_FILENAME"
SEED_ISO="$STATE_DIR/media/autoinstall-seed.iso"
PAYLOAD_ISO="$STATE_DIR/media/runtime-profile.iso"
PID_PATH="$STATE_DIR/runtime/qemu.pid"
MONITOR_PATH="/tmp/workspace-runtime-vm-$(id -u).sock"

if [[ ! -f "$DISK_PATH" ]]; then
  echo "VM disk is missing. Run ./distro/scripts/vm-prepare.sh first." >&2
  exit 1
fi

if [[ -f "$PID_PATH" ]]; then
  EXISTING_PID="$(tr -dc '0-9' < "$PID_PATH")"
  if [[ -n "$EXISTING_PID" ]] && kill -0 "$EXISTING_PID" 2>/dev/null; then
    echo "The VM is already running with process id $EXISTING_PID." >&2
    exit 1
  fi
  unlink "$PID_PATH"
fi

if [[ ! -f "$VARS_PATH" ]]; then
  cp "$FIRMWARE_VARS" "$VARS_PATH"
fi

PRODUCT_NAME="$(jq -r '.Branding.ProductName // "Workspace Runtime"' "$ROOT_DIR/config/branding.json" 2>/dev/null || echo "Workspace Runtime")"

unlink "$MONITOR_PATH" 2>/dev/null || true

SYSTEM_DISK_OPTIONS="if=none,format=qcow2,file=$DISK_PATH,id=system_disk,discard=unmap"
if [[ "$MODE" == "try" ]]; then
  SYSTEM_DISK_OPTIONS+=",snapshot=on"
fi

QEMU_ARGS=(
  -name "$PRODUCT_NAME"
  -machine virt,accel=hvf,highmem=on
  -cpu host
  -smp "$VM_CPUS"
  -m "$VM_MEMORY_MIB"
  -drive "if=pflash,format=raw,unit=0,readonly=on,file=$FIRMWARE_CODE"
  -drive "if=pflash,format=raw,unit=1,file=$VARS_PATH"
  -drive "$SYSTEM_DISK_OPTIONS"
  -device virtio-scsi-pci,id=scsi0
  -device scsi-hd,drive=system_disk,bus=scsi0.0,bootindex=1
  -device ramfb
  -display "cocoa,show-cursor=on,zoom-to-fit=on,full-screen=$VM_FULLSCREEN"
  -device qemu-xhci
  -device usb-kbd
  -device usb-tablet
  -device virtio-rng-pci
  -netdev "user,id=vmnet,hostfwd=tcp:127.0.0.1:$VM_SSH_PORT-:22,hostfwd=tcp:127.0.0.1:$VM_API_PORT-:5148"
  -device virtio-net-pci,netdev=vmnet
  -boot menu=on
  -no-reboot
  -pidfile "$PID_PATH"
  -monitor "unix:$MONITOR_PATH,server=on,wait=off"
)

if [[ "$MODE" == "install" ]]; then
  for required_media in "$ISO_PATH" "$SEED_ISO" "$PAYLOAD_ISO"; do
    if [[ ! -f "$required_media" ]]; then
      echo "VM setup media is missing. Run ./distro/scripts/vm-prepare.sh first." >&2
      exit 1
    fi
  done

  QEMU_ARGS+=(
    -drive "if=none,media=cdrom,format=raw,readonly=on,file=$ISO_PATH,id=installer"
    -device scsi-cd,drive=installer,bus=scsi0.0,bootindex=0
    -drive "if=none,media=cdrom,format=raw,readonly=on,file=$SEED_ISO,id=autoinstall_seed"
    -device scsi-cd,drive=autoinstall_seed,bus=scsi0.0
    -drive "if=none,media=cdrom,format=raw,readonly=on,file=$PAYLOAD_ISO,id=runtime_profile"
    -device scsi-cd,drive=runtime_profile,bus=scsi0.0
  )
fi

echo "Opening $PRODUCT_NAME in QEMU ($MODE mode)..."
echo "Display: full-screen, resizable, zoom-to-fit enabled"
if [[ "$MODE" == "try" ]]; then
  echo "Storage: temporary snapshot; all changes are discarded at shutdown"
fi
echo "VM account: workspace"
echo "Development password: workspace"

exec "$QEMU_BIN" "${QEMU_ARGS[@]}"
