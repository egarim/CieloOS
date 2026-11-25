#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/distro/cpu-flags.sh"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
check() {
  local expected=$1 flag=$2 actual=no
  if has_flag "$tmp/cpuinfo" "$flag"; then actual=yes; fi
  [[ "$actual" == "$expected" ]] || { echo "$flag: expected $expected, got $actual" >&2; exit 1; }
}
printf 'flags : sse2 avx\nflags : sse2 avx\n' > "$tmp/cpuinfo"
check yes avx
check no avx2
check no fma
printf 'flags : avx avx2 fma\nflags : avx\n' > "$tmp/cpuinfo"
check yes avx
check no avx2
check no fma
printf 'flags : avx2 fma\nflags : avx2 fma\n' > "$tmp/cpuinfo"
check no avx
check yes avx2
check yes fma
printf 'Features : fp asimd\n' > "$tmp/cpuinfo"
check no avx
: > "$tmp/cpuinfo"
check no avx2
echo 'CPU fixture tests passed'
