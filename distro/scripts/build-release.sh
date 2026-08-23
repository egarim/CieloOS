#!/usr/bin/env bash
# Build a self-contained Lun.Os release bundle: the runtime (self-contained, no
# .NET needed on the target) + the built panel + surfaces + config, tarred up.
# Carry the tarball to any Ubuntu (VPS / old machine) or run it locally, then run
# the bundled install.sh. See distro/install.sh for the target-side steps.
#
#   distro/scripts/build-release.sh [linux-x64|linux-arm64]   (default linux-x64)
set -euo pipefail

ARCH="${1:-linux-x64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="$ROOT/release"
STAGE="$OUT/lunos"

echo "==> Lun.Os release bundle ($ARCH)"
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

echo "==> Staging installer + service unit + self-test"
cp "$ROOT/distro/install.sh" "$STAGE/install.sh"
cp "$ROOT/distro/scripts/lunos-selftest.sh" "$STAGE/lunos-selftest.sh"
cp "$ROOT/distro/services/lunos-runtime.service" "$STAGE/systemd/lunos-runtime.service"
cp "$ROOT/distro/config/lunos.env.example" "$STAGE/lunos.env.example"
cp "$ROOT/distro/RELEASE-README.md" "$STAGE/README.md" 2>/dev/null || true
chmod +x "$STAGE/install.sh" "$STAGE/lunos-selftest.sh"

TARBALL="$OUT/lunos-$ARCH.tar.gz"
echo "==> Packing $TARBALL"
tar czf "$TARBALL" -C "$OUT" lunos

SIZE="$(du -sh "$TARBALL" | cut -f1)"
echo "==> Done: $TARBALL ($SIZE)"
echo "    On the target: tar xzf $(basename "$TARBALL") && sudo ./lunos/install.sh --mode headless"
