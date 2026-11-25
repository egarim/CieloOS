# shellcheck shell=bash
has_flag() {
  awk -v flag="$2" '/^flags[[:space:]]*:/ {n++; found=0; for(i=2;i<=NF;i++) if($i==flag) found=1; if(found) ok++} END {exit !(n>0 && n==ok)}' "$1"
}
