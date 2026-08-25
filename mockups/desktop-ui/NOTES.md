# CieloOS desktop mockup notes

`index.html` is v1. `v2.html` is the current mockup; open it directly, it needs no build step.

---

## v2 — what changed and why

### The approach that won: layered compression

Three designs were assessed for the permission prompt. **A (layered)** won on both
fidelity and impatience, and v2 grafts its structure into the desktop.

The rule it is built on: **the short line a person reads in three seconds is a
compression of the real written reason, never a replacement for it.** It has to be
true, and it has to point at the same danger. The full reasoning stays reachable
without being the first thing seen.

Four layers, in reading order:

1. **Headline** — the action in the person's words (`Open reservas.hotel-lumen.pt?`).
2. **The one line** — the danger, compressed. Both halves of the real reason survive.
3. **The facts** — what exactly is being approved *this time*: the literal address,
   where it came from, what it is for. This is where the decidable content actually
   lives.
4. **The reasoning in full** — the real prose, kept whole, with precise terms glossed
   rather than rewritten.

The whole decision — headline, line, the incriminating fact, and both buttons — fits
on one screen at 1280×800 with no scrolling, in every scene. Verified: card heights
599–642px against 644px of available space. That property is the reason A won the
impatience read, and it is the first thing to check if anyone adds a row.

### Fault 1 — the reason was replaced, not compressed

v1 said: *"This site will see your IP address, like any site you visit."* True,
irrelevant, and it would have got a hostile navigation approved just as readily. It is
gone.

Both short lines now carry both halves of their source reason:

- **Website** — "Opening a page is also how information leaves this machine — and
  Cielo got this address from a page it read, not from you." First clause is the
  egress claim (not a softened *contact* claim: fetching **is** sending). Second is the
  attacker-chosen-destination claim.
- **Typing** — "Anything on the screen Cielo is reading can steer these keystrokes —
  into a command, or a secret, you never asked for — and they land as if you had typed
  them yourself." Mechanism (reads the screen), adversary (steer), stakes (command **or**
  secret), and why nothing downstream catches it.

Two fidelity leaks the judges found in A were fixed here:

- **The hedge is gone.** A said Cielo "may have" taken the address from a page, while
  its own facts row stated it definitely. `got` now.
- **Refused, not re-asked.** A promised that a redirect means Cielo "stops again and
  asks you". The real reasoning says such a move is *refused and named*. Both the scope
  line and the full reasoning now say refused, and they say the same thing.

The typing example ships the payload that exercises it — `curl -sL … | sh` then Return,
taken from a support page Cielo is reading — not a benign search string. The second
danger shape is demonstrated, not asserted.

### Fault 2 — the agent is Cielo

Renamed throughout: menu bar, greeting, shelf, Desk, Chat, Activity, Settings. No
"Mira" remains in the file.

### Fault 3 — the recording indicator can now be stopped

v1 showed a chip reading `Recording 03:18` with no control. In v2:

- **Stop is one click, always visible**, sitting inside the chip. Stopping is never a
  question — it takes effect immediately and asks nothing.
