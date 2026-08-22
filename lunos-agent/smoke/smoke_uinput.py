#!/usr/bin/env python3
"""Phase 2 smoke test: move + click + type via the uinput daemon.

Start the daemon first (needs /dev/uinput access):
    sudo -E LUNOS_SCREEN_W=1920 LUNOS_SCREEN_H=1080 python3 action/uinputd.py
Then, in the desktop session, focus a text field and run this.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from action.client import InputClient  # noqa: E402

w = int(os.environ.get("LUNOS_SCREEN_W", "1920"))
h = int(os.environ.get("LUNOS_SCREEN_H", "1080"))
inp = InputClient()

print("move+click center:", inp.click_at(w // 2, h // 2))
print("type:", inp.type("hello from lunos-agent"))
print("press enter:", inp.key("enter"))
print("scroll down:", inp.scroll("down", 3))
