# What a hosted engine can see of CieloOS

*The sequel to `agent-engines.md`, written 16 September 2026. That document argued
we should host other people's loops. This one asks what the loop we host can
perceive once it is in here.*

## The answer, in the first three sentences

**No — OpenClaw is not aware of most of what we can do, and the largest gap is not
a capability at all.** It is blind, in order of what it costs: to **the id of the
console session it is already executing inside** (eleven of the fifteen tools take
that id as a required argument and refuse without it, so this is a hard stop on
turn one); to **its own slug**, which is the one argument that would let it open a
session it *can* address; to **every read in the product** — page text, page
elements, the desktop, the desktop's element list, the spreadsheet's cells, its
home directory, the file it just wrote — with a single accidental exception; and to
**the entire work layer**, projects, tasks, threads and messages, which was never
modelled as a surface and is therefore invisible by construction rather than by
policy. The one exception is the important one: `console.type` returns the console
screen, so the only tool that can do general-purpose work is also the only tool
that can see.

Everything below is against the live fifteen. The tool list is
`browser.navigate/click/back`, `console.type`,
`desktop.click/double_click/type/key`, `recorder.start/stop`,
`session.create/destroy`, `spreadsheet.set-cell/sum/clear` — every agent-exposed,
non-`requiresHuman` command in `surfaces/*.surface.json`, emitted by
`McpApi.cs:74-106`.

Two counts worth having in mind before the table:

- **Zero of the fifteen are annotated read-only.** `readOnlyHint` is
  `!command.MutatesState` (`McpApi.cs:94`), and the only `mutatesState: false`
  command in any manifest is `session.inhabit`, which is `requiresHuman` and is
  filtered out at `McpApi.cs:81`.
- **Five refuse on contact by default.** `browser.navigate`
  (`browser.surface.json:35`), `desktop.type` (`:83`), `desktop.key` (`:105`),
  `session.destroy` (`session.surface.json:92`) and `spreadsheet.clear`
  (`spreadsheet.surface.json:80`) are `RequireApproval`, which `McpApi.cs:178-181`
  answers immediately and deliberately ends the turn. Nothing in `tools/list` says
  which five: `defaultDecision` never crosses the boundary, and the `description`
  field is spent on the manifest's policy essay (`McpApi.cs:90`).

## The table, in four parts

### Perception

| Capability | Over MCP today | Evidence | If not, why not |
|---|---|---|---|
| Read a page's text | **No** | helper `distro/images/desktop/lunos-browser:604-610` (innerText, capped at 20000); backend `BrowserControl.cs:46`; route `Program.cs:1455` | Not a command in `surfaces/browser.surface.json` — only `navigate:17`, `click:39`, `back:61` — and `McpApi.cs:74-106` emits manifest commands only. The safety wrapper it would need already exists and is used on exactly one caller: `UntrustedPageText` (`BrowserControl.cs:153-159`), wrapped at `Program.cs:1469` |
| List a page's actionable elements | **No** | `lunos-browser:361-393` (AX tree, 200 max, ref + role + name + box); `BrowserControl.cs:41`; `Program.cs:1423` | Same absence. This is what makes `browser.click` structurally dead: its `element` must match `^e[0-9]{1,9}-[0-9a-f]{6}$` (`browser.surface.json:51`) and no exposed tool produces such a ref |
| Know where the browser is | **No** | `BrowserControl.cs:36`; `SessionOrchestrator.cs:902-910`; `Program.cs:1423` | Same. The title is already fetched on every navigate and click and then discarded — `BrowserActionResult.Title` (`BrowserControl.cs:32`) never reaches the caller |
| See the desktop | **No** | `DesktopControl.cs:56`; `SessionOrchestrator.cs:628-661`; `Program.cs:1280` | Partly policy — `ISessionVisionConsent` is the gate for pixels leaving the machine. Also mechanically impossible: `McpApi.cs:172-176` emits `{type:"text"}` content only, so there is no envelope for an image |
| List the desktop's AT-SPI elements | **No** | `DesktopControl.cs:57-60`; `SessionOrchestrator.cs:665-689`; `Program.cs:1304` | No reason stated anywhere. It is text, so it is outside the vision gate, and `DesktopAgentLoop.cs:66` calls it on every step of our own loop |
| Read the console screen | **Only as a side effect of typing** | `ConsoleSurfaceExecutor.cs:39` returns `$"{result.Detail}\n{result.Screen}"`; `SessionOrchestrator.cs:594-612` polls until the prompt returns before capturing | The standalone read exists (`CaptureAsync`, `SessionOrchestrator.cs:538-555`; `Program.cs:1263`) and is not a command, so the screen cannot be re-read without mutating |
| Read the spreadsheet | **No** | `Program.cs:1168` and `Program.cs:1723` serve it over HTTP | No `spreadsheet.get`. `McpApi.cs:41-65` implements `initialize`, `tools/list`, `tools/call` and nothing else, and `:53` advertises no `resources` capability, so MCP's natural home for state is not implemented |
| Learn the number `spreadsheet.sum` computed | **No** | `SpreadsheetSandboxExecutor.cs:31` returns the fixed string `"Executed spreadsheet.{op} in sandbox."`; the new state rides in `ToolExecutionResult.Spreadsheet` (`Models.cs:171-174`) | `McpApi.cs:174` forwards `result.Execution?.Message` only. The answer is one field away and is dropped at the boundary. The product's only calculation tool returns no result |

