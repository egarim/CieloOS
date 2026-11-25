#!/usr/bin/env bash
# Read-only inventory; no root, network, package installation or service changes.
set -euo pipefail
export LC_ALL=C
if [[ $# -ne 0 ]]; then echo "Usage: bash distro/preflight.sh" >&2; exit 2; fi
if [[ "$(uname -s)" != Linux ]]; then echo "Linux required" >&2; exit 2; fi
# Read values without sourcing executable shell configuration.
os_value() { awk -F= -v key="$1" '$1 == key {sub(/^[^=]*=/,""); gsub(/^"|"$/,""); print; exit}' /etc/os-release; }
arch=$(uname -m)
os=$(os_value ID)
version=$(os_value VERSION_ID)
mem=$(awk '$1=="MemTotal:" {print $2}' /proc/meminfo)
available=$(awk '$1=="MemAvailable:" {print $2}' /proc/meminfo)
swap=$(awk '$1=="SwapTotal:" {print $2}' /proc/meminfo)
disk=$(df -Pk / | awk 'NR==2 {print $4}')
# Require every reported logical CPU to have a flag, not just the first.
source "$(dirname "${BASH_SOURCE[0]}")/cpu-flags.sh"
flag_value() { if has_flag /proc/cpuinfo "$1"; then printf yes; else printf no; fi; }
avx=$(flag_value avx); avx2=$(flag_value avx2); fma=$(flag_value fma)
printf 'os=%s\nversion=%s\narchitecture=%s\n' "$os" "$version" "$arch"
printf 'ram_kib=%s\navailable_kib=%s\nswap_kib=%s\nroot_free_kib=%s\n' "$mem" "$available" "$swap" "$disk"
printf 'avx=%s\navx2=%s\nfma=%s\n' "$avx" "$avx2" "$fma"
if [[ "$arch" == x86_64 && "$avx2" == no ]]; then
  echo 'inference_hint=legacy-x86-build-required'
elif [[ "$arch" == x86_64 || "$arch" == aarch64 ]]; then
  echo 'inference_hint=validate-native-build'
else
  echo 'inference_hint=unsupported-architecture'
fi
if [[ "$os" == ubuntu && "$version" == 24.04 && ( "$arch" == x86_64 || "$arch" == aarch64 ) ]]; then
  echo 'baseline=matches'
else
  echo 'baseline=manual-review'
fi
for cmd in podman systemctl curl; do
  if command -v "$cmd" >/dev/null 2>&1; then printf '%s=present\n' "$cmd"; else printf '%s=missing\n' "$cmd"; fi
done
echo 'readiness=inventory-only'
