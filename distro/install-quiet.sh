#!/usr/bin/env bash
# Run install.sh behind the animated install screen.
#
#   sudo ./install-quiet.sh --mode kiosk        # same options as install.sh
#
# install.sh is unchanged and still the thing that does the work: this only
# watches its output. Every line goes to /var/log/cielo-install.log, the
# "==> [n/9] Title" step headers drive the animation, and nothing reaches the
# terminal until the install is over — then the closing banner does, because the
# last screen is the one people read and it has to be the true one.
#
# A failure prints the tail of the log, so a broken install never hides behind a
# nice picture. Set CIELO_TUI=0 (or pipe this anywhere) to fall back to plain
# install.sh output.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL="$HERE/install.sh"
TUI="$HERE/scripts/cielo-install-tui.sh"
[[ -x "$TUI" ]] || TUI="$HERE/cielo-install-tui.sh" # flat release bundle
LOG="${CIELO_INSTALL_LOG:-/var/log/cielo-install.log}"

[[ -x "$INSTALL" ]] || { echo "install.sh not found next to $0" >&2; exit 2; }

# Pull the values the animation shows in the runtime box out of the same
# arguments install.sh is about to parse.
MODE=headless PORT=5148
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  case "${args[i]}" in
    --mode) MODE="${args[i + 1]:-headless}" ;;
    --port) PORT="${args[i + 1]:-5148}" ;;
  esac
done
case "$MODE" in headless) BIND="0.0.0.0" ;; *) BIND="127.0.0.1" ;; esac

mkdir -p "$(dirname "$LOG")" 2>/dev/null || true
: >"$LOG" || { echo "cannot write $LOG" >&2; exit 2; }

ANIMATED=0
if [[ -t 1 && "${CIELO_TUI:-1}" != "0" && -x "$TUI" ]]; then
  ANIMATED=1
fi

finish() {
  [[ "$ANIMATED" -eq 1 ]] && "$TUI" stop || true
}
trap finish EXIT INT TERM

if [[ "$ANIMATED" -eq 1 ]]; then
  "$TUI" start "$LOG"
  "$TUI" env "$MODE · $BIND:$PORT"
fi

# stdbuf keeps install.sh's step headers arriving as they happen rather than in
# 4 KB gulps, which is the difference between a live animation and a lying one.
set -o pipefail
if [[ "$ANIMATED" -eq 1 ]]; then
  stdbuf -oL -eL "$INSTALL" "$@" 2>&1 | while IFS= read -r line; do
    printf '%s\n' "$line" >>"$LOG"
    if [[ "$line" =~ ^==\>\ \[([0-9]+)/9\]\ (.*)$ ]]; then
      "$TUI" set "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}"
    fi
  done
  rc=${PIPESTATUS[0]}
else
  "$INSTALL" "$@" 2>&1 | tee -a "$LOG"
  rc=${PIPESTATUS[0]}
fi

finish
trap - EXIT INT TERM

if [[ "$rc" -eq 0 ]]; then
  # The banner and any "did not work" list install.sh prints at the end. Only
  # worth replaying if the animation swallowed it; the fallback path already
  # printed everything as it happened.
  if [[ "$ANIMATED" -eq 1 ]]; then
    if grep -q '^================ CieloOS' "$LOG"; then
      sed -n '/^================ CieloOS/,$p' "$LOG"
    else
      tail -n 20 "$LOG"
    fi
  fi
else
  echo "==> Install failed (exit $rc)." >&2
  # Behind the animation nobody saw why, so say why.
  if [[ "$ANIMATED" -eq 1 ]]; then
    echo "    Last 40 lines of $LOG:" >&2
    tail -n 40 "$LOG" >&2
  fi
fi
echo
echo "Full install log: $LOG"
exit "$rc"
