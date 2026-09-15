# CieloOS as a host for other agent harnesses

Written 15 September 2026, immediately after the OpenClaw benchmark
(`agent-benchmark.md`). It is a proposal, not a decision.

## The premise, stated accurately

OpenClaw has a better agent loop. That is one component, and it is the component
CieloOS is weakest at and least differentiated in. It is also the fastest-moving
part of this field: every month someone ships a better loop, and none of them
ship a multi-user operating system underneath it.

What the benchmark actually measured was the loop. What it could not measure,
because OpenClaw has none of it, is everything CieloOS put around the loop:
an owner per agent, a home volume per owner, a policy decision per action, an
approval a person can read, an audit trail that caught #50, and sessions that now
survive a restart. On the strength of one benchmark the reasonable conclusion is
not "we lost". It is **"we are building the wrong half"**.

So: stop competing on the loop. Host it.

## The idea

A CieloOS **engine** is whatever turns a goal into actions. Today there is
exactly one, welded in place — `ConsoleAgentLoop` plus an `IConsoleAgentBrain`.
Make it one of several, and let OpenClaw, Hermes, or next month's winner be the
others.

The host keeps the parts that are ours:

| Host owns | Engine owns |
|---|---|
| identity, ownership, the home volume | planning |
| which model, and the bill for it | prompting |
| the policy decision on every action | tool choice |
| approvals and asking | recovery from failure |
| the audit trail | the reply |
| the portal the person actually looks at | |

## Why this is nearer than it looks

`surfaces/*.surface.json` is already an MCP tool list that nobody has served yet.
Compare a command spec with what MCP `tools/list` wants:

| Surface manifest | MCP |
|---|---|
| `"{surface}.{command}"` | tool `name` |
| `displayName` | `title` |
| `input` (already JSON Schema, verbatim) | `inputSchema` |
| `exposedToAgent: false` | omit from the list |
| `mutatesState: false` | `readOnlyHint` |
| `reversible: false` | `destructiveHint` |

`tools/call` is `SubmitToolRequestDto` → `ManifestPolicyEngine` → the same
executor → the same `AuditEvent`. The gate, the executor and the audit already
exist and are tested. **The missing piece is a protocol adapter, not a
capability.**

The other wire already exists too: the runtime serves
`POST /v1/chat/completions`. A foreign engine gets that URL and a per-run token
and has no other way to reach a model — which means the model is pinned by the
host rather than by whoever configured the harness, and `TokenMeteringHandler`
bills the run without the engine's cooperation. Today that route goes to local
inference only and carries no per-run identity; both are part of the work.

## Spike results - 15 September 2026

Run against the real OpenClaw 2026.9.4 in the `openclaw` distro, with a stub MCP
server standing in for the surface registry. The distro was restored afterwards
(`tools.allow` unset, server removed) so the benchmark stays valid.

**Interposition works, and it is configuration rather than a fork.**

- `openclaw mcp add cielo --url ... --transport streamable-http --header
  "Authorization=Bearer <token>"` registers and probes. The bearer token rides on
  every request - `initialize`, `tools/list`, `tools/call` - so per-run identity
  needs nothing from the engine.
- Global `tools.allow` is an **absolute allowlist that replaces the profile
  defaults**. Set it to one MCP tool and the agent has no shell, no file write,
  nothing else.
- Asked for a CSV with that as its only tool, it called `cielo.write_file` 2.4s
  after `tools/list`, with arguments matching our JSON Schema. **We wrote the
  file. It had no other way to.**

**The blocking-call idea in the first draft of this document is wrong.** It was
worth writing down only because testing it produced the most useful result of the
day:

| when | what |
|---|---|
| t+0.00s | `tools/call` issued |
| t+30.45s | OpenClaw sends `notifications/cancelled` |
| t+45s | the tool's side effect completes anyway |
| end | the agent replies **"Done - `colours.csv` is saved in the workspace"** |

`requestTimeoutMs` was set to 120000 and did not govern this. Three things are
wrong at once: the client gives up at 30s, the server's work lands after the
client stopped waiting, and the agent reports success for a call it cancelled.
A denial would not have prevented the write. That disqualifies holding the call
open - and it would stay disqualified even with a longer timeout, because a run
parked on an open HTTP connection does not survive the runtime restart that #52
just finished making survivable.

**What works instead: answer immediately, and make the refusal the question.**

Returning `isError: true` with a reason, in under a second:

> I couldn't write the file - the workspace owner hasn't approved writes yet, so
> nothing was created. Here's exactly what I want to save as `colours.csv`: [...]
> Approve the write and I'll create it right away.

Nothing was written. The agent ended its turn honestly and asked. So the shape is:

1. `tools/call` -> policy says RequireApproval -> return the refusal **now**, with
   the reason and the pending approval id.
2. The engine's turn ends. Nothing is held open.
3. The owner answers in the portal, where the approval dialog already lives.
4. The host starts a **new turn** carrying the answer.

That is resumable, survives a restart, needs no engine feature beyond MCP, and
asking is not a separate mechanism - it is what a refusal with a reason already
does. #40 closes for every engine including ours, and the honest reply above is
better than what either agent produced in the benchmark.

