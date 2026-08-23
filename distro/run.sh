#!/usr/bin/env bash
# Run CieloOS in the foreground, straight from the unpacked release bundle —
# no root, no systemd, nothing written outside the bundle dir. This is the
# "run it like an app" path (WSL2 on Windows, a laptop, a container):
#
#   tar xzf cielo-linux-arm64.tar.gz
#   ./cielo/run.sh                 # or: PORT=6000 ./cielo/run.sh
#
# Same runtime as `install.sh --mode app`; the only difference is that this one
# stays in your terminal and keeps its state in <bundle>/.data instead of
# /opt/cielo + a systemd unit. Ctrl-C stops it. For a machine-wide service that
# survives reboot, use install.sh (needs root and systemd).
#
# Sessions (console/desktop) additionally need rootless podman on the host; the
# control plane — panel, claim, Models, spreadsheet, audit — runs without it.
set -euo pipefail

PORT="${PORT:-5148}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) PORT="${2:?--port needs a value}"; shift 2 ;;
    -h|--help) awk 'NR>1 { if (!/^#/) exit; sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "Unknown option: $1 (use --port <n>)" >&2; exit 2 ;;
  esac
done

BUNDLE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test -x "$BUNDLE/bin/WorkspaceRuntime.Api" || {
  echo "Run this from the unpacked bundle ($BUNDLE/bin/WorkspaceRuntime.Api missing)." >&2; exit 1; }

# Everything the runtime resolves off the bundle: panel, surfaces, branding, and
# .data (SQLite + secrets). No /opt, no sudo.
export WORKSPACE_RUNTIME_ROOT="$BUNDLE"
export Panel__Path="$BUNDLE/panel"
export Runtime__SeedDemo=false
export Database__Provider=sqlite
export ASPNETCORE_URLS="http://127.0.0.1:${PORT}"
mkdir -p "$BUNDLE/.data"

echo "==> CieloOS (foreground, no root) — state in $BUNDLE/.data"
echo "    Open http://localhost:${PORT}/ — first run shows the claim wizard"
echo "    Health check from another shell:  $BUNDLE/cielo-selftest.sh --url http://127.0.0.1:${PORT}"
echo "    Ctrl-C to stop."
echo
cd "$BUNDLE"
exec "$BUNDLE/bin/WorkspaceRuntime.Api"
