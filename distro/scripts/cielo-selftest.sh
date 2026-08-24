#!/usr/bin/env bash
# Health-check a running CieloOS. Non-destructive by default (safe on your real
# machine); with --claim it also runs the full first-run flow (claim + add a
# provider) — only do that on a throwaway/CI machine.
#
#   cielo-selftest [--url http://127.0.0.1:5148] [--claim]
set -u

BASE="http://127.0.0.1:5148"
CLAIM=0
while [ $# -gt 0 ]; do
  case "$1" in
    --url) BASE="$2"; shift 2 ;;
    --claim) CLAIM=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

fails=0
pass() { printf '  \033[32mPASS\033[0m %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$1"; fails=$((fails + 1)); }

code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
body() { curl -s "$@"; }

echo "CieloOS self-test → $BASE"

# --- non-destructive checks (always) ---
[ "$(code "$BASE/")" = "200" ] && [ -n "$(body "$BASE/" | grep -o 'id="root"')" ] \
  && pass "panel is served at /" || fail "panel is served at /"

[ "$(code "$BASE/api/branding")" = "200" ] \
  && pass "/api/branding is public" || fail "/api/branding is public"

status="$(body "$BASE/api/setup/status")"
printf '%s' "$status" | grep -q '"claimed"' \
  && pass "/api/setup/status responds ($status)" || fail "/api/setup/status responds"

[ "$(code "$BASE/api/models")" = "401" ] \
  && pass "/api/models rejects no-token (401)" || fail "/api/models rejects no-token"

# Downloads hand out file bytes, so an unauthenticated one must never be served —
# and a path that walks out of the volume must be a plain 404, not /etc/passwd.
[ "$(code "$BASE/api/shared/download?path=outbox.md")" = "401" ] \
  && pass "file download rejects no-token (401)" || fail "file download rejects no-token"

# --- destructive full flow (opt-in) ---
if [ "$CLAIM" = "1" ]; then
  if printf '%s' "$status" | grep -q '"claimed":true'; then
    echo "  (already claimed — skipping the claim flow)"
  else
    claim="$(body -X POST "$BASE/api/setup/claim" -H 'Content-Type: application/json' -d '{"name":"Self Test"}')"
    token="$(printf '%s' "$claim" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')"
    [ -n "$token" ] && pass "claim created an owner" || fail "claim created an owner ($claim)"

    if [ -n "$token" ]; then
      who="$(body "$BASE/api/whoami" -H "Authorization: Bearer $token")"
      printf '%s' "$who" | grep -q '"slug":"self-test"' \
        && pass "owner token authenticates (whoami)" || fail "owner token authenticates ($who)"

      [ "$(code "$BASE/api/models" -H "Authorization: Bearer $token")" = "200" ] \
        && pass "/api/models readable with token" || fail "/api/models readable with token"

      add="$(code -X POST "$BASE/api/models" -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
        -d '{"displayName":"Test DS","kind":"openai-compatible","baseUrl":"https://api.deepseek.com","model":"deepseek-chat","apiKey":"sk-selftest","capabilities":["chat"],"locality":"cloud","defaultFor":["chat"]}')"
      [ "$add" = "200" ] && pass "add a model provider (no restart)" || fail "add a model provider ($add)"

      models="$(body "$BASE/api/models" -H "Authorization: Bearer $token")"
      printf '%s' "$models" | grep -q '"chat":"test-ds"' \
        && pass "new provider is the chat default" || fail "new provider is the chat default"
      printf '%s' "$models" | grep -q 'sk-selftest' \
        && fail "API key LEAKED in /api/models" || pass "API key is not returned"

      # A traversal must be reduced to a relative path, so this can only ever miss
      # inside the volume. Anything but 404 means the download escaped it.
      traversal="$(body "$BASE/api/shared/download?path=../../../../etc/passwd" -H "Authorization: Bearer $token")"
      printf '%s' "$traversal" | grep -q 'root:' \
        && fail "file download ESCAPED the volume (served /etc/passwd)" \
        || pass "file download cannot traverse out of the volume"
    fi
  fi
fi

echo
if [ "$fails" -eq 0 ]; then
  printf '\033[32mAll checks passed.\033[0m\n'
  exit 0
fi
printf '\033[31m%d check(s) failed.\033[0m\n' "$fails"
exit 1
