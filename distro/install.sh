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

# Things that failed without failing the install.
#
# Several steps here are deliberately non-fatal: a session image that will not
# build should not cost you the runtime, the panel and the service units that all
# installed perfectly well. That judgement is right. What was wrong is where the
# warning ended up — printed once, on stderr, and then followed by six more steps
# and a closing banner reading "CieloOS installed" with an active green service
# under it. Between two multi-gigabyte image pulls, nobody scrolls back. The last
# screen is the one people read, and it said everything was fine.
#
# So every soft failure is recorded here and reprinted at the very end, after the
# banner, as the last thing on screen.
DEGRADED=()
degrade() { DEGRADED+=("$1"); }

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
# useradd --create-home leaves an EXISTING directory's ownership alone, and by the
# time this runs something else may already have made it. On a fresh install that
# left /var/lib/cielo owned by root:root — so cielo could not write to its own home,
# the search service died on `mkdir $HOME/lunos` with permission denied, and
# `systemctl --user enable` could not create its symlink. Both surfaced as unrelated
# failures in the closing banner and neither named the cause.
chown cielo:cielo /var/lib/cielo
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

echo "==> [3/9] Session images + the search service"
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
  # The build's own output used to go to /dev/null, which keeps the install
  # readable and makes a failure impossible to diagnose without running the whole
  # thing again — a 3GB pull and a 35-step build to find out which step broke. A
  # log file costs nothing and keeps both.
  IMAGE_LOG="/var/log/cielo-session-images.log"
  echo "    building now (this takes a while); log: $IMAGE_LOG"
  if runuser -u cielo -- env HOME=/var/lib/cielo XDG_RUNTIME_DIR="/run/user/${CIELO_UID}" \
       /usr/local/bin/cielo-build-session-images >"$IMAGE_LOG" 2>&1; then
    echo "    session images built"
  else
    echo "    WARNING: image build failed; see $IMAGE_LOG" >&2
    # The last few lines are almost always the actual error. Showing them here
    # means the common case needs no second command.
    tail -n 4 "$IMAGE_LOG" 2>/dev/null | sed 's/^/      | /' >&2 || true
    degrade "Sessions cannot start: the session images did not build.
    Nothing else is affected — the runtime, panel and services are fine.
    Why: see $IMAGE_LOG (the last lines are the error).
    Fix:  sudo -u cielo /usr/local/bin/cielo-build-session-images
    It also retries by itself at the next boot."
  fi
fi

# The search service the console image's `websearch` tool talks to.
#
# It is started here because it never was anywhere. distro/services/searxng/run.sh
# has been in the repo the whole time, referenced by nothing, and services/ was not
# staged into the tarball — so websearch has failed on every installed machine while
# the agent's own prompt told it to use it. Two benchmark runs against OpenClaw were
# lost to hand-rolled scraping that got bot-challenged, by an agent whose actual
# search tool was one command away and pointed at nothing.
if [[ "$CI" -eq 1 || "$OFFLINE" -eq 1 ]]; then
  echo "    (search service deferred)"
elif [[ ! -f "$BUNDLE/services/searxng/run.sh" ]]; then
  echo "    (no services/ in this bundle — skipping the search service)"
else
  SEARCH_LOG="/var/log/cielo-search.log"
  echo "    starting the search service (websearch needs it); log: $SEARCH_LOG"
  if runuser -u cielo -- env HOME=/var/lib/cielo XDG_RUNTIME_DIR="/run/user/${CIELO_UID}" HOME=/var/lib/cielo \
       bash "$BUNDLE/services/searxng/run.sh" >"$SEARCH_LOG" 2>&1; then
    echo "    search service up on :8888"
  else
    echo "    WARNING: the search service did not start; see $SEARCH_LOG" >&2
    tail -n 3 "$SEARCH_LOG" 2>/dev/null | sed 's/^/      | /' >&2 || true
    degrade "The agent cannot search the web: the search service did not start.
    Everything else works — this only costs the websearch tool, which agents lean on
    for any research task.
    Why: see $SEARCH_LOG
    Fix:  sudo -u cielo HOME=/var/lib/cielo bash /opt/cielo/services/searxng/run.sh"
  fi
