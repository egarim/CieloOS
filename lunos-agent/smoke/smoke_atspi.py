#!/usr/bin/env python3
"""Phase 3 smoke test: dump the active window's actionable elements — no model.

Open GNOME Text Editor or Firefox, then run this from the same desktop session.
Expect a numbered list of buttons/links/inputs with correct on-screen boxes.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from perceive import atspi_reader  # noqa: E402

elements = atspi_reader.read_active_window()
print(f"{len(elements)} actionable elements in the active window:\n")
print(atspi_reader.to_prompt(elements))
