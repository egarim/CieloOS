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
:root{color-scheme:light;--acc:#5b5bd6;--acc2:#7c3aed}
body{margin:0;font-family:Inter,system-ui,sans-serif;background:#f2f4f8;color:#1c2333;display:grid;place-items:center;min-height:100vh;padding:22px 0}
.page{width:min(760px,94vw);display:grid;gap:18px}
.card{background:#fff;border:1px solid #e4e8f0;border-radius:18px;padding:30px 30px 26px;box-shadow:0 16px 40px rgba(28,35,51,.08)}
.logo{display:flex;align-items:center;gap:12px;margin-bottom:22px}
.dot{width:34px;height:34px;border-radius:9px;background:linear-gradient(135deg,var(--acc),var(--acc2));animation:pulse 1.6s ease-in-out infinite}
h1{font-size:20px;margin:0;letter-spacing:.3px}
.sub{color:#6b7280;font-size:13px;margin:2px 0 0}
.barwrap{margin-top:0;height:10px;border-radius:999px;background:#eef1f6;overflow:hidden}
.bar{height:100%;width:0%;border-radius:999px;background:linear-gradient(90deg,var(--acc),var(--acc2));transition:width .5s ease}
.msg{margin-top:14px;font-size:14px;color:#374151;min-height:20px}
.pct{margin-top:6px;font-size:12px;color:#9ca3af}
.ready{margin-top:16px;display:none}
.ready a{display:inline-block;background:var(--acc);color:#fff;padding:11px 20px;border-radius:10px;text-decoration:none;font-weight:600}
@keyframes pulse{0%,100%{transform:scale(1)}50%{transform:scale(1.12)}}
/* showcase */
.showcase{background:#fff;border:1px solid #e4e8f0;border-radius:18px;box-shadow:0 16px 40px rgba(28,35,51,.08);overflow:hidden}
.kicker{text-align:center;color:#8a93a3;font-size:12px;font-weight:600;letter-spacing:.08em;text-transform:uppercase;padding:18px 0 4px}
.stage{position:relative;min-height:220px}
.slide{position:absolute;inset:0;display:flex;gap:20px;align-items:center;padding:20px 28px 26px;opacity:0;transform:translateY(12px);transition:opacity .6s ease,transform .7s ease;pointer-events:none}
.slide.on{opacity:1;transform:none;pointer-events:auto}
.thumb{flex:0 0 200px;height:150px;border-radius:14px;border:1px solid #e6e9f2;background:linear-gradient(160deg,#f7f8fc,#eef1f9);display:grid;place-items:center;overflow:hidden}
.copy .t{font-weight:700;font-size:17px;margin-bottom:6px}
.copy .d{color:#5b6472;font-size:14px;line-height:1.5;max-width:340px}
.dots{display:flex;justify-content:center;gap:7px;padding:0 0 16px}
.dots span{width:7px;height:7px;border-radius:50%;background:#dde2ec;transition:all .3s}
.dots span.on{background:var(--acc);width:20px;border-radius:999px}
/* thumb visuals (CSS-only, offline) */
.v{width:120px;height:120px;position:relative}
.v .win{position:absolute;inset:0;background:#fff;border-radius:10px;box-shadow:0 8px 20px rgba(28,35,51,.14);border:1px solid #e6e9f2;overflow:hidden}
.v .tb{height:18px;background:#eef1f7;display:flex;align-items:center;gap:4px;padding:0 8px}
.v .tb i{width:6px;height:6px;border-radius:50%;background:#d0d6e2;display:inline-block}
.v .body{padding:10px;display:grid;gap:6px}
.v .ln{height:7px;border-radius:4px;background:#eceff6}
.v .ln.a{background:linear-gradient(90deg,var(--acc),var(--acc2));width:70%}
.v .ln.b{width:92%}.v .ln.c{width:60%}
.cursor{display:inline-block;width:7px;height:13px;background:var(--acc);vertical-align:middle;animation:blink 1s steps(2) infinite;margin-left:3px}
@keyframes blink{50%{opacity:0}}
/* spreadsheet */
.v .grid{display:grid;grid-template-columns:repeat(4,1fr);gap:5px;padding:12px}
.v .cell{height:18px;border-radius:4px;background:#eff2f8}
.v .cell.h{background:#dcdff0}
.v .cell.fill{background:linear-gradient(90deg,var(--acc),var(--acc2));animation:cellIn 2.4s infinite ease}
.v .cell.fill:nth-child(3){animation-delay:.3s}.v .cell.fill:nth-child(7){animation-delay:.7s}.v .cell.fill:nth-child(11){animation-delay:1.1s}.v .cell.fill:nth-child(13){animation-delay:1.5s}
@keyframes cellIn{0%,45%{opacity:.18}60%,100%{opacity:1}}
/* browser */
.v .addr{height:14px;margin:8px;border-radius:7px;background:#eef1f7;display:flex;align-items:center;padding:0 7px}
.v .addr b{width:46px;height:6px;border-radius:4px;background:#d0d6e2;display:inline-block}
.v .load{position:absolute;left:8px;right:8px;bottom:8px;height:6px;border-radius:4px;background:#eef1f6;overflow:hidden}
.v .load i{display:block;height:100%;width:34%;border-radius:4px;background:linear-gradient(90deg,var(--acc),var(--acc2));animation:load 2s infinite ease}
@keyframes load{0%{transform:translateX(-120%)}100%{transform:translateX(300%)}}
/* chat */
.v .bub{margin:10px;padding:7px 10px;border-radius:10px;background:var(--acc);color:#fff;font-size:11px;max-width:70%}
.v .bub.you{background:#eef1f7;color:#4b5563;margin-left:auto}
/* users */
.v .avs{display:flex;justify-content:center;gap:12px;align-items:center;height:100%}
.v .av{width:30px;height:30px;border-radius:50%;background:linear-gradient(135deg,var(--acc),var(--acc2));animation:av 2s infinite ease}
.v .av:nth-child(2){animation-delay:.4s}.v .av:nth-child(3){animation-delay:.8s}
@keyframes av{0%,100%{transform:translateY(0)}50%{transform:translateY(-6px)}}
/* shield */
.v .shield{position:absolute;inset:12px;border-radius:14px;background:linear-gradient(135deg,#eef1f6,#e2e8f4);display:grid;place-items:center}
.v .shield b{width:44px;height:44px;border-radius:50%;background:#fff;display:grid;place-items:center;box-shadow:0 6px 16px rgba(28,35,51,.12);color:var(--acc);font-size:22px}
@media(max-width:560px){.slide{flex-direction:column;text-align:center;align-items:center}.copy .d{max-width:none}.thumb{flex:0 0 auto;width:180px;height:130px}}
</style></head><body><div class="page">
  <div class="card">
    <div class="logo"><div class="dot"></div><div><h1>CieloOS</h1><p class="sub">Installing…</p></div></div>
    <div class="barwrap"><div class="bar" id="bar"></div></div>
    <div class="msg" id="msg">Setting up</div>
    <div class="pct" id="pct"></div>
    <div class="ready" id="ready"><a id="go" href="#">Open CieloOS</a></div>
  </div>
  <div class="showcase">
    <div class="kicker">While you wait — things you can do</div>
    <div class="stage" id="stage"></div>
    <div class="dots" id="dots"></div>
  </div>
</div>
<script>
const SLIDES=[
 {t:"Ask in plain words",d:"No commands. Just tell it what you need and it gets to work.",v:'<div class="v"><div class="win"><div class="tb"><i></i><i></i><i></i></div><div class="body"><div class="ln a"></div><div class="ln b"></div><div class="ln c"></div><div class="ln a" style="width:40%"></div><span class="cursor"></span></div></div></div>'},
 {t:"Make a spreadsheet in a minute",d:"A little budget, a planner, a list — just ask for it.",v:'<div class="v"><div class="win"><div class="grid"><div class="cell h"></div><div class="cell h"></div><div class="cell h"></div><div class="cell h"></div><div class="cell"></div><div class="cell fill"></div><div class="cell fill"></div><div class="cell"></div><div class="cell"></div><div class="cell"></div><div class="cell fill"></div><div class="cell"></div><div class="cell fill"></div><div class="cell"></div><div class="cell"></div><div class="cell"></div></div></div></div>'},
 {t:"Look things up for you",d:"It searches and shows you the answer right there.",v:'<div class="v"><div class="win"><div class="addr"><b></b></div><div class="body"><div class="ln b"></div><div class="ln a" style="width:64%"></div><div class="ln c"></div><div class="ln b" style="width:82%"></div></div><div class="load"><i></i></div></div></div>'},
 {t:"Drive the desktop",d:"Ask for a real desktop window and it opens, uses, and shows it to you.",v:'<div class="v"><div class="win"><div class="tb"><i></i><i></i><i></i></div><div class="body"><div class="ln a"></div><div class="ln b"></div><div class="ln c"></div></div></div></div>'},
 {t:"Everyone gets their own space",d:"Each person has their own assistant, files, and desktop.",v:'<div class="v"><div class="avs"><span class="av"></span><span class="av"></span><span class="av"></span></div></div>'},
 {t:"Your things stay on this machine",d:"Private by default, safe on this machine, and backed up.",v:'<div class="v"><div class="shield"><b>✓</b></div></div>'}
];
const stage=document.getElementById('stage'),dots=document.getElementById('dots');
SLIDES.forEach((s,i)=>{
 const el=document.createElement('div');el.className='slide'+(i===0?' on':'');el.innerHTML='<div class="thumb">'+s.v+'</div><div class="copy"><div class="t">'+s.t+'</div><div class="d">'+s.d+'</div></div>';
 stage.appendChild(el);
 const dot=document.createElement('span');dot.className=i===0?'on':'';dots.appendChild(dot);
});
let cur=0;const n=SLIDES.length;
function go(i){[].slice.call(stage.children).forEach((x,j)=>x.classList.toggle('on',j===i));[].slice.call(dots.children).forEach((x,j)=>x.classList.toggle('on',j===i));}
setInterval(()=>{cur=(cur+1)%n;go(cur);},5200);
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