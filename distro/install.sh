#!/usr/bin/env bash
# Install Lun.Os from a release bundle onto Ubuntu (24.04+). Run as root from the
# unpacked bundle directory:
#
#   sudo ./install.sh --mode headless        # VPS / old machine, reach it + token
#   sudo ./install.sh --mode app             # your own machine, localhost only
#   sudo ./install.sh --mode kiosk           # boot into a fullscreen panel browser
#
# Options: --mode <headless|app|kiosk>  --port <5148>
#
# The three modes differ ONLY in bind address and whether a kiosk browser is
# installed — the runtime is identical. The first-owner claim is loopback-only, so
# you claim ON the box (a local/kiosk browser, or `lunos-claim` over SSH); after
# that you can sign in from anywhere with the token.
#
# NOTE: not yet validated on real hardware — the rootless-podman-under-systemd and
# kiosk paths are the parts to shake out on the target. Provider-free by default.
set -euo pipefail

MODE="headless"
PORT="5148"
CI=0   # --ci: container-safe install (no systemd/linger, minimal deps) for automated tests
while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) MODE="${2:?}"; shift 2 ;;
    --port) PORT="${2:?}"; shift 2 ;;
    --ci) CI=1; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
case "$MODE" in headless|app|kiosk) ;; *) echo "--mode must be headless|app|kiosk" >&2; exit 2 ;; esac

if [[ "$(id -u)" -ne 0 ]]; then echo "Run as root (sudo)." >&2; exit 1; fi
BUNDLE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test -x "$BUNDLE/bin/WorkspaceRuntime.Api" || { echo "Run this from the unpacked bundle (bin/WorkspaceRuntime.Api missing)." >&2; exit 1; }

# app/kiosk are single-machine → bind loopback; headless → bind all interfaces.
if [[ "$MODE" == "headless" ]]; then BIND="http://0.0.0.0:$PORT"; else BIND="http://127.0.0.1:$PORT"; fi

echo "==> [1/6] Dependencies"
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
if [[ "$CI" -eq 1 ]]; then
  # Automated test only needs what the runtime + helpers use; podman/session bits
  # can't be exercised in a plain container anyway.
  apt-get install -y --no-install-recommends curl ca-certificates
else
  apt-get install -y --no-install-recommends podman uidmap slirp4netns fuse-overlayfs curl ca-certificates
fi

echo "==> [2/6] Service user 'lunos' + rootless podman prerequisites"
if ! id -u lunos >/dev/null 2>&1; then
  useradd --system --create-home --home-dir /var/lib/lunos --shell /bin/bash lunos
fi
grep -q '^lunos:' /etc/subuid || usermod --add-subuids 100000-165535 lunos
grep -q '^lunos:' /etc/subgid || usermod --add-subgids 100000-165535 lunos
# linger (so /run/user/<uid> exists for rootless podman) needs systemd — skip in CI.
[[ "$CI" -eq 1 ]] || loginctl enable-linger lunos
LUNOS_UID="$(id -u lunos)"

echo "==> [3/6] Install to /opt/lunos"
install -d /opt/lunos
cp -a "$BUNDLE/bin" "$BUNDLE/panel" "$BUNDLE/surfaces" "$BUNDLE/config" /opt/lunos/
install -d -o lunos -g lunos /opt/lunos/.data
chown -R lunos:lunos /opt/lunos

echo "==> [4/6] Environment ($MODE, $BIND)"
install -d /etc/lunos
sed -e "s#UID_PLACEHOLDER#${LUNOS_UID}#" \
    -e "s#^ASPNETCORE_URLS=.*#ASPNETCORE_URLS=${BIND}#" \
    "$BUNDLE/lunos.env.example" > /etc/lunos/lunos.env
chmod 0640 /etc/lunos/lunos.env

echo "==> [5/6] systemd service"
cp "$BUNDLE/systemd/lunos-runtime.service" /etc/systemd/system/lunos-runtime.service
if [[ "$CI" -eq 1 ]]; then
  echo "    (--ci: service file installed but not started; the harness runs the binary directly)"
