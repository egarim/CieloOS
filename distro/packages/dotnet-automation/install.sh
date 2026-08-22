#!/usr/bin/env bash
set -euo pipefail

POWERSHELL_VERSION="7.6.4"
POWERSHELL_ARCHIVE="powershell-${POWERSHELL_VERSION}-linux-arm64.tar.gz"
POWERSHELL_URL="https://github.com/PowerShell/PowerShell/releases/download/v${POWERSHELL_VERSION}/${POWERSHELL_ARCHIVE}"
POWERSHELL_SHA256="d4ef2382fa452f2ccbdb48a01adbbce9ed64954872123970c16be6d086d1224b"
POWERSHELL_ROOT="/opt/microsoft/powershell/${POWERSHELL_VERSION}"

download_verified_archive() {
  local destination="$1"

  for attempt in 1 2 3; do
    rm -f "$destination"
    if curl --fail --location --retry 3 --retry-all-errors \
      --output "$destination" "$POWERSHELL_URL" && \
      echo "$POWERSHELL_SHA256  $destination" | sha256sum --check; then
      return 0
    fi

    echo "PowerShell archive verification failed (attempt $attempt of 3)." >&2
  done

  return 1
}

if ! command -v dotnet >/dev/null 2>&1; then
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y dotnet-sdk-10.0
fi

if [[ "$(dpkg --print-architecture)" != "arm64" ]]; then
  echo "The V0.1 PowerShell profile currently supports ARM64 only." >&2
  exit 1
fi

if [[ ! -x "$POWERSHELL_ROOT/pwsh" ]]; then
  temporary_directory="$(mktemp -d)"
  trap 'rm -rf "$temporary_directory"' EXIT
  archive_path="$temporary_directory/$POWERSHELL_ARCHIVE"

  if [[ -f "distro/vendor/$POWERSHELL_ARCHIVE" ]]; then
    cp "distro/vendor/$POWERSHELL_ARCHIVE" "$archive_path"
    echo "$POWERSHELL_SHA256  $archive_path" | sha256sum --check
  else
    download_verified_archive "$archive_path"
  fi

  install -d "$POWERSHELL_ROOT"
  tar -xzf "$archive_path" -C "$POWERSHELL_ROOT"
  chmod 0755 "$POWERSHELL_ROOT/pwsh"
fi

ln -sfn "$POWERSHELL_ROOT/pwsh" /usr/local/bin/pwsh

dotnet --info >/dev/null
pwsh -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' >/dev/null

install -d /opt/workspace-runtime/profiles
install -d /opt/workspace-runtime/tools

if [[ -f distro/profiles/dotnet-automation.json ]]; then
  cp distro/profiles/dotnet-automation.json /opt/workspace-runtime/profiles/dotnet-automation.json
fi

for manifest in distro/tools/dotnet.*.json distro/tools/powershell.run.json; do
  if [[ -f "$manifest" ]]; then
    cp "$manifest" /opt/workspace-runtime/tools/
  fi
done

echo ".NET 10 file-based apps and PowerShell $POWERSHELL_VERSION are installed."
