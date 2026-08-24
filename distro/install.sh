#!/usr/bin/env bash
# Install CieloOS from a release bundle onto Ubuntu (24.04+). Run as root from the
# unpacked bundle directory:
#
#   sudo ./install.sh --mode headless        # VPS / old machine, reach it + token
#   sudo ./install.sh --mode app             # your own machine, localhost only
#   sudo ./install.sh --mode kiosk           # boot into a fullscreen panel browser
#
# Options: --mode <headless|app|kiosk>  --port <5148>  --no-chat
#
# The three modes differ ONLY in bind address and whether a kiosk browser is
# installed — the runtime is identical. The first-owner claim is loopback-only, so
# you claim ON the box (a local/kiosk browser, or `cielo-claim` over SSH); after
# that you can sign in from anywhere with the token.
#
# NOTE: not yet validated on real hardware — the rootless-podman-under-systemd and
# kiosk paths are the parts to shake out on the target. Provider-free by default.
set -euo pipefail

MODE="headless"
PORT="5148"
CI=0        # --ci: container-safe install (no systemd/linger, minimal deps) for automated tests
SKIP_IMAGES=0 # --skip-images: do not build the session images (faster install; sessions
            # will not start until someone builds them)
NO_CHAT=0   # --no-chat: do not install the Open WebUI chat service or link it from the panel
OFFLINE=0   # --offline: install into a not-yet-running system (autoinstall in-target/chroot):
            # enable units + linger via files, never start/daemon-reload — first boot activates.
while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) MODE="${2:?}"; shift 2 ;;
    --port) PORT="${2:?}"; shift 2 ;;
    --ci) CI=1; shift ;;
    --offline) OFFLINE=1; shift ;;
    --skip-images) SKIP_IMAGES=1; shift ;;
    --no-chat) NO_CHAT=1; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
case "$MODE" in headless|app|kiosk) ;; *) echo "--mode must be headless|app|kiosk" >&2; exit 2 ;; esac
# LIVE = a running system where we can start/verify services now.
LIVE=1; { [[ "$CI" -eq 1 ]] || [[ "$OFFLINE" -eq 1 ]]; } && LIVE=0

# Enable a systemd unit whether the system is running (systemctl) or not (symlink,
# searching the vendor unit dirs for package-provided units like seatd).
enable_unit() {
  local unit="$1"
  if [[ "$LIVE" -eq 1 ]]; then
    systemctl enable "$unit"
    return
  fi
  local src="/etc/systemd/system/$unit"
  [[ -f "$src" ]] || src="/lib/systemd/system/$unit"
  [[ -f "$src" ]] || src="/usr/lib/systemd/system/$unit"
  install -d /etc/systemd/system/multi-user.target.wants
  ln -sf "$src" "/etc/systemd/system/multi-user.target.wants/$unit"
}

if [[ "$(id -u)" -ne 0 ]]; then echo "Run as root (sudo)." >&2; exit 1; fi
BUNDLE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test -x "$BUNDLE/bin/WorkspaceRuntime.Api" || { echo "Run this from the unpacked bundle (bin/WorkspaceRuntime.Api missing)." >&2; exit 1; }

# app/kiosk are single-machine → bind loopback; headless → bind all interfaces.
if [[ "$MODE" == "headless" ]]; then BIND="http://0.0.0.0:$PORT"; else BIND="http://127.0.0.1:$PORT"; fi

echo "==> [1/9] Dependencies"
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
if [[ "$CI" -eq 1 ]]; then
  # Automated test only needs what the runtime + helpers use; podman/session bits
  # can't be exercised in a plain container anyway.
  apt-get install -y --no-install-recommends curl ca-certificates
else
  apt-get install -y --no-install-recommends podman uidmap slirp4netns fuse-overlayfs curl ca-certificates
fi

echo "==> [2/9] Service user 'cielo' + rootless podman prerequisites"
if ! id -u cielo >/dev/null 2>&1; then
  useradd --system --create-home --home-dir /var/lib/cielo --shell /bin/bash cielo
fi
grep -q '^cielo:' /etc/subuid || usermod --add-subuids 100000-165535 cielo
grep -q '^cielo:' /etc/subgid || usermod --add-subgids 100000-165535 cielo
# linger (so /run/user/<uid> exists for rootless podman): loginctl on a live system,
# a marker file when installing offline; skipped entirely in CI.
if [[ "$LIVE" -eq 1 ]]; then
  loginctl enable-linger cielo
elif [[ "$OFFLINE" -eq 1 ]]; then
  install -d /var/lib/systemd/linger && : > /var/lib/systemd/linger/cielo
fi
CIELO_UID="$(id -u cielo)"

echo "==> [3/9] Session images (built here so they match this machine's architecture)"
# The desktop Containerfile takes the ONLYOFFICE package as a build arg and defaults
# to arm64; on an x64 target that would install a foreign-architecture .deb, which
# either fails or gets masked by apt-get -f and silently ships no editor.
case "$(dpkg --print-architecture 2>/dev/null || uname -m)" in
  arm64|aarch64) OO_DEB="https://download.onlyoffice.com/install/desktop/editors/linux/onlyoffice-desktopeditors_arm64.deb" ;;
  *)             OO_DEB="https://download.onlyoffice.com/install/desktop/editors/linux/onlyoffice-desktopeditors_amd64.deb" ;;
esac

# The runtime now defaults to these local tags, so an install that cannot build them
# has no working sessions at all. Stage the sources and a first-boot unit that builds
# whatever is missing: that covers --offline (chroot, no podman yet) and recovers from
# a build that failed here.
if [[ -d "$BUNDLE/images" ]]; then
  install -d -o cielo -g cielo /var/lib/cielo/images
  cp -a "$BUNDLE/images/." /var/lib/cielo/images/
  chown -R cielo:cielo /var/lib/cielo/images
  cat > /etc/systemd/system/cielo-session-images.service <<UNIT
[Unit]
Description=Build the CieloOS session images if they are missing
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
User=cielo
Environment=XDG_RUNTIME_DIR=/run/user/${CIELO_UID}
ExecStart=/usr/local/bin/cielo-build-session-images
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
UNIT
  cat > /usr/local/bin/cielo-build-session-images <<SCRIPT
#!/usr/bin/env bash
# Builds any missing session image. Safe to re-run: present images are left alone.
set -euo pipefail
OO_DEB="${OO_DEB}"
for img in console desktop; do
  if podman image exists "localhost/lunos-\$img:latest"; then continue; fi
  args=(build -t "localhost/lunos-\$img:latest")
  if [[ "\$img" == "desktop" ]]; then args+=(--build-arg "ONLYOFFICE_DEB=\${OO_DEB}"); fi
  podman "\${args[@]}" "/var/lib/cielo/images/\$img"
done
SCRIPT
  chmod +x /usr/local/bin/cielo-build-session-images
  # Not under --ci: that mode installs no podman, so the unit could only fail.
  [[ "$CI" -eq 1 ]] || enable_unit cielo-session-images.service || true
fi

if [[ "$CI" -eq 1 ]]; then
  echo "    (skipped: --ci)"
elif [[ "$OFFLINE" -eq 1 ]]; then
  echo "    (deferred: --offline; cielo-session-images.service builds them on first boot)"
elif [[ "$SKIP_IMAGES" -eq 1 ]]; then
  echo "    (skipped: --skip-images; cielo-session-images.service will build them at next boot)"
elif [[ ! -d "$BUNDLE/images" ]]; then
  echo "    (no images/ in this bundle - skipping)"
else
  echo "    building now (this takes a while); first boot would otherwise do it"
  runuser -u cielo -- env XDG_RUNTIME_DIR="/run/user/${CIELO_UID}" \
    /usr/local/bin/cielo-build-session-images >/dev/null || {
      echo "    WARNING: image build failed here; cielo-session-images.service retries at boot." >&2; }
fi

echo "==> [4/9] Session restart policy"
# Sessions are created with --restart=unless-stopped; podman-restart.service is what
# acts on that after a reboot. Without it the runtime comes back and every session is
# dead. This is a USER unit for cielo, so offline installs get the wants-symlink
# written directly rather than being skipped: systemctl cannot run in a chroot.
if [[ "$CI" -eq 1 ]]; then
  echo "    (skipped: --ci)"
