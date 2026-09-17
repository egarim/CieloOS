# Agent version control — how everything an agent makes ends up in a history

Spine: the **runtime-commits** design (the repository on the host, outside every volume; the runtime as the only writer; the agent told it is recorded and told to do nothing about it). Grafted in and marked where they appear: the plumbing-not-`git add` capture and the per-file owner verbs from **owner-facing**; the end-of-run sweep at the engine boundary, the refusal to let a commit buy an approval relaxation, and the `.gitignore`-is-a-display-filter distinction from **agent-commits**. Every flaw six judges marked fatal is either fixed below or answered in §11 by name.

All line references verified against the working tree at `C:\Users\joche\CieloOS` on 2026-09-16.

---

## 1. THE DECISION

**The repository lives on the host filesystem at `<WORKSPACE_RUNTIME_ROOT>/history/`, one bare git directory per podman volume, and there is no `.git` anywhere inside any volume an agent can reach.** **The runtime commits — host-side, after each action that changed the tree, plus a sweep when a run ends and another when the next one starts — and the agent never runs git.** **The agent is told, in the prompt it already gets, that its work in `~` and `~/shared` is recorded automatically, that it does not need to save or back anything up, and that `git status` in those folders will say "not a repository" and that this is correct rather than a problem to fix.**

Everything else in this document follows from those three sentences, and most of it follows from the first one. The brief framed the second question as a trade — an agent that must remember will sometimes not; a runtime that always commits may fight an agent running its own git. The second half of that dilemma dissolves once the repository has no presence in the volume: there is no index, no config, no `.git`, and no lockfile for the agent to touch, so there is nothing to fight over.

---

## 2. WHAT EXISTS TODAY, AND WHY IT IS NOT SOURCE CONTROL

`PodmanVersionStore` (`src/backend/WorkspaceRuntime.Infrastructure/PodmanVersionStore.cs`) does one thing: `podman unshare tar -czf` the whole home volume into a content-addressed `.tar.gz` with a small JSON sidecar (`:42`, `:49-50`). Restore is `tar -xzf` back over the live volume (`:87`). There are no diffs, no per-file history, no messages, and no way to ask what happened to one file. It is a rollback blob, and the interface says so honestly — `RecordBeforeAsync` / `ListAsync` / `RestoreAsync` (`Versioning.cs:20-29`).

Three things are wrong with it as a foundation, and the third is the one that decides its future.

### It snapshots the wrong volume

`RuntimeServices.cs:326-328` passes `store.GetUser(request.UserId).Slug`. For an agent, `request.UserId` came from `ActingAgent` (`Program.cs:2453-2459`), which for a `PrincipalKind.Agent` returns `self.OwnerUserId` — **the owner's user id**. So `PodmanVersionStore.cs:28` builds `lunos-home-<humanSlug>` and tars the human's XFCE desktop home.

The agent's session is owned by the *agent* slug: `CreateAsync` builds `options.HomeVolumePrefix + owner` from the session owner (`SessionOrchestrator.cs:140`), and the chat path matches sessions on the agent's slug. The agent writes into `lunos-home-<agentSlug>` and `lunos-shared-<rootUser>` (`SessionOrchestrator.cs:177-184`). **Neither is ever snapshotted.** Every claim of recoverability resting on this store is currently false, and it is false in the quietest possible way — the tar succeeds, the sidecar is written, the ledger looks complete.

### It fires on every keystroke

`console.type` is `"reversible": false` with `defaultDecision: Allow` (`surfaces/console.surface.json`), `UndoPolicy.ShouldSnapshot` is `command is not null && !command.Reversible` and nothing else (`Versioning.cs:87-88`), and `RuntimeServices.cs:324-329` calls `RecordBeforeAsync` on the Allow path before **every** execution. With `Sessions:Versioning=true`, a twenty-step run is twenty complete tar archives of an entire home volume, sharing nothing. A desktop home carrying ONLYOFFICE (`distro/images/desktop/Containerfile:5,9`) is gigabytes. That is not a size problem to be tuned; it explains "off by default" better than the approval concern does.

### Turning it on silently drops five approval requirements

This is the part that is not cosmetic. `RuntimeServices.cs:296-303` rewrites `RequireApproval` to `Allow` for any non-reversible command the moment a real store is wired in, with the reason *"Recoverable: a home snapshot is taken before this action."* Walking every manifest, the commands that are both `reversible: false` and `RequireApproval` — that is, the complete list of what `Sessions:Versioning=true` relaxes today:

| Command | Manifest | What the tar actually covers |
|---|---|---|
| `session.destroy` | `session.surface.json` | Nothing. `DestroyAsync` is `podman rm -f` on the container and touches no volume (`SessionOrchestrator.cs:354-361`) |
| `browser.navigate` | `browser.surface.json` | Nothing. Its own manifest calls it the egress decision. No archive un-sends a request |
| `spreadsheet.clear` | `spreadsheet.surface.json` | Nothing. `state.document: surface:spreadsheet`, `revisioned: true` — it lives in the runtime store, not a volume |
| `desktop.type` | `desktop.surface.json` | The owner's home — genuinely, if the slug bug is fixed |
| `desktop.key` | `desktop.surface.json` | The same |

`docs/openclaw-awareness.md:492-508` already found two of these — `spreadsheet.clear` and `browser.navigate` — and wrote down the right diagnosis: *"The guard needs to compare the snapshot's scope against the command's blast radius, not read one boolean."* It is latent rather than live only because `appsettings.json` has no `Sessions` section, so `Program.cs:165-175` falls back to `NullVersionStore` — and the author was careful about exactly that case (`RuntimeServices.cs:292-294`: *"a no-op store never relaxes a prompt, because then 'recoverable' would be fiction"*).

### What happens to it

**The relaxation clause at `RuntimeServices.cs:296-303` is deleted, and history never inherits it.** Not narrowed — deleted. A git commit is taken *after* the action, so it is not a pre-image and cannot be one; it covers two volumes and says nothing about the runtime store or the network; and the agent is root and will damage the repository by accident. Any one of those disqualifies it. Shipping history while re-pointing that clause at the new store would be the same lie with a better backend.

**`PodmanVersionStore` survives, narrowed to the one job it is genuinely better at than git: a whole-volume pre-image before a destructive migration** — a desk image rebuild, a whole-tree restore, a runtime upgrade. Those operations call it explicitly. It stops being called from `SubmitAsync`, so it stops firing per keystroke, and the wrong-slug bug at `:326-328` is fixed in the same change even though the call site is going away, because `RestoreAsync` reads the same field.