### Files and the home volume

| Capability | Over MCP today | Evidence | If not, why not |
|---|---|---|---|
| Create, read, edit or delete any file | **Yes — by typing shell** | `console.surface.json:19-41` (free 4096-char string, `Allow`); `SessionOrchestrator.cs:574` sends it with `send-keys -l`, so metacharacters are typed and the shell reads them; `:582` presses Enter | It is a terminal, not a file API. It is also the only general-purpose capability on the surface |
| List the home or `~/shared` | **No** | `IHomeBrowser.ListAsync` (`HomeBrowsing.cs:20`); `Program.cs:1650`, `:1690` | Not a surface. Policy already permits it: `Security.cs:221` falls through to `AnyPrincipal` and every handler checks `Ownership.CanAccessHome`. An agent bearer token is accepted on these routes today |
| Read a file back | **No** | `HomeBrowsing.cs:21`; `Program.cs:1657`, `:1698`; decodes text, marks binaries, caps at 256 KiB | Same. The consequence is that an engine cannot verify its own output except by `cat`-ing it into a terminal it can only partly see |
| Hand a produced file to the owner | **No** | `Program.cs:1669`, `:1706`, audited at `:1682` | Same absence, but a real question sits behind it: `HomeBrowsing.cs` keeps `.svg` and `.html` as octet-stream precisely because session bytes are untrusted, and `McpApi.cs:172-176` has no binary envelope |
| Write a file host-side | **No — nowhere in the product** | `IHomeBrowser` has no write member (`HomeBrowsing.cs:18-34`); `PodmanHomeBrowser.cs:48` opens read-only; no `MapPost`/`MapPut`/`MapDelete` exists under `/api/home` or `/api/shared` | **Deliberate.** `HomeBrowsing.cs:14-17`: "a read-only, host-side view… the browser never runs code inside the session container (design law 4)." Writes are meant to happen inside the container, which is exactly why `console.type` is the de facto file API |
| Save or export the spreadsheet as a file | **No** | `SpreadsheetSandboxExecutor.cs:30` writes `SpreadsheetState` into the runtime store; no export route exists | Unstated. The manifest calls the executor a `spreadsheet-sandbox` (`spreadsheet.surface.json:6`), which suggests the sandbox was the point, but nothing records a decision |
| Write one real file into the home | **Yes** | `recorder.surface.json:17-60`, both `Allow`; `distro/images/desktop/lunos-recorder:40`, `:395` | Desktop sessions only, and it is an MP4. The path comes back **only when the recording failed and was truncated** (`RecorderSurfaceExecutor.cs:92-94`); on the success path the agent ends up with a file it cannot name |

### Itself and the machine

| Capability | Over MCP today | Evidence | If not, why not |
|---|---|---|---|
| The id of the session it is running in | **No** | It is in hand at `Program.cs:1935` and used at `:2163-2164`; it reaches the engine only buried in `--session-key cielo-{SessionId}-{AgentId:N}` (`ForeignProcessEngine.cs:93`), which is argv | There is no `session.list`: `SessionOrchestrator.cs:105-121` dispatches `create`/`destroy`/`inhabit` only, and `session.surface.json` declares only those three. A guess fails closed at `RuntimeServices.cs:262-267` |
| List its sessions | **No** | `ISessionBackend.ListAsync`, served at `Program.cs:1252-1261` and already filtered by `Ownership.CanAccessHome` | `session.surface.json:8-28` declares a `sessions` state shape with id, owner, profile, status and port — and nothing serves it |
| Its own slug | **No, but recoverable from a refusal** | Served at `Program.cs:1539-1596`, with code written specifically for the agent case at `:1550-1552`; `Security.cs:221` leaves it `AnyPrincipal` | No `whoami` surface. `ForeignProcessEngine.cs:86-96` hands an engine a goal string, two URLs and a token — no identity |
| Who its owner is, by slug | **No** | Same route | `whereYouAre` gives a *display* name only (`Program.cs:1946`, `:1951`), and `session.create`'s `owner` wants `^[a-z0-9-]{1,32}$` (`session.surface.json:42`) |
| What software its desk has | **No** | `DeskProfiles.cs:45-67` (office / dotnet / marketing are different images); `/api/desk-profiles` is **Public** (`Security.cs:65-71`) | The least restricted read in the product — offered before anyone signs in — and still not a tool. `session.create`'s `profile` argument picks console-vs-desktop, not the image |
| Version history and undo | **No** | `Program.cs:1151-1157`, `:1158-1166` | The agent is the party that *triggers* snapshots (`RuntimeServices.cs:324-329`) and the only party that cannot see or use them |