elif [[ "$OFFLINE" -eq 1 ]]; then
  install -d -o cielo -g cielo /var/lib/cielo/.config/systemd/user/default.target.wants
  ln -sf /usr/lib/systemd/user/podman-restart.service \
    /var/lib/cielo/.config/systemd/user/default.target.wants/podman-restart.service
  chown -h cielo:cielo /var/lib/cielo/.config/systemd/user/default.target.wants/podman-restart.service
  echo "    podman-restart linked for cielo (activates on first boot)"
else
  runuser -u cielo -- env XDG_RUNTIME_DIR="/run/user/${CIELO_UID}" \
    systemctl --user enable podman-restart.service >/dev/null 2>&1 \
    && echo "    podman-restart enabled for cielo" \
    || echo "    WARNING: could not enable podman-restart; sessions will not survive a reboot." >&2
fi

echo "==> [5/9] Install to /opt/cielo"
install -d /opt/cielo
cp -a "$BUNDLE/bin" "$BUNDLE/panel" "$BUNDLE/surfaces" "$BUNDLE/config" /opt/cielo/
install -d -o cielo -g cielo /opt/cielo/.data
chown -R cielo:cielo /opt/cielo

echo "==> [6/9] Environment ($MODE, $BIND)"
install -d /etc/cielo
sed -e "s#UID_PLACEHOLDER#${CIELO_UID}#" \
    -e "s#^ASPNETCORE_URLS=.*#ASPNETCORE_URLS=${BIND}#" \
    "$BUNDLE/cielo.env.example" > /etc/cielo/cielo.env
# The panel reads Chat:Url at startup, so it has to be in the file BEFORE stage 7
# starts the runtime — appending it later would leave the panel without a chat
# link until the next restart.
if [[ "$NO_CHAT" -eq 0 ]]; then
  printf '\n# The chat UI installed by stage 8 (loopback; see /etc/cielo/chat.env).\nChat__Url=http://localhost:8080/\n' >> /etc/cielo/cielo.env
fi
chmod 0640 /etc/cielo/cielo.env

echo "==> [7/9] systemd service"
cp "$BUNDLE/systemd/cielo-runtime.service" /etc/systemd/system/cielo-runtime.service
if [[ "$CI" -eq 1 ]]; then
  echo "    (--ci: service file installed but not started; the harness runs the binary directly)"
else
  enable_unit cielo-runtime.service
  if [[ "$LIVE" -eq 1 ]]; then
    systemctl daemon-reload
    systemctl restart cielo-runtime.service
  else
    echo "    (--offline: enabled; starts on first boot)"
  fi
fi

# On-box claim + add-user helpers (curl to loopback — no extra binary needed).
cat > /usr/local/bin/cielo-claim <<EOF
#!/usr/bin/env bash
# Claim the first owner on THIS machine (loopback-only). Usage: cielo-claim "Your Name"
set -euo pipefail
name="\${1:?Usage: cielo-claim \"Your Name\"}"
curl -fsS -XPOST "http://127.0.0.1:${PORT}/api/setup/claim" \
  -H 'Content-Type: application/json' -d "{\"name\": \"\${name}\"}"
echo
EOF
cat > /usr/local/bin/cielo-add-user <<EOF
#!/usr/bin/env bash
# Add a teammate. Usage: cielo-add-user "Their Name" <owner-token>
set -euo pipefail
name="\${1:?Usage: cielo-add-user \"Name\" <owner-token>}"
token="\${2:?owner token required}"
curl -fsS -XPOST "http://127.0.0.1:${PORT}/api/users" \
  -H "Authorization: Bearer \${token}" \
  -H 'Content-Type: application/json' -d "{\"name\": \"\${name}\"}"
echo
EOF
chmod +x /usr/local/bin/cielo-claim /usr/local/bin/cielo-add-user
install -m 0755 "$BUNDLE/cielo-selftest.sh" /usr/local/bin/cielo-selftest