- **Clicking the chip opens a panel** naming what is filming, when it started, and
  where the file goes ("this machine only, in Files → recordings — it is not sent
  anywhere"). Stopping is available there too.
- **After stopping**, the chip stays as a quiet "Recording stopped · 03:33" and the
  panel says how much was already filmed and where to find it. The file appears in Files.
- **The dock sits above the request scrim on purpose.** A recording control that a
  permission dialog can cover is not a control. v1 also hid the chip entirely below
  720px; v2 keeps it at every width.

---

## What was borrowed from the designs that did not win

**From B (two consequences):**

- *Queue rows carry the full compressed line.* B lost nothing at triage depth; A
  compressed a second time into a five-word tag and dropped the egress half doing it.
  In v2 the rail rows and the desktop shelf rows both show the whole line and are
  allowed to grow.
- *No colour on the choice.* The two buttons are pixel-identical — same white, same
  border, same text colour, same size, same slate icon, same hover. Colour appears only
  in the record afterwards (green/red dots in Activity). A tinted its decline button
  red on hover; C put a permanent red X on it. Both make refusing look like an error.
- *No auto-advance.* After an answer, the next request is **not** served. You get a
  short confirmation and a list; you open the next one yourself. A's 700ms lock is kept
  on top, for cards that are newly put in front of you.
- *The cost of refusing, demoted.* B put "the printer stays offline until you run it
  yourself" in amber beside the Allow button, where under pressure it becomes a
  pre-written excuse to approve. The honesty is worth keeping, the placement is not: it
  is now the last section of the full reasoning, called "What saying no costs", far from
  the buttons.
- *"…everything carried in the address itself"* — the most concrete statement of what
  actually leaves during a navigation — folded into the full reasoning.

**From C (annotated):**

- *Gloss rather than rewrite.* `cross-origin redirect`, `http and https` and
  `curl -sL … | sh` stay on screen with a dotted underline and a plain-English note
  beside them. Keeping the real term makes drift visible at the word level, because the
  source is still there. C's mistake was location — glossing lives only in layer 3
  here, never in the fast-read zone, because a gloss nobody clicks is decoration.
- *The redirect clause, near-verbatim.*
- *A persistent rail.* Three at once = one full card with a sticky rail of the others
  beside it, always in view rather than below the fold. C's per-item age counters were
  dropped: they invite triage by age.
- *The record line.* "Either answer is written down in Activity, with the reason you
  were shown" — and it is true. Activity stores each answer together with the exact
  short line that was on screen when it was given, so a person can check later whether
  they were told the truth.

## What was rejected

- **B's outcome framing as the main structure.** Two columns of prose is a reading task
  pretending to be a scanning task, and it teaches fifty outcomes and zero mechanism.
- **C's serif paragraph as the fast layer.** Accurate and inert: three load-bearing
  words behind glosses nobody opens under time pressure.
- **Bulk approval, in any form.** No "approve all", and no decide-from-the-row button
  anywhere — not in the rail, not in the desktop shelf. There are exactly two
  decision buttons on screen at any moment, and they belong to the card you are reading.
- **Esc as a dismissal that decides.** Esc sets the request aside; it never answers.

---

## The one thing added beyond the graft

The sharpest criticism of the winning approach was its own: the short line is a
**constant per action type**, byte-identical on the fortieth benign navigation and on
the one hostile one, so it is the first layer to become wallpaper.

Two answers in v2:

1. **The amber flag is instance-specific and conditional.** "You did not name this
   site" / "The batch ends with the Return key" appear only when they are true of this
   request, and they sit in the fast zone beside the line, not buried in a table.
2. **The second half of the line varies with the instance.** The mockup ships the same
   navigation twice (`Open a website` and `…one you named`). The egress half is
   constant because it is always true; the provenance half changes, and the alarm drops
   away when there is nothing to be alarmed about. The reasoning underneath is
   identical in both — it is the reasoning for the *kind* of action and does not move.

A prompt that cries wolf identically every time is a prompt people learn to click
through. This is the part of the design that has to survive repetition, so it is the
part worth testing first.

---

## Design claims still standing from v1

- **The agent's state lives beside the apps, not inside one.** "Cielo is working" and
  requests waiting for a decision stay visible from the desktop. When something is
  waiting, the menu bar, the greeting, the shelf, the Desk and Chat all say so, and the
  menu bar chip reopens the request.
- **Permission is an interruption, not a feed item.** Leaving a request waiting returns
  you to the desktop with nothing decided and Cielo still stopped — an escape that
  never answers on your behalf.
- **Desk means the live screen; Activity means the human-readable history.**
- **First-run coexists with the desktop** rather than blocking it with a wizard.

## What I would test

1. Comprehension of the two short lines, cold: after reading only the line, can someone
   say what could go wrong? The v1 line failed this — people learned about IP addresses.
2. Whether anyone opens "Why Cielo stops for this", and whether the people who do are
   the ones who needed it. If open rates are a few percent, layer 3 is an alibi and the
   line and the facts are carrying everything — which is how it was written, but it
   should be measured, not assumed.
3. Repetition. Forty benign navigations, then a hostile one. Does the amber flag get
   noticed at forty-one? That is the only question that decides whether this design is
   better than a pretty one.
4. Whether "Leave this waiting" becomes the default action — a third choice that quietly
   eats the other two would be its own failure.
5. Whether anybody sitting down at a filmed desk finds Stop without being told.
