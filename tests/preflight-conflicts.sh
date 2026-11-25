#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/distro/install-conflicts.sh"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
check() {
  local expected=$1 actual
  actual=$(subid_status "$tmp/subid" "${2:-}")
  [[ "$actual" == "$expected" ]] || { echo "expected $expected, got $actual" >&2; exit 1; }
}
check unknown
: > "$tmp/subid"; check clear
printf 'alice:100000:65536\n' > "$tmp/subid"; check conflict
printf 'alice:165535:1\n' > "$tmp/subid"; check conflict
printf 'alice:165536:65536\n' > "$tmp/subid"; check clear
printf 'alice:99999:1\n' > "$tmp/subid"; check clear
printf 'cielo:200000:65536\n' > "$tmp/subid"; check existing-review
printf '998:200000:65536\n' > "$tmp/subid"; check existing-review 998
printf 'alice:invalid:65536\n' > "$tmp/subid"; check unknown
[[ "$(account_status '')" == not-found ]]
[[ "$(account_status 'cielo:x:998:998::/home/cielo:/bin/bash')" == existing-review ]]
echo 'Installer conflict fixtures passed'