fi

echo "==> [4/9] Session restart policy"
# Sessions are created with --restart=unless-stopped. podman-restart.service runs
#     podman start --all --filter restart-policy=always
# which cannot match them, so it has never restarted a single session. Measured on a
# real reboot: runtime back, every session dead; and measured again with the filters
# side by side — =unless-stopped matched all three containers on the box, =always
# matched none.
#
# Rather than change the sessions to always (which would also resurrect ones the
# owner deliberately stopped, the exact thing unless-stopped exists to prevent), we
# ship the unit podman is missing. podman-restart stays enabled: it costs nothing and
# covers anything that really is =always.
#
# These are USER units for cielo, so offline installs get the wants-symlink written
# directly rather than being skipped: systemctl cannot run in a chroot.
install -d -o cielo -g cielo /var/lib/cielo/.config/systemd/user
cat > /var/lib/cielo/.config/systemd/user/cielo-sessions.service <<'UNIT'
[Unit]
Description=Start CieloOS sessions that were running before the reboot
Documentation=https://github.com/egarim/CieloOS
After=podman.socket
StartLimitIntervalSec=0

[Service]
Type=oneshot
RemainAfterExit=yes
# unless-stopped is what SessionOrchestrator sets, and what podman's own
# podman-restart.service does NOT match.
ExecStart=/usr/bin/podman start --all --filter restart-policy=unless-stopped

[Install]
WantedBy=default.target
UNIT
chown cielo:cielo /var/lib/cielo/.config/systemd/user/cielo-sessions.service
if [[ "$CI" -eq 1 ]]; then
  echo "    (skipped: --ci)"
elif [[ "$OFFLINE" -eq 1 ]]; then
  install -d -o cielo -g cielo /var/lib/cielo/.config/systemd/user/default.target.wants
  ln -sf /usr/lib/systemd/user/podman-restart.service \
    /var/lib/cielo/.config/systemd/user/default.target.wants/podman-restart.service
  chown -h cielo:cielo /var/lib/cielo/.config/systemd/user/default.target.wants/podman-restart.service
  ln -sf /var/lib/cielo/.config/systemd/user/cielo-sessions.service \
    /var/lib/cielo/.config/systemd/user/default.target.wants/cielo-sessions.service
  chown -h cielo:cielo /var/lib/cielo/.config/systemd/user/default.target.wants/cielo-sessions.service
  echo "    podman-restart + cielo-sessions linked for cielo (activate on first boot)"