else
  systemctl daemon-reload
  systemctl enable --now lunos-runtime.service
fi

# On-box claim + add-user helpers (curl to loopback — no extra binary needed).
cat > /usr/local/bin/lunos-claim <<EOF
#!/usr/bin/env bash
# Claim the first owner on THIS machine (loopback-only). Usage: lunos-claim "Your Name"
set -euo pipefail
name="\${1:?Usage: lunos-claim \"Your Name\"}"
curl -fsS -XPOST "http://127.0.0.1:${PORT}/api/setup/claim" \
  -H 'Content-Type: application/json' -d "{\"name\": \"\${name}\"}"
echo
EOF
cat > /usr/local/bin/lunos-add-user <<EOF
#!/usr/bin/env bash
# Add a teammate. Usage: lunos-add-user "Their Name" <owner-token>
set -euo pipefail
name="\${1:?Usage: lunos-add-user \"Name\" <owner-token>}"
token="\${2:?owner token required}"
curl -fsS -XPOST "http://127.0.0.1:${PORT}/api/users" \
  -H "Authorization: Bearer \${token}" \
  -H 'Content-Type: application/json' -d "{\"name\": \"\${name}\"}"
echo
EOF
chmod +x /usr/local/bin/lunos-claim /usr/local/bin/lunos-add-user
install -m 0755 "$BUNDLE/lunos-selftest.sh" /usr/local/bin/lunos-selftest

echo "==> [6/6] Presentation mode: $MODE"
if [[ "$MODE" == "kiosk" && "$CI" -eq 0 ]]; then
  # Minimal Wayland kiosk: `cage` runs a single fullscreen app (chromium) as lunos.
  # (Least-packages GUI; shake this out on the actual hardware/GPU.)
  apt-get install -y --no-install-recommends cage seatd chromium-browser || \
    apt-get install -y --no-install-recommends cage seatd chromium || true
  systemctl enable seatd || true
  cat > /etc/systemd/system/lunos-kiosk.service <<EOF
[Unit]
Description=Lun.Os kiosk browser
After=lunos-runtime.service systemd-user-sessions.service
Wants=lunos-runtime.service

[Service]
User=lunos
PAMName=login
TTYPath=/dev/tty1
Environment=XDG_RUNTIME_DIR=/run/user/${LUNOS_UID}
# Wait for the runtime, then open the panel fullscreen.
ExecStartPre=/bin/sh -c 'until curl -fsS http://127.0.0.1:${PORT}/api/setup/status >/dev/null; do sleep 1; done'
ExecStart=/usr/bin/cage -- chromium --kiosk --no-first-run --disable-translate "http://127.0.0.1:${PORT}/"
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
  systemctl enable lunos-kiosk.service || true
  echo "    Kiosk service installed. It opens the panel on tty1 at boot."
fi

echo
echo "================ Lun.Os installed ($MODE) ================"
if [[ "$CI" -eq 0 ]]; then
  systemctl --no-pager --lines=0 status lunos-runtime.service || true
fi
echo
echo "Verify anytime with:  lunos-selftest            (non-destructive)"
echo "                      lunos-selftest --claim    (throwaway machine only)"
echo "First-owner claim (loopback-only — do it on this box):"
if [[ "$MODE" == "headless" ]]; then
  echo "  ssh in and run:   lunos-claim \"Your Name\""
  echo "  then from your laptop open:  http://<this-host-ip>:${PORT}/  and log in with the printed token"
else
  echo "  a local/kiosk browser at http://127.0.0.1:${PORT}/ shows the claim wizard"
  echo "  or run:  lunos-claim \"Your Name\""
fi
echo
echo "Sessions (console/desktop) need their podman images. If not present they build"
echo "on first use; to prebuild, run the distro image Containerfiles as the 'lunos' user."
echo "Add an AI provider anytime from the panel's Models tab (no restart)."
