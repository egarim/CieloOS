# Giving the agent hands and eyes

Goal: an agent that fulfills open-ended, real-world requests by *using the
computer* — e.g. "search the web for the top 10 posts about El Salvador and make
a spreadsheet." The governed observe→decide→act loop is built (console loop).
What varies is *reach*: the sandbox's tools and how the agent perceives + acts.

Two tiers, matching the direction doc (`typed where possible, pixels where
necessary`). Both reuse the same spine: the loop, the policy bus, the audit
ledger, ownership, and artifacts landing in the agent's home.

---

## Step 1 — tools in the console — SHIPPED (2026-08-22)

The console agent gets real capabilities as CLI tools it drives like a person,
plus a private search service. A text model is enough and results are *real*
(no hallucinated "top 10").

- **SearXNG** — self-hosted metasearch, JSON API (`?format=json`), no external
  key, no rate limits. Runs as a podman service (`lunos-searxng`, host `:8888`);
  reachable from session containers via `host.containers.internal`. Config +
  launcher in `distro/services/searxng/` (`run.sh`).
- **Sandbox tools** — the console image (`distro/images/console/`) has `curl`,
  `jq`, `w3m`, `python3` + `openpyxl`/`requests`, and a `websearch "QUERY" N`
  wrapper that prints TSV (rank, title, url, snippet).
- **Proven end-to-end**: DeepSeek (the console-loop brain) ran *"search the web
  for the top 10 posts about El Salvador → save as .xlsx"* and produced a real
  `el-salvador.xlsx` with 10 live results — every keystroke policy-checked and
  audited. Note the loop's observe-after-act now *waits for the command to
  finish* (polls `pane_current_command`) instead of a fixed delay.

Bounded to what a shell can do; the *brain* here is cloud (DeepSeek). The desktop
tier below is deliberately different: local-only.

---

## Step 2 — local desktop-control agent — PLANNED

The general capability: the agent *sees the screen* and drives a real desktop
(click/type/scroll) like a person — PyAutoGUI-style, but model-driven. **Hard
constraint: everything runs locally, no cloud APIs.** (This refines direction-doc
D4, which had said cloud-first vision: the desktop-control component is local
only. Decided — recorded, not up for re-litigation.)

### Architecture (decided)

- **Hybrid perception.** AT-SPI2 accessibility tree first (free, exact, no GPU);
  a vision model only as fallback for Electron apps, canvases, games, and broken
  a11y trees.
- **Perception → text → decision.** The decision model never sees pixels or
  emits raw coordinates. It gets a *numbered list of screen elements* and replies
  with an action referencing an element ID — `click(7)`, `type(12, "hello")`.
  Code resolves the ID to box-center coordinates.
- **Two-tier decision models (via Ollama):**
  - *Planner* (once per task → step list): `deepseek-r1:7b`
    (DeepSeek-R1-Distill-Qwen-7B) — reasoning; latency acceptable here.
  - *Executor* (per step → picks element IDs): `qwen2.5:7b-instruct` (or 3B) —
    fast, no chain-of-thought. Element selection is reading comprehension; a
    plain instruct model beats R1 distills here.
- **Vision fallback:** `qwen2.5vl:7b` (Qwen2.5-VL) — natively outputs
  pixel-coordinate boxes/points, trained on UI screenshots. Optional later:
  Microsoft OmniParser v2 (YOLOv8 + Florence-2, not an LLM) as a resident service
  turning screenshots into labeled element lists (~1–2 GB VRAM, ~0.5–1 s/frame).
- **Input injection:** a small privileged daemon writing to `/dev/uinput`
  (python-evdev). Works on X11 and every Wayland compositor — no ydotool /
  pyautogui in the final design. Prefer ABS positioning (ABS_X/ABS_Y).
- **Screenshots:** xdg-desktop-portal screencast on Wayland (or a compositor hook
  built into the distro); `mss` on X11 for dev convenience.
- **Agent loop:** screenshot + a11y tree → element list → executor action → uinput
  → observe → repeat. A perception/action **service on the command bus**, not a
  per-app script. Natural target environment: the `agent-desktop` (webtop XFCE)
  sessions Lun.Os already runs.

### Phases

1. **Environment.** In the Linux VM: `python3-pyatspi`/`python-atspi`,
   `python3-evdev`, `mss`, Pillow; enable a11y (`GTK_MODULES=gail:atk-bridge`,
   `QT_ACCESSIBILITY=1`, at-spi2 dbus running). Install Ollama; pull
   `qwen2.5:7b-instruct`, `qwen2.5vl:7b`, `deepseek-r1:7b`. Verify
   `ollama run qwen2.5vl:7b` on a test screenshot returns sensible boxes.
2. **Action layer (uinput daemon).** move/click/double-click/scroll/type/
   key-combo over a local socket (JSON lines). python-evdev `UInput` with
   REL/ABS + BTN_LEFT/RIGHT + full keyboard. Acceptance: a script clicks an
   arbitrary (x, y) and types into any focused Wayland app.
3. **Perception A — AT-SPI reader.** Walk the active window's AT-SPI tree → a
   numbered element list `[id] role "name" (x,y,w,h) states=[…]`; filter to
   visible + actionable (buttons, links, inputs, menu items, tabs); `dump_screen()`
   merges top-level windows. Acceptance: correct clickable coords on GNOME Text
   Editor / Firefox with **no** model call.
4. **Perception B — vision fallback.** Screenshot module (portal / `mss`); a
   Qwen2.5-VL prompt returning elements in the **same JSON schema as Phase 3**
   (source-agnostic downstream); fallback policy — use vision only when the a11y
   tree is empty/sparse (< N actionable) or the executor reports "target not in
   list". Optional later: OmniParser v2.
5. **Agent loop.** Executor prompt: step + element list → one action
   `click(id)` / `type(id,text)` / `scroll(dir)` / `key(combo)` / `done` /
   `not_found` (Ollama structured output, JSON schema, temp ~0). Planner prompt:
   task + first screen → numbered steps; re-plan on repeated failure. Per-step
   verification (re-read screen, retry/backoff, hard step cap). Acceptance task:
   *"open the file manager, create a folder named test, rename it"* end-to-end,
   no human input.
6. **Lun.Os integration (later).** Wrap perception (`screen.read`) and action
   (`input.click`/`type`/…) as services on the command bus; the agent becomes
   just another bus client. MCP exposure after bus unification (per existing
   direction: MCP after unification).

### Risks / notes

- Wayland portal screenshot permissions: since it's our distro, pre-grant or
  patch the portal — don't fight the consent dialog.
- Electron/Chrome a11y: Chromium only populates AT-SPI with accessibility flags —
  the distro can set `ACCESSIBILITY_ENABLED=1` / `--force-renderer-accessibility`
  globally, which shrinks the vision-fallback surface a lot.
- R1-distill latency: planner only, out of the inner loop.
- VRAM budget: Qwen2.5-VL 7B Q4 ≈ 6–8 GB; executor 3B if GPU-poor; everything can
  run CPU-only, just slower.

### Deliverable (next build)

Phases 2–3 as a small Python package `lunos-agent/`:
`action/uinputd.py`, `perceive/atspi_reader.py`, `perceive/screenshot.py`,
`agent/loop.py`, plus a smoke-test script per phase.

---

## Why both

Step 1 makes today's requests work now (with the cloud model already wired). Step
2 is the north star and is local-only. They are not exclusive: a typed
`search`/SearXNG tool stays useful even once the agent can browse a desktop
visually — reliability where we can get it, pixels only where we must.
