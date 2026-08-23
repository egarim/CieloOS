#!/usr/bin/env bash
# Definitive amd64 test: run the x86-64 release bundle in a FULL-SYSTEM QEMU VM
# (a real emulated x86 CPU + real Ubuntu + real systemd), so .NET's JIT and
# exception handling work — unlike Docker's user-mode emulation, which FailFasts.
# cloud-init runs install.sh + cielo-selftest --claim unattended and powers off;
# we read the verdict off the serial console.
#
# Prereqs: qemu-system-x86_64, xorriso, and the Ubuntu amd64 cloud image at
# $CIELO_VM_WORK/noble-amd64.img (download separately). Set CIELO_VM_WORK to a
# scratch dir. Slow (full-system TCG on Apple Silicon) — minutes.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="${CIELO_VM_WORK:-$ROOT/.vmtest}"
BASE_IMG="$WORK/noble-amd64.img"
DISK="$WORK/cielo-test-disk.qcow2"
SEED="$WORK/seed.iso"
SEEDDIR="$WORK/seed"
SERIAL="$WORK/serial.log"
TIMEOUT="${CIELO_VM_TIMEOUT:-1200}"   # seconds

command -v qemu-system-x86_64 >/dev/null || { echo "qemu-system-x86_64 missing (brew install qemu)"; exit 1; }
command -v xorriso >/dev/null || { echo "xorriso missing (brew install xorriso)"; exit 1; }
[[ -f "$BASE_IMG" ]] || { echo "Base image missing: $BASE_IMG"; echo "Download: curl -fsSL -o '$BASE_IMG' https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img"; exit 1; }

echo "==> Building amd64 bundle"
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then bash "$ROOT/distro/scripts/build-release.sh" linux-x64 >/dev/null; fi
TARBALL="$ROOT/release/cielo-linux-x64.tar.gz"
[[ -f "$TARBALL" ]] || { echo "bundle not found: $TARBALL"; exit 1; }

echo "==> Preparing overlay disk + NoCloud seed"
rm -f "$DISK" "$SEED"; rm -rf "$SEEDDIR"; mkdir -p "$SEEDDIR"
qemu-img create -f qcow2 -F qcow2 -b "$BASE_IMG" "$DISK" 12G >/dev/null

cp "$TARBALL" "$SEEDDIR/cielo-linux-x64.tar.gz"
cat > "$SEEDDIR/meta-data" <<'EOF'
instance-id: cielo-vmtest
local-hostname: cielo-vmtest
EOF
# The guest-side test: install (--ci: fast, no podman), start the runtime exactly
# as the service would, run the self-test, print a verdict, power off.
cat > "$SEEDDIR/runtest.sh" <<'GUEST'
#!/bin/bash
set +e
exec > >(tee /dev/console) 2>&1
echo "===== CIELO VM TEST START ====="
mount -L CIDATA -o ro /mnt 2>/dev/null || { mkdir -p /mnt/cidata && mount -L CIDATA -o ro /mnt/cidata && ln -sfn /mnt/cidata /mnt/seed; }
SRC=/mnt; [ -f /mnt/cielo-linux-x64.tar.gz ] || SRC=/mnt/cidata
tar xzf "$SRC/cielo-linux-x64.tar.gz" -C /root
bash /root/cielo/install.sh --ci --mode headless
runuser -u cielo -- env WORKSPACE_RUNTIME_ROOT=/opt/cielo Runtime__SeedDemo=false \
  Database__Provider=sqlite ASPNETCORE_URLS=http://127.0.0.1:5148 \
  /opt/cielo/bin/WorkspaceRuntime.Api >/var/log/cielo-runtime.log 2>&1 &
PID=$!
ok=0
for i in $(seq 1 120); do
  curl -fsS http://127.0.0.1:5148/api/setup/status >/dev/null 2>&1 && { ok=1; break; }
  kill -0 "$PID" 2>/dev/null || { echo "RUNTIME EXITED EARLY:"; tail -40 /var/log/cielo-runtime.log; break; }
  sleep 2
done
if [ "$ok" = 1 ]; then
  cielo-selftest --claim
  echo "CIELO_VM_RESULT:$?"
else
  echo "CIELO_VM_RESULT:99"
fi
echo "===== CIELO VM TEST END ====="
sync
poweroff -f
GUEST
cat > "$SEEDDIR/user-data" <<'EOF'
#cloud-config
runcmd:
  - [ bash, /run/cielo-runtest.sh ]
write_files:
  - path: /run/cielo-runtest.sh
    permissions: '0755'
    content: |
      #!/bin/sh
      mkdir -p /mnt/cidata
      mount -L CIDATA -o ro /mnt/cidata
      exec bash /mnt/cidata/runtest.sh
EOF
xorriso -as mkisofs -quiet -o "$SEED" -V CIDATA -J -r "$SEEDDIR"

echo "==> Booting full-system amd64 VM (TCG; this is slow) — serial → $SERIAL"
: > "$SERIAL"
qemu-system-x86_64 \
  -machine q35 -cpu max -m 4096 -smp 4 \
  -drive file="$DISK",if=virtio,format=qcow2 \
  -drive file="$SEED",if=virtio,format=raw,media=cdrom \
  -netdev user,id=n0 -device virtio-net-pci,netdev=n0 \
  -nographic -serial "file:$SERIAL" -monitor none \
  >/dev/null 2>&1 &
QEMU_PID=$!

echo "==> Waiting for the verdict (timeout ${TIMEOUT}s, qemu pid $QEMU_PID)"
deadline=$((SECONDS + TIMEOUT))
result=""
while [[ $SECONDS -lt $deadline ]]; do
  if ! kill -0 "$QEMU_PID" 2>/dev/null; then break; fi
  if grep -q 'CIELO_VM_RESULT:' "$SERIAL" 2>/dev/null; then
    result="$(grep -o 'CIELO_VM_RESULT:[0-9]*' "$SERIAL" | tail -1 | cut -d: -f2)"
    break
  fi
  sleep 5
done
kill "$QEMU_PID" 2>/dev/null || true; wait "$QEMU_PID" 2>/dev/null || true

echo "---- self-test output ----"
grep -E 'PASS|FAIL|CIELO_VM|RUNTIME EXITED|All checks|check\(s\) failed' "$SERIAL" || tail -30 "$SERIAL"
echo "--------------------------"
if [[ "$result" == "0" ]]; then
  echo "==> PASS: amd64 verified on a full-system VM"
  exit 0
fi
echo "==> FAIL/incomplete (result='${result:-none}'). Full log: $SERIAL"
exit 1
