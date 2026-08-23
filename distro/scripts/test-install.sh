#!/usr/bin/env bash
# Automated end-to-end test of a Lun.Os release bundle on REAL Linux, in Docker:
# builds the bundle, runs install.sh --ci in ubuntu:24.04, starts the runtime as
# the service would, and runs lunos-selftest --claim (panel + claim + add-user +
# models + key-not-leaked). Proves the exact self-contained binary + installer +
# panel + first-run flow work on Linux — before you touch the target machine.
#
#   distro/scripts/test-install.sh [linux-x64|linux-arm64]
#
# Default = the host's native arch (reliable). NOTE: running linux-x64 on an arm64
# Mac (or vice-versa) uses QEMU user-mode emulation, under which .NET FailFasts
# while unwinding exceptions in EF Core's dynamic query codegen — an EMULATOR bug,
# not a defect (the same path passes natively). The authoritative cross-arch test
# is `lunos-selftest` on real target hardware.
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

command -v docker >/dev/null || { echo "docker not found (start colima?)" >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo "docker daemon not reachable (run: colima start)" >&2; exit 1; }

echo "==> Building bundle ($ARCH)"
bash "$ROOT/distro/scripts/build-release.sh" "$ARCH" >/dev/null
STAGE="$ROOT/release/lunos"

echo "==> install.sh --ci + self-test in ubuntu:24.04 ($PLATFORM)"
docker run --rm --platform "$PLATFORM" -v "$STAGE":/bundle:ro ubuntu:24.04 bash -euo pipefail -c '
  export DEBIAN_FRONTEND=noninteractive
  cp -r /bundle /work && cd /work
  bash ./install.sh --ci --mode headless

  # Start the runtime exactly as the systemd unit would (as lunos, provider-free).
  runuser -u lunos -- env \
    WORKSPACE_RUNTIME_ROOT=/opt/lunos Runtime__SeedDemo=false Database__Provider=sqlite \
    ASPNETCORE_URLS=http://127.0.0.1:5148 \
    /opt/lunos/bin/WorkspaceRuntime.Api >/tmp/runtime.log 2>&1 &
  RUNTIME_PID=$!

  ok=0
  for i in $(seq 1 90); do
    if curl -fsS http://127.0.0.1:5148/api/setup/status >/dev/null 2>&1; then ok=1; break; fi
    if ! kill -0 "$RUNTIME_PID" 2>/dev/null; then echo "RUNTIME EXITED EARLY:"; cat /tmp/runtime.log; exit 1; fi
    sleep 1
  done
  if [ "$ok" != "1" ]; then echo "RUNTIME NEVER BECAME READY:"; tail -40 /tmp/runtime.log; exit 1; fi

  set +e
  lunos-selftest --claim
  rc=$?
  kill "$RUNTIME_PID" 2>/dev/null
  exit $rc
'
echo "==> PASS: install + first-run verified on Linux ($ARCH)"