## Two boundaries, and they are not the same one

Be precise about this, because it is the part that is easy to oversell.

- **The container is the security boundary.** An engine runs inside the owner's
  existing session container: rootless podman, `lunos-home-<slug>`,
  `lunos-shared-<rootUser>`. A harness that ignores every tool we offer and
  shells out directly can still only touch that one owner's files. That holds
  whether or not the engine cooperates.
- **MCP is the policy boundary.** It is what produces approval, the readable
  reason, and an audit line. A harness with its own built-in shell walks around
  it.

### Containment, measured

Rootless podman 4.9.3 on the live box, 15 September 2026:

- **`--network=none` is total.** DNS fails (`gaierror`), a raw IP connection fails
  (`OSError`). Not a filter with holes in it - there is no network.
- **One bind-mounted unix socket still reaches the host**, verified end to end with
  `curl --unix-socket`.
- **Loopback inside the container still works**, so a small forwarder can present an
  ordinary `http://127.0.0.1:<port>` to whatever runs in there. A foreign engine
  needs no special support: it sees a normal HTTP endpoint.

This is better than the egress allowlist this document first proposed, and better
than #32's "half a fence". An allowlist is a rule that has to keep holding; this is
an absence. The model endpoint and the tool endpoint become the only two things
that exist, so **the pinned model is a fact about the container rather than a
setting somebody can change** - and an engine cannot quietly bill a provider
directly, because it cannot reach one.

An engine that bypasses the bus therefore loses oversight, not containment. And
because every bus call is audited and the container's own activity is not, the
gap is measurable: **bus coverage**, the share of an engine's actions that were
policy-checked. Our engine is 100% by construction. An engine that keeps its own
`bash` will not be, and that number belongs in the benchmark next to the task
scores — it is the difference between work you can review and work you merely
received.

## What is still unknown

The load-bearing question - can OpenClaw run with its native tools disabled
against a foreign MCP server - is **answered yes, and demonstrated**. What
remains is smaller:

- **Tool naming.** OpenClaw rewrote `cielo.write_file` to
  `cielo__cielo-write_file` "to keep the tool name provider-safe". Any engine may
  rename our tools, so the audit must key on what we dispatched, not on the name
  the engine used.
- **Session continuity is the engine's, not ours.** Three runs in this spike
  answered from a previous conversation's memory instead of calling the tool at
  all. Whatever CieloOS considers a thread, the engine has its own idea, and the
  mapping has to be explicit.
- **Cancellation semantics.** The 30s cancel is undocumented here and we should
  find where it comes from before relying on any call taking longer than a moment.
- Latency stacks; per-engine images get fat with Node and Python toolchains; and
  hosting other people's harnesses means owning their release cadence.

## A note on the competitive picture

`openclaw fleet` is "provision and manage isolated tenant cells (experimental)",
and there is an `approvals` command tree with a host concept already in it. The
gap we would be building into is real today but it is not permanent, and nothing
here should be planned as though it were.

## Order of work

Step 1 of the first draft is done; it is the section above.

1. ~~Spike.~~ **Done, affirmative.**
2. ~~**`IAgentEngine` seam.**~~ **Done.** `IAgentEngine` + `EngineRun` in
   Application, `CieloConsoleEngine` in Infrastructure, all three console call
   sites converted; 352 backend tests pass. `DesktopAgentLoop` is deliberately not
   converted - a desktop run resolves a second, vision brain under separate
   consent, so it is its own conversion. The guard test says so out loud rather
   than tolerating the one remaining direct call quietly.
3. **MCP server over `ISurfaceRegistry`.** `tools/list` is nearly mechanical;
   `tools/call` reuses the existing submit path. Key the audit on what we
   dispatched, not on the name the engine used.
4. **Return policy decisions immediately, and resume on the answer.** Refusal
   carries the reason and the approval id; the owner answers in the portal; the
   host starts the next turn. Closes #40 for every engine.
5. **`ForeignProcessEngine`** plus an `openclaw` engine manifest: what to
   install, how to invoke, which environment carries the two URLs.
6. **The wizard, last.** See below.
7. **Re-run the benchmark as a conformance suite over engines** rather than as a
   rivalry, with bus coverage as a reported column.

Steps 2-4 are worth doing on their own merits even if no foreign engine is ever
installed: they are how our own agent learns to ask.

## Why the wizard comes last

An "add an agent" wizard in the admin area is the right destination and the wrong
starting point, for one reason: **a wizard that installs an engine we cannot
govern ships something worse than no wizard**, because it looks supervised. The
person who used it would reasonably assume the approvals and the audit trail
cover the thing it just installed. Until step 4 exists, they would not.

When it is built, three things about it are not cosmetic:

- **It installs into the owner's session container, never the host.** Adding an
  engine must not be a way to run `npm install` as `cielo` on the machine.
- **Pinned versions and a recorded digest.** "Install the latest OpenClaw" from a
  panel is a supply-chain decision made by whoever happens to click it.
- **It shows bus coverage after the first run**, so the person can see how much of
  what the engine did was actually policy-checked. An engine that keeps its own
  shell is a legitimate choice; not knowing is not.
