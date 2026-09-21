#!/usr/bin/env bash
# CieloOS one-line installer.
#
#   curl -fsSL https://raw.githubusercontent.com/egarim/CieloOS/main/distro/get.sh | sudo bash
#
# Fetches the latest release for this machine's architecture, unpacks it, and runs
# the bundled install.sh. Everything it does is what the README already told you to
# do by hand; the point is that a person with a fresh VPS and an SSH session should
# not have to build a tarball on another machine first.
#
# Options, as environment variables because this runs through a pipe and has no argv:
#   CIELO_MODE=headless|app|kiosk   (default headless — this is usually a VPS)
#   CIELO_VERSION=v0.1.10           (default: the latest release)
#   CIELO_URL=https://.../x.tar.gz  (a specific bundle; skips the release lookup)
#   CIELO_PORT=5148
#   CIELO_CHAT=1                    (also install the Open WebUI chat; off by default)
set -euo pipefail

REPO="${CIELO_REPO:-egarim/CieloOS}"
MODE="${CIELO_MODE:-headless}"
PORT="${CIELO_PORT:-5148}"
# This script forwarded exactly --mode and --port, so install.sh's chat flag was
# unreachable from the one-line install — the only path the README documents.
CHAT="${CIELO_CHAT:-0}"

die() { echo "cielo: $*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run this with sudo — it installs a system service.

  curl -fsSL https://raw.githubusercontent.com/$REPO/main/distro/get.sh | sudo bash"

# Refuse early and clearly rather than failing two minutes in on a missing package.
command -v curl >/dev/null 2>&1 || die "curl is required."
command -v tar  >/dev/null 2>&1 || die "tar is required."
[ -r /etc/os-release ] || die "cannot identify this system (no /etc/os-release)."
. /etc/os-release
case "${ID:-}:${VERSION_ID:-}" in
  ubuntu:24.*|ubuntu:25.*|ubuntu:26.*) ;;
  *) echo "cielo: warning — tested on Ubuntu 24.04+, found ${PRETTY_NAME:-unknown}. Continuing." >&2 ;;
esac

case "$(dpkg --print-architecture 2>/dev/null || uname -m)" in
  amd64|x86_64)  ARCH=linux-x64 ;;
  arm64|aarch64) ARCH=linux-arm64 ;;
  *) die "unsupported architecture: $(uname -m). CieloOS ships linux-x64 and linux-arm64." ;;
esac

if [ -n "${CIELO_URL:-}" ]; then
  URL="$CIELO_URL"
  echo "==> Using the bundle you named"
else
  VERSION="${CIELO_VERSION:-}"
  if [ -z "$VERSION" ]; then
    echo "==> Finding the latest release of $REPO"
    # Deliberately plain: no jq on a fresh VPS.
    VERSION="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
      | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)"
    [ -n "$VERSION" ] || die "could not find a release. Pass CIELO_VERSION=vX.Y.Z, or CIELO_URL=<tarball>."
  fi
  URL="https://github.com/$REPO/releases/download/$VERSION/cielo-$ARCH.tar.gz"
  echo "==> CieloOS $VERSION ($ARCH)"
fi

WORK="$(mktemp -d)"
# mktemp -d is 0700 and we are root, so no other account can even traverse into it.
# install.sh drops to the service user to start the search service from this
# directory, and with 0700 that read failed - and got reported as "the search service
# did not start", which is true and says nothing about the actual cause. A directory
# that only ever holds a public release tarball has no reason to be private.
chmod 755 "$WORK"
# Leave the unpacked bundle behind on failure — install.sh is in it, and someone
# debugging should not have to download 50MB again to read the script that broke.
trap '[ "${KEEP:-0}" = "1" ] || rm -rf "$WORK"' EXIT

echo "==> Downloading"
curl -fSL --progress-bar -o "$WORK/cielo.tar.gz" "$URL" \
  || die "download failed: $URL"

echo "==> Unpacking"
tar xzf "$WORK/cielo.tar.gz" -C "$WORK" || die "the download is not a readable tarball."
[ -x "$WORK/cielo/install.sh" ] || { KEEP=1; die "this bundle has no install.sh — left it in $WORK"; }

CHAT_ARGS=""
case "$CHAT" in
  1|true|yes|on) CHAT_ARGS="--chat" ;;
  0|false|no|off|"") ;;
  *) die "CIELO_CHAT must be 1 or 0, got: $CHAT" ;;
esac

echo "==> Installing (--mode $MODE --port $PORT $CHAT_ARGS)"
KEEP=1
# Unquoted on purpose: quoted, an empty CHAT_ARGS becomes an empty argument and
# install.sh exits 2 on "Unknown option: ". The value is one of the fixed strings
# the case above allows, so there is nothing here to word-split badly.
# shellcheck disable=SC2086
"$WORK/cielo/install.sh" --mode "$MODE" --port "$PORT" $CHAT_ARGS
rm -rf "$WORK"