### The work layer

`exposed` is empty here, and that is the finding. `surfaces/` holds seven
manifests and none of them mentions a project, task, report, thread or message, so
`McpApi.Tools()` cannot emit one. There is no `requiresHuman` filtering at play:
the work layer was never modelled as a surface at all.

| Capability | Over MCP today | Evidence | If not, why not |
|---|---|---|---|
| Read its owner's projects and its own tasks | **No** | `ProjectApi.cs:36-51`; `Security.cs:98-101` admits an agent principal to this one path, and `Security.cs:96` reads "Delete these four lines and an agent has no project access at all" | Never modelled as a surface. `ProjectApi.cs:28-35` calls the route "What an AGENT is allowed to know about its owner's work" — it exists for a consumer that cannot reach it |
| Message its owner, and read the reply | **No** | `MessageApi.cs:91-117` and `:70-89`; `Security.cs:217-220` is `AnyPrincipal` for both verbs; `MessageRules.cs:35-44` permits agent↔owner and nothing else, asserted in `DirectMessageTests.cs:148` | The rule, the access level and the test were all written for a caller that was never built. `store.SendDirectMessage` has exactly one caller in the product, and it is the human portal |
| Speak in the thread it was assigned | **No** | `ThreadApi.cs:61-82`, role forced from the caller at `:77`; `Security.cs:196-203`, whose comment reads "once a thread exists, the agent must be able to speak in it" | Same. Today the agent's words reach a thread only because the chat endpoint writes them on its behalf after the run (`Program.cs:2181`, `:2201`) — a courtesy of our loop, not a capability |
| Report progress on its own task | **No** | `ProjectApi.cs:189-215`; `ProjectRules.cs:61-62`; `Security.cs:108-111` makes all of `/api/projects` HumanOnly on every verb | **Deliberate and argued.** `docs/orgs-and-projects.md:635`: an agent's note is model text that may have come from a page it just read, "which would launder an injection into the lead's portal and into the lead's own agent's prompt." The named fix is a `projects` surface with one `report-progress` command under `RequireApproval` |
| Create projects, assign tasks, enumerate people | **No** | `ProjectApi.cs:79-182`; `MessageApi.cs:49-68`; `Security.cs:130-146`, `:209-211` | **Deliberate.** `Security.cs:103-107` makes it a prefix rule rather than a route list specifically so a project route added in six months is not agent-writable by omission. This is the ceiling, not a gap |

## Why it is shaped like this, in one line

