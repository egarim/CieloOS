# Pointer and keyboard on a real desktop

The agent opens an application, types into it, and saves a file — on the XFCE
desktop you are looking at, not a headless copy.

## What to watch for

**It clicks the centre of named elements, not guessed pixels.** Perception comes
from the desktop's own accessibility tree, so the agent asks for "the Save button"
and gets its exact box. When the tree cannot describe something — a canvas, some
icons — a screenshot and a vision model are the fallback, not the default. That
ordering is why this works with a small local model and why, by default, nothing
about your screen leaves the machine.

**Typing may stop and ask.** Keystrokes are the highest-risk thing an agent can do
on a desktop: text read off the screen could steer it into typing a command or a
secret. You can grant a time-boxed lease that lets it type without asking each
time, which is a decision you make once rather than a prompt you dismiss twenty
times.

**You can take the mouse.** Nothing locks you out. The agent is another occupant of
this session, not its owner.
