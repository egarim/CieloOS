"""Screenshot capture (Phase 4).

`mss` on X11 (fast, dev-friendly). On Wayland, real capture goes through the
xdg-desktop-portal ScreenCast API (pipewire) or a compositor hook built into the
distro — left as a clear TODO because it needs a portal session. The agent only
reaches for a screenshot when the AT-SPI tree is too sparse (vision fallback).
"""
from __future__ import annotations

import os


def capture_x11():
    import mss
    from PIL import Image

    with mss.mss() as sct:
        monitor = sct.monitors[1]  # primary
        raw = sct.grab(monitor)
        return Image.frombytes("RGB", raw.size, raw.bgra, "raw", "BGRX")


def capture_wayland():
    raise NotImplementedError(
        "Wayland capture needs the xdg-desktop-portal ScreenCast API (pipewire) "
        "or a distro compositor hook — planned in Phase 4. Pre-grant the portal "
        "permission since it's our distro; don't fight the consent dialog."
    )


def capture():
    """Best-effort screenshot as a PIL.Image."""
    if os.environ.get("WAYLAND_DISPLAY"):
        try:
            return capture_wayland()
        except NotImplementedError:
            # mss works under XWayland for many apps; fall back to it for now.
            return capture_x11()
    return capture_x11()


def save(path):
    capture().save(path)
    return path
