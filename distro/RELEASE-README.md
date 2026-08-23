# Lun.Os — release bundle

A self-contained Lun.Os: the runtime (no .NET needed on the target) that **serves
its own panel**, plus surfaces and config. Provider-free — you add an AI provider
from the panel's **Models** tab (no restart), or set a key in `/etc/lunos/lunos.env`.

## Install (Ubuntu 24.04+)

```
tar xzf lunos-linux-x64.tar.gz
sudo ./lunos/install.sh --mode headless   # or: app | kiosk
```

| Mode | For | You see the panel via | Binds |
|------|-----|-----------------------|-------|
| `app` | your own machine | a local browser at `http://127.0.0.1:5148/` | loopback |
| `headless` | a VPS / old machine | your browser + a token, over the LAN/internet | all interfaces |
| `kiosk` | an appliance | the machine boots into a fullscreen panel browser | loopback |

## First owner (loopback-only, by design)

The first claim only works **from the machine itself** — nobody on the network can
claim your box. Do it on the box:

- **app / kiosk:** the local browser at `http://127.0.0.1:5148/` shows the claim wizard.
- **headless:** `ssh` in and run `lunos-claim "Your Name"` — it prints your token.
  Then open `http://<host-ip>:5148/` from your laptop and sign in with that token.

Add a teammate later: `lunos-add-user "Their Name" <owner-token>` (or the panel).

## Notes

- Provider-free and single SQLite DB under `/opt/lunos/.data`.
- Sessions are rootless podman containers; their images build on first use (or
  prebuild them from the distro Containerfiles as the `lunos` user).
- Dogfood posture: plain HTTP, token-gated. For a VPS, put it behind TLS (a reverse
  proxy) or reach it over an SSH tunnel.
