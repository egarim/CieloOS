#!/usr/bin/env bash
# CieloOS animated install page. While a long install runs (podman image builds,
# mostly), this serves a small local page with a CSS-animated logo and a progress
# bar, driven by a JSON state file the installer updates. Pure sugar — the install
# works fine without it; the page only makes the wait look alive.
#
#   ./cielo-install-ui.sh serve [port]      start the page (background)
#   ./cielo-install-ui.sh set <pct> <msg>   update the bar + message
#   ./cielo-install-ui.sh done <url>        mark finished, offer a jump to <url>
#   ./cielo-install-ui.sh stop              stop the server
#
# The page is open at http://localhost:<port>/ (WSL forwards localhost to Windows).
set -euo pipefail

STATE="${INSTALL_STATE:-/tmp/cielo-install.json}"
PIDFILE=/tmp/cielo-install-ui.pid
ROOTFILE=/tmp/cielo-install-ui.root
PORT="${2:-8080}"

write_state() { printf '%s' "$1" > "$STATE"; }

case "${1:-}" in
  serve)
    rm -f "$PIDFILE"
    : > "$STATE"
    ROOT="$(mktemp -d)"
    printf '%s' "$ROOT" > "$ROOTFILE"

    cat > "$ROOT/index.html" <<'HTML'
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>CieloOS — installing</title><style>
:root{color-scheme:light}
body{margin:0;font-family:Inter,system-ui,sans-serif;background:#f2f4f8;color:#1c2333;display:grid;place-items:center;min-height:100vh}
.card{width:min(420px,90vw);background:#fff;border:1px solid #e4e8f0;border-radius:18px;padding:40px 36px;box-shadow:0 16px 40px rgba(28,35,51,.08)}
.logo{display:flex;align-items:center;gap:12px;margin-bottom:26px}
.dot{width:34px;height:34px;border-radius:9px;background:linear-gradient(135deg,#5b5bd6,#7c3aed);animation:pulse 1.6s ease-in-out infinite}
h1{font-size:20px;margin:0;letter-spacing:.3px}
.sub{color:#6b7280;font-size:13px;margin:2px 0 0}
.barwrap{margin-top:22px;height:10px;border-radius:999px;background:#eef1f6;overflow:hidden}
.bar{height:100%;width:0%;border-radius:999px;background:linear-gradient(90deg,#5b5bd6,#7c3aed);transition:width .5s ease}
.msg{margin-top:14px;font-size:14px;color:#374151;min-height:20px}
.pct{margin-top:6px;font-size:12px;color:#9ca3af}
.ready{margin-top:18px;display:none}
.ready a{display:inline-block;background:#5b5bd6;color:#fff;padding:11px 20px;border-radius:10px;text-decoration:none;font-weight:600}
@keyframes pulse{0%,100%{transform:scale(1)}50%{transform:scale(1.12)}}
</style></head><body><div class="card">
  <div class="logo"><div class="dot"></div><div><h1>CieloOS</h1><p class="sub">Installing…</p></div></div>
  <div class="barwrap"><div class="bar" id="bar"></div></div>
  <div class="msg" id="msg">Setting up</div>
  <div class="pct" id="pct"></div>
  <div class="ready" id="ready"><a id="go" href="#">Open CieloOS</a></div>
</div>
<script>
const bar=document.getElementById('bar'),msg=document.getElementById('msg'),pct=document.getElementById('pct'),ready=document.getElementById('ready'),go=document.getElementById('go');
async function poll(){try{const r=await fetch('/status');const d=await r.json();bar.style.width=(d.pct||0)+'%';msg.textContent=d.message||'Working…';pct.textContent=(d.pct||0)+'%';if(d.done){go.href=d.url||'/';ready.style.display='block';go.textContent='Open CieloOS';}}catch(e){}setTimeout(poll,400)}
poll();
</script></body></html>
HTML

    cat > "$ROOT/server.py" <<'PY'
import http.server, socketserver, json, os
STATE = os.environ.get("INSTALL_STATE", "/tmp/cielo-install.json")
PORT = int(os.environ.get("CIELO_UI_PORT", "8080"))
class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        if self.path == "/status":
            data = {}
            if os.path.exists(STATE):
                try: data = json.load(open(STATE, encoding="utf-8"))
                except Exception: pass
            b = json.dumps(data).encode()
            self.send_response(200); self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
        else:
            p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "index.html")
            b = open(p, "rb").read()
            self.send_response(200); self.send_header("Content-Type", "text/html")
            self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
class S(socketserver.TCPServer): allow_reuse_address = True
with S(("127.0.0.1", PORT), H) as s: s.serve_forever()
PY

    env CIELO_UI_PORT="$PORT" INSTALL_STATE="$STATE" python3 "$ROOT/server.py" &
    echo $! > "$PIDFILE"
    echo "Install page: http://localhost:$PORT/"
    ;;

  set)
    pct="${2:-0}"; msg="${3:-}"
    escaped="$(printf '%s' "$msg" | sed 's/"/\\"/g')"
    write_state "{\"step\":$(date +%s),\"pct\":${pct},\"message\":\"${escaped}\",\"done\":false}"
    ;;

  done)
    url="${2:-/}"
    escaped="$(printf '%s' "$url" | sed 's/"/\\"/g')"
    write_state "{\"step\":$(date +%s),\"pct\":100,\"message\":\"Ready\",\"done\":true,\"url\":\"${escaped}\"}"
    ;;

  stop)
    [[ -f "$PIDFILE" ]] && kill "$(cat "$PIDFILE")" 2>/dev/null || true
    [[ -f "$ROOTFILE" ]] && rm -rf "$(cat "$ROOTFILE")" || true
    rm -f "$PIDFILE" "$ROOTFILE"
    ;;

  *)
    echo "usage: cielo-install-ui.sh serve [port] | set <pct> <message> | done <url> | stop" >&2
    exit 2
    ;;
esac