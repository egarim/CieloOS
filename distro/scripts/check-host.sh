#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:${PATH}"

missing=0
for tool in curl dotnet jq qemu-system-aarch64 xorriso; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "missing: $tool"
    missing=1
  else
    echo "found: $tool"
  fi
done

if [[ "$missing" -eq 1 ]]; then
  echo "Host is not ready for the Apple Silicon VM workflow yet."
  exit 1
fi

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  echo "warning: this V0.1 workflow is tuned for Apple Silicon macOS"
fi

echo "Host has the expected ARM64 VM tools."
