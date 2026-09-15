# The portal against the original brief

Checked 15 September 2026 against `LunOS_End_User_Portal_UI_Competition_Brief.md`.

## The headline

We built a different thing from what the brief asked for, and it is worth being
precise about the difference rather than scoring ourselves against a target we
were not aiming at.

The brief specifies **three blind prototypes on mock data**, judged on a
scorecard, with a winner selected and only then implemented against real APIs. We
skipped the competition and went straight to implementation. What exists is a
portal wired to the live runtime — it signs in, talks to the real agent, and
produces real files — with roughly the skeleton of the four concepts and almost
none of the experience the brief describes.

That trade bought something real: the portal found #49, #50 and #51, which a mock
prototype could not have found. It also cost something real: the brief's
**"Living Workspace"** design — the Today screen, the activity strip, the artifact
opening *beside* the conversation rather than replacing it — was never built, never
evaluated, and is a better idea than what we have.

## Section by section

### 1. Product boundary — met

Four concepts, admin panel untouched. One deliberate divergence: the brief calls
the first concept **Chat**; we renamed it **Agent**, at the owner's request, so it
would not be confused with Messages once agents could message people.

### 2. Principles

| Principle | State |
|---|---|
| Chat is the centre, not the whole product | met — all four reachable without prompting |
| Progressive disclosure | mostly — the permission dialog hides its rationale behind a disclosure |
| Safe authority is visible | met — the permission dialog is the most finished thing we built |
| No technical security language | met, deliberately: "token", "desk" and a credential path were removed from sign-in |
| **Every action has a state** | **not met** — we have working/idle. No queued, no cancelled, no failed-with-retry |
| **Artifacts outlive conversations** | half — files persist, but cannot be attached to another conversation |
| Responsive by composition | **not met** — mobile is a stacked desktop, not routes and sheets |
| Thin client | met |
| One identity | met |

### 3. Information architecture

- **No Home.** The brief's nav is Home, Chat, Files, Messages, Widgets. We have four.
- **No right context panel.** Nothing shows the current task, related files or
  details beside the workspace.
- **No command palette.**
- **Mobile navigation is wrong.** The brief asks for bottom navigation and says in
  as many words that mobile is not a scaled-down desktop. Ours is a 2×2 grid of the
  desktop nav at the top of the page. Verified at 390×844: usable, not right.

### 4. Required screens

| Screen | State |
|---|---|
| **Home** | **absent entirely** |
| Chat | conversation list ✓ · streaming ✓ · **no attachments** · activity shown as raw shell commands, not user language · **no inline file or widget result** · approval is a separate overlay, not inline · **no cancel or retry** |
| Files | shared only — no recent/generated/uploaded split · **no grid/list switch, preview, attach-to-chat, rename, move, share or delete** · download works |
| Messages | people ✓ · agents ✓ · unread ✓ · **no approval inbox** · **no link from a message to its task, file or widget** |
| Widgets | **the model is wrong** — see below |
| Responsive | 1440×900 ✓ · 390×844 renders but does not meet the brief |

### 5. The "Living Workspace" — not built

None of it. No Today screen, no activity strip, no Continue cards, and in
particular **no workspace canvas**: the brief's central idea is that a file or
widget opens *beside* the conversation so the person can ask for changes while
looking at the artifact. Ours replaces the view entirely — you read about a file in
Chat and then navigate away to Files to see it.

This is the biggest single gap, and it is a design gap rather than a missing
feature. It would change the layout of everything already built.

### 6. Widget model — we built something else

The brief specifies a declarative mini app: id, name, icon, version; compact,
inline and expanded presentations; JSON input/state/output; declared actions and
capabilities; loading/empty/ready/error/offline/permission-required states; a host
owning navigation, auth, theme, permissions, persistence and network mediation.

We built **a saved prompt with a Run button**, kept in `localStorage`. It is
genuinely useful — one click and the agent does a job you ask for often — and it is
not the brief's widget in any respect. There is no manifest, no capability
declaration, no host, and no permission gate before first use.

### 7. Technical recommendation

| Asked | Built |
|---|---|
| React + TypeScript, responsive | ✓ React 19 + TS + Vite |
| PWA | ✗ no manifest, no service worker, no offline |
| SignalR for streaming | ✗ SSE. SignalR is #41 |
| `portal-core` / `portal-ui` / `platform-web` / `widget-sdk` boundaries | ✗ flat `src/portal/` |
| No agent-runtime logic in the frontend | ✓ |
| Tauri as optional shell, later | not started — correctly, it was sequenced after UX selection |

### 10. The shared demo scenario

Step by step, against what exists today:

1. "Prepare the September proposal from my notes and latest pricing" — **works**
2. Shows it is reading two authorized files — partially: the live step stream shows
   the shell commands, which is not "reading your notes and pricing"
3. Proposal preview appears beside the chat — **no**
4. Pricing widget appears inline — **no**
5. Editing a widget value updates the preview — **no**
6. "Send the result to Anna" — **no send capability exists at all**
7. A concrete approval card because the file leaves the system — the dialog exists
   and is good, but nothing triggers it here
8. The sent document remains in Files — n/a
9. Messages contains a linked confirmation — messages work, links do not

**Two of nine steps.**

## What to do with this

Three honest options.

**Run the competition as written.** It is still the right way to choose a design,
and the brief is good. The cost is that three prototypes on mock data will not find
another #50.

**Take the Living Workspace design and implement it directly.** The owner wrote the
proposal in section 5 and it is stronger than what I built. It needs Home, the
context panel, and the canvas — a real piece of work, but against APIs that now
exist and are proven.

**Keep going as we are and accept the brief as a backlog.** Cheapest, and the way
the gaps stay invisible: each one looks small on its own, and the list above is
what they amount to together.

My recommendation is the second. The competition's purpose was to produce a design
worth building; section 5 already is one, and we now have something the competition
could not have produced — a backend known to work, and three bugs found by using it
that a mock would have hidden.
