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
mkdir -p "$STAGE/bin" "$STAGE/panel" "$STAGE/surfaces" "$STAGE/engines" "$STAGE/config" "$STAGE/systemd"

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
# Both entries, and both actually loadable. "The file exists" is not the same
# claim: a stale or empty index.html passes that check and ships a release that
# opens to a blank page. So follow the module script each page declares and prove
# the bundle it names was staged too.
check_panel_page() {
  page="$1"
  path="$STAGE/panel/$page"
  test -f "$path" || { echo "ERROR: panel $page missing" >&2; exit 1; }

  asset=$(grep -o '<script[^>]*src="[^"]*"' "$path" | head -1 | sed 's/.*src="//; s/".*//')
  test -n "$asset" || { echo "ERROR: panel $page declares no module script" >&2; exit 1; }

  case "$asset" in
    /*) asset_path="$STAGE/panel${asset}" ;;
     *) asset_path="$STAGE/panel/${asset}" ;;
  esac
  test -s "$asset_path" || { echo "ERROR: panel $page points at $asset, which was not staged" >&2; exit 1; }
}

check_panel_page index.html
check_panel_page portal.html

echo "==> Staging surfaces + engines + config"
cp "$ROOT"/surfaces/*.surface.json "$STAGE/surfaces/"
# The translations travel WITH the manifests they translate. A glob for
# *.surface.json alone would ship a bundle whose consent prompts are English
# everywhere, and it would look like it worked — the panel falls back rather than
# failing, so nobody would see a bug, just a machine that never speaks Russian.
if [[ -d "$ROOT/surfaces/i18n" ]]; then
  mkdir -p "$STAGE/surfaces/i18n"
  cp -a "$ROOT/surfaces/i18n/." "$STAGE/surfaces/i18n/"
fi
# Engine manifests travel for the same reason surfaces do: FileEngineCatalog reads
# <bundle>/engines, so a release without them is a machine where "add an engine"
# lists nothing and the panel looks broken rather than empty. This is the #49
# shape — a feature that works in a checkout and has never existed on an installed
# machine, with nothing failing loudly enough to notice.
if [[ -d "$ROOT/engines" ]]; then
  mkdir -p "$STAGE/engines"
  cp "$ROOT"/engines/*.engine.json "$STAGE/engines/"
fi
# The same shape again, and this one cost more than the engines did. The console
# image ships /usr/local/bin/websearch and the agent's prompt tells it to use it;
# websearch queries a SearXNG service whose setup script is distro/services/searxng
# /run.sh, referenced nowhere in install.sh and never staged here. So the search
# tool has never worked on ANY installed machine — every benchmark run against
# OpenClaw was made by an agent whose only research faculty was unplugged, and
# nothing failed loudly enough to notice.
if [[ -d "$ROOT/distro/services" ]]; then
  mkdir -p "$STAGE/services"
  cp -a "$ROOT/distro/services/." "$STAGE/services/"
fi
cp "$ROOT/config/branding.json" "$STAGE/config/branding.json"
# Local inference: the config names a model registry, and the registry names the
# per-provider manifests. All three have to travel or /api/inference/status reports
# "not configured" on every installed layout — which it did, because only a git
# checkout ever had them. Flattened out of distro/ so the runtime finds them beside
# the binary, the same shape branding.json already uses.
cp "$ROOT/distro/config/local-inference.json" "$STAGE/config/local-inference.json"
mkdir -p "$STAGE/models"
cp -a "$ROOT/distro/models/." "$STAGE/models/"

echo "==> Staging installer + foreground launcher + service unit + self-test"
cp "$ROOT/distro/install.sh" "$STAGE/install.sh"
cp "$ROOT/distro/run.sh" "$STAGE/run.sh"
cp "$ROOT/distro/scripts/cielo-selftest.sh" "$STAGE/cielo-selftest.sh"
cp "$ROOT/distro/services/cielo-runtime.service" "$STAGE/systemd/cielo-runtime.service"
cp "$ROOT/distro/config/cielo.env.example" "$STAGE/cielo.env.example"
# The session images are BUILT ON THE TARGET so they match its architecture and
# carry the agent's tooling; install.sh needs their Containerfiles to do that.
mkdir -p "$STAGE/images"
cp -a "$ROOT/distro/images/." "$STAGE/images/"
cp "$ROOT/distro/RELEASE-README.md" "$STAGE/README.md" 2>/dev/null || true
# The third-party attribution rides in the bundle so it can be placed at
# /opt/cielo/THIRD-PARTY.md by install.sh alongside the installed runtime.
cp "$ROOT/THIRD-PARTY.md" "$STAGE/THIRD-PARTY.md"
# CieloOS is AGPL-3.0 (#25), and section 4 requires the licence to travel with
# the program — a tarball that omits it is not a licensed copy, it is an
# unlicensed one. Not `|| true`: a release without its licence must not build.
cp "$ROOT/LICENSE" "$STAGE/LICENSE"
# The animated "Installing CieloOS..." page and the first-run auto-claim used by
# the one-liner install.
cp "$ROOT/distro/scripts/cielo-install-ui.sh" "$STAGE/cielo-install-ui.sh"
cp "$ROOT/distro/scripts/cielo-first-run.sh" "$STAGE/cielo-first-run.sh"
chmod +x "$STAGE/install.sh" "$STAGE/run.sh" "$STAGE/cielo-selftest.sh" "$STAGE/cielo-install-ui.sh" "$STAGE/cielo-first-run.sh"

TARBALL="$OUT/cielo-$ARCH.tar.gz"
echo "==> Packing $TARBALL"
tar czf "$TARBALL" -C "$OUT" cielo

SIZE="$(du -sh "$TARBALL" | cut -f1)"
echo "==> Done: $TARBALL ($SIZE)"
echo "    On the target: tar xzf $(basename "$TARBALL") && sudo ./cielo/install.sh --mode headless"
echo "    Or as an app, no root/systemd:  ./cielo/run.sh  → http://localhost:5148/"
