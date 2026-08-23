#!/usr/bin/env bash
# Automated end-to-end test of a CieloOS release bundle on REAL Linux, in Docker:
# builds the bundle, runs install.sh --ci in ubuntu:24.04, starts the runtime as
# the service would, and runs cielo-selftest --claim (panel + claim + add-user +
# models + key-not-leaked). Proves the exact self-contained binary + installer +
# panel + first-run flow work on Linux — before you touch the target machine.
#
#   distro/scripts/test-install.sh [linux-x64|linux-arm64]
#
# Default = the host's native arch (reliable). NOTE: running linux-x64 on an arm64
# Mac (or vice-versa) uses QEMU user-mode emulation, under which .NET FailFasts
# while unwinding exceptions in EF Core's dynamic query codegen — an EMULATOR bug,
# not a defect (the same path passes natively). The authoritative cross-arch test
# is `cielo-selftest` on real target hardware.
set -euo pipefail

case "$(uname -m)" in
  arm64|aarch64) HOST_ARCH="linux-arm64" ;;
  *)             HOST_ARCH="linux-x64" ;;
esac
ARCH="${1:-$HOST_ARCH}"
case "$ARCH" in
  linux-x64)   PLATFORM="linux/amd64" ;;
  linux-arm64) PLATFORM="linux/arm64" ;;
  *) echo "arch must be linux-x64 or linux-arm64" >&2; exit 2 ;;
esac
if [[ "$ARCH" != "$HOST_ARCH" ]]; then
  echo "WARNING: $ARCH on a $HOST_ARCH host runs under QEMU emulation; .NET may FailFast" >&2
  echo "         in EF dynamic codegen (emulator artifact). Trust native + real hardware." >&2
fi
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# Docker or podman: this runs the same either way, and a CieloOS host already has
# podman (it is what sessions use), so requiring Docker kept the test from ever
# running on the machine it was meant to validate.
if command -v docker >/dev/null && docker info >/dev/null 2>&1; then
  ENGINE=docker
elif command -v podman >/dev/null && podman info >/dev/null 2>&1; then
  ENGINE=podman
else
  echo "neither docker nor podman is usable (start colima, or install podman)" >&2; exit 1
fi
# Podman on an SELinux-enforcing host cannot read a bind mount without a relabel,
# so the bundle copy fails with permission denied. Lowercase z is the shared label:
# the same bundle is mounted into several containers here, and Z (exclusive) would
# relabel it per container and break the next one.
MOUNT_OPTS="ro"
if [ "$ENGINE" = podman ]; then MOUNT_OPTS="ro,z"; fi
echo "==> container engine: $ENGINE (mount opts: $MOUNT_OPTS)"

echo "==> Building bundle ($ARCH)"
bash "$ROOT/distro/scripts/build-release.sh" "$ARCH" >/dev/null
STAGE="$ROOT/release/cielo"

echo "==> install.sh --ci + self-test in ubuntu:24.04 ($PLATFORM)"
"$ENGINE" run --rm --platform "$PLATFORM" -v "$STAGE":/bundle:$MOUNT_OPTS ubuntu:24.04 bash -euo pipefail -c '
  export DEBIAN_FRONTEND=noninteractive
  cp -r /bundle /work && cd /work
  bash ./install.sh --ci --mode headless

  # Start the runtime exactly as the systemd unit would (as cielo, provider-free).
  runuser -u cielo -- env \
    WORKSPACE_RUNTIME_ROOT=/opt/cielo Runtime__SeedDemo=false Database__Provider=sqlite \
    ASPNETCORE_URLS=http://127.0.0.1:5148 \
    /opt/cielo/bin/WorkspaceRuntime.Api >/tmp/runtime.log 2>&1 &
  RUNTIME_PID=$!

  ok=0
  for i in $(seq 1 90); do
    if curl -fsS http://127.0.0.1:5148/api/setup/status >/dev/null 2>&1; then ok=1; break; fi
    if ! kill -0 "$RUNTIME_PID" 2>/dev/null; then echo "RUNTIME EXITED EARLY:"; cat /tmp/runtime.log; exit 1; fi
    sleep 1
  done
  if [ "$ok" != "1" ]; then echo "RUNTIME NEVER BECAME READY:"; tail -40 /tmp/runtime.log; exit 1; fi

  set +e
  cielo-selftest --claim
  rc=$?
  kill "$RUNTIME_PID" 2>/dev/null
  exit $rc
