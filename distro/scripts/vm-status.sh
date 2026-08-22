#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:${PATH}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
STATE_DIR="${WR_VM_STATE_DIR:-$ROOT_DIR/distro/.vm}"
PID_PATH="$STATE_DIR/runtime/qemu.pid"

if [[ ! -f "$PID_PATH" ]]; then
  echo "VM is not running."
  exit 1
fi

VM_PID="$(tr -dc '0-9' < "$PID_PATH")"
if [[ -n "$VM_PID" ]] && kill -0 "$VM_PID" 2>/dev/null; then
  echo "VM is running (process $VM_PID)."
  echo "SSH: ssh -p 2222 workspace@127.0.0.1"
  echo "API: http://127.0.0.1:5148"
  exit 0
fi

echo "VM is not running."
exit 1