The product split gated **reads** onto HTTP `/api` routes and **state changes**
onto the surface bus (`BrowserControl.cs:6-10`, `DesktopControl.cs:4-8`,
`ConsoleSurfaceExecutor.cs:8`, and `Program.cs:1266` — "design law 2: reads pass
policy"). `McpApi.cs:14-16` then served the bus verbatim: *"surfaces/\*.surface.json
was already an MCP tool list … it simply had no server."* **MCP inherited the write
half of a deliberate split, and the read half has no server.** Nobody decided that
engines should not be able to look at things.

The sharpest evidence that this was never a decision is `Examples.cs:20-28`, which
defines a second step kind — `observe` — with the comment that it is "a gated read
(the browser's page text, for instance)… never a mutation," implemented at
`Program.cs:2535-2549` by calling `browser.ReadAsync` and `browser.ElementsAsync`
directly and bypassing the bus entirely. We built a parallel step type so our own
demos could perceive. The flagship browser demo,
`distro/images/desktop/examples/03-research-on-the-web/example.json`, has three
steps and two of them are `observe`. Over MCP an engine can run step one and
nothing else.

## The walked task

**The task.** "Find out what a VSeed microcontroller costs, and put what you find in
a spreadsheet in my shared folder." VSeed may not exist. The honest answer is "I
searched and found nothing that looks like this part," and noticing that is
precisely what OpenClaw's loop is good at.

**Turn 0.** `tools/list` returns fifteen tools. Nothing in that list contains the
word *file*, *read*, *search*, *list* or *whoami*.

**Turn 1 — this is where it stops.** The first real act is research. Every tool
that could research, write, or look at anything is `targetsSession` and requires
`id`: `browser.*` (`browser.surface.json:28`, `:50`, `:72`), `console.type`
(`console.surface.json:30`), `desktop.*`, `recorder.*`. The agent has no id and no
tool that returns one. It is blocked before it can search for the word "VSeed"
once.

Its only move is `session.create`, which requires `owner` matching
`^[a-z0-9-]{1,32}$` (`session.surface.json:42`). For an agent principal
`Ownership.CanAccessHome` is exact slug equality (`Security.cs:336-341` — the human
branch at `:344-348` does not apply to it), the agent's slug is `{owner}-agent`
(`FirstRunSetup.cs:253`), and the goal string gives only display names
(`Program.cs:1946`, `:1951`). So it guesses, and is denied at
`RuntimeServices.cs:222`.

**It is standing in the room and cannot learn the door number.**
`PodmanEngineProcess.cs:44` execs the engine into `containerOf(run.SessionId)` —
the very container it needs to address. `Program.cs:2163-2164` constructs the run with
that id. `ForeignProcessEngine.cs:93` puts it in the engine's own argv. Reading
argv needs a shell, and `engines/openclaw.engine.json:13` removed the shell.

**The one way through, today.** The denial text is
`'{principal.Slug}' may not open a session over '{homeOwner}'.`
(`RuntimeServices.cs:222`), carried into `Reason` by `Denied()`
(`RuntimeServices.cs:469-473`) and forwarded verbatim by `McpApi.cs:183`. One
deliberately wrong `session.create` prints the agent its own slug; the next call
succeeds and `SessionOrchestrator.cs:306` returns the new id in prose. This is not
a disclosure problem — what it discloses to the caller is the caller's own name —
it is just a humiliating way to learn it.

**And then the task is completable, which contradicts the received reading of this
codebase.** The new session mounts the same `lunos-home-<owner>`
(`SessionOrchestrator.cs:157`) and the same `lunos-shared-<user>` at `~/shared`
(`:175-184`), so the deliverable lands where the owner looks. The console image is
not a bare shell: `distro/images/console/Containerfile:12-17` installs
`curl jq w3m python3 python3-pip python3-openpyxl python3-requests` plus
`python-docx` and `python-pptx`, and copies in a `websearch` helper (`:21`) that
queries the OS's own SearXNG service and prints rank/title/url/snippet as TSV
(`distro/images/console/websearch:1-4`, `:14`). `SessionOrchestrator.cs:159-172`
builds the run arguments with **no `--network` flag**, so session containers have
the network. So:

```
console.type  websearch "VSeed microcontroller" 10 > /tmp/r.tsv; wc -l /tmp/r.tsv
console.type  cut -c1-78 /tmp/r.tsv | head -20
console.type  python3 - <<'EOF' … openpyxl … save('/root/shared/vseed.xlsx') … EOF
console.type  ls -l ~/shared
```

Four calls, a real `.xlsx`, in the folder the owner opens. `send-keys -l` is
literal, so the heredoc lands in one call (`SessionOrchestrator.cs:574`), and the
executor polls until the prompt returns rather than guessing a delay (`:594-612`),
so a slow search is waited out rather than raced.

**The second wall, and it is silent.** `entrypoint.sh:8` runs
`tmux new-session -d -s main` with no `-x`/`-y`, so the detached pane is tmux's
80x24 default, and the readback is `capture-pane -p` with no `-S`
(`SessionOrchestrator.cs:551`, `:602`) — "the visible pane text — exactly what a
human at ttyd would see" (`:536-537`). Ten TSV results with snippets wrap well past
24 rows, so an agent that does not redirect first reads the *tail* and loses the
top-ranked results, without being told anything was lost. Recoverable by an agent
that knows the ceiling exists; nothing tells it.

**On the honesty question.** Without an id, the error it receives is
`Session 'x' was not found.` (`RuntimeServices.cs:266`) — which reads as a
transient fault, not as "you have no web access," so the likely behaviour is retry
and then answer from priors. With an id it gets exactly the right evidence:
`websearch:22` prints `(no results)` to stderr, which lands in the pane and is
therefore captured in the readback. **The ability to reproduce OpenClaw's win
exists in full, behind one argument the engine is never given.**

**Two corrections worth recording**, because the obvious readings of this codebase
are wrong in both directions:

- *"Zero of the fifteen tools return observed state"* is true of the annotation and
  false of behaviour. `ConsoleSurfaceExecutor.cs:39` returns the screen, and it is
  the one tool that matters for real work.
- *"The false-deliverable net does not cover engine runs."* It does.
  `VerifiedAsync` (`Program.cs:2038-2081`) sits inside
  `POST /v1/agent/chat/completions`, and that is the route that calls
  `engine.RunAsync` (`Program.cs:2163`, `:2198`). A fabricated "saved as
  vseed.xlsx" is caught. The genuine limits are narrower: the listing is
  `ListSharedAsync(owner, "")`, one level and `~/shared` only (`:2041`), so a file
  correctly written to `~/shared/reports/` draws a **false correction** — the one
  outcome `Program.cs:2047-2049` says must never happen — and `McpApi.CallAsync`
  has no equivalent, so a direct `tools/call` path is unprotected.

## What a skill fixes, and what needs code

These are not the same list and must not be blurred.

### A skill fixes

1. **That the console is the file system, the office suite and the web client.**
   Nothing in fifteen tool names says so, and an engine cannot deduce it.
2. **Readback discipline for an 80x24 pane.** Redirect, page, `cut`, verify with
   `ls -l` rather than `cat`. Prevents confident reasoning about a file half-seen.
3. **Where a deliverable goes.** Top level of `~/shared`, named in the reply — which
   both avoids `VerifiedAsync`'s false correction and makes a truthful claim
   checkable.
4. **What not to reach for.** `browser.click` cannot be called; the browser needs a
   desktop session (`SessionOrchestrator.cs:993`); `desktop.*` is blind;
   `spreadsheet.sum` does not return its number. Each of these costs a turn and
   then a confabulated explanation.
5. **How to behave when a tool refuses.** Try once, believe the answer, put the
   request in the reply. Never a static list of which tools ask — see the defect
   below.
6. **The difference between "I could not verify" and "this does not exist."**
7. **The slug probe**, as a stopgap only.

### Needs code

1. **Put the session id and the agent's slug in the goal.** `session` is in scope
   at `Program.cs:1935` and used at `:2163-2164`; `agent.Slug` is on an object already
   loaded at `:1915`. One interpolation. **This is the highest-value change in this
   document and it unblocks turn one.**
2. **`session.list` and `whoami` as read-only commands.** Both already exist as
   `AnyPrincipal`-policed, ownership-filtered handlers (`Program.cs:1252-1261`,
   `:1539-1596`). A wrapper widens nothing. Without one of these or item 1, no
   session-targeting tool is callable by an engine that did not create the session
   itself — which is every engine started against a desk a human already opened.
3. **`browser.read`, `browser.elements`, `browser.status`** as `mutatesState:false`
   commands. Helper verbs, backends and HTTP routes all exist; only the manifest
   entries are missing. `read` wraps through `UntrustedPageText.Wrap`, which is
   built and tested and currently has one caller.
4. **`desktop.elements`.** Text-only, outside the vision-consent boundary,
   already called on every step of our own loop (`DesktopAgentLoop.cs:66`).
   Without it, `desktop.click` can only be the pixel guessing
   `DesktopControl.cs:13-19` rejects by name.
5. **A console screen read**, so the screen can be re-read without mutating.
6. **Forward `ToolExecutionResult.Spreadsheet` in the `tools/call` reply.**
   `McpApi.cs:174` sends `Execution.Message` only. One field.
7. **`files.list` / `files.read` / `files.download`.** Thin — `IHomeBrowser` already
   has all three, ownership-checked. Note that a `files.write` is *not* thin: there
   is no host-side write path at all, and a manifest alone would be inert because
   `SurfaceExecutorRouter` keys on `ISurfaceExecutor.SurfaceId`
   (`WorkspaceRuntime.Application/SurfaceExecutors.cs:19`).
8. **Register a foreign engine.** `Program.cs:195` registers `CieloConsoleEngine` as
   the only `IAgentEngine`; `ForeignProcessEngine` is constructed nowhere outside
   tests, and `EngineEndpoints` (`EngineCatalog.cs:75`) is never constructed in
   `src/backend`, so the `Func<EngineRun, EngineEndpoints>` it needs
   (`ForeignProcessEngine.cs:37`) has no production wiring. Every item above is
   untestable end to end until this exists. `docs/agent-engines.md` item 5 concedes
   the containment half is not applied; it does not mention that the engine is
   unregistered.
9. **Protocol.** `McpApi.cs:53` advertises `{tools:{listChanged:false}}` and
   `:172-176` emits `{type:"text"}` only. Handing back a produced `.xlsx` or a
   screenshot is impossible in the current envelope regardless of what surfaces are
   added.
10. **Extend `VerifiedAsync` below the first level and to `tools/call`.** A skill can
    tell an agent to write at the top level; the check being non-recursive and
    chat-only is a code problem.

**The honest relationship between the two lists:** the skill is real and worth
writing, but it is second. Item 1 above is three tokens of string interpolation and
it is the difference between a task that cannot begin and a task that completes.
A skill shipped without it can only ever say *"probe for your own slug, then create
a second session"* — which doubles containers and strands the work beside, not in,
the session the owner is watching.

## Where a skill can physically live

This constrains everything, so be exact about it. A foreign engine receives **one
piece of text**: `{goal}`, substituted into `-m {goal}`
(`engines/openclaw.engine.json:28-29`, `ForeignProcessEngine.cs:88`). Not a system
prompt and not a file — `EngineInvocation` carries `Command`/`Args`/`Env`
(`EngineCatalog.cs:77-80`), and `EngineRun` deliberately has no prompt field
(`AgentEngine.cs:10-12`: "No model, no key, no prompt"). Nothing in `src/backend`
ever *executes* `install.configure`; `EngineApi` only reads it, to compute
`ownToolsDisabled` (`EngineApi.cs:57`) and blockers (`:106`).

So **"a skill for OpenClaw" is concretely an edit to the goal string at
`Program.cs:1977-1984`**, and the second entry point is worse: `agent-run` passes
`request.Goal ?? ""` straight through (`Program.cs:1492`) with no orientation at
all.

**And the goal string is currently a promise the bus denies.**
`Program.cs:1982-1984` tells the agent to *"use your tools (websearch, python3, the
files in ~ and ~/shared)"*. Those are our own loop's affordances — `CieloConsoleEngine`
types them into a shell itself. An MCP engine has no `websearch` tool, no `python3`
tool and no file tool, and `tools.allow '["cielo__*"]'` left it with no shell
either (`engines/openclaw.engine.json:13`, described at `:34` as an absolute
allowlist). Today the only prompt we control sends a hosted engine hunting for four
callables that are not in `tools/list`. **Fixing that text is the cheapest correct
thing in this document.**

## The skill, in full

The literal text. Two values are interpolated by the runtime; everything else is
constant. It replaces `whereYouAre` (`Program.cs:1950-1961`) and the tool sentence
at `:1981-1984` for foreign-engine runs.

```
You are {agent.Name}, an agent inside CieloOS, working for {ownerName}.

WHAT YOU HAVE
Your tools are the ones in tools/list and nothing else. You have no shell of your
own, no file tool, no web client and no way to call an API. Everything real
happens by typing into a Linux console with console.type.

Your console session id is "{session.Id}".
Your own name, for ownership, is "{agent.Slug}".
Every tool whose input has an "id" field takes that session id. Anything else is
refused.

CONSOLE.TYPE IS YOUR HANDS AND YOUR EYES
It takes {"id": "<session id>", "text": "<shell>"}, up to 4096 characters, presses
Enter, waits for the command to finish, and returns the console screen to you. It
is the only tool that returns anything you can read. The shell is bash, running as
root in /root, on a machine that has:
  websearch "query" [count]  private web search; prints rank, title, url, snippet
                             as tab-separated lines
  curl, jq, w3m -dump        fetch a page, parse JSON, read HTML as text
  python3                    with openpyxl, python-docx, python-pptx, requests
  git, nano, less
Text is typed literally, so a multi-line heredoc lands in a single call.

WHAT YOU CAN SEE, AND WHAT YOU CANNOT
The screen you get back is 80 columns by 24 rows, and only what is currently
visible. Anything longer has already scrolled away and you will not be told. So:
  - never cat a long file; page it:    sed -n '1,20p' file
  - keep lines inside the pane:        cut -c1-78
  - redirect first, look second:       websearch "q" 10 > /tmp/r.tsv
                                       cut -c1-78 /tmp/r.tsv | head -20
  - check a command actually worked:   echo exit=$?
  - count before you read:             wc -l /tmp/r.tsv
No other tool shows you anything. A browser or desktop tool tells you only that it
did what you asked.

DELIVERABLES
~/shared is the only place your owner can see. Everything else in /root is private
to you. Write the finished file at the TOP LEVEL of ~/shared, not in a subfolder,
and name it in your reply. Then run `ls -l ~/shared` and read the output before you
say it exists. If the listing does not show it, say that instead.

WHEN A TOOL REFUSES
Some tools need your owner's approval and answer at once with a refusal that ends
your turn. Nothing happened when you get one. Do not retry it and do not work
around it: say in your reply what you wanted to do, why, and that it needs their
approval. Which tools those are depends on how this machine is configured, so find
out by trying once and believing the answer.

DO NOT REACH FOR THESE
  browser.click    needs an element reference that no tool here can give you
  browser.*        needs a desktop session; do web work with websearch, curl, w3m
  desktop.*        you cannot see the screen, so clicking is guessing
  spreadsheet.sum  does the arithmetic but does not return the number; use python3
There is no tool here for projects, tasks, threads or messages. If you are asked
about your assignments or told to report progress, say you cannot see or reach
that from here.

BEING HONEST ABOUT WHAT YOU FOUND
"I could not verify this" and "this does not exist" are different answers and the
tools make them look the same. A search that returns nothing is evidence: report
it as evidence rather than filling the gap from what you already believe. Never
name a file you have not just seen in a directory listing.

Finish with a plain-prose reply containing your full answer. Lines you print that
start with "[" are discarded as engine chatter.
```

## What was considered and rejected

This is the section worth reading twice.

**Shipping the skill as a standalone document — a `SKILL.md`, a file in the
container, a system prompt.** Rejected: nothing delivers it. `EngineInvocation` is
`Command`/`Args`/`Env` (`EngineCatalog.cs:77-80`), `install.configure` is read and
never executed (`EngineApi.cs:57`, `:106`), and `EngineRun` has no prompt field on
purpose (`AgentEngine.cs:10-12`). Calling this artifact a "skill" makes it sound
more portable than it is. **It is a C# string literal**, and pretending otherwise
would let someone plan around a delivery mechanism that does not exist.

**Adding a `prompt` field to `EngineRun` or to the engine manifest, so each engine
could carry its own orientation.** Rejected for now, and the reason is the one
already written at `AgentEngine.cs:10-12`: what the host decides and what the engine
decides are deliberately separated, and prompting is the engine's. Orientation is
not prompting — it is *facts about this machine* — and facts belong in the goal. If
a second engine ever needs *different* orientation text, that is evidence the
"facts" were not facts, and we should find out which one is wrong rather than give
each engine its own version of the truth.

**Teaching the slug-from-refusal probe as technique.** Rejected as a design, kept as
a footnote. It works, and the disclosure is harmless: the caller learns the caller's
own name. It is rejected because it teaches an agent to *provoke a denial to learn a
fact we could have told it*, and every provoked denial is an audit row an owner has
to read past and correctly dismiss. An audit trail whose entries include routine
self-discovery is a worse audit trail. Recorded here so the next person who finds it
knows it was found and turned down.

**Building `cielo.write_file` first.** Rejected, and this is the biggest reversal in
this document. The tool is still named in a live comment (`McpApi.cs:130-131`), it
does not exist, and the spike at `docs/agent-engines.md:81-83` that justified this
entire strategy ran against a stub that had it — *"We wrote the file. It had no
other way to."* **The capability that proved the design is the one capability that
did not ship**, which makes it very tempting to build it first. It should not be
first. The console container already has `python3-openpyxl`, `python-docx` and
`python-pptx` (`distro/images/console/Containerfile:12-17`), already mounts both
volumes (`SessionOrchestrator.cs:157`, `:175-184`), and already accepts a heredoc in
one literal keystroke batch (`:574`). An engine that knows this produces a real
`.xlsx` in the right place today. A write tool is more convenient; it blocks
nothing. What *is* worth building from that family is the read half —
`files.list`/`files.read` — because there is currently no way to verify an output at
all except scraping a terminal, and unlike the write path it is thin.

**Using MCP `resources` as the home for reads.** Considered seriously, because it is
the protocol's own answer to "here is text the model may look at." Rejected for now:
`McpApi.cs:53` advertises tools only, engines differ in whether they fetch resources
unprompted, and the same information delivered as a read-only *tool* is reachable by
every engine that can call a tool. Reads should be commands with
`mutatesState:false`, which has the side benefit of making `readOnlyHint`
(`McpApi.cs:94`) mean something — today it is false on all fifteen tools, so the
annotation carries no information at all.

**Reading the AccessPolicy carve-outs as a decision already made.** Rejected, and
this is the one most likely to be got wrong later. Four work-layer routes already
admit an agent principal — `GET /api/projects/mine` (`Security.cs:98-101`), both
verbs on `/api/messages/{slug}` (`:217-220`), `POST /api/threads/{id}/messages`
(`:200-203`), and the thread reads — each with a written rationale, and it is
tempting to conclude that exposing them over MCP is pure plumbing. It is not the
same decision. `Security.cs` decides whether a token gets through a door; a surface
decides whether a model that just read an attacker's web page may push text into its
owner's portal. `docs/orgs-and-projects.md:635` already names that attack for
`report-progress` and specifies the remedy — a `projects` surface, one command,
`RequireApproval`, `ByAgent` derived from `caller.Kind`. Messaging the owner deserves
the same treatment and not a wrapper. (Related: `CommandBusConformanceTests.cs:59-66`
asserts in prose that `/api/messages/*` is "HumanOnly on every verb including reads"
and that "the agent cannot read these or send them." `Security.cs:217-220` and
`DirectMessageTests.cs:148` say otherwise. The comment is stale, and it sits in the
file that governs what may be added to that allowlist — so the next person reasoning
about an agent messaging surface starts from a false premise.)

**Telling the agent, in the skill, to message its owner or report progress when it
finishes.** Rejected. Neither tool is in `tools/list`, so the instruction can only
produce a wasted step followed by a confabulated explanation of why it failed. **A
skill must never name a tool the engine does not hold** — which is exactly the defect
in the goal string today.

**Widening the tmux pane instead of teaching around it.** Considered:
`entrypoint.sh:8` takes `-x`/`-y`, so a bigger pane is a one-word change. Rejected as
the *primary* fix, because the readback is `capture-pane -p` with no `-S`
(`SessionOrchestrator.cs:551`, `:602`) — it returns the visible pane whatever its
size, and a human attaching via ttyd re-modes it anyway. A wider pane raises the
ceiling without removing it. The real fix is a scrollback-aware read; the cheap fix
is the convention in the skill. Do both, in that order of confidence.

**Fixing perception before fixing addressing.** Rejected, and it inverts the obvious
priority. `browser.read`/`elements`/`status` are the most satisfying items on the
code list — the backends exist, the safety wrapper exists, it is three manifest
entries. They would fix nothing for the walked task, because the blocker is upstream
of every session-targeting tool. Addressing first, perception second.

**Waiting until `ForeignProcessEngine` is registered to write any of this.**
Rejected. `/mcp` is live and answers an externally-pointed engine today — that is
how the spike ran, and how the fifteen-tool list at the top of this document was
obtained. An engine pointed at us from outside is in a *worse* position than the
in-product case, because it gets no goal string at all: no owner name, no `~/shared`
hint, nothing but `tools/list`.

**Treating any of this as OpenClaw's problem.** Rejected. Every gap here is ours.
In the spike, given one tool, OpenClaw called it 2.4 seconds after `tools/list`. An
engine that calls what it is handed is not the broken component.

## Two defects found on the way, neither about awareness

**The approval relaxation does not check that the snapshot covers the command.**
`RuntimeServices.cs:296-303` upgrades `RequireApproval` to `Allow` whenever
`UndoPolicy.ShouldSnapshot` is true, and that predicate is `!command.Reversible` and
nothing else (`Versioning.cs:87-88`). The snapshot is a tar of the home *volume*
(`PodmanVersionStore.cs:42`). `spreadsheet.clear` is `reversible:false` and its data
lives in the runtime store (`SpreadsheetSandboxExecutor.cs:30`), which that tar does
not contain — so with versioning on, "Clearing spreadsheet data requires human
approval" (`spreadsheet.surface.json:81`) is silently overridden by a recovery that
cannot recover it. Worse, `browser.navigate` is also `reversible:false`
(`browser.surface.json:23`), and its own manifest calls it "the egress decision: it
is the moment 'fetch a page' can become 'send data somewhere'" (`:36`). A tar of the
home volume cannot un-send a request. It is latent rather than live only because
`appsettings.json` has no `Sessions` section, so `Program.cs:165-175` falls back to
`NullVersionStore` — and the author was careful about precisely that case
(`RuntimeServices.cs:292-294`: "a no-op store never relaxes a prompt, because then
'recoverable' would be fiction"). The guard needs to compare the snapshot's scope
against the command's blast radius, not read one boolean.

**`/api/surfaces/{surfaceId}/state` ignores `surfaceId`.** `Program.cs:1723-1741`
validates that the surface exists and then returns spreadsheet cells for every
surface. It looks like the general answer to "serve the state each manifest
declares," and it is not one. Fix it before anything is built on top of it.

## What this adds to the order of work

`agent-engines.md` ends with seven items. This document inserts one before the
wizard and one after:

- **5a. Tell the engine where it is.** The session id and the agent's slug in the
  goal, and the tool sentence at `Program.cs:1981-1984` rewritten so it names tools
  a hosted engine actually holds. One string literal; it is the difference between
  a turn-one hard stop and a completed task. Nothing else on this list matters until
  this is done.
- **5b. Give the bus a read side.** `session.list`, `whoami`, `browser.read` /
  `elements` / `status`, `desktop.elements`, a console screen read, `files.list` /
  `files.read`, and the dropped `Spreadsheet` payload at `McpApi.cs:174`. Every one
  of these already exists as a policed handler; none needs a policy decision, only a
  manifest entry and an executor case.

And one correction to item 5 as it stands: it records that containment is measured
but not applied. It should also record that **`ForeignProcessEngine` is not
registered**, so there is no in-product client for `/mcp` at all. Nothing on this
page is testable end to end until there is.