echo "==> [8/9] Chat UI (Open WebUI against /v1/agent)"
# The agent endpoint has existed since V0.6 and nothing ever started a client for
# it, so the best chat in the product was invisible (issue #8). This installs one.
#
# Loopback only, deliberately. WEBUI_AUTH=False makes whoever opens the page the
# owner, because the runtime has no login yet (issue #9) — so it must not be
# reachable from the network. Change CHAT_HOST in /etc/cielo/chat.env only once
# that is no longer true; on a headless box, tunnel instead:
#   ssh -N -L 8080:127.0.0.1:8080 you@box
if [[ "$NO_CHAT" -eq 1 ]]; then
  # Opting out has to UNDO a previous install, not merely skip this one: a running
  # cielo-chat is an unauthenticated page holding the owner's token, and leaving it
  # up on a machine whose operator just said "no chat" would be the worst outcome.
  if [[ "$LIVE" -eq 1 ]]; then
    systemctl disable --now cielo-chat.service >/dev/null 2>&1 || true
    runuser -u cielo -- env XDG_RUNTIME_DIR="/run/user/${CIELO_UID}" \
      podman rm -f cielo-chat >/dev/null 2>&1 || true
  fi
  rm -f /etc/systemd/system/cielo-chat.service \
        /etc/systemd/system/multi-user.target.wants/cielo-chat.service \
        /usr/local/bin/cielo-chat-run
  # The panel must stop advertising a chat that is no longer there.
  sed -i '/^Chat__Url=/d' /etc/cielo/cielo.env 2>/dev/null || true
  [[ "$LIVE" -eq 1 ]] && systemctl daemon-reload || true
  echo "    (--no-chat: not installed; any previous chat service removed)"
  echo "    (its data volume, cielo-chat-data, is left alone — remove it yourself if you mean to)"
else
  install -d /etc/cielo
  if [[ ! -f /etc/cielo/chat.env ]]; then
    cat > /etc/cielo/chat.env <<ENV
# CieloOS chat (Open WebUI). CHAT_HOST is loopback because the chat has no login
# of its own: anyone who reaches it acts as the owner. See issue #9.
CHAT_HOST=127.0.0.1
CHAT_PORT=8080
CHAT_IMAGE=ghcr.io/open-webui/open-webui:main
# Whose agent the chat talks to. Empty = ask the runtime, which answers only while
# this box has exactly one human — with teammates it cannot know who you meant, so
# name the slug here (as printed by cielo-claim / cielo-add-user).
CHAT_OWNER=
ENV
    chmod 0644 /etc/cielo/chat.env
  fi

  cat > /usr/local/bin/cielo-chat-run <<SCRIPT
#!/usr/bin/env bash
# Runs the chat UI against this box's agent endpoint, as the owner.
#
# It exits rather than waits when the box is not ready — no runtime, or no owner
# yet — because the token it needs does not exist until someone claims the box.
# systemd restarts it, so claiming is all it takes for chat to come up.
set -euo pipefail
if [[ -f /etc/cielo/chat.env ]]; then
  # shellcheck disable=SC1091
  . /etc/cielo/chat.env
fi
CHAT_HOST="\${CHAT_HOST:-127.0.0.1}"
CHAT_PORT="\${CHAT_PORT:-8080}"
CHAT_IMAGE="\${CHAT_IMAGE:-ghcr.io/open-webui/open-webui:main}"