'
echo "==> run.sh (foreground, non-root, no systemd) in ubuntu:24.04 ($PLATFORM)"
# The install.sh path above launches bin/WorkspaceRuntime.Api directly, so it never
# exercises the staged launcher. Run it the way a WSL2/laptop user does: unprivileged,
# straight from the bundle dir, and check the panel comes up and state lands in .data.
"$ENGINE" run --rm --platform "$PLATFORM" -v "$STAGE":/bundle:$MOUNT_OPTS ubuntu:24.04 bash -euo pipefail -c '
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -y >/dev/null && apt-get install -y --no-install-recommends curl ca-certificates >/dev/null
  useradd --create-home --home-dir /home/app app
  cp -r /bundle /home/app/cielo && chown -R app:app /home/app/cielo
  test -x /home/app/cielo/run.sh || { echo "run.sh is not executable in the bundle"; exit 1; }

  runuser -u app -- /home/app/cielo/run.sh >/tmp/run.log 2>&1 &
  RUN_PID=$!

  ok=0
  for i in $(seq 1 90); do
    if curl -fsS http://127.0.0.1:5148/api/setup/status >/dev/null 2>&1; then ok=1; break; fi
    if ! kill -0 "$RUN_PID" 2>/dev/null; then echo "run.sh EXITED EARLY:"; cat /tmp/run.log; exit 1; fi
    sleep 1
  done
  [ "$ok" = "1" ] || { echo "run.sh NEVER BECAME READY:"; tail -40 /tmp/run.log; exit 1; }

  # It must serve the panel and keep its state inside the bundle, not /opt.
  curl -fsS http://127.0.0.1:5148/ | grep -q "id=.root." || { echo "panel not served by run.sh"; exit 1; }
  test -f /home/app/cielo/.data/workspace-runtime.db || { echo "run.sh did not create .data/workspace-runtime.db"; exit 1; }
  test ! -e /opt/cielo || { echo "run.sh wrote to /opt/cielo"; exit 1; }

  # A bad port must fail fast rather than surfacing an opaque bind error.
  if runuser -u app -- /home/app/cielo/run.sh --port 99999 >/dev/null 2>&1; then
    echo "run.sh accepted an out-of-range port"; exit 1
  fi

  kill "$RUN_PID" 2>/dev/null
  echo "  run.sh OK (non-root, panel served, state in .data)"
'

echo "==> install.sh --offline leaves a bootable system ($PLATFORM)"
# The autoinstall path installs into a system that is not running, so nothing can be
# started or verified there. What CAN be checked is that it left the right things
# behind for first boot — this is where the image build and the restart policy are
# deferred to, so a regression here is silent until someone reboots a real machine.
"$ENGINE" run --rm --platform "$PLATFORM" -v "$STAGE":/bundle:$MOUNT_OPTS ubuntu:24.04 bash -euo pipefail -c '
  export DEBIAN_FRONTEND=noninteractive
  cp -r /bundle /work && cd /work
  bash ./install.sh --offline --mode headless >/dev/null 2>&1

  test -x /usr/local/bin/cielo-build-session-images || { echo "no first-boot image builder"; exit 1; }
  test -L /etc/systemd/system/multi-user.target.wants/cielo-session-images.service \
    || { echo "image builder not enabled for first boot"; exit 1; }
  test -L /etc/systemd/system/multi-user.target.wants/cielo-runtime.service \
    || { echo "runtime not enabled for first boot"; exit 1; }
  test -L /var/lib/cielo/.config/systemd/user/default.target.wants/podman-restart.service \
    || { echo "podman-restart not enabled for cielo: sessions would not survive a reboot"; exit 1; }
  test -f /var/lib/systemd/linger/cielo || { echo "no linger marker"; exit 1; }

  # The ONLYOFFICE package must match the target, not the Containerfile default.
  want=amd64; [ "$(dpkg --print-architecture)" = arm64 ] && want=arm64
  grep -q "onlyoffice-desktopeditors_${want}.deb" /usr/local/bin/cielo-build-session-images \
    || { echo "image builder would install the wrong ONLYOFFICE architecture"; exit 1; }

  echo "  offline install OK (image build + restart policy deferred to first boot)"
'


echo "==> PASS: install + run.sh + first-run verified on Linux ($ARCH)"
