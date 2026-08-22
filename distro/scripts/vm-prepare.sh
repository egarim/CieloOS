#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:${PATH}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
STATE_DIR="${WR_VM_STATE_DIR:-$ROOT_DIR/distro/.vm}"

# shellcheck source=../vm/defaults.env
source "$ROOT_DIR/distro/vm/defaults.env"

for tool in curl dotnet jq xorriso; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "Required tool is missing: $tool" >&2
    exit 1
  fi
done

QEMU_BIN="$(command -v qemu-system-aarch64 || true)"
if [[ -z "$QEMU_BIN" ]]; then
  echo "Required tool is missing: qemu-system-aarch64" >&2
  exit 1
fi
QEMU_IMG="$(cd "$(dirname "$QEMU_BIN")" && pwd)/qemu-img"
if [[ ! -x "$QEMU_IMG" ]]; then
  echo "Could not locate qemu-img beside qemu-system-aarch64." >&2
  exit 1
fi

mkdir -p "$STATE_DIR/downloads" "$STATE_DIR/media" "$STATE_DIR/runtime"

ISO_PATH="$STATE_DIR/downloads/$UBUNTU_ISO_FILENAME"
SUMS_PATH="$STATE_DIR/downloads/SHA256SUMS"
DISK_PATH="$STATE_DIR/runtime/system.qcow2"

echo "Downloading the Ubuntu $UBUNTU_RELEASE ARM64 installer when needed..."
if command -v aria2c >/dev/null 2>&1; then
  aria2c \
    --allow-overwrite=true \
    --auto-file-renaming=false \
    --continue=true \
    --dir="$STATE_DIR/downloads" \
    --max-connection-per-server=8 \
    --min-split-size=8M \
    --out="$UBUNTU_ISO_FILENAME" \
    --split=8 \
    "$UBUNTU_ISO_URL"
else
  curl --fail --location --continue-at - --output "$ISO_PATH" "$UBUNTU_ISO_URL"
fi
curl --fail --location --output "$SUMS_PATH" "$UBUNTU_SHA256_URL"

EXPECTED_SHA=""
while read -r candidate_sha candidate_name; do
  candidate_name="${candidate_name#\*}"
  if [[ "$candidate_name" == "$UBUNTU_ISO_FILENAME" ]]; then
    EXPECTED_SHA="$candidate_sha"
    break
  fi
done < "$SUMS_PATH"

if [[ -z "$EXPECTED_SHA" ]]; then
  echo "Could not find $UBUNTU_ISO_FILENAME in the official checksum file." >&2
  exit 1
fi

ACTUAL_SHA="$(shasum -a 256 "$ISO_PATH" | awk '{print $1}')"
if [[ "$ACTUAL_SHA" != "$EXPECTED_SHA" ]]; then
  echo "Installer checksum does not match Ubuntu's published checksum." >&2
  exit 1
fi

POWERSHELL_PROFILE="$ROOT_DIR/distro/profiles/dotnet-automation.json"
POWERSHELL_URL="$(jq -r '.packages.powershell.source' "$POWERSHELL_PROFILE")"
POWERSHELL_SHA256="$(jq -r '.packages.powershell.sha256' "$POWERSHELL_PROFILE")"
POWERSHELL_ARCHIVE="${POWERSHELL_URL##*/}"
POWERSHELL_PATH="$STATE_DIR/downloads/$POWERSHELL_ARCHIVE"

if [[ ! -f "$POWERSHELL_PATH" ]] || \
  [[ "$(shasum -a 256 "$POWERSHELL_PATH" | awk '{print $1}')" != "$POWERSHELL_SHA256" ]]; then
  echo "Downloading the pinned PowerShell ARM64 archive..."
  rm -f "$POWERSHELL_PATH"
  curl --fail --location --retry 3 --retry-all-errors \
    --output "$POWERSHELL_PATH" "$POWERSHELL_URL"
fi

if [[ "$(shasum -a 256 "$POWERSHELL_PATH" | awk '{print $1}')" != "$POWERSHELL_SHA256" ]]; then
  echo "PowerShell archive checksum does not match the pinned release checksum." >&2
  exit 1
fi

if [[ ! -f "$DISK_PATH" ]]; then
  "$QEMU_IMG" create -f qcow2 "$DISK_PATH" "${VM_DISK_GIB}G"
fi

TEMP_DIR="$(mktemp -d)"
cleanup() {
  rm -rf "$TEMP_DIR"
}
trap cleanup EXIT

SEED_ROOT="$TEMP_DIR/seed"
PAYLOAD_ROOT="$TEMP_DIR/payload"
mkdir -p "$SEED_ROOT" "$PAYLOAD_ROOT/repository/distro" "$PAYLOAD_ROOT/repository/runtime/bin"
cp "$ROOT_DIR/distro/autoinstall/user-data" "$SEED_ROOT/user-data"
cp "$ROOT_DIR/distro/autoinstall/meta-data" "$SEED_ROOT/meta-data"

for payload_item in config installer models packages profiles services tools; do
  cp -R "$ROOT_DIR/distro/$payload_item" "$PAYLOAD_ROOT/repository/distro/$payload_item"
done
mkdir -p "$PAYLOAD_ROOT/repository/distro/vendor"
cp "$POWERSHELL_PATH" "$PAYLOAD_ROOT/repository/distro/vendor/$POWERSHELL_ARCHIVE"
mkdir -p "$PAYLOAD_ROOT/repository/config"
cp "$ROOT_DIR/config/branding.json" "$PAYLOAD_ROOT/repository/config/branding.json"
mkdir -p "$PAYLOAD_ROOT/repository/surfaces"
cp "$ROOT_DIR"/surfaces/*.surface.json "$PAYLOAD_ROOT/repository/surfaces/"

echo "Publishing the ARM64 runtime included with the VM setup media..."
dotnet publish "$ROOT_DIR/src/backend/WorkspaceRuntime.Api/WorkspaceRuntime.Api.csproj" \
  --configuration Release \
  --runtime linux-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:DebugType=None \
  --output "$PAYLOAD_ROOT/repository/runtime/bin"

dotnet publish "$ROOT_DIR/src/backend/WorkspaceRuntime.Setup/WorkspaceRuntime.Setup.csproj" \
  --configuration Release \
  --runtime linux-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:DebugType=None \
  --output "$PAYLOAD_ROOT/repository/runtime/bin"

dotnet publish "$ROOT_DIR/src/backend/WorkspaceRuntime.Agent/WorkspaceRuntime.Agent.csproj" \
  --configuration Release \
  --runtime linux-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:DebugType=None \
  --output "$PAYLOAD_ROOT/repository/runtime/bin"

SEED_ISO="$STATE_DIR/media/autoinstall-seed.iso"
PAYLOAD_ISO="$STATE_DIR/media/runtime-profile.iso"
rm -f "$SEED_ISO" "$PAYLOAD_ISO"
xorriso -as mkisofs -quiet -rock -joliet -volid CIDATA -output "$SEED_ISO" "$SEED_ROOT"
xorriso -as mkisofs -quiet -rock -joliet -volid WR_CONFIG -output "$PAYLOAD_ISO" "$PAYLOAD_ROOT/repository"

echo
echo "VM media is ready."
echo "Installer: $ISO_PATH"
echo "Disk:      $DISK_PATH"
echo "Next:      ./distro/scripts/vm-run.sh --install"
