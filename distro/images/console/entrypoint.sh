#!/bin/sh
set -e

# Start a PERSISTENT detached tmux session at boot so the console screen exists
# even with nobody attached. The agent operates it through `podman exec tmux
# capture-pane / send-keys -t main`; a human attaches to the SAME session via
# ttyd, so both see one live screen (this is what makes shadow/become real).
tmux new-session -d -s main -c "$HOME"

# Don't accept web clients until the server socket is actually up.
i=0
while [ "$i" -lt 20 ]; do
  if tmux has-session -t main 2>/dev/null; then
    break
  fi
  i=$((i + 1))
  sleep 0.2
done

# ttyd attaches clients to the already-running 'main' (-A attaches if it
# exists); -W makes the terminal writable.
exec ttyd -p 7681 -W tmux new -A -s main