else
  runuser -u cielo -- env HOME=/var/lib/cielo XDG_RUNTIME_DIR="/run/user/${CIELO_UID}" \
    systemctl --user enable podman-restart.service >/dev/null 2>&1 \
    && echo "    podman-restart enabled for cielo" \
    || { echo "    WARNING: could not enable podman-restart." >&2
         degrade "podman-restart could not be enabled.
    Fix:  sudo -u cielo systemctl --user enable podman-restart.service"; }

  # Ours, enabled separately. Chained behind podman-restart with && it reported the
  # WRONG unit when it failed — the banner blamed podman-restart for a fault that was
  # entirely in cielo-sessions, which is worse than no message.
  runuser -u cielo -- env HOME=/var/lib/cielo XDG_RUNTIME_DIR="/run/user/${CIELO_UID}" \
    systemctl --user daemon-reload >/dev/null 2>&1 || true
  runuser -u cielo -- env HOME=/var/lib/cielo XDG_RUNTIME_DIR="/run/user/${CIELO_UID}" \
    systemctl --user enable cielo-sessions.service >/dev/null 2>&1 \
    && echo "    cielo-sessions enabled for cielo" \
    || { echo "    WARNING: could not enable cielo-sessions; sessions will not survive a reboot." >&2
         degrade "Sessions will not survive a reboot: cielo-sessions could not be enabled.
    They start fine and keep running; they just will not come back after the machine restarts.
    podman-restart cannot cover this — it only matches restart-policy=always, and sessions
    are created unless-stopped.
    Fix:  sudo -u cielo HOME=/var/lib/cielo systemctl --user enable cielo-sessions.service"; }
fi

echo "==> [5/9] Install to /opt/cielo"
# Replace files only while the runtime is stopped: overwriting a running
# executable is undefined at best. Stage 7 starts it again after the service
# unit and environment file have been refreshed.
if [[ "$LIVE" -eq 1 && "$CI" -eq 0 ]]; then
  systemctl stop cielo-runtime.service || true
fi
install -d /opt/cielo
cp -a "$BUNDLE/bin" "$BUNDLE/panel" "$BUNDLE/surfaces" "$BUNDLE/config" /opt/cielo/
# models/ holds the local-inference registry and its provider manifests, which
# config/local-inference.json points at. Older bundles predate it, so tolerate its
# absence rather than failing an upgrade from one.
[[ -d "$BUNDLE/models" ]] && cp -a "$BUNDLE/models" /opt/cielo/
# The third-party attribution lands with the files it describes, not in the data
# dir: it is documentation of what /opt/cielo runs, so /opt/cielo is the right home.
if [[ -f "$BUNDLE/THIRD-PARTY.md" ]]; then
  cp "$BUNDLE/THIRD-PARTY.md" /opt/cielo/THIRD-PARTY.md
fi
# services/ travels to /opt/cielo too, so the "Fix:" line printed when the search
# service fails names a path that exists on the installed machine rather than a
# directory that only ever lived in the unpacked tarball.
if [[ -d "$BUNDLE/services" ]]; then
  cp -a "$BUNDLE/services" /opt/cielo/
fi
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
  # Reinstalling a box whose /etc/cielo/chat.env was edited must advertise the
  # address the chat will actually listen on, not the default. Read it in a
  # subshell so the installer does not inherit those names.
  chat_host="$( ( [[ -f /etc/cielo/chat.env ]] && . /etc/cielo/chat.env; printf '%s' "${CHAT_HOST:-127.0.0.1}" ) )"
  chat_port="$( ( [[ -f /etc/cielo/chat.env ]] && . /etc/cielo/chat.env; printf '%s' "${CHAT_PORT:-8080}" ) )"
  # A browser on the box reaches every local bind as localhost; only a specific
  # non-local address has to be named.
  case "$chat_host" in
    127.0.0.1|localhost|0.0.0.0|"") chat_link_host="localhost" ;;
    *)                              chat_link_host="$chat_host" ;;
  esac
  printf '\n# The chat UI installed by stage 8 (see /etc/cielo/chat.env).\nChat__Url=http://%s:%s/\n' \
    "$chat_link_host" "$chat_port" >> /etc/cielo/cielo.env
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
# Add a teammate. Usage: cielo-add-user "Their Name" [desk-profile]
# The desk profile decides their toolchain (office, dotnet, marketing); omitted
# means office, which is the desk everyone had before profiles existed.
#
# This took the owner's identity token as an argument until creating a person
# became an action you have to prove a password for. It signs in instead: the
# password is typed here and never becomes a shell argument, so it stays out of
# the process list and out of .bash_history, which is more than the token it
# replaced ever managed.
#
# If you have not set a password yet, do that first — on this box, because a
# first password is loopback-only:
#   curl -fsS -XPOST http://127.0.0.1:${PORT}/api/auth/password \
#     -H "Authorization: Bearer \$(cat /opt/cielo/.data/secrets/<you>.token)" \
#     -H 'Content-Type: application/json' -d '{"newPassword":"..."}'
set -euo pipefail
name="\${1:?Usage: cielo-add-user \"Name\" [desk-profile]}"
desk="\${2:-office}"
read -rp  "Your desk name: " who
read -rsp "Your password:  " pass; echo
jar="\$(mktemp)"; trap 'rm -f "\$jar"' EXIT
curl -fsS -c "\$jar" -XPOST "http://127.0.0.1:${PORT}/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d "{\"slug\": \"\${who}\", \"password\": \"\${pass}\"}" >/dev/null
curl -fsS -b "\$jar" -XPOST "http://127.0.0.1:${PORT}/api/users" \
  -H 'Content-Type: application/json' -d "{\"name\": \"\${name}\", \"deskProfile\": \"\${desk}\"}"
echo
EOF
cat > /usr/local/bin/cielo-build-desk-image <<'EOF'
#!/usr/bin/env bash
# Build a desk profile's image on this machine. Usage: cielo-build-desk-image dotnet
#
# These are built on demand rather than at install (issue #15): a developer desk
# is several gigabytes, and installing it on every machine to serve one office
# user is the wrong default. The panel starts this too; it is here for the box.
set -euo pipefail
id="${1:?Usage: cielo-build-desk-image <profile>}"

# Rootless podman keeps a store PER USER. Built as root, the image would land in
# root's store and be invisible to the runtime, which runs as cielo — so the
# session would still say the image is missing, with the image sitting right
# there. Drop to cielo the way the session-image builder does.
if [[ "$(id -u)" -eq 0 ]]; then
  exec runuser -u cielo -- env XDG_RUNTIME_DIR="/run/user/$(id -u cielo)" \
    /usr/local/bin/cielo-build-desk-image "$id"
fi

root="/var/lib/cielo/images/profiles/${id}"
test -d "$root" || { echo "No desk profile '${id}' under /var/lib/cielo/images/profiles." >&2; exit 2; }

# VS Code ships a per-architecture .deb, like ONLYOFFICE in the session image.
case "$(dpkg --print-architecture 2>/dev/null || uname -m)" in
  arm64|aarch64) VSCODE_DEB="https://update.code.visualstudio.com/latest/linux-deb-arm64/stable" ;;
  *)             VSCODE_DEB="https://update.code.visualstudio.com/latest/linux-deb-x64/stable" ;;
