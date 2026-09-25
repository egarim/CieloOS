#!/usr/bin/env bash
# LUNOS splash — a city on a river turning from day to night under a full moon.
#
#   ./lunos-splash.sh play          day to night, then hold on the night loop
#   ./lunos-splash.sh play --once   play through once and exit
#   ./lunos-splash.sh play --hold 30  hold the night loop for 30s, then exit
#
# Any key, Ctrl-C, or the hold expiring ends it and hands the screen back
# untouched (it draws on the alternate buffer, so scrollback survives).
#
# Frames are pre-rendered by ASCII Motion into lunos-splash-frames/ — see the
# README there for how to change the scene. 120 frames at 12fps: 0..95 is the
# transition, 96..119 is a seamless night loop.
#
# Every cell is '▀' with a foreground and a background colour, which is how N
# character rows hold a 2N-pixel-tall picture. That needs a terminal that can do
# colour; on anything else this exits quietly rather than spraying escapes.
#
# The scene ships at two sizes and the larger one is used when it fits:
#
#   160x48 chars -> 160x96 px    four times the pixels
#    80x24 chars ->  80x48 px    fits anywhere
#
# Set LUNOS_SIZE=80x24 to force the small one.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FRAME_DIR="${LUNOS_FRAMES:-$HERE/lunos-splash-frames}"
FPS="${LUNOS_FPS:-12}"
TRANSITION_END=96 # first frame of the night loop

# Largest first; the first one that fits the terminal wins.
SIZES=(160x48 80x24)

ART_W=80
ART_H=24

usable() {
  [[ -t 1 ]] || return 1
  [[ "${TERM:-dumb}" != "dumb" ]] || return 1
  [[ -d "$FRAME_DIR" ]] || return 1
  [[ "${LUNOS_SPLASH:-1}" != "0" ]] || return 1
  command -v zcat >/dev/null 2>&1 || return 1
}

# Truecolor if the terminal admits to it, 256 colours otherwise. The 256-colour
# stream loses tones the palettes rely on — it is a fallback, not a choice.
#
# Sets ART_W/ART_H as a side effect, since the size chosen decides them.
pick_stream() {
  local cols=$1 lines=$2 depth=256 size w h
  case "${COLORTERM:-}" in truecolor | 24bit) depth=24bit ;; esac

  for size in "${SIZES[@]}"; do
    w="${size%x*}"
    h="${size#*x}"
    [[ -n "${LUNOS_SIZE:-}" && "$LUNOS_SIZE" != "$size" ]] && continue
    ((cols >= w && lines >= h)) || continue
    [[ -r "$FRAME_DIR/lunos-$size-$depth.ans.gz" ]] || continue
    ART_W="$w"
    ART_H="$h"
    echo "$FRAME_DIR/lunos-$size-$depth.ans.gz"
    return 0
  done
  return 1
}

restore() {
  printf '\033[?25h\033[?1049l' >/dev/tty 2>/dev/null || true
  stty echo 2>/dev/null || true
}

play() {
  local once=0 hold=0
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --once) once=1; shift ;;
      --hold) hold="${2:-0}"; shift 2 ;;
      *) shift ;;
    esac
  done

  usable || return 0

  local cols lines pad top stream
  cols=$(tput cols 2>/dev/null || echo 80)
  lines=$(tput lines 2>/dev/null || echo 24)

  # The size is picked before anything is drawn, because it decides ART_W/ART_H
  # and therefore the margins.
  if ! stream="$(pick_stream "$cols" "$lines")"; then
    echo "no frame set fits a ${cols}x${lines} terminal" >&2
    return 1
  fi

  pad=$(((cols - ART_W) / 2)); ((pad < 0)) && pad=0
  top=$(((lines - ART_H) / 2)); ((top < 0)) && top=0

  local frames=()
  mapfile -d $'\f' -t frames < <(zcat "$stream")
  ((${#frames[@]})) || return 1

  # Bake the left margin in once rather than once per frame. On a terminal that
  # is exactly 80 wide this is skipped entirely, which is the kiosk case.
  if ((pad > 0)); then
    local padstr k
    printf -v padstr '%*s' "$pad" ''
    for k in "${!frames[@]}"; do
      frames[k]="$padstr${frames[k]//$'\n'/$'\n'$padstr}"
    done
  fi

  trap 'restore; exit 0' INT TERM EXIT
  printf '\033[?1049h\033[?25l\033[2J' >/dev/tty
  stty -echo 2>/dev/null || true

  local delay
  delay=$(awk -v f="$FPS" 'BEGIN{printf "%.3f", 1/f}')
  local deadline=0
  ((hold > 0)) && deadline=$((SECONDS + hold))

  # The frame delay normally comes from waiting on a keypress, which is also how
  # the splash is dismissed. If /dev/tty cannot be read, that wait returns
  # instantly and the loop would spin at full speed — so fall back to sleeping
  # and give up on the keypress.
  local interactive=1
  read -rsn1 -t 0.01 _ </dev/tty 2>/dev/null
  [[ $? -gt 128 ]] || interactive=0

  local i=0 total=${#frames[@]}
  while :; do
    printf '\033[%d;1H%s' "$((top + 1))" "${frames[i]}" >/dev/tty

    if ((interactive)); then
      # any keypress ends it
      if read -rsn1 -t "$delay" _ </dev/tty 2>/dev/null; then break; fi
    else
      sleep "$delay"
    fi

    i=$((i + 1))
    if ((i >= total)); then
      ((once)) && break
      i=$TRANSITION_END # fall back into the night loop, never back to daylight
    fi
    ((deadline > 0 && SECONDS >= deadline)) && break
  done

  trap - INT TERM EXIT
  restore
}

case "${1:-}" in
  play) shift; play "$@" ;;
  *)
    sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
