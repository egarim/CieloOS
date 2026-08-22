#!/usr/bin/env python3
"""Lun.Os input-injection daemon (Phase 2).

A small privileged service that turns high-level actions (move / click / scroll /
type / key) into Linux input events via /dev/uinput. It works on X11 and every
Wayland compositor because it operates *below* the display server. It uses
ABSOLUTE pointer positioning, so a caller passes screen pixel coordinates
directly (the perception layer resolves an element id to its box center, this
daemon does the rest).

Protocol: newline-delimited JSON on a unix socket (default /run/lunos/uinput.sock,
override with LUNOS_UINPUT_SOCK). One request per line, one JSON reply per line:

    {"op": "move", "x": 640, "y": 360}
    {"op": "click", "button": "left"}            # left | right | middle
    {"op": "double_click", "button": "left"}
    {"op": "scroll", "dir": "down", "amount": 3}
    {"op": "type", "text": "hello world"}
    {"op": "key", "combo": "ctrl+shift+t"}
        -> {"ok": true}  |  {"ok": false, "error": "..."}

Requires python3-evdev and write access to /dev/uinput (run as root, or grant the
service user access to /dev/uinput). Screen size comes from LUNOS_SCREEN_W /
LUNOS_SCREEN_H (default 1920x1080) — set these to the session's real resolution
so absolute coordinates map 1:1 to pixels.
"""
from __future__ import annotations

import json
import os
import socket
import sys
import time

from evdev import AbsInfo, UInput, ecodes as e

SCREEN_W = int(os.environ.get("LUNOS_SCREEN_W", "1920"))
SCREEN_H = int(os.environ.get("LUNOS_SCREEN_H", "1080"))
SOCK_PATH = os.environ.get("LUNOS_UINPUT_SOCK", "/run/lunos/uinput.sock")

_BUTTONS = {"left": e.BTN_LEFT, "right": e.BTN_RIGHT, "middle": e.BTN_MIDDLE}

_MODIFIERS = {
    "ctrl": e.KEY_LEFTCTRL, "control": e.KEY_LEFTCTRL,
    "alt": e.KEY_LEFTALT, "shift": e.KEY_LEFTSHIFT,
    "meta": e.KEY_LEFTMETA, "super": e.KEY_LEFTMETA, "win": e.KEY_LEFTMETA,
}

_NAMED = {
    "enter": e.KEY_ENTER, "return": e.KEY_ENTER, "tab": e.KEY_TAB,
    "esc": e.KEY_ESC, "escape": e.KEY_ESC, "space": e.KEY_SPACE,
    "backspace": e.KEY_BACKSPACE, "delete": e.KEY_DELETE, "del": e.KEY_DELETE,
    "home": e.KEY_HOME, "end": e.KEY_END, "pageup": e.KEY_PAGEUP,
    "pagedown": e.KEY_PAGEDOWN, "up": e.KEY_UP, "down": e.KEY_DOWN,
    "left": e.KEY_LEFT, "right": e.KEY_RIGHT, "insert": e.KEY_INSERT,
    **{f"f{i}": getattr(e, f"KEY_F{i}") for i in range(1, 13)},
}


def _build_charmap():
    """Map printable ASCII to (keycode, needs_shift) for a US layout."""
    m = {}
    for c in "abcdefghijklmnopqrstuvwxyz":
        code = getattr(e, f"KEY_{c.upper()}")
        m[c] = (code, False)
        m[c.upper()] = (code, True)
    shifted_digits = {"1": "!", "2": "@", "3": "#", "4": "$", "5": "%",
                      "6": "^", "7": "&", "8": "*", "9": "(", "0": ")"}
    for digit, sym in shifted_digits.items():
        code = getattr(e, f"KEY_{digit}")
        m[digit] = (code, False)
        m[sym] = (code, True)
    m.update({
        "-": (e.KEY_MINUS, False), "_": (e.KEY_MINUS, True),
        "=": (e.KEY_EQUAL, False), "+": (e.KEY_EQUAL, True),
        "[": (e.KEY_LEFTBRACE, False), "{": (e.KEY_LEFTBRACE, True),
        "]": (e.KEY_RIGHTBRACE, False), "}": (e.KEY_RIGHTBRACE, True),
        "\\": (e.KEY_BACKSLASH, False), "|": (e.KEY_BACKSLASH, True),
        ";": (e.KEY_SEMICOLON, False), ":": (e.KEY_SEMICOLON, True),
        "'": (e.KEY_APOSTROPHE, False), '"': (e.KEY_APOSTROPHE, True),
        "`": (e.KEY_GRAVE, False), "~": (e.KEY_GRAVE, True),
        ",": (e.KEY_COMMA, False), "<": (e.KEY_COMMA, True),
        ".": (e.KEY_DOT, False), ">": (e.KEY_DOT, True),
        "/": (e.KEY_SLASH, False), "?": (e.KEY_SLASH, True),
        " ": (e.KEY_SPACE, False), "\t": (e.KEY_TAB, False), "\n": (e.KEY_ENTER, False),
    })
    return m


