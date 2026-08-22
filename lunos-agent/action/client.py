"""Client for the Lun.Os uinput daemon — resolves high-level actions to socket
requests. The agent loop uses this after the perception layer turns an element
id into a box-center coordinate.
"""
from __future__ import annotations

import json
import os
import socket

SOCK_PATH = os.environ.get("LUNOS_UINPUT_SOCK", "/run/lunos/uinput.sock")


class InputClient:
    def __init__(self, sock_path: str = SOCK_PATH):
        self.sock_path = sock_path

    def _send(self, req: dict) -> dict:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
            sock.connect(self.sock_path)
            sock.sendall((json.dumps(req) + "\n").encode())
            reply = sock.makefile("rb").readline()
        return json.loads(reply or b'{"ok": false, "error": "no reply"}')

    def move(self, x, y):
        return self._send({"op": "move", "x": x, "y": y})

    def click(self, button="left"):
        return self._send({"op": "click", "button": button})

    def double_click(self, button="left"):
        return self._send({"op": "double_click", "button": button})

    def scroll(self, direction="down", amount=1):
        return self._send({"op": "scroll", "dir": direction, "amount": amount})

    def type(self, text):
        return self._send({"op": "type", "text": text})

    def key(self, combo):
        return self._send({"op": "key", "combo": combo})

    def click_at(self, x, y, button="left"):
        self.move(x, y)
        return self.click(button)