owner="\${CHAT_OWNER:-}"
if [[ -z "\$owner" ]]; then
  status="\$(curl -fsS "http://127.0.0.1:${PORT}/api/setup/status")" || {
    echo "runtime not answering on ${PORT} yet" >&2; exit 1; }
  owner="\$(printf '%s' "\$status" | sed -n 's/.*"owner":"\\([^"]*\\)".*/\\1/p')"
fi
[[ -n "\$owner" ]] || {
  echo "no owner to act as: claim the box (cielo-claim \"Your Name\"), or if it" >&2
  echo "already has several users, set CHAT_OWNER in /etc/cielo/chat.env" >&2
  exit 1; }
token_file="/opt/cielo/.data/secrets/\${owner}.token"
[[ -r "\$token_file" ]] || { echo "no token file for '\$owner'" >&2; exit 1; }

# --network host so the container reaches a loopback-bound runtime (app and kiosk
# modes bind 127.0.0.1); HOST then keeps the chat itself off the network.
exec podman run --rm --replace --name cielo-chat \\
  --network host \\
  -e HOST="\$CHAT_HOST" \\
  -e PORT="\$CHAT_PORT" \\
  -e WEBUI_NAME="CieloOS Chat" \\
  -e WEBUI_AUTH=False \\
  -e OPENAI_API_BASE_URL="http://127.0.0.1:${PORT}/v1/agent" \\
  -e OPENAI_API_KEY="\$(cat "\$token_file")" \\
  -v cielo-chat-data:/app/backend/data \\
  "\$CHAT_IMAGE"
SCRIPT
  chmod +x /usr/local/bin/cielo-chat-run

  cat > /etc/systemd/system/cielo-chat.service <<UNIT
[Unit]
Description=CieloOS chat (Open WebUI) against the agent endpoint
After=network-online.target cielo-runtime.service
Wants=network-online.target

[Service]
Type=simple
User=cielo
Environment=XDG_RUNTIME_DIR=/run/user/${CIELO_UID}
ExecStart=/usr/local/bin/cielo-chat-run
# Always, not on-failure: before the box is claimed this exits cleanly with
# nothing to do, and claiming is what makes the next attempt succeed.
Restart=always
RestartSec=15

[Install]
WantedBy=multi-user.target
UNIT

  if [[ "$CI" -eq 1 ]]; then
    echo "    (--ci: unit installed but not enabled; no podman in this mode)"
  else
    enable_unit cielo-chat.service || true
    if [[ "$LIVE" -eq 1 ]]; then
      systemctl daemon-reload
      # The image is ~1 GB and podman pulls it on first run, so this can take a
      # while; it is not something the install should block on.
      systemctl restart cielo-chat.service || true
      echo "    starting (first run downloads the image) → http://127.0.0.1:8080/"
    else
      echo "    (--offline: enabled; pulls the image and starts on first boot)"
    fi
  fi
fi

echo "==> [9/9] Presentation mode: $MODE"
if [[ "$MODE" == "kiosk" && "$CI" -eq 0 ]]; then
  # Minimal Wayland kiosk: `cage` runs a single fullscreen app (chromium) as cielo.
  # (Least-packages GUI; shake this out on the actual hardware/GPU.)
  apt-get install -y --no-install-recommends cage seatd chromium-browser || \
    apt-get install -y --no-install-recommends cage seatd chromium || true
  enable_unit seatd.service || true
  cat > /etc/systemd/system/cielo-kiosk.service <<EOF
[Unit]
Description=CieloOS kiosk browser
After=cielo-runtime.service systemd-user-sessions.service
Wants=cielo-runtime.service

[Service]
User=cielo
PAMName=login
TTYPath=/dev/tty1
Environment=XDG_RUNTIME_DIR=/run/user/${CIELO_UID}
# Wait for the runtime, then open the panel fullscreen.
ExecStartPre=/bin/sh -c 'until curl -fsS http://127.0.0.1:${PORT}/api/setup/status >/dev/null; do sleep 1; done'
ExecStart=/usr/bin/cage -- chromium --kiosk --no-first-run --disable-translate "http://127.0.0.1:${PORT}/"
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF
  enable_unit cielo-kiosk.service || true
  [[ "$LIVE" -eq 1 ]] && systemctl daemon-reload
  echo "    Kiosk service installed. It opens the panel on tty1 at boot."
fi

echo
echo "================ CieloOS installed ($MODE) ================"
if [[ "$LIVE" -eq 1 ]]; then
  systemctl --no-pager --lines=0 status cielo-runtime.service || true
fi
echo
echo "Verify anytime with:  cielo-selftest            (non-destructive)"
echo "                      cielo-selftest --claim    (throwaway machine only)"
echo "First-owner claim (loopback-only — do it on this box):"
if [[ "$MODE" == "headless" ]]; then
  echo "  ssh in and run:   cielo-claim \"Your Name\""
  echo "  then from your laptop open:  http://<this-host-ip>:${PORT}/  and log in with the printed token"
else
  echo "  a local/kiosk browser at http://127.0.0.1:${PORT}/ shows the claim wizard"
  echo "  or run:  cielo-claim \"Your Name\""
fi
echo
echo "Sessions (console/desktop) need their podman images. If not present they build"
echo "on first use; to prebuild, run the distro image Containerfiles as the 'cielo' user."
echo "Add an AI provider anytime from the panel's Models tab (no restart)."
