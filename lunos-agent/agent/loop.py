"""Agent loop (Phase 5 — skeleton).

Ties perception -> executor decision -> action, all local:

    elements = atspi_reader.read_active_window()   # vision fallback if sparse
    action   = decide(step, elements)              # Ollama executor, JSON schema
    apply    = resolve element id -> box center -> click/type via the uinput daemon
    observe  = re-read; verify; retry/backoff; hard step cap

The perception and action halves are real. The model calls (planner + executor)
call a local Ollama instance; prompts are first-cut and are Phase 5's to refine.
Nothing here talks to a cloud — everything runs on the box.
"""
from __future__ import annotations

import json
import os
import sys
import urllib.request

# Allow `python3 agent/loop.py` from the package root.
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from action.client import InputClient  # noqa: E402
from perceive import atspi_reader  # noqa: E402

OLLAMA = os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434")
EXECUTOR_MODEL = os.environ.get("LUNOS_EXECUTOR_MODEL", "qwen2.5:7b-instruct")
PLANNER_MODEL = os.environ.get("LUNOS_PLANNER_MODEL", "deepseek-r1:7b")
VISION_MODEL = os.environ.get("LUNOS_VISION_MODEL", "qwen2.5vl:7b")
MIN_ELEMENTS = int(os.environ.get("LUNOS_MIN_ELEMENTS", "3"))

EXECUTOR_SCHEMA = {
    "type": "object",
    "properties": {
        "action": {"type": "string",
                   "enum": ["click", "type", "scroll", "key", "done", "not_found"]},
        "id": {"type": "integer"},
        "text": {"type": "string"},
        "dir": {"type": "string"},
        "combo": {"type": "string"},
    },
    "required": ["action"],
}


def _ollama_chat(model, messages, fmt=None):
    body = {"model": model, "messages": messages, "stream": False,
            "options": {"temperature": 0}}
    if fmt:
        body["format"] = fmt
    req = urllib.request.Request(
        f"{OLLAMA}/api/chat",
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=180) as resp:
        return json.loads(resp.read())["message"]["content"]


def plan(task, first_screen_text):
    """Planner: task + first screen -> ordered step list (reasoning model)."""
    content = _ollama_chat(PLANNER_MODEL, [
        {"role": "system",
         "content": "Break the user's desktop task into a short numbered list of concrete UI steps."},
        {"role": "user", "content": f"TASK: {task}\n\nSCREEN ELEMENTS:\n{first_screen_text}"},
    ])
    return [line.strip() for line in content.splitlines() if line.strip()]


def decide(step, elements):
    """Executor: one step + element list -> one action referencing an element id."""
    content = _ollama_chat(EXECUTOR_MODEL, [
        {"role": "system",
         "content": ("You control a Linux desktop. Given the STEP and a numbered ELEMENT "
                     "LIST, reply with exactly one action as JSON. Reference an element by "
                     "its id from the list; NEVER invent coordinates.")},
        {"role": "user", "content": f"STEP: {step}\n\nELEMENTS:\n{atspi_reader.to_prompt(elements)}"},
    ], fmt=EXECUTOR_SCHEMA)
    return json.loads(content)


def perceive():
    elements = atspi_reader.read_active_window()
    if len(elements) < MIN_ELEMENTS:
        # TODO Phase 4: screenshot + VISION_MODEL -> elements in the SAME schema,
        # so everything below stays source-agnostic.
        pass
    return elements


def apply(action, elements, inp):
    by_id = {el.id: el for el in elements}
    kind = action.get("action")
    if kind == "click":
        el = by_id.get(action.get("id"))
        if not el:
            return "not_found"
        inp.click_at(*el.center())
    elif kind == "type":
        el = by_id.get(action.get("id"))
        if el:
            inp.click_at(*el.center())
        inp.type(action.get("text", ""))
    elif kind == "scroll":
        inp.scroll(action.get("dir", "down"), 3)
    elif kind == "key":
        inp.key(action.get("combo", ""))
    return kind


def run(task, max_steps=25):
    inp = InputClient()
    elements = perceive()
    steps = plan(task, atspi_reader.to_prompt(elements))
    performed = 0
    for step in steps:
        for _ in range(3):  # per-step retry/backoff
            elements = perceive()
            action = decide(step, elements)
            result = apply(action, elements, inp)
            performed += 1
            if action.get("action") == "done":
                return {"done": True, "reason": "executor reported done", "steps": performed}
            if performed >= max_steps:
                return {"done": False, "reason": "step cap reached", "steps": performed}
            if result != "not_found":
                break
    return {"done": True, "reason": "plan exhausted", "steps": performed}


if __name__ == "__main__":
    task = " ".join(sys.argv[1:]) or "open the file manager, create a folder named test, rename it"
    print(run(task))
