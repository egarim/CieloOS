# lunos-agent — local desktop-control agent

The Step 2 "hands and eyes" prototype from [`../docs/hands-and-eyes.md`](../docs/hands-and-eyes.md):
an agent that reads the screen and drives a real desktop (click/type/scroll),
**fully local, no cloud**. Perception is AT-SPI-first with a vision fallback;
the decision model picks *element ids* from a text list, never raw coordinates;
input goes through a `/dev/uinput` daemon so it works on X11 and Wayland.

## Status

| Phase | What | State |
|---|---|---|
| 2 | `action/uinputd.py` — /dev/uinput injection daemon + `action/client.py` | **implemented** |
| 3 | `perceive/atspi_reader.py` — AT-SPI tree → numbered element list | **implemented** |
| 4 | `perceive/screenshot.py` — mss (X11) done; Wayland portal capture is a TODO | partial |
| 5 | `agent/loop.py` — perceive→decide→act loop; Ollama planner/executor wired, prompts first-cut | skeleton |
| 6 | expose `screen.read` / `input.*` as Lun.Os command-bus services | later |

It needs the Phase-1 environment to actually run (a Linux desktop session with
AT-SPI + `/dev/uinput`, and Ollama for the loop) — see below.

## Phase 1 — environment

```bash
# accessibility + input + capture
sudo apt install -y python3-pyatspi gir1.2-atspi-2.0 python3-evdev
pip install -r requirements.txt          # evdev, mss, Pillow
export GTK_MODULES=gail:atk-bridge QT_ACCESSIBILITY=1   # enable a11y for apps

# local models
curl -fsSL https://ollama.com/install.sh | sh
ollama pull qwen2.5:7b-instruct          # executor
ollama pull deepseek-r1:7b               # planner
ollama pull qwen2.5vl:7b                 # vision fallback
```

## Run the smoke tests (from this directory)

```bash
# Phase 2 — start the daemon (needs /dev/uinput), then inject input:
sudo -E LUNOS_SCREEN_W=1920 LUNOS_SCREEN_H=1080 python3 action/uinputd.py &
python3 smoke/smoke_uinput.py            # focus a text field first

# Phase 3 — dump the active window's elements (no model, no GPU):
python3 smoke/smoke_atspi.py             # open Text Editor / Firefox first

# Phase 4 — capture a screenshot:
python3 smoke/smoke_screenshot.py

# Phase 5 (needs Ollama running):
python3 agent/loop.py "open the file manager, create a folder named test, rename it"
```

## Notes / gotchas

- **Screen size** drives absolute pointer mapping — set `LUNOS_SCREEN_W/H` to the
  session's real resolution so pixel coordinates land where you expect.
- **Chromium/Electron** only expose AT-SPI with `--force-renderer-accessibility`;
  set it globally in the distro to shrink the vision-fallback surface.
- **Wayland screenshots** need the xdg-desktop-portal ScreenCast API — pre-grant
  the permission in the distro rather than fighting the consent dialog.
- **VRAM**: Qwen2.5-VL 7B Q4 ≈ 6–8 GB; drop the executor to a 3B model if the box
  is GPU-poor; everything can run CPU-only, just slower. The arm64 dev VM has no
  GPU, so the loop's real home is a GPU Linux box or bare metal.
