#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bash -n "$root/distro/preflight.sh"
out=$(bash "$root/distro/preflight.sh")
for key in os version architecture ram_kib available_kib swap_kib root_free_kib avx avx2 fma inference_hint baseline podman systemctl curl readiness; do
  grep -q "^$key=" <<<"$out"
done
for key in ram_kib available_kib swap_kib root_free_kib; do
  grep -Eq "^$key=[0-9]+$" <<<"$out"
done
grep -qx 'readiness=inventory-only' <<<"$out"
if bash "$root/distro/preflight.sh" --install >/dev/null 2>&1; then
  echo 'Unexpected argument accepted' >&2; exit 1
fi
echo 'Preflight smoke test passed'
