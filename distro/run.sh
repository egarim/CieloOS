#!/usr/bin/env bash
# Run CieloOS in the foreground, straight from the unpacked release bundle —
# no root, no systemd, and the control plane's own state confined to the bundle
# dir. This is the "run it like an app" path (WSL2 on Windows, a laptop, a
# container):
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
# NOTE: if you do create sessions, podman stores their images and named volumes
# under ~/.local/share/containers, OUTSIDE this bundle — deleting the bundle dir
# removes the control-plane state, not those. See `podman volume ls`.
set -euo pipefail

PORT="${PORT:-5148}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) PORT="${2:?--port needs a value}"; shift 2 ;;
    -h|--help) awk 'NR>1 { if (!/^#/) exit; sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "Unknown option: $1 (use --port <n>)" >&2; exit 2 ;;
  esac
done

# A bad port would otherwise surface as an opaque runtime bind failure.
case "$PORT" in
  ''|*[!0-9]*) echo "Invalid port '$PORT': must be a whole number 1-65535." >&2; exit 2 ;;
esac
if [[ "$PORT" -lt 1 || "$PORT" -gt 65535 ]]; then
  echo "Invalid port '$PORT': must be in 1-65535." >&2; exit 2
fi

BUNDLE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test -x "$BUNDLE/bin/WorkspaceRuntime.Api" || {
  echo "Run this from the unpacked bundle ($BUNDLE/bin/WorkspaceRuntime.Api missing)." >&2; exit 1; }

# .data holds the SQLite DB and the bearer-token/model-key secrets, so keep it
# owner-only — the installed path gets this from install.sh + the service user.
umask 077

# Pin every state path to the bundle. SqlitePath/SecretsPath are set explicitly
# (not just derived from the root) so an inherited Database__SqlitePath or
# Auth__SecretsPath from a shell profile or an earlier install cannot silently
# redirect the DB or secrets somewhere outside this directory.
export WORKSPACE_RUNTIME_ROOT="$BUNDLE"
export Panel__Path="$BUNDLE/panel"
export Runtime__SeedDemo=false
export Database__Provider=sqlite
export Database__SqlitePath="$BUNDLE/.data/workspace-runtime.db"
export Auth__SecretsPath="$BUNDLE/.data/secrets"
export ASPNETCORE_URLS="http://127.0.0.1:${PORT}"
mkdir -p "$BUNDLE/.data"

echo "==> CieloOS (foreground, no root) — state in $BUNDLE/.data"
echo "    Open http://localhost:${PORT}/ — first run shows the claim wizard"
echo "    Health check from another shell:  $BUNDLE/cielo-selftest.sh --url http://127.0.0.1:${PORT}"
echo "    Ctrl-C to stop."
echo
cd "$BUNDLE"
exec "$BUNDLE/bin/WorkspaceRuntime.Api"
