# CieloOS — release bundle

A self-contained CieloOS: the runtime (no .NET needed on the target) that **serves
its own panel**, plus surfaces and config. Provider-free — you add an AI provider
from the panel's **Models** tab (no restart), or set a key in `/etc/cielo/cielo.env`.

## Install (Ubuntu 24.04+)

```
tar xzf cielo-linux-x64.tar.gz          # or cielo-linux-arm64.tar.gz on ARM
sudo ./cielo/install.sh --mode headless   # or: app | kiosk
```

| Mode | For | You see the panel via | Binds |
|------|-----|-----------------------|-------|
| `app` | your own machine | a local browser at `http://127.0.0.1:5148/` | loopback |
| `headless` | a VPS / old machine | your browser + a token, over the LAN/internet | all interfaces |
| `kiosk` | an appliance | the machine boots into a fullscreen panel browser | loopback |

### Or run it as an app — no root, no systemd

```
tar xzf cielo-linux-<arch>.tar.gz    # linux-arm64 on ARM, linux-x64 on Intel/AMD
./cielo/run.sh                       # or: PORT=6000 ./cielo/run.sh
```

Same runtime as `install.sh --mode app`, but foreground (Ctrl-C stops it) with the
control plane's state in `<bundle>/.data` — no `/opt`, no service, no `sudo`. This is
the path for **WSL2 on Windows**, including Windows-on-ARM (use the `linux-arm64`
bundle): WSL forwards `localhost`, so the Windows browser reaches the loopback bind
and the first-run claim works.

Note that `run.sh` installs no helper commands — `cielo-claim` / `cielo-add-user` /
`cielo-selftest` come from `install.sh`. On this path, claim and add teammates from
the panel, and run the bundled `./cielo/cielo-selftest.sh` directly.

Step-by-step for Windows: `docs/wsl-quickstart.md` in the repository
(https://github.com/egarim/CieloOS/blob/main/docs/wsl-quickstart.md).

## Upgrading an existing installation

The runtime keeps its state in SQLite. Before applying any schema change, the new
binary writes a timestamped backup beside the database (`workspace-runtime.db.<timestamp>.bak`)
and logs the path. If you ever need to go back to an older build, stop the runtime,
restore that backup over the live database, then start the older build.

**Installed layout (`install.sh`)**

Keep `/opt/cielo/.data` in place and run the same installer from the new bundle.
`install.sh` stops `cielo-runtime.service` before replacing `bin`, `panel`,
`surfaces`, and `config`, then restarts it afterward.

```
tar xzf cielo-linux-<arch>.tar.gz
sudo ./cielo/install.sh --mode <app|headless|kiosk>
```

Pass the same options you used originally (for example `--mode`, `--port`,
`--no-chat`).

**App layout (`run.sh`)**

The `.data` directory lives inside the bundle and is not part of a fresh tarball.
Copy it into the new bundle before starting the new build.

```
# stop the old run.sh first (Ctrl-C, or kill the process)
mkdir -p new-cielo
tar xzf cielo-linux-<arch>.tar.gz -C new-cielo --strip-components=1
cp -a cielo/.data new-cielo/.data
./new-cielo/run.sh
```

Keep the old bundle directory until the new build has started cleanly and the
health check responds.

## First owner (loopback-only, by design)

The first claim only works **from the machine itself** — nobody on the network can
claim your box. Do it on the box:

- **app / kiosk:** the local browser at `http://127.0.0.1:5148/` shows the claim wizard.
- **headless:** `ssh` in and run `cielo-claim "Your Name"` — it prints your token.
  Then open `http://<host-ip>:5148/` from your laptop and sign in with that token.

Add a teammate later: `cielo-add-user "Their Name" <owner-token>` (or the panel).

## Chat with your agent

`install.sh` installs **Open WebUI** as `cielo-chat.service`, pointed at this box's
`/v1/agent` endpoint and authenticated as the owner. Every message runs the console
loop: the agent uses its tools and operates the OS, and you get its reply.

It starts by itself once the box is claimed — before that there is no owner to act
as, so the service exits and retries every 15 seconds. First start downloads the
image (~1 GB), so give it a few minutes.

```
http://127.0.0.1:8080/
```

**It is loopback-only on purpose.** The chat has no login of its own yet, so whoever
opens it acts as the owner. From another machine, tunnel rather than exposing it:

```bash
ssh -N -L 8080:127.0.0.1:8080 you@box
```

`/etc/cielo/chat.env` holds the host, port and image. Install without it entirely
with `./install.sh --no-chat`; the panel then shows no chat link.

Reply quality tracks the chat model: a small local model will do things like type
`Hello!` into its own shell instead of answering. A capable cloud model behaves.

## Notes

- Provider-free and single SQLite DB under `/opt/cielo/.data` (or `<bundle>/.data` with
  `run.sh`). Session containers keep their images and named volumes in podman storage
  outside either location, so removing the install/bundle does not remove those.
- Sessions are rootless podman containers; their images build on first use (or
  prebuild them from the distro Containerfiles as the `cielo` user).
- Dogfood posture: plain HTTP, token-gated. For a VPS, put it behind TLS (a reverse
  proxy) or reach it over an SSH tunnel.