**`Sessions:Versioning` is retired as a policy switch and `History:Enabled` takes its place**, with the old key mapped forward. It must not silently inherit the new meaning on an existing box: an install that had `Sessions:Versioning=true` and therefore ran `session.destroy` and `browser.navigate` without approval gets those approvals **back** in the same release. Net effect on the approval surface: history relaxes zero actions where versioning relaxed five. This is a tightening and it ships as one.

`IVersionStore`, `NullVersionStore` and `InMemoryVersionStore` are untouched, so `VersioningTests` keeps passing. `GET /api/version/history` stays as a read-only alias over the new store.

---

## 3. WHERE THE REPOSITORY IS

Two volumes get a repository; the third deliberately does not.

**`lunos-shared-<rootUser>` — the record the owner reads.** One per owner group, shared by the user and every one of their agents (`SessionOrchestrator.cs:177-184`). This is already the deliverable space the prompt points at (`Program.cs:1977-1980`), already what the portal lists (`Files.tsx:36`, and its own comment at `:12-17` explains why it reads `/api/shared` and not `/api/home/<owner>`: the home's `shared` directory is a mount point and is always empty), and already what the runtime inspects after every run to catch invented file claims (`Program.cs:2064-2107`).

**`lunos-home-<agentSlug>` — the work record.** Committed on the same schedule, presented as working notes inside a run entry rather than in the main list. One premise in the brief is wrong at the API level and it matters here: `Ownership.CanAccessHome` returns **true** for a human reaching an agent it owns (`Security.cs:344-348`), and `GET /api/home/{owner}/list|read|download` honours it (`Program.cs:1673-1707`). What is blocked is the other direction — an agent reading its owner's home — and that stays blocked. So versioning the agent's private home and showing it to the owner discloses nothing new; it only needs a view, which `Shell.tsx:21-33` does not have.

The reason to record it at all is empirical and sits in the tree: `Program.cs:2090-2104` exists because agents claim files they did not put in `~/shared`. If only `~/shared` is recorded, everything the agent got *nearly* right is unrecoverable, and the history has a hole exactly where the failures are.

**`lunos-home-<user>` — excluded, on purpose.** The owner's own desktop home holds their documents, their ONLYOFFICE state, their browser profile, and (per `PodmanHomeBrowser.cs:102-109`) `.ssh` and `.gnupg`. Recording it every few seconds is surveillance of a person, not accountability for an agent, and it is where the size actually explodes. This is also precisely the volume today's store targets, so "we stopped versioning the human's home" is a correction, not a regression.

**Not one repository spanning both.** `~/shared` is a separate volume mounted *inside* the home at `<homePath>/shared` (`SessionOrchestrator.cs:183`). A single repository rooted at `/root` would straddle two volumes with different lifetimes, put the deliverables' objects in the private home volume, and give every agent of a user a private and divergent copy of the same shared history. So `shared/` is excluded from the home repository and the shared volume carries its own.

**The layout:**

```
<WORKSPACE_RUNTIME_ROOT>/history/
  shared/<rootUserSlug>.git          # bare; work tree is the mounted volume
  home/<slug>.git                    # one per desk, agent or human-with-agents
  <name>.git/cielo-stat-cache        # path+size+mtime+ino -> blob sha (see §8)
  <name>.git/cielo-ledger            # commit sha -> actor, run, request id (see §5)
```

Default root `/var/lib/cielo`, beside the existing snapshot root (`Program.cs:173-174`). This placement is the spine of the design: it is what makes "assume the agent deletes `.git` by accident" a non-event rather than a mitigation, and it is what lets a retired desk's history outlive its volume.

**The trade, named:** the history no longer travels with the volume. A `podman volume export` of a home leaves its past behind. Accepted, because `/var/lib/cielo` already holds the runtime database and the snapshot root, so it is already the unit that must be backed up for the product to mean anything.

---

## 4. HOW THE CAPTURE WORKS, AND WHY NOT `git add`

The recorder never runs `git add`, never uses a working-tree scanner, and never lets git resolve an ignore rule. It enumerates the volume itself and builds the commit with plumbing:

```
git hash-object -w --no-filters <file>        # per changed file, host-side
git update-index --add --cacheinfo <mode>,<sha>,<path>
git write-tree
git commit-tree <tree> -p <parent>
git update-ref --stdin                        # compare-and-swap, see §6
```

Three separate holes close because of this one decision, and each of them is fatal to the `git add -A` version.

**In-tree `.gitignore` is agent-writable.** `git add -A` honours every `.gitignore` in the work tree, and the work tree is the agent's home. A `~/shared/.gitignore` containing `*.xlsx` — written by the agent, by a tool it ran, or by a prompt injection in a file it read — silently stops the deliverable from ever being recorded, and nothing announces it: HEAD still advances, no gap is reported, the owner just sees a history that never mentions the file. There is no flag that fixes this. `git add -f` overrides *all* ignore sources including the recorder's own `info/exclude`, and there is no `--no-in-tree-ignores`. Enumerating ourselves is not a workaround; it is the only mechanism that works.

**In-tree `.gitattributes` is agent-writable.** `text` and `eol` normalisation rewrite bytes on the way into the object store, so the version the owner restores is not the file that was on disk. `--no-filters` on `hash-object` is the answer, and it is only available on the plumbing path.

**A nested `git init` makes a subtree go dark.** If the agent runs `git init` in `~/shared/project` — a normal first move for a coding task — `git add -A` records that directory as a gitlink (mode `160000`) and stores **none of its contents**. The files the owner cares about become invisible while the history continues to look healthy. Under plumbing there is no gitlink: the agent's `.git` is recorded as ordinary files, or excluded by name, and either way the source beside it is recorded.

The enumeration reuses the walker `PodmanHomeBrowser` already has rather than writing a second one. That guard opens a path once and asks the kernel what it actually opened via `/proc/self/fd/3` (`PodmanHomeBrowser.cs:35-54`), because `Sanitize()` strips `..` but a session owns its home and can drop a symlink to `/etc` in it, and checking a name then opening it again is a race. It also type-checks before opening, because a FIFO left in a home blocks `open()` until a writer appears. A recorder that walks a volume the agent controls needs both properties, and they are already written and already tested.

**Reaching the volume.** `podman volume inspect --format {{.Mountpoint}}` for the path and `podman unshare` to read idmapped files — the host-side pattern `PodmanVersionStore.cs:91-95` and `PodmanHomeBrowser.cs:8-13` already use. No network, no daemon, no code inside the session, and it works on a laptop in a field. See §11 open question 5 on whether `git` runs on the host or in a throwaway container, and open question 8 on `safe.directory`.

**Torn files.** A file whose mtime is within **2 seconds** of the capture is skipped and left for the next pass. ONLYOFFICE saving an `.xlsx` and ffmpeg writing an MP4 both produce a window where the bytes on disk are not a file, and a corrupt spreadsheet offered behind a "Put this back" button is worse than no history at all. A file that is still moving at the end of a run is caught by the sweep; if it is *still* moving then, it is recorded and the commit is stamped `PARTIAL: <path> was being written`.

---

## 5. WHO COMMITS, AND WHEN

The runtime, host-side, never `podman exec git` inside the container. Running git inside the session would run it as the agent's root, in the agent's namespace, with the agent's `PATH`, its `~/.gitconfig` and its `core.hooksPath` — every one of which the agent controls.

**Four trigger points.**

1. **After each allowed action, outside the mutation gate.** The natural site is `AgentRuntime.SubmitAsync` beside the audit row, because every path funnels through it — the console loop (`ConsoleAgentLoop.cs:216-224`), the desktop loop, the MCP bus, any foreign engine. But `RuntimeServices.cs:330` and `:353` are both **inside** `mutationGate` (taken at `:305`, released at `:370`), and that gate's own comment says what it is: *"Serializes every mutating operation in this single-process runtime"* (`:147-149`). A full-volume hash inside it would block every other user's every tool request on the box. So `SubmitAsync` **enqueues** a capture request (request id, volume, action, step, actor, timestamp) after the gate releases, and a per-repository recorder drains the queue.
2. **At the end of every run**, at the `IAgentEngine.RunAsync` boundary (`AgentEngine.cs:40-43`) rather than inside the loop. `ConsoleAgentLoop` has **six** exits, not four: token budget (`:170`), console unavailable (`:176`), asked (`:189`), done (`:197`), policy-blocked (`:241`), step limit (`:246`), plus two more inside `EndByAskingAsync` (`:311`, `:320`). Hanging the sweep off the engine covers all of them without enumerating any.
3. **At the start of the next run**, before the first step. This is what catches a hard kill — `podman rm -f`, a host reboot, an API crash — where no result ever returned, and it is what makes the sweep idempotent rather than merely redundant.
4. **Before `DestroyAsync`'s `podman rm -f`** (`SessionOrchestrator.cs:354-361`), followed by an empty `session ended` commit, so the log shows the boundary instead of an unexplained gap between two days of work.

**The timing claim that does not survive contact with the code.** It is tempting to say the commit lands after the command finished, because `console.type` polls until the shell is idle. It does — for eighteen seconds. `SessionOrchestrator.cs:598` is `for (var i = 0; i < 90; i++)` around `Task.Delay(200)`, and past that bound it returns success with detail `"Typed and submitted."` and no signal that anything is still running (`:614`). A `pip install`, a pandas dump, a large `.xlsx` write: the action returns, the recorder captures a tree mid-write, and the quiescence window in §4 defers the file to the next pass. **So the record is a sampling of the tree taken as soon after each action as the recorder can get to it, not a transaction boundary, and the design says so rather than asserting a guarantee it cannot keep.** The end-of-run sweep is what makes the last step's effects land at all, which is why it is trigger 2 and not an optimisation.

**Concurrency and attribution, which is where confident answers go wrong.** `lunos-shared-<rootUser>` is mounted into the user's sessions *and* all their agents' sessions (`SessionOrchestrator.cs:177-184`). Two agents working at once is the normal configuration. The mutation gate serialises API calls, not effects inside containers, so whichever action returns first can sweep up the other session's writes.

The rule: **when exactly one run is live against a volume at capture time, the commit is authored by that run's agent. When more than one is, the commit is authored by the runtime and its subject says so** — `Changed while Ada and Scout were both working` — with both runs in the trailers. The History view renders that as "Ada or Scout" and offers both runs' step lists. Honest uncertainty beats a confident false name, and the owner's second question is literally *what changed it*.

**Identity is not read from the commit message.** Author and committer headers are ours and the agent never touches the object, but the *message* contains agent-produced text — `successDetail` for `console.type` is the agent's own typed input (`RuntimeServices.cs:345-348`), and `action.Note` is the agent's own sentence (`ConsoleAgentLoop.cs:13`). A note ending in a blank line and `Cielo-Actor: joche` would put a trailer-shaped line into the last paragraph. So agent-produced text goes in the body **indented by four spaces**, trailers are written only by the recorder, and the portal reads actor / on-behalf-of / run id from `cielo-ledger`, a host-side row keyed by commit sha, not from `git log --format=%(trailers)`.

**A commit that fails never fails the agent's action.** The action already happened; refusing to report it would be the worst outcome. A failure becomes a `history.record-failed` audit row, a banner in the History view naming when the gap started, and — see §7 — the removal of the recorded-ness sentence from the agent's prompt until the gap closes.

### What a commit means

**One commit per action that changed the tree.** Not per step, because a step that types `ls` changes nothing and a history that cannot be skimmed has lost the only property that made it better than a pile of tarballs. Not per run, because a run that goes wrong at step 6 of 8 is exactly when the owner wants the file as it stood at step 5. Not per deliverable, because the runtime cannot tell what the agent considered one.

This makes commits and audit rows one ledger seen from two sides: both are keyed by `request.Id`, and `AuditEvent.CorrelationId` already carries it (`RuntimeServices.cs:353`).

```
console.type — input: python3 build_quote.py

    Checking the third supplier's price list before writing the sheet.

Cielo-Run: 8f3a1c2e-...
Cielo-Correlation: 4b7c9d10-...
Cielo-Step: 3/8
Cielo-Action: console.type
Cielo-Volume: lunos-shared-yulia
Cielo-Ask: find three suppliers and put them in a sheet
```

The subject reuses the detail the audit row already computes (`RuntimeServices.cs:342-352`) so the two records cannot drift into describing the same act differently. The indented paragraph is `action.Note`, which is the agent's own one-line reasoning — the best half of "let the agent write the message", recovered without the agent having to remember anything. `Cielo-Ask` is the owner's own words, already available at `Program.cs:2004`, and it is in the trailer so the History list can be indexed by what they said rather than by a run id.

**Author is the agent, committer is the runtime.** `Ada (agent of Yulia) <yulia-ada@agent.cielo.local>` and `CieloOS <runtime@cielo.local>`. Git has two identity slots and this product has two actors; using them honestly means `%an` answers "who did this" and `%cn` answers "what recorded it". When the owner acts — a restore, a file dropped into `~/shared` from their desktop — the author is them. This mirrors the dual-actor shape the audit trail already carries as `Principal` / `OnBehalfOf`.

One ref per repository. No branches, no tags, no merges, no remotes.

---

## 6. WHEN THE AGENT FIGHTS THE MECHANISM

The agent runs as root inside its own container with a real shell, and git is installed (`distro/images/console/Containerfile:14`). Assume every one of these happens.

**It runs `git status` or `git log` in `~` or `~/shared`.** *"fatal: not a git repository."* There is no in-tree `.git` and there never will be. The prompt has already told it this is the expected answer, which is the whole reason that clause is in §7.

**It runs `git init` in `~`.** It now owns `~/.git`. The recorder does not care — it uses its own git directory and never consults a work-tree config. `.git` is on the home repository's exclusion list, so the agent's private history is invisible to the record and harmless. Two histories, neither lying: the agent's is scratch, the runtime's is the record.

**It runs `git init` in a subdirectory.** Under `git add -A` this would silently replace the subtree with a gitlink and store nothing. Under plumbing it is a directory of ordinary files, and the recorder stamps the commit `NESTED-REPO: <path>` plus an audit row so the owner is told *"the agent started its own version control here"* rather than being left to wonder. Live lock files are excluded by name — `.git/index.lock`, `.git/*.lock`, `.git/objects/tmp_*` — because capturing a half-written index and handing it back later means a repository that refuses every command.

**It runs `rm -rf .git`.** It deletes its own scratch repository, if it made one. There is no `.git` of ours for it to delete. That is the point of the detached git directory, and it is a structural answer rather than a guard.

**It rewrites history.** It cannot address ours. But the backstop exists anyway, because a disk fault, a bad restore or a future bug can do what the agent cannot: the recorder keeps the last commit sha it wrote for each repository in `cielo-ledger`, on the host, and before each commit checks that `HEAD` resolves and descends from it. If it does not, the recorder does **not** restart quietly — it re-inits, imports the current tree as a new root commit, and writes the break as the most visible entry in the log (`RECORD BROKEN: the history was removed or rewritten at 14:05`), plus a `history.broken` audit row and a banner in the History view. Nothing is deleted; the old git directory stays on disk beside the new one.

**It writes outside the two volumes.** `/tmp`, `/opt`, `/srv`, `/var/tmp` are neither recorded nor kept — the container is removed on destroy (`SessionOrchestrator.cs:354-361`) and that work is gone entirely, not merely unrecorded. This is a real hazard created by the prompt: an agent told "everything you write is kept" will generalise, and staging a build in `/tmp` is ordinary behaviour. The fix is one clause in §7, and it is not optional.

**It is killed mid-run.** `podman rm -f`, a host reboot, a crashed API: no result returns, no engine-boundary sweep fires, and the last step's writes sit uncommitted on a volume that survives. Trigger 3 in §5 — the sweep at the *start* of the next run — is what lands them. They are attributed to the previous run by the queued request id where one exists, and to `Changes found in the shared folder` where the kill lost it. If the desk is never used again, the owner's first visit to the History view triggers the same lazy sweep.

**Two writers that are not the agent.** The owner's own desktop mounts the same shared volume, and "Put this back" is a host-side write. Both are unattributable to any action, so both produce a commit authored by the owner, and both take the same per-repository lock as the recorder. `update-ref` is always a compare-and-swap against the expected old value with a bounded retry, never a bare `update-ref <ref> <new>`: a lost race would orphan a commit object, and the nightly repack would then discard it. Silent permanent loss caused by the maintenance step the design itself mandates is the worst available outcome, and it is prevented at the write, not hoped away.

---

## 7. THE AGENT-FACING TEXT

Today the agent's prompt says nothing about version control — zero mentions in `EngineBriefing.cs` or `ModelConsoleBrain.cs`. The text goes in the `whereYouAre` block (`Program.cs:1973-1984`), which is built per-request and already reads live state (`ownerSlug`, `hasDesktop`), immediately after the `~/shared` sentence at `:1977-1980`.

**It cannot go in `EngineBriefing.NativeTail`, for two reasons.** `NativeTail` is a `const` (`EngineBriefing.cs:43`) concatenated unconditionally at `Program.cs:2009`, so it structurally cannot know whether `History:Enabled` is set — on a default box it would promise recording where no recorder exists, which is precisely the believe-you-saved-it failure this design is for. And `Retarget` **replaces** `NativeTail` with `ForeignTail` rather than keeping it (`EngineBriefing.cs:78-80`), so anything appended there is lost entirely for every hosted engine. `whereYouAre` survives `Retarget` untouched and is inherited by both paths.

### Recording on — paste this

> Everything you write in ~ and in ~/shared is recorded automatically by the machine, after each command you run. You never need to save, commit, back anything up, or make dated copies and version-numbered filenames like report_v2_final.xlsx — edit the file in place under its real name, and your owner can look at any earlier copy of it and put it back themselves. There is no repository in these folders for you to use: `git status` there will tell you it is not a git repository, and that is correct, not a problem to fix. If you run git yourself, that is your own repository and has nothing to do with this history. Only ~ and ~/shared are recorded — anything you write elsewhere, including /tmp, is neither kept nor recorded and is gone when the session is rebuilt.

### Recording off, or the record has an open gap — paste this instead

> Nothing you write is being version-controlled right now, so a file you overwrite is gone. Keep anything you might need again under a new name in ~/shared, and tell your owner what you kept.

### The one sentence that is true either way, and stays constant

The `git status` clause belongs to both variants. Keep it in both strings rather than concatenating it separately; a model reading a promise and then a contradiction in the next paragraph is worse off than one reading either alone.

### Why each clause is there

**"automatically… after each command you run"** — an agent that believes it must remember will sometimes not, and an agent that believes nothing is recorded will re-write files defensively or tar things up to be safe. Both waste steps out of a budget the model is already being taught to spend deliberately (`ModelConsoleBrain.cs:69-73`: *"a smaller result that exists beats a better one you ran out of steps before writing"*).

**"never need to… make dated copies and version-numbered filenames"** — this clause pays for itself twice. It removes the reason agents produce `suppliers_final.xlsx` and `notes.md.bak`, and it is what makes the history *work*: a file edited under one name has a readable per-file history, and the same content under four names has none. Behaviour change and record quality out of one sentence.

**"`git status` there will tell you it is not a git repository, and that is correct"** — the load-bearing clause, and it is there for a measured failure mode. The model's own system prompt tells it to stop when blocked and to check a hypothesis it can name (`ModelConsoleBrain.cs:60-64`). An agent told its work is version-controlled, which then sees *"not a git repository"*, will conclude the environment is broken and burn two or three of eight steps on `git init` — the one accident that degrades the record. Naming the expected error text in advance costs one clause and removes the whole failure path. This is the same lesson the repo already paid for: `EngineBriefing.cs:31-42` records two benchmark runs lost because `websearch` was named as a capability rather than as a command, so the model hand-rolled urllib against three search engines and got bot-challenged by all three.

**"Only ~ and ~/shared are recorded — anything you write elsewhere… is gone"** — without it the promise generalises to the whole filesystem. The existing sentence at `Program.cs:1978-1980` says files written elsewhere are *"private to you and your owner cannot get at them"*, which reads as a privacy property, not a data-loss warning. That sentence needs rewriting in the same edit, not appending after.

**"If you run git yourself, that is your own repository"** — pre-empts the collision by telling the truth about the architecture rather than forbidding anything.

**What is deliberately not in the prompt:** no instruction to commit, no mention of commit messages, no request to describe changes, no `.gitignore` guidance, no "remember to save your work". Every one of those is a thing the agent could forget, and the point of putting the repository on the host is that there is nothing left for it to forget.

**Foreign engines.** `ForeignTail` correctly tells a hosted engine it has *"no shell, no python, no web search and no way to write a file directly"* (`EngineBriefing.cs:51-58`). It needs no history sentence of its own because `whereYouAre` already carries one and survives the swap. See §11 open question 6 on the direct agent-run path, which may not compose `whereYouAre` at all.

---

## 8. SIZE AND LIFETIME

### The numbers

**Exclusions, split by volume, held in the recorder's own list on the host** — not in an in-tree file, so the agent can neither add a line to hide its work nor remove one to flood the record.

- In `lunos-home-<slug>`: `recordings`, `.cache`, `.config`, `.local`, `.npm`, `.ssh`, `.gnupg`, `.git`, `node_modules`, `.venv`, `venv`, `__pycache__`, `*.pyc`, `bin`, `obj`, `target`, and `shared` (which is the other volume).
- In `lunos-shared-<rootUser>`: **nothing but the per-file ceiling.** A dotfile in the shared folder is the person's file — a `.env`, a config the owner asked for — and a deliverable that is silently unrecorded because its name starts with a dot is the failure this whole document exists to prevent.

That split is the correction to a tempting mistake. `PodmanHomeBrowser.cs:102-109` hides *every* dot-entry from the owner, and it is right to: a home is full of `.ICEauthority` and `.dbus` and an office user should never be shown them. But that is a **display** filter expressed as the glob `.*`, and this is a **durability** filter expressed as a list. Unifying them on the display rule would quietly stop recording a class of real deliverables. Two decisions, two lists, and they must not be shared as "one constant so they cannot drift".

**`recordings` is excluded by name and it is not optional.** `distro/images/desktop/lunos-recorder:40` writes MP4s to `$HOME/recordings` inside the home volume, `MAX_SECONDS = 1800` on both sides (`:45`, `RecorderControl.cs:49`), and "record a demo" is an advertised example. A 30-minute 1920×1080 capture is commonly 0.5–2 GB of incompressible video that git deltas at approximately zero. A commit per action during a recording would store N growing partial copies of it.

**Per-file ceiling: 100 MB.** Over it, the file is recorded as a placeholder entry — path, size, sha256, timestamp, and the text *"too large to keep a copy of"*. Never a silent skip: a large file that vanishes from the record with no trace is the failure that teaches an owner to stop trusting the log.

**Per-owner-group budget: warn at 1.5 GiB, stop storing blobs at 2 GiB.** Past the ceiling the recorder keeps committing file lists, sizes and hashes — so the history still answers *what changed and when* — and the History view says so in a banner. The warning fires on approach, not on arrival, because an owner who learns at the ceiling that history is degraded has already lost the window.

**Steady state, honestly.** A 30 KB `.xlsx` rewritten twenty times a day, 250 working days: 5,000 blobs. An `.xlsx` is already a zip, so zlib gains little and cross-version delta compression is close to nothing — call it **100–200 MB per desk-year** for a busy office desk, not the "tens of megabytes" it is tempting to claim. Text deliverables are an order of magnitude cheaper. The cases that hurt are video, images, and an agent rebuilding a large artifact every step, and those are what the exclusions and the ceiling are for.

**The stat cache is what makes this fast enough to run per action.** Building commits with plumbing means throwing away git's index, which is the only reason `git add` is quick on a large tree — without a replacement, every capture re-reads and re-hashes every byte in the volume. So `cielo-stat-cache` maps `(path, size, mtime_ns, inode)` to blob sha, lives beside the repository on the host, and is consulted before every `hash-object`. This is not a nicety; a full-volume re-hash per action, twenty times a run, is the difference between a feature and a box-wide stall.

**Maintenance: `git repack -d` nightly, `gc.auto=0` set explicitly** so git never forks a repack into the middle of an agent's step on its own initiative. Never `gc --prune`, never `reflog expire`, never `filter-branch`, never a force-update of the ref.

### Lifetime

The home volume already outlives every session: `DestroyAsync` is `podman rm -f` on the container and nothing else (`SessionOrchestrator.cs:354-361`), and `PreviewAsync` already tells the user so — *"the home volume persists"* (`:129`). Grepping the whole tree for `volume rm` and `volume prune` returns nothing in `src/`, `distro/` or `scripts/`: this product never removes a volume. So session destruction costs the history nothing, and the recorder marks the boundary with a `session ended` commit so the log does not show an unexplained gap.

When a volume *is* eventually removed by hand — an agent retired — the history does not go with it, because the git directory is on the host. It stays at `<root>/history/home/<slug>.git`, still readable by the owner, marked *"Ada's desk was retired on <date>"*. That is the concrete payoff of the detached git directory.

### "Nothing can be deleted" versus a finite disk

These cannot both be absolute, and pretending otherwise produces a feature that dies permanently at the ceiling with no way out. The line this design draws:

> **The record of what changed is never deleted. The stored copy of a file's contents may be released, by an explicit human act, and the release is itself an entry in the history.**

So a path, its size, its hash and the time it changed survive forever; the bytes behind one entry can be let go. A release says which files, how much was recovered and who asked, and it appears in the History view like any other event. This is the same shape as every other irreversible thing in this product — `DELETE /api/keys/{id}` sets `RevokedAt` and removes nothing — applied to the one resource that genuinely cannot grow without bound. No timer prunes anything, and the runtime never releases on its own initiative.

---

## 9. WHAT THE OWNER SEES

A new **History** place in `Shell.tsx:21-33`, between Files and Messages. The words *commit*, *repository*, *branch*, *revert*, *diff*, *HEAD* and *merge* do not appear in it, and neither does a hash.

Entries are reverse-chronological, one per run, led by the owner's own words — they do not remember "run 8f3a", they remember asking for suppliers:

> **Tuesday, 14:05** — You asked Ada: *"find three suppliers and put them in a sheet"*
> She worked for 6 minutes and ran 8 commands.
> **suppliers.xlsx** — new file, 24 KB · *Open · Save this version*
> **notes.md** — changed · *See what changed · Put this back*
> ▸ show what she ran (8 commands)

> **Tuesday, 09:12** — Changes found in the shared folder
> **brief.docx** — new file, 88 KB

Four decisions inside that:

**Files first, commands second.** The owner's question is *what changed*, not *what did it type*. The command list is behind a disclosure and renders the existing `ConsoleLoopStep.Text` values, the same thing the Chat view already streams live.

**Six weeks later, they are not looking for Tuesday — they are looking for `suppliers.xlsx`.** So the entry point that matters most is not this list. Every row of the existing Files list (`Files.tsx:106-133`) gets a clock: *"5 earlier versions"* → date, sentence, download. And the History view has a filename search, because `Files.tsx` today is a flat listing of the shared root with no drill-in and no search, so a deliverable in a subfolder or one since renamed has no row to hang a clock on. Per-file history is `git log -- <path>` over a single ref and comes almost free; leaving it out would fail the exact scenario this document was written for.

**"See what changed", never a diff view.** For text: old and new side by side, plain, no `@@` headers. For an `.xlsx` or `.docx` — which is most of what these agents produce (`Containerfile:15-16` ships openpyxl, python-docx, python-pptx) — it says *"This file was replaced — 41 KB, was 38 KB"* with a **Save this version** button, reusing the binary detection at `PodmanHomeBrowser.cs:179-194`. Pretending to diff a spreadsheet is worse than declining to.

**One verb: "Put this back", per file, and it is itself a new entry.** It writes the old bytes forward as a new commit authored by the owner — *"Wednesday 10:30 — You put back suppliers.xlsx as it was on Tuesday at 14:05"* — never `git reset`, never a rewind. Nothing vanishes, and undo is always undoable, which is what lets a person who is afraid of the button press it. Whole-tree restore exists behind *"Put everything back to how it was here"* and is likewise a forward commit; see §11 open question 3, because it is the one that has a file-ownership problem.

**Two things the view must say out loud rather than let the owner infer:**

- The owner's own desktop home is not recorded. A file on their Desktop is not recoverable while a file in `~/shared` is, and that inconsistency destroys trust in the whole feature if they discover it by trying. One line in History is not enough; it belongs in Files, at the point of filing.
- The in-product spreadsheet is not recorded either. `spreadsheet.surface.json` is a revisioned document living in the runtime store, not in any volume, so no repository touches it and — because `set-cell` is `reversible: true` — no snapshot ever did either. An owner who opens the portal spreadsheet and asks what it looked like before gets nothing. See §11 open question 1.

### Routes, named explicitly

`AccessPolicy.Required` falls through to `return AccessLevel.AnyPrincipal` (`Security.cs:221`), so an unlisted route is agent-writable by omission.

| Route | Level | Why |
|---|---|---|
| `GET /api/history` | AnyPrincipal | Scoped by `RootUserSlug` like `/api/shared/list` (`Program.cs:1713-1719`) |
| `GET /api/history/entry/{id}` | AnyPrincipal | Same scope |
| `GET /api/history/file?path=&at=` | **HumanOnly** | Bytes leaving a desk — audited exactly like `shared.download` (`Program.cs:1738-1739`) |
| `POST /api/history/restore` | **HumanOnly** | Changes files. Never an agent's call |
| `GET /api/history/home/{owner}` | **HumanOnly** + `CanAccessHome` | An agent's private home, readable only by its owner |
| `GET /api/version/history` | AnyPrincipal | Kept as an alias; **must be named**, it is unnamed today |
| `POST /api/version/{id}/restore` | **HumanOnly** | Fixes the hole below |

Reads of the shared history stay `AnyPrincipal` deliberately: an agent asking whether its last run actually wrote the file is a question the record should answer, and `RootUserSlug` already scopes it to its own owner group.

```csharp
// Everything about history, on EVERY verb including reads, plus /api/version —
// a prefix rule rather than a list, because the fall-through at the end of this
// function is AnyPrincipal and a history route added in six months would
// otherwise be agent-writable by omission. The same shape /api/projects already
// uses at :103-112, and for the reason that comment gives.
//
// /api/version is in here because it is broken TODAY, not as tidying. There is
// no /api/version rule anywhere in this file, so POST /api/version/{id}/restore
// hits :221 as AnyPrincipal — and its handler resolves the target with
// Ownership.RootUserSlug(caller.Slug, store) (Program.cs:1183), which maps an
// AGENT slug to its OWNER's slug (:356-374). With versioning enabled, an agent
// token can untar an arbitrary snapshot over the exact home volume
// Ownership.CanAccessHome forbids it from reading (:350). It has no UI consumer
// — "api/version" appears nowhere in src/frontend/src — so nobody has noticed.
//
// The READ is split: the shared record is group-scoped and an agent may ask it,
// but a file's BYTES and any restore are HumanOnly. An agent that can roll its
// owner's files back to a state it preferred can undo the owner's corrections.
if (path == "/api/history"
    || path.StartsWith("/api/history/", StringComparison.Ordinal)
    || path == "/api/version"
    || path.StartsWith("/api/version/", StringComparison.Ordinal))
{
    // ... HumanOnly for the file, restore and home routes; AnyPrincipal for lists.
}
```

The normalisation at `Security.cs:47` lowercases the path and trims a trailing slash before any `==` runs, so these comparisons are safe as written.

Every string lands in `src/frontend/src/i18n/{en,es,ru}.json` or the parity test fails.

---

## 10. WHAT THIS DELIBERATELY DOES NOT DO

- **The agent is never given a way to read or write the record.** No history API for an agent beyond the group-scoped list, no `.git` to reach, no path worth knowing.
- **No branches, no merges, no remotes, no push.** One ref per repository, every commit a child of the last. Nothing in this design touches a network, by constraint and by preference.
- **It does not record the owner's own desktop home**, and it says so in the Files view rather than letting them find out.
- **It does not record the in-product spreadsheet**, which lives in the runtime store. Open question 1.
- **It does not make anything recoverable for policy purposes.** No approval is relaxed by the existence of a history, now or later. If someone wants that back, it is a scope check against a declared blast radius, designed on its own, and it is not this.
- **Commits accumulate and are never pruned on a timer.** By design. The bounded resource is blob storage, and the only thing that reduces it is an explicit human release that is itself recorded.

---

## 11. WHAT WAS CONSIDERED AND REJECTED

**The agent commits, told to by its prompt** — a `save "one line"` wrapper shipped at `/usr/local/bin/save` beside `websearch` (`Containerfile:21-22`). Genuinely contested, and it gives better messages, because the agent knows *why* it changed a file and the runtime only knows *that* it did. Rejected on four grounds that compound:

- *It silently no-ops from the cwd the agent is actually standing in.* `distro/images/console/entrypoint.sh:8` starts the persistent tmux session with `-c "$HOME"`, i.e. `/root`, which this design deliberately does not make a repository — and the session is persistent, so cwd also survives a `cd` from three steps ago and from the previous run.
- *Nothing carries the result back.* `ConsoleAgentLoop.cs:229` pushes only `history.Add($"typed: {text}")` — no exit code, no output — and `SubmitAsync` returns a `PolicyDecision`, which is whether the agent was allowed to type, not whether the command worked. The normal run shape is save at step 7, `done=true` at step 8, and Done returns at `:192-197` without another capture. A failed save is indistinguishable from a good one at exactly the moment the agent writes its final reply claiming the work is saved.
- *The loop punishes it.* The anti-loop check at `:205-214` keys on the exact typed string with `RepeatCeiling = 2`, and a repeat routes into `EndByAskingAsync` — so a second `save "done"` with the same sentence is never executed *and* converts the run into an Asked result, handing the owner a question instead of a deliverable. Saving on the last step falls out of the `for` into `:246-250` and reports "Reached the step limit" for a run that had finished.
- *The measured behaviour in this codebase says agents do not do housekeeping.* They were told to put deliverables in `~/shared` and `Program.cs:2090-2104` had to be written because they claim files that are not there; they were told `websearch` exists and hand-rolled urllib instead (`EngineBriefing.cs:32-42`); they were told not to write `outbox.md` and `Program.cs:2011-2015` still keeps the fallback. A version-control instruction would land in exactly that company, and the run that forgets is the run that crashed or got blocked at step 3 — the one whose files nobody will ever see again.

The best half is kept anyway: `action.Note` goes in the commit body, so the agent's reasoning reaches the log without the agent having to remember anything.

**A `.git` inside the volume, with the runtime driving it.** Rejected. The agent is root with a real shell and will eventually `rm -rf .git`, `git reset --hard`, or leave an `index.lock` the recorder has to break. Every one of those becomes a case to handle, and a host mirror to recover from. The detached git directory answers all of them at once by removing the shared resource instead of guarding it.

**`git add -A` over the mounted volume.** Rejected for three independent reasons, each sufficient: it honours an agent-writable in-tree `.gitignore` and there is no flag that keeps the recorder's excludes while dropping the agent's (`add -f` overrides both); it honours an agent-writable `.gitattributes`, so the bytes restored are not the bytes that were on disk; and it records a nested `git init` as a gitlink storing none of the contents. See §4.

**Sharing one exclusion list with `PodmanHomeBrowser`'s dotfile filter** "so the two cannot drift". Rejected: the browser hides `.*` as a glob for *display*, and the recorder needs an enumerated list for *durability*. Unified on the browser's rule, a `.env` the owner asked for is silently never recorded. Not hiding and not keeping are two different decisions.

**Putting the recorded-ness sentence in `EngineBriefing.NativeTail`.** Rejected twice over. `NativeTail` is a `const` and cannot know whether `History:Enabled` is set, so on a default box it promises a recorder that does not exist — the believe-you-saved-it failure, shipped by the design meant to prevent it. And `Retarget` **replaces** `NativeTail` rather than keeping it (`EngineBriefing.cs:78-80`), so every hosted engine would lose the sentence entirely. `whereYouAre` is built per request and survives the swap.

**Committing inside the mutation gate**, at the natural site beside the audit row. Rejected: `mutationGate` is a `SemaphoreSlim(1, 1)` that serialises every mutating operation for every user on the box (`RuntimeServices.cs:147-149`, taken `:305`, released `:370`). A full-volume hash there blocks every other person's every action.

**Letting a git commit buy the `RequireApproval` → `Allow` relaxation.** Rejected on three counts and any one is fatal: the commit is *after* the action so it is not a pre-image; it covers two volumes and says nothing about the runtime store, the session runtime or egress; and the agent is root and can damage the repository. A fourth, subtler one: the relaxation is granted at decision time (`:296-303`) and the record would land later, with no rollback — so a commit that half-succeeds pays out the autonomy and delivers nothing, silently, on exactly the destructive command it was relaxed for.

**Narrowing the relaxation to a declared blast-radius scope** (`HomeFiles`, `SharedFiles`, `RuntimeStore`, `SessionRuntime`, `Egress`) instead of deleting it. The better idea, and the one `docs/openclaw-awareness.md:492-508` is already pointing at. Rejected *for this change*, not forever: it is a manifest schema change across six surfaces plus a policy change, it is worth designing on its own, and shipping it inside a history feature would mean the history feature is where a future reader goes to understand the approval model. Deleting the clause is the honest interim, and it is strictly safer than either alternative.

**Replacing `PodmanVersionStore` outright.** Rejected. A tar of a whole volume is genuinely better than git at one thing — a pre-image before a destructive migration, where what you want back is the machine and not a file — and git is worse at whole-volume rollback. Keep it, narrow its trigger from "any non-reversible command" to an explicit set of destructive operations that call it by name, and take it out of the policy path entirely.

**Versioning the owner's own home volume.** Rejected on privacy and size: it holds their documents, their browser profile, `.ssh` and `.gnupg`. Recording it every few seconds is surveillance of a person. Worth stating that this is the volume the current store already targets, so this is a correction.

**One repository spanning the home and `~/shared`.** Rejected: `~/shared` is a separate volume mounted inside the home (`SessionOrchestrator.cs:183`), so one repository straddles two lifetimes; and every agent of a user would keep a private, divergent duplicate of every deliverable.

**A commit per agent step rather than per action that changed the tree.** Rejected: a step that types `ls` changes nothing, and twenty empty commits per run make a log nobody can skim, which is the only property that made this better than a pile of tarballs.

**A commit per run.** Rejected the other way: a run that goes wrong at step 6 of 8 is precisely when the owner wants the file as it stood at step 5.

**Whole-tree restore as the default verb.** Rejected: "roll the home back to Tuesday" is a git-shaped operation and "give me the spreadsheet as it was" is the owner-shaped one. It exists, but behind per-file restore and behind a confirmation, because of the ownership problem in open question 3.

**`git reset` or any history rewind for restore.** Rejected: later versions would vanish from the feed, and nothing in this product vanishes.

**`gc --prune`, `reflog expire`, `filter-branch`, shallow grafts, a rolling-window repack.** Rejected: these are the operations that delete. A shallowed repository also cannot participate cleanly in any later mirroring, which quietly forecloses options. The bounded resource is blob storage and the answer is an explicit, recorded release (§8), not a git operation that makes history disappear.

**Reading actor identity from commit trailers in the portal.** Rejected: `successDetail` for `console.type` is the agent's typed text (`RuntimeServices.cs:345-348`) and `action.Note` is the agent's sentence, so a note ending in a blank line and a trailer-shaped string is a forgery vector. "The author header cannot be forged" is true of the header and false of what the owner reads. Identity comes from `cielo-ledger`, keyed by commit sha, host-side.

**Saying the word "git" to the agent at all.** Rejected. With a budget of eight steps it would spend three on `git status`, `git log` and `git diff`. `save` was the same trap in wrapper form. What the agent needs is one sentence saying it is handled and one saying what the error message means.

**Claiming the commit lands after the command finished.** Rejected as false. `SessionOrchestrator.cs:598` bounds the idle poll at 90 × 200 ms and then returns `"Typed and submitted."` regardless. The design says "sampled as soon after the action as the recorder can get to it" and adds the end-of-run sweep, rather than asserting a boundary the executor does not provide.

**Any remote, mirror-over-network, mesh VPN or hosted backup.** Rejected by constraint and on merit: a bare git directory on the same host needs none of it, and this must work on a laptop with no network.

**A `DELETE` on anything.** Named here because it is the constraint most likely to be violated by accident. There is no delete verb anywhere in this design; a release of blob storage is a forward-written entry that keeps every path, size, hash and timestamp.

---

## 12. OPEN QUESTIONS THAT NEED A HUMAN DECISION

1. **The in-product spreadsheet has no history and the owner's canonical question is about a spreadsheet.** `surfaces/spreadsheet.surface.json` declares `state.document: surface:spreadsheet`, `revisioned: true`; it is EF-persisted in the runtime store, so no repository touches it, and `set-cell` is `reversible: true` so `UndoPolicy.ShouldSnapshot` never fired on it either. Every cell an agent changes is unrecoverable today. Two options: snapshot the document into the shared repository as a file per changed action — it is cells of strings and it is cheap — or say plainly in the History view that this surface is not recorded. Doing neither is the current state and it is the worst of the three.
2. **Who releases blob storage at the ceiling, and with what confirmation?** §8 says the record is never deleted and the stored bytes may be released by an explicit human act. That act needs an owner, a UI, and a decision about whether a non-technical owner should ever be asked. The alternative is a box that silently stops keeping file contents and never starts again.
3. **Restore, file ownership, and the two home layouts.** Every existing host-side volume accessor is read-only — `PodmanHomeBrowser` offers List, Read and Download and says so at `:8-13`. Restore is a new host-side **write**, and the uid it lands as matters: the console home is `/root` at uid 0 (`SessionOrchestrator.cs:46`), the desktop home is `/config` on `lscr.io/linuxserver/webtop` (`distro/images/desktop/Containerfile:5`), which runs as `abc`/uid 1000. A write under `podman unshare` lands as namespace root, so ONLYOFFICE cannot save over an `.xlsx` the owner just restored, and a whole-tree restore would re-own a desktop home to root and break the session. Either add an explicit chown pass per session kind, or restrict restore to the shared volume, or back whole-tree restore with the retained tar — and say which, because the third contradicts "one recovery story".
4. **Does the owner get a History view for an agent's private home at all?** `CanAccessHome` already permits it (`Security.cs:344-348`) and `/api/home/{owner}/list` already serves it, so this is a product decision and not an access one. Recording the agent's scratch work is how "everything an agent creates" stays true; surfacing it is how an agent's half-finished drafts become something the owner reads. Decide before building the view, because the answer changes whether the home repository needs a UI at all.
5. **Host `git`, or `git` in a throwaway container?** The console image ships git (`Containerfile:14`); the desktop image does not. A host dependency is one more thing an install has to provide; `podman run --rm -v <volume>:/work:ro -v <hostrepo>:/repo localhost/lunos-console:latest git ...` needs none but forks a container per capture, which at one capture per action is a real cost. Probably: host git as the fast path, container as the fallback, with the choice logged once at startup.
6. **Does `whereYouAre` reach every path an agent runs on?** It is composed in the chat handler (`Program.cs:1968-1984`) and concatenated into the goal at `:2000-2010`. A run started directly through `/api/sessions/{id}/agent-run` may never see it, in which case an agent reached that way is never told it is recorded — and after §7 that is the difference between "does not know" and "assumes not". Check and, if so, move the composition somewhere both paths cross.
7. **What triggers a capture when only the owner is working?** The owner's desktop mounts the same shared volume, and a file they drop there produces no action and therefore no queued capture. The lazy sweep on opening the History view covers it, but that means their own edits sit unrecorded for an arbitrary period and then all land at once with one timestamp. A periodic sweep per active owner group is the obvious answer and it has a cost; decide the interval deliberately rather than defaulting to "when someone looks".
8. **Does git accept a detached work tree inside a `podman unshare` namespace?** `PodmanVersionStore.cs:42` proves the access pattern for `tar`, which has no ownership check. Git does: a work tree whose files carry mapped uids is a *"detected dubious ownership"* candidate and may need explicit `safe.directory` handling. Verify on the real box before the plumbing path carries the design's spine, because the fallback — running git inside the session — is the one thing §5 exists to avoid.
9. **Does `History:Enabled` default to on?** It cannot inherit `Sessions:Versioning`'s default, because that key is being retired as a policy switch and an install with it set must *lose* five relaxations in the same release. Once it buys no approval change, the argument for opt-in is weaker — a history is strictly additive — but it does consume disk on every box, and "off by default" is what the prompt variant in §7 exists to describe honestly.
