#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:${PATH}"

cleanup() {
  if [[ -n "${BACKEND_PID:-}" ]]; then
    kill "${BACKEND_PID}" 2>/dev/null || true
  fi
}
trap cleanup EXIT

export BACKEND_PORT="${BACKEND_PORT:-5148}"

if command -v lsof >/dev/null 2>&1 && lsof -nP -iTCP:"${BACKEND_PORT}" -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Port ${BACKEND_PORT} is already in use (a running VM forwards its API to 5148)." >&2
  echo "Pick another port: BACKEND_PORT=5149 ./scripts/dev.sh" >&2
  exit 1
fi

dotnet run --project src/backend/WorkspaceRuntime.Api --urls "http://127.0.0.1:${BACKEND_PORT}" &
BACKEND_PID=$!

HUMAN_TOKEN_FILE=".data/secrets/joche.token"
(
  for _ in $(seq 1 60); do
    if [[ -f "${HUMAN_TOKEN_FILE}" ]]; then
      echo ""
      echo "==> Session token for joche (paste into the panel): $(cat "${HUMAN_TOKEN_FILE}")"
      echo "    (yulia's token is .data/secrets/yulia.token)"
      echo ""
      exit 0
    fi
    sleep 1
  done
) &

cd src/frontend
if [[ ! -d node_modules ]]; then
  npm install
fi
npm run dev
