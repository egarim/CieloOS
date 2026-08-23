#!/usr/bin/env python3
"""AT-SPI element reader via GObject-introspection (gi.repository.Atspi).

Emits a numbered list of actionable, on-screen elements with EXACT screen
coordinates — no pixels, no model. This is the desktop's PRIMARY perception path
(the screenshot + a vision model is the fallback for surfaces the accessibility
tree does not expose, e.g. canvases). Run as the desktop user with the session
DBUS_SESSION_BUS_ADDRESS exported (see the `lunos-atspi` wrapper).

Uses GI directly (python3-gi + gir1.2-atspi-2.0) rather than the python3-pyatspi
wrapper, which has a version-pin conflict in the webtop base image.

Usage: atspi_gi.py [all|active]   -> JSON list of {id,role,name,x,y,w,h}
"""
import json
import sys

import gi
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi

ACTIONABLE = {
    "push button", "toggle button", "link", "entry", "text", "password text",
    "check box", "radio button", "combo box", "menu item", "check menu item",
    "radio menu item", "page tab", "list item", "icon", "menu", "spin button",
    "slider", "label",
}


def extents(acc):
    for getter in (
        lambda a: a.get_extents(Atspi.CoordType.SCREEN),
        lambda a: a.get_component_iface().get_extents(Atspi.CoordType.SCREEN),
    ):
        try:
            e = getter(acc)
            return int(e.x), int(e.y), int(e.width), int(e.height)
        except Exception:
            continue
    return None


def showing_enabled(acc):
    try:
        s = acc.get_state_set()
        if not s.contains(Atspi.StateType.SHOWING):
            return False
        if not s.contains(Atspi.StateType.VISIBLE):
            return False
        if not (s.contains(Atspi.StateType.ENABLED) or s.contains(Atspi.StateType.SENSITIVE)):
            return False
        return True
    except Exception:
        return False


def walk(acc, out, counter, depth=0):
    if acc is None or depth > 30:
        return
    try:
        role = acc.get_role_name()
        if role in ACTIONABLE and showing_enabled(acc):
            box = extents(acc)
            if box and box[2] > 0 and box[3] > 0:
                name = acc.get_name() or ""
                if role != "label" or name.strip():
                    out.append({"id": counter[0], "role": role, "name": name,
                                "x": box[0], "y": box[1], "w": box[2], "h": box[3]})
                    counter[0] += 1
        for i in range(acc.get_child_count()):
            walk(acc.get_child_at_index(i), out, counter, depth + 1)
    except Exception:
        return


def read(active_only=False):
    out, counter = [], [0]
    Atspi.init()
    desktop = Atspi.get_desktop(0)
    for i in range(desktop.get_child_count()):
        app = desktop.get_child_at_index(i)
        if app is None:
            continue
        if active_only:
            for j in range(app.get_child_count()):
                frame = app.get_child_at_index(j)
                if frame is None:
                    continue
                try:
                    if frame.get_state_set().contains(Atspi.StateType.ACTIVE):
                        walk(frame, out, counter)
                except Exception:
                    continue
        else:
            walk(app, out, counter)
    return out


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "all"
    print(json.dumps(read(active_only=(mode == "active"))))
