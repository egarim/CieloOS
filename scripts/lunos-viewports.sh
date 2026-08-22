#!/usr/bin/env bash
# Keep Mac->VM SSH port-forwards in sync with the runtime's LIVE session viewport
# ports. The panel opens a session at http://localhost:<viewportPort>/, and those
# ports are assigned by podman per session (random, new on every recreate). QEMU
# only host-forwards 2222/5148, so without a forward the viewport is unreachable
# from the Mac -> "sometimes I can't Shadow/Open a session".
#
# Design:
#  - Singleton (atomic mkdir lock) so two copies never fight over the same ports.
#  - ONE persistent SSH master connection; forwards are added/removed INCREMENTALLY
#    with `ssh -O forward` / `-O cancel`, so opening/closing one session never
#    disturbs the streams already open in other sessions.
#  - A transient API failure is ignored (existing forwards are kept), not treated
#    as "no sessions".
set -uo pipefail

TOKEN="${LUNOS_TOKEN:-REDACTED-DEV-TOKEN}"
API="${LUNOS_API:-http://127.0.0.1:5150/api/sessions}"
SSH_HOST="workspace@localhost"
SSH_PORT="2222"
CTL="/tmp/lunos-viewports-ctl.sock"
LOCK="/tmp/lunos-viewports.lock"

# --- Singleton guard: atomic mkdir; steal the lock if its owner is dead. ---
if ! mkdir "$LOCK" 2>/dev/null; then
  old=$(cat "$LOCK/pid" 2>/dev/null || echo "")
  if [ -n "$old" ] && kill -0 "$old" 2>/dev/null; then
    echo "[viewports] already running (pid $old) — exiting"; exit 0
  fi
  rm -rf "$LOCK"; mkdir "$LOCK" 2>/dev/null || { echo "[viewports] cannot lock"; exit 1; }
fi
echo $$ > "$LOCK/pid"

cleanup() {
  ssh -S "$CTL" -O exit -p "$SSH_PORT" "$SSH_HOST" 2>/dev/null || true
  rm -rf "$LOCK" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

SSH_BASE=(-o StrictHostKeyChecking=accept-new -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -p "$SSH_PORT")
FWD=" "  # space-delimited set of currently-forwarded ports, e.g. " 39323 41773 "

ensure_master() {
  if ! ssh -S "$CTL" -O check "${SSH_BASE[@]}" "$SSH_HOST" 2>/dev/null; then
    ssh -M -S "$CTL" -fN "${SSH_BASE[@]}" "$SSH_HOST" 2>/dev/null || return 1
    FWD=" "  # fresh master owns no forwards yet
    echo "[viewports] master connection (re)established"
  fi
  return 0
}

echo "[viewports] watching $API (pid $$)"
while true; do
  if ensure_master; then
    resp=$(curl -s -m 5 -H "Authorization: Bearer $TOKEN" "$API" 2>/dev/null || echo "")
    # Only reconcile on a valid JSON array response; a transient failure or auth
    # error must NOT be read as "no sessions" (which would cancel every forward).
    if printf '%s' "$resp" | jq -e 'type=="array"' >/dev/null 2>&1; then
      # Normalize the wanted set to a single space-delimited string " a b c "
      # (jq emits newline-delimited; membership tests below are space-delimited).
      want=" $(printf '%s' "$resp" | jq -r '.[] | select(.status=="running") | .viewportPort' 2>/dev/null | sort -un | tr '\n' ' ') "
      # add missing forwards
      for p in $want; do
        case "$FWD" in
          *" $p "*) : ;;
          *) if ssh -S "$CTL" -O forward -L "127.0.0.1:${p}:localhost:${p}" "${SSH_BASE[@]}" "$SSH_HOST" 2>/dev/null; then
               FWD="${FWD}${p} "; echo "[viewports] + $p"
             fi ;;
        esac
      done
      # cancel stale forwards (both sets are space-delimited now)
      for p in $FWD; do
        case "$want" in
          *" $p "*) : ;;
          *) ssh -S "$CTL" -O cancel -L "127.0.0.1:${p}:localhost:${p}" "${SSH_BASE[@]}" "$SSH_HOST" 2>/dev/null || true
             FWD=" $(echo "$FWD" | tr ' ' '\n' | grep -v "^${p}$" | tr '\n' ' ') "
             echo "[viewports] - $p" ;;
        esac
      done
    fi
  fi
  sleep 5
done
