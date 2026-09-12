# Design brief: the CieloOS end-user portal

Produce **one self-contained HTML file** that mocks up a new end-user interface for
CieloOS. This is a design proposal, judged against three others. It will be opened
directly in a browser — no build step, no npm, no server.

## What CieloOS is

An operating system meant to be operated by an AI agent. Every action a human or an
agent takes goes through one policy engine that answers Allow, Deny, or "ask the
human", and everything lands on one audit trail. Each person has their own agent that
works on their behalf on a real Linux desktop — it can drive apps, browse, write
files, record what it did.

## Who this is for — and who it is NOT for

The interface that exists today is the **administrator's** panel: sessions, desk
profiles, audit events, model providers, token budgets. That stays, and it is fine.

This brief is for **everyone else**. A person who works at a company that runs
CieloOS. They have an AI coworker. They do not know what a container is, they will
never build an image, and they should never see the word "token", "surface",
"desk profile", "session" or "home volume". If a person could not explain a label to
a colleague, the label is wrong.

Assume they are not technical, are possibly on a laptop or a tablet, and that this is
the software they live in all day.

## The portal has exactly four things

Do not add a fifth. Deciding how these four relate to each other **is** the design
problem.

1. **Chat** — talking to their agent. Ask it to do something; watch it work.
2. **Files** — what they and the agent have made. Documents, spreadsheets, videos.
3. **Messages** — conversations with other people on this machine (and with the
   agent when it needs to tell them something).
4. **Widgets** — small mini-apps. A note, a to-do list, a calculator, a weather
   card, a shortcut to a task the agent runs. Think of them as things that sit on a
   surface and can be added or removed.

## Three problems the design must actually solve

Proposals that ignore these are incomplete.

**1. The permission moment.** The agent frequently has to stop and ask: "May I open
this website?", "May I type into this spreadsheet?", "May I send this?". This is the
most important interaction in the entire product — a person who does not understand
the question will click yes to make it go away, and that is a consent failure, not a
UI inconvenience. Show how a request appears, what the person sees, and how they
answer. It must be readable by someone who is not paying much attention.

**2. The agent working while they are not watching.** Tasks take minutes. The person
should be able to ask for something, go away, and come back. Show what "in progress"
looks like, and how they find out it finished.

**3. Three languages.** This ships in English, Russian and Spanish. Russian strings
run roughly 30% longer than English. Do not design anything that only works at
English text lengths. You do not need to translate the mockup, but the layout must
obviously survive it.

## Constraints

- **One HTML file.** Inline CSS and JS. No external requests — no CDN scripts, no
  web fonts, no images from the network. System font stacks, inline SVG, CSS
  gradients and emoji are all fine.
- **Real content, never lorem.** Write the actual strings a person would see:
  real file names, real chat turns, a real permission request with a real URL.
- **Both light and dark.** Use CSS custom properties and
  `@media (prefers-color-scheme: dark)`.
- **Responsive.** It must be usable narrow. Say what happens on a phone.
- **Make it clickable where it matters.** A little JS to switch between the four
  areas, open a permission dialog and dismiss it, is worth far more than a static
  picture.
- The product is called **CieloOS** — *cielo* is Spanish and Italian for sky. Use
  that or ignore it, but make a deliberate choice.

## What will decide the winner

- Whether a non-technical person would understand it without being taught.
- Whether the permission moment is genuinely clear, not merely pretty.
- Whether the four areas feel like one product rather than four tabs.
- A real point of view. Four safe proposals are worth less than one that commits to
  an idea and executes it.

## Deliverable

The HTML file only, at the path you were told to write it to. At the very top of the
file, put an HTML comment of no more than 10 lines stating the central idea of your
design and the one trade-off you knowingly made. No other commentary.