esac

# A desk is two images: the desktop the person uses, and the console their AGENT
# works in. Building only the desktop would give a .NET desk whose agent cannot
# run dotnet — the toolchain present for the human and missing for the machine.
build_tagged() {
  local tag="$1"
  local context="$2"
  local build_arg="${3:-}"
  local before="$(podman image inspect -f '{{.Id}}' "$tag" 2>/dev/null || true)"

  if [[ -n "$build_arg" ]]; then
    podman build --build-arg "$build_arg" -t "$tag" "$context"
  else
    podman build -t "$tag" "$context"
  fi

  # podman untags the image this build replaced rather than deleting it. Drop the
  # previous image now that the new one built, otherwise every rebuild leaves
  # another multi-gigabyte dangling image.
  if [[ -n "$before" ]]; then
    local after="$(podman image inspect -f '{{.Id}}' "$tag" 2>/dev/null || true)"
    if [[ -n "$after" && "$after" != "$before" ]]; then
      podman image rm "$before" >/dev/null 2>&1 || true
    fi
  fi
}

if [ -d "$root/desktop" ]; then
  build_tagged "localhost/cielo-desk-${id}:latest" "$root/desktop" "VSCODE_DEB=${VSCODE_DEB}"
fi
if [ -d "$root/console" ]; then
  build_tagged "localhost/cielo-console-${id}:latest" "$root/console"
fi
EOF
chmod +x /usr/local/bin/cielo-claim /usr/local/bin/cielo-add-user /usr/local/bin/cielo-build-desk-image
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
    runuser -u cielo -- env HOME=/var/lib/cielo XDG_RUNTIME_DIR="/run/user/${CIELO_UID}" \
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

# The chat gets its OWN revocable key rather than the owner's master credential
# (issue #9, point 6). Minted once with the owner token and cached 0600; revoke it
# from the panel and this chat stops working without touching anything else.
key_file="/var/lib/cielo/chat-api-key"
if [[ ! -s "\$key_file" ]]; then
  minted="\$(curl -fsS -XPOST "http://127.0.0.1:${PORT}/api/keys" \\
    -H "Authorization: Bearer \$(cat "\$token_file")" \\
    -H 'Content-Type: application/json' \\
    -d '{"name": "cielo-chat"}' 2>/dev/null | sed -n 's/.*"secret":"\\([^"]*\\)".*/\\1/p')"
  if [[ -n "\$minted" ]]; then
    install -d -m 0700 "\$(dirname "\$key_file")"
    printf '%s' "\$minted" > "\$key_file"
    chmod 0600 "\$key_file"
  fi
