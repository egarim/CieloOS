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
