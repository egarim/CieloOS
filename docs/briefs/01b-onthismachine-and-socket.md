# Brief 01b — `OnThisMachine`, and the socket it cannot ship without

## Read this before anything else

`docs/tls-and-proxies.md` §5 says, in bold:

> **So the second conjunct does not ship on its own.** … A headless install with
> the narrowing and no socket has no onboarding path whatsoever, and that is not a
> follow-up commit, it is the same commit. **Sequencing note for whoever builds
> this: the socket is a co-requisite, not hardening.**

The reason: `cielo-claim` runs `curl http://127.0.0.1:$PORT/api/setup/claim`. On
`--mode headless` Kestrel binds `0.0.0.0`, so that connection's **local** endpoint
is a specific interface address, not loopback. Narrowing `OnThisMachine` to
require a loopback local endpoint therefore refuses the operator's own on-box
claim, and tells them *"Setup can only be claimed from the machine itself"* while
they are standing on it.

**This brief is part one of two, and it deliberately changes no behaviour.** You
are building the predicate and the transport. The four gates keep calling
`IsLoopback` exactly as they do today, and are switched in 01b-2 only once this
part is proven on a real machine. Do not switch them. Do not "helpfully" wire
`OnThisMachine` into anything.

## What to build

### 1. `OnThisMachine` in `TransportFacts`

True when **either**:

- the connection arrived over the admin unix socket; **or**
- `RemoteIpAddress` is loopback **and** `LocalIpAddress` is loopback **and** the
  peer is not in the named-terminator set.

No configuration value may make it true where it is false today — that is the
invariant `ConfidentialTests` already guards and it must keep holding. A named
terminator is never "on this machine": the operator has told us that address is a
proxy, and a proxy is not the box.

For a unix-socket connection Kestrel leaves both `RemoteIpAddress` and
`LocalIpAddress` null. Decide how you detect it, say which you chose in NOTES, and
**test it** — if you rely on "both addresses are null", say out loud that this is
only sound because the process listens on TCP and unix sockets and nothing else.

Add it beside `Confidential` in `src/backend/WorkspaceRuntime.Api/TransportFacts.cs`.

### 2. The socket, as configuration rather than code

Kestrel already accepts a unix socket in `ASPNETCORE_URLS`:

```
ASPNETCORE_URLS=http://0.0.0.0:5148;http://unix:/run/cielo/admin.sock
```

So the runtime needs no listener code. What it needs is for the directory to
exist, owned correctly, before `ExecStart`:

- `distro/services/cielo-runtime.service` gains `RuntimeDirectory=cielo` and
  `RuntimeDirectoryMode=0750`. systemd creates `/run/cielo` owned by the unit's
  `User=`/`Group=` (both `cielo`) before start, and removes it on stop.
- `distro/install.sh` appends the socket to `ASPNETCORE_URLS` when it writes
  `/etc/cielo/cielo.env`. It currently does this with a `sed` on
  `distro/config/cielo.env.example`; extend that, do not restructure it.

**Only for systemd installs.** `distro/run.sh` runs the runtime in the foreground
with no root and no systemd, so `/run/cielo` will not exist and Kestrel fails hard
on a socket path it cannot bind — that would turn a working dev command into a
crash. Leave `run.sh` on TCP only and say so in NOTES.

The socket ends up `cielo:cielo` mode `0660`, not `root:cielo` as the document
says, because the service runs as `cielo` and creates its own socket. That is the
same authority in practice — root and the `cielo` group can open it, `www-data`
cannot — but it is a deviation from the document, so record it in CONCERNS rather
than silently matching or silently differing.

### 3. Tests

In `tests/backend/WorkspaceRuntime.Tests/`.

1. A loopback peer on a loopback local endpoint is on this machine.
2. A loopback peer on a **non**-loopback local endpoint is **not** — this is the
   ambiguous case the narrowing exists for: a co-located proxy forwarding the
   internet looks exactly like an on-box caller from the remote half alone.
3. A named terminator is never on this machine, even from loopback.
4. A connection with both addresses null (the unix socket) **is** on this machine.
5. The invariant, again, from this angle: with `Network__TlsTerminatedBy`
   populated, no peer that was off-machine before becomes on-machine.
6. The four gates are unchanged. Assert that `IsLoopback` still returns what it
   returned before for the same inputs — this is the test that proves part one
   changed no behaviour.

## Out of scope

Switching the four gates. Moving `cielo-claim`, `cielo-add-user` or
`cielo-set-password` to `curl --unix-socket`. Both are 01b-2, and 01b-2 does not
begin until this is running on a real VM.

`/etc/cielo/network.env`, the `EnvironmentFile=-` line, and the installer's
`is-active` check remain out of scope; they are still in followups from 01a.

## Rules

`Program.cs` is 2,880 lines. Use **EDIT blocks** for it — a whole-file reply will
be truncated and refused. `install.sh` is 1,100 lines: EDIT blocks for it too.
FILE blocks are fine for `TransportFacts.cs` and for test files.

Quote OLD text exactly from the context supplied. Change nothing you were not
asked to change.