fi
# Fall back to the owner token only if minting failed (an older runtime, say):
# a working chat beats a chat that refuses to start over its own credential.
chat_credential="\$(cat "\$key_file" 2>/dev/null || cat "\$token_file")"

# --network host so the container reaches a loopback-bound runtime (app and kiosk
# modes bind 127.0.0.1); HOST then keeps the chat itself off the network.
exec podman run --rm --replace --name cielo-chat \\
  --network host \\
  -e HOST="\$CHAT_HOST" \\
  -e PORT="\$CHAT_PORT" \\
  -e WEBUI_NAME="CieloOS Chat" \\
  -e WEBUI_AUTH=False \\
  -e OPENAI_API_BASE_URL="http://127.0.0.1:${PORT}/v1/agent" \\
  -e OPENAI_API_KEY="\$chat_credential" \\
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
  # Minimal Wayland kiosk: `cage` runs a single fullscreen app (Firefox ESR) as
  # cielo. Ubuntu 24.04's chromium packages are snap-only stubs, so install
  # firefox-esr from Mozilla's apt repository instead.
  if ! {
    install -d -m 0755 /etc/apt/keyrings
    apt-get install -y --no-install-recommends gnupg
    curl -fsSL https://packages.mozilla.org/apt/repo-signing-key.gpg | gpg --dearmor > /etc/apt/keyrings/packages.mozilla.org.gpg
    echo "deb [signed-by=/etc/apt/keyrings/packages.mozilla.org.gpg] https://packages.mozilla.org/apt mozilla main" > /etc/apt/sources.list.d/mozilla.list
    apt-get update -y
    apt-get install -y --no-install-recommends cage seatd firefox-esr
  }; then
    echo "==> Kiosk browser install failed; this machine will boot without a UI." >&2
    exit 1
  fi
  if ! KIOSK_BROWSER="$(command -v firefox-esr)"; then
    echo "==> Kiosk browser binary was not found; this machine will boot without a UI." >&2
    exit 1
  fi
  enable_unit seatd.service || true
  cat > /etc/systemd/system/cielo-kiosk.service <<EOF
[Unit]
Description=CieloOS kiosk browser
After=cielo-runtime.service systemd-user-sessions.service
Wants=cielo-runtime.service
Conflicts=getty@tty1.service

[Service]
User=cielo
PAMName=login
TTYPath=/dev/tty1
Environment=XDG_RUNTIME_DIR=/run/user/${CIELO_UID}
# Wait for the runtime, then open the panel fullscreen.
ExecStartPre=/bin/sh -c 'until curl -fsS http://127.0.0.1:${PORT}/api/setup/status >/dev/null; do sleep 1; done'
ExecStart=/usr/bin/cage -- "$KIOSK_BROWSER" --kiosk --no-remote "http://127.0.0.1:${PORT}/"
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF
  enable_unit cielo-kiosk.service || true
  [[ "$LIVE" -eq 1 ]] && systemctl daemon-reload
  echo "    Kiosk service installed. It opens the panel on tty1 at boot."
fi

echo
CIELO_VERSION="$(cat "$BUNDLE/bin/VERSION" 2>/dev/null || echo unknown)"
echo "================ CieloOS $CIELO_VERSION installed ($MODE) ================"
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
echo "Sessions (console/desktop) need their podman images. They do NOT build on demand:"
echo "until the image exists, creating a session is refused. cielo-session-images.service"
echo "builds them at boot, or run: sudo -u cielo /usr/local/bin/cielo-build-session-images"
echo "Add an AI provider anytime from the panel's Models tab (no restart)."

# Last, so it is the last thing on screen. An install that half-worked should not
# be able to end on a green line.
if [[ "${#DEGRADED[@]}" -gt 0 ]]; then
  echo
  echo "=============== BUT ${#DEGRADED[@]} THING(S) DID NOT WORK ==============="
  for item in "${DEGRADED[@]}"; do
    echo
    echo "  * $item"
  done
  echo
  echo "CieloOS is installed and running; the above is what it cannot do yet."
  echo "======================================================="
fi
