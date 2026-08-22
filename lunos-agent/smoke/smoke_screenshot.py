#!/usr/bin/env python3
"""Phase 4 smoke test: capture the screen to /tmp/lunos-screen.png."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from perceive import screenshot  # noqa: E402

print("saved:", screenshot.save("/tmp/lunos-screen.png"))
