#!/usr/bin/env bash
# First-run bootstrap for a fresh, single-user machine. Claims the box as the OS
# user (so the desk slug matches your username, not a name you typed), sets a
# password, and prints the credentials — no CLI wizard, no dead-end at the token
# box. Safe to re-run: if the box is already claimed it says so and leaves it.
#
#   ./cielo-first-run.sh [port]     (default 5148)
#
# Runs on the machine itself: the claim and the first-password set are both
# loopback-only, and this is expected to run on the box (a WSL one-liner, or
# install.sh). Credentials are printed to the terminal, which is fine because the
# claim token is already shown to whoever claims the machine.
set -euo pipefail

PORT="${1:-5148}"
BASE="http://127.0.0.1:${PORT}"
USER_NAME="${USER:-}"

if [[ -z "$USER_NAME" ]]; then
  echo "No OS username (\$USER) to claim as." >&2
  exit 1
fi

status="$(curl -fsS "$BASE/api/setup/status")" || {
  echo "The runtime is not up on $BASE yet (start it first)." >&2
  exit 1
}

if printf '%s' "$status" | grep -q '"claimed":true'; then
  echo "This box is already claimed. Log in with your existing credentials"
  echo "(your desk, and the token in ~/cielo/.data/secrets/<slug>.token if you"
  echo "have not set a password yet)."
  exit 0
fi

echo "==> Claiming CieloOS for '$USER_NAME'"
resp="$(curl -fsS -XPOST "$BASE/api/setup/claim" -H 'Content-Type: application/json' \
  -d "{\"name\":\"${USER_NAME}\"}")" || {
  echo "Claim failed; see the runtime log." >&2
  exit 1
}

slug="$(printf '%s' "$resp" | sed -n 's/.*"slug":"\([^"]*\)".*/\1/p')"
token="$(printf '%s' "$resp" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')"
if [[ -z "$slug" || -z "$token" ]]; then
  echo "Unexpected claim response: $resp" >&2
  exit 1
fi

# A password you'll actually use, long enough for the length rule (>= 10).
pw="$(tr -dc 'a-zA-Z0-9' < /dev/urandom | head -c 16)"

# Setting the FIRST password is loopback-only and needs the owner token as the
# bearer — both true here. If it fails (say the runtime predates passwords), fall
# back to the token.
curl -fsS -XPOST "$BASE/api/auth/password" \
  -H "Authorization: Bearer ${token}" -H 'Content-Type: application/json' \
  -d "{\"newPassword\":\"${pw}\"}" >/dev/null 2>&1 || {
  echo "  (could not set a password here; log in with the token and set one in the panel's Models tab)" >&2
}

echo
echo "==> CieloOS is ready."
echo "    Your desk:  ${slug}"
echo "    Password:   ${pw}      (change it in the panel: Models tab)"
echo "    Token:      ${token}   (backup; written to ~/cielo/.data/secrets/${slug}.token)"
echo
echo "    Open http://localhost:${PORT}/ and sign in."