_CHARMAP = _build_charmap()


def _make_device():
    caps = {
        e.EV_KEY: (
            [e.BTN_LEFT, e.BTN_RIGHT, e.BTN_MIDDLE]
            + sorted({code for code, _ in _CHARMAP.values()}
                     | set(_MODIFIERS.values()) | set(_NAMED.values()))
        ),
        e.EV_ABS: [
            (e.ABS_X, AbsInfo(0, 0, SCREEN_W, 0, 0, 0)),
            (e.ABS_Y, AbsInfo(0, 0, SCREEN_H, 0, 0, 0)),
        ],
        e.EV_REL: [e.REL_WHEEL, e.REL_HWHEEL],
    }
    return UInput(caps, name="lunos-virtual-input", version=0x1)


class Injector:
    def __init__(self, ui):
        self.ui = ui

    def move(self, x, y):
        self.ui.write(e.EV_ABS, e.ABS_X, max(0, min(SCREEN_W, int(x))))
        self.ui.write(e.EV_ABS, e.ABS_Y, max(0, min(SCREEN_H, int(y))))
        self.ui.syn()

    def _button(self, button, down):
        self.ui.write(e.EV_KEY, _BUTTONS[button], 1 if down else 0)
        self.ui.syn()

    def click(self, button="left"):
        self._button(button, True)
        time.sleep(0.02)
        self._button(button, False)

    def double_click(self, button="left"):
        self.click(button)
        time.sleep(0.05)
        self.click(button)

    def scroll(self, direction="down", amount=1):
        step = -1 if direction == "down" else 1
        for _ in range(int(amount)):
            self.ui.write(e.EV_REL, e.REL_WHEEL, step)
            self.ui.syn()
            time.sleep(0.01)

    def _tap(self, code, shift=False):
        if shift:
            self.ui.write(e.EV_KEY, e.KEY_LEFTSHIFT, 1)
            self.ui.syn()
        self.ui.write(e.EV_KEY, code, 1)
        self.ui.syn()
        self.ui.write(e.EV_KEY, code, 0)
        self.ui.syn()
        if shift:
            self.ui.write(e.EV_KEY, e.KEY_LEFTSHIFT, 0)
            self.ui.syn()

    def type(self, text):
        for ch in text:
            mapped = _CHARMAP.get(ch)
            if mapped is None:  # skip unmappable chars rather than fail the whole batch
                continue
            code, shift = mapped
            self._tap(code, shift)
            time.sleep(0.005)

    def key(self, combo):
        parts = [p.strip().lower() for p in combo.split("+") if p.strip()]
        mods = [_MODIFIERS[p] for p in parts if p in _MODIFIERS]
        keys = [p for p in parts if p not in _MODIFIERS]
        for mod in mods:
            self.ui.write(e.EV_KEY, mod, 1)
        self.ui.syn()
        for k in keys:
            code = _NAMED.get(k) or (_CHARMAP.get(k) or (None,))[0]
            if code is None:
                continue
            self.ui.write(e.EV_KEY, code, 1)
            self.ui.syn()
            self.ui.write(e.EV_KEY, code, 0)
            self.ui.syn()
        for mod in reversed(mods):
            self.ui.write(e.EV_KEY, mod, 0)
        self.ui.syn()


def _handle(inj, req):
    op = req.get("op")
    if op == "move":
        inj.move(req["x"], req["y"])
    elif op == "click":
        inj.click(req.get("button", "left"))
    elif op == "double_click":
        inj.double_click(req.get("button", "left"))
    elif op == "scroll":
        inj.scroll(req.get("dir", "down"), req.get("amount", 1))
    elif op == "type":
        inj.type(req.get("text", ""))
    elif op == "key":
        inj.key(req.get("combo", ""))
    else:
        raise ValueError(f"unknown op '{op}'")


def main():
    os.makedirs(os.path.dirname(SOCK_PATH), exist_ok=True)
    if os.path.exists(SOCK_PATH):
        os.unlink(SOCK_PATH)

    ui = _make_device()
    inj = Injector(ui)
    time.sleep(0.3)  # give the compositor a moment to notice the new device

    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(SOCK_PATH)
    os.chmod(SOCK_PATH, 0o660)
    server.listen(8)
    print(f"lunos-uinputd: listening on {SOCK_PATH} (screen {SCREEN_W}x{SCREEN_H})", flush=True)

    try:
        while True:
            conn, _ = server.accept()
            with conn, conn.makefile("rwb", buffering=0) as stream:
                for line in stream:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        _handle(inj, json.loads(line))
                        reply = {"ok": True}
                    except Exception as exc:  # noqa: BLE001 - report, don't die
                        reply = {"ok": False, "error": str(exc)}
                    stream.write((json.dumps(reply) + "\n").encode())
    finally:
        server.close()
        ui.close()
        if os.path.exists(SOCK_PATH):
            os.unlink(SOCK_PATH)


if __name__ == "__main__":
    sys.exit(main())
