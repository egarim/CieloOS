# Brief 01b-2 — flip the four gates onto the socket

## The socket is proven, so this can proceed

Verified on a real headless install at `v0.1.10-…`:

```
/run/cielo                         drwxr-x---  cielo cielo
admin.sock                         present, runtime listening
GET /api/setup/status over socket  -> HTTP 200 {"claimed":false,"owner":null}
GET over TCP                       -> HTTP 200
as a sudo user who is not cielo    -> connection refused
```

`OnThisMachine` and the socket both exist and neither is wired to anything. This
brief wires them, and it is the change that can make a machine unclaimable, so
read the whole thing before starting.

## 1. The four gates

In `src/backend/WorkspaceRuntime.Api/Program.cs`, replace
`IsLoopback(context.Connection.RemoteIpAddress)` with
`TransportFacts.OnThisMachine(context, tlsTerminatedBy)` at exactly these four
places and nowhere else:

| line | what it gates |
|---|---|
| 562 | whether `/api/setup/status` discloses the owner slug |
| 567 | the first-owner claim |
| 726 | setting a machine-wide budget |
| 877 | the first password |

`tlsTerminatedBy` is already in scope at the top of the file (01a added it).

Leave `IsLoopback` itself in place — it now delegates to `TransportFacts` and the
login throttle still reads the raw peer deliberately.

### The refusal has to say why

`docs/tls-and-proxies.md` §8: when the gate closes because the peer is a **named
terminator**, the message must say so. *"Setup can only be claimed from the
machine itself"* is actively misleading to an operator whose proxy is forwarding
them, because they believe they are on the machine and in a sense they are.

`FirstRunSetup.cs` produces the claim refusal text and takes a `bool`. Widen it
only as far as this needs — a second `bool`, or a small enum, whatever keeps the
call sites honest — and say which you chose and why in NOTES.

## 2. Four callers move to the socket

All four run on the box and currently reach the API over TCP loopback. On
`--mode headless` Kestrel binds `0.0.0.0`, so after step 1 their connections stop
being "on this machine" and they break. They are generated inside
`distro/install.sh`:

- **`cielo-claim`** — the first-owner claim. Breaks with *"Setup can only be
  claimed from the machine itself"* told to somebody standing on it.
- **`cielo-set-password`** — the first password is a gated route.
- **`cielo-add-user`** — signs in first, which is not gated, but it belongs on the
  same transport as its siblings.
- **`cielo-chat-run`** — and this one is the trap. It reads
  `/api/setup/status` and parses `owner` out of it. After step 1 that field is
  `null` on headless, so the chat service exits with *"no owner to act as: claim
  the box"* on a box that is already claimed. Nothing about that message points
  at this change.

Replace `curl ... http://127.0.0.1:$PORT/...` with
`curl --unix-socket /run/cielo/admin.sock ... http://localhost/...` in all four.
The path stays; only the transport changes.

`cielo-chat-run` runs as `User=cielo`, so it can open the socket. Do not change
its unit.

## 3. cielo-claim now needs privilege, and must say so

`/run/cielo` is `0750 cielo:cielo`. A plain user cannot traverse it, so
`cielo-claim` run without privilege will now fail — and the banner currently tells
people to run it bare:

```
  ssh in and run:   cielo-claim "Your Name"
```

Make `cielo-claim` re-exec itself under `sudo` when it is not root and not
`cielo`, the way `cielo-build-session-images` and `cielo-podman` already do for
their own reasons. It prompts for a name and a password on `/dev/tty`, so
whatever you use must keep the terminal attached — `exec sudo -- "$0" "$@"` does;
piping does not.

If elevation is impossible, fail with a sentence that says what to run, not with
a curl error.

Update the closing banner in `install.sh` to match whatever you choose.

The installer's own claim prompt already runs as root, so it is unaffected —
but check it, do not assume it.

## 4. Tests

1. Each of the four gates refuses a caller that is not on this machine and admits
   one that is. Unit-level against `OnThisMachine` is fine; there is no
   `TestServer` in this solution and adding one is out of scope.
2. A named terminator gets the *named-terminator* refusal text, not the generic
   one.
3. `bash -n` clean for `install.sh` — you are editing generated scripts inside a
   heredoc and a quoting error there is invisible until install time.

## Rules

`Program.cs` is 2,920 lines and `install.sh` is 1,140. **EDIT blocks only** for
both; a whole-file reply will be truncated and refused.

The helper scripts live inside heredocs in `install.sh`. Some are quoted
(`<<'CLAIM'`) and some are not (`<<EOF`); in an unquoted one, `$` and `\` are
eaten at install time and must be escaped. Check which kind you are editing
before you write a `$`. This has broken twice in this repository, most recently a
password escaper that became `sed -e 's/\/\\/g'`.

## Out of scope

Invitations. The panel. `/etc/cielo/network.env`. Anything not listed above.
