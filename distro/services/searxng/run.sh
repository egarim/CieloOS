#!/usr/bin/env bash
# Stand up the Lun.Os private search service (SearXNG) as a podman container.
# Self-hosted metasearch with the JSON API enabled, no external API key, no rate
# limits. Session containers reach it via host.containers.internal:8888, and the
# `websearch` tool in the console image queries it. Idempotent — safe to re-run.
set -euo pipefail

DIR="${SEARXNG_DIR:-$HOME/lunos/services/searxng}"
PORT="${SEARXNG_PORT:-8888}"
mkdir -p "$DIR"

if [ ! -f "$DIR/settings.yml" ]; then
  SECRET="$(openssl rand -hex 32 2>/dev/null || head -c 32 /dev/urandom | base64)"
  cat > "$DIR/settings.yml" <<YML
use_default_settings: true
general:
  instance_name: "Lun.Os Search"
server:
  secret_key: "$SECRET"
  limiter: false        # no redis/valkey required
  image_proxy: false
search:
  formats:
    - html
    - json              # the JSON API the agent's websearch tool uses
YML
  chmod 644 "$DIR/settings.yml"
fi

podman rm -f lunos-searxng >/dev/null 2>&1 || true
podman run -d --name lunos-searxng --restart=unless-stopped \
  -p "${PORT}:8080" \
  -v "$DIR/settings.yml:/etc/searxng/settings.yml:ro" \
  docker.io/searxng/searxng:latest

echo "SearXNG starting on :${PORT} — JSON API at http://127.0.0.1:${PORT}/search?q=...&format=json"
