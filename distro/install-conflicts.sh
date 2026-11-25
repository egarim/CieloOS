# shellcheck shell=bash
# Inventory only. The current installer requests 100000-165535 if cielo has no mapping.
subid_status() {
  if [[ ! -r "$1" ]]; then echo unknown; return; fi
  awk -F: '
    NF != 3 || $2 !~ /^[0-9]+$/ || $3 !~ /^[0-9]+$/ || $3 == 0 {bad=1; next}
    $1 == "cielo" || (uid != "" && $1 == uid) {existing=1; next}
    $2 <= 165535 && $2+$3-1 >= 100000 {overlap=1}
    END {if(bad) print "unknown"; else if(existing) print "existing-review"; else if(overlap) print "conflict"; else print "clear"}
  ' uid="${2:-}" "$1"
}
account_status() {
  if [[ -n "$1" ]]; then echo existing-review; else echo not-found; fi
}

# Strict fresh-host gate: existing installations and ambiguous allocations require
# manual review. No writes, and no automatic adoption of accounts.
fresh_host_guard() {
  local account="$1" uid_status gid_status
  if [[ -n "$account" ]]; then
    echo "Existing cielo account: refusing fresh-host install." >&2
    return 1
  fi
  uid_status=$(subid_status "$2")
  gid_status=$(subid_status "$3")
  if [[ "$uid_status" != clear || "$gid_status" != clear ]]; then
    echo "Subordinate ID allocation requires review: uid=$uid_status gid=$gid_status" >&2
    return 1
  fi
}
