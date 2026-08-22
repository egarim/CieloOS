"""AT-SPI2 screen reader (Phase 3).

Walk the accessibility tree and emit a NUMBERED list of actionable elements with
on-screen bounding boxes — no model, no pixels. This is the primary perception
path; the vision fallback (perceive/screenshot.py + a VL model) only runs when
this returns too few elements.

Requires python3-pyatspi and a running at-spi2 bus with accessibility enabled
(GTK_MODULES=gail:atk-bridge, QT_ACCESSIBILITY=1). Chromium/Electron populate the
tree only when started with --force-renderer-accessibility.
"""
from __future__ import annotations

from dataclasses import dataclass, field

import pyatspi

ACTIONABLE_ROLES = {
    pyatspi.ROLE_PUSH_BUTTON, pyatspi.ROLE_TOGGLE_BUTTON, pyatspi.ROLE_LINK,
    pyatspi.ROLE_ENTRY, pyatspi.ROLE_TEXT, pyatspi.ROLE_PASSWORD_TEXT,
    pyatspi.ROLE_CHECK_BOX, pyatspi.ROLE_RADIO_BUTTON, pyatspi.ROLE_COMBO_BOX,
    pyatspi.ROLE_MENU_ITEM, pyatspi.ROLE_CHECK_MENU_ITEM, pyatspi.ROLE_RADIO_MENU_ITEM,
    pyatspi.ROLE_PAGE_TAB, pyatspi.ROLE_LIST_ITEM, pyatspi.ROLE_SLIDER,
    pyatspi.ROLE_SPIN_BUTTON,
}

_STATES_OF_INTEREST = (
    pyatspi.STATE_FOCUSABLE, pyatspi.STATE_ENABLED, pyatspi.STATE_FOCUSED,
    pyatspi.STATE_SELECTED, pyatspi.STATE_CHECKED, pyatspi.STATE_EDITABLE,
)


@dataclass
class Element:
    id: int
    role: str
    name: str
    x: int
    y: int
    w: int
    h: int
    states: list = field(default_factory=list)

    def center(self):
        return (self.x + self.w // 2, self.y + self.h // 2)

    def as_line(self):
        return f'[{self.id}] {self.role} "{self.name}" ({self.x},{self.y},{self.w},{self.h}) states={self.states}'


def _is_visible_actionable(acc) -> bool:
    try:
        states = acc.getState()
        if not states.contains(pyatspi.STATE_SHOWING):
            return False
        if not states.contains(pyatspi.STATE_VISIBLE):
            return False
        if acc.getRole() not in ACTIONABLE_ROLES:
            return False
        # Skip clearly-disabled controls.
        if not states.contains(pyatspi.STATE_ENABLED) and not states.contains(pyatspi.STATE_SENSITIVE):
            return False
        return True
    except Exception:
        return False


def _extents(acc):
    ext = acc.queryComponent().getExtents(pyatspi.DESKTOP_COORDS)
    return int(ext.x), int(ext.y), int(ext.width), int(ext.height)


def _state_names(acc):
    states = acc.getState()
    return [pyatspi.stateToString(st) for st in _STATES_OF_INTEREST if states.contains(st)]


def _walk(acc, out, counter):
    try:
        if _is_visible_actionable(acc):
            x, y, w, h = _extents(acc)
            if w > 0 and h > 0:
                out.append(Element(counter[0], pyatspi.roleToString(acc.getRole()),
                                   acc.name or "", x, y, w, h, _state_names(acc)))
                counter[0] += 1
        for i in range(acc.childCount):
            child = acc.getChildAtIndex(i)
            if child is not None:
                _walk(child, out, counter)
    except Exception:
        return


def read_active_window():
    """Element list for the active application's active window."""
    out, counter = [], [0]
    desktop = pyatspi.Registry.getDesktop(0)
    for app in desktop:
        if app is None:
            continue
        for frame in app:
            try:
                if frame.getState().contains(pyatspi.STATE_ACTIVE):
                    _walk(frame, out, counter)
            except Exception:
                continue
    return out


def dump_screen():
    """Element list merged across all top-level windows."""
    out, counter = [], [0]
    desktop = pyatspi.Registry.getDesktop(0)
    for app in desktop:
        if app is None:
            continue
        _walk(app, out, counter)
    return out


def to_prompt(elements) -> str:
    """Render the element list as text for the executor model."""
    return "\n".join(el.as_line() for el in elements)
