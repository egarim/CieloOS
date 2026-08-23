#!/usr/bin/env bash
# Build a self-contained CieloOS release bundle: the runtime (self-contained, no
# .NET needed on the target) + the built panel + surfaces + config, tarred up.
# Carry the tarball to any Ubuntu (VPS / old machine) or run it locally, then run
# the bundled install.sh (systemd service) — or run.sh for a foreground app with no
# root and no systemd (WSL2 on Windows-on-ARM: see docs/wsl-quickstart.md).
#
#   distro/scripts/build-release.sh [linux-x64|linux-arm64]   (default linux-x64)
set -euo pipefail

ARCH="${1:-linux-x64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="$ROOT/release"
STAGE="$OUT/cielo"

echo "==> CieloOS release bundle ($ARCH)"
rm -rf "$STAGE"
mkdir -p "$STAGE/bin" "$STAGE/panel" "$STAGE/surfaces" "$STAGE/config" "$STAGE/systemd"

echo "==> Publishing runtime (self-contained, $ARCH)"
dotnet publish "$ROOT/src/backend/WorkspaceRuntime.Api/WorkspaceRuntime.Api.csproj" \
  -c Release -r "$ARCH" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -p:InvariantGlobalization=true \
  -o "$STAGE/bin" >/dev/null
# The service ExecStart target — fail loudly if the assembly name ever changes.
test -f "$STAGE/bin/WorkspaceRuntime.Api" || { echo "ERROR: runtime executable missing" >&2; exit 1; }
chmod +x "$STAGE/bin/WorkspaceRuntime.Api"

echo "==> Building panel"
( cd "$ROOT/src/frontend" && npm install --no-audit --no-fund >/dev/null 2>&1 && npm run build >/dev/null )
cp -a "$ROOT/src/frontend/dist/." "$STAGE/panel/"
test -f "$STAGE/panel/index.html" || { echo "ERROR: panel index.html missing" >&2; exit 1; }

echo "==> Staging surfaces + config"
cp "$ROOT"/surfaces/*.surface.json "$STAGE/surfaces/"
cp "$ROOT/config/branding.json" "$STAGE/config/branding.json"

echo "==> Staging installer + foreground launcher + service unit + self-test"
cp "$ROOT/distro/install.sh" "$STAGE/install.sh"
cp "$ROOT/distro/run.sh" "$STAGE/run.sh"
cp "$ROOT/distro/scripts/cielo-selftest.sh" "$STAGE/cielo-selftest.sh"
cp "$ROOT/distro/services/cielo-runtime.service" "$STAGE/systemd/cielo-runtime.service"
cp "$ROOT/distro/config/cielo.env.example" "$STAGE/cielo.env.example"
cp "$ROOT/distro/RELEASE-README.md" "$STAGE/README.md" 2>/dev/null || true
chmod +x "$STAGE/install.sh" "$STAGE/run.sh" "$STAGE/cielo-selftest.sh"

TARBALL="$OUT/cielo-$ARCH.tar.gz"
echo "==> Packing $TARBALL"
tar czf "$TARBALL" -C "$OUT" cielo

SIZE="$(du -sh "$TARBALL" | cut -f1)"
echo "==> Done: $TARBALL ($SIZE)"
echo "    On the target: tar xzf $(basename "$TARBALL") && sudo ./cielo/install.sh --mode headless"
echo "    Or as an app, no root/systemd:  ./cielo/run.sh  → http://localhost:5148/"
