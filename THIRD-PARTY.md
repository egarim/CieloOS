# Third-Party Components

CieloOS redistributes a number of third-party works — as base images, as packages
installed into those images, as a self-contained runtime, and as the panel's
frontend build. This file names what is shipped and under what licence.

> This is attribution for bundled third-party software. It is **not** the CieloOS
> project licence; the licence of CieloOS itself is the repository owner's choice
> and is not recorded here.

Where a component is a distribution (Ubuntu) rather than a single work, the exact
licence set is per package; the dominant licences are listed rather than one
invented for the whole. Components whose licence could not be pinned down are
flagged rather than guessed.

## Ubuntu live-server ISO

`distro/scripts/build-usb.sh` remasters an **Ubuntu 24.04 LTS live-server** ISO into
a bootable installer. Ubuntu is free and open-source software: the Linux kernel is
GPL-2.0-or-later, and the userland packages carry their own licences (GPL, LGPL,
MIT, BSD, Apache-2.0, MPL and others), governed by the Ubuntu licence policy
(roughly the Debian Free Software Guidelines). It is a collection, not a single
work, so there is no one licence for the ISO; the licence of each package applies
to that package.

## Session images

The session images (`distro/images/desktop/Containerfile` and
`distro/images/console/Containerfile`) are built on the target machine by
`install.sh`. Their contents are licensed as follows.

### linuxserver.io webtop base (`lscr.io/linuxserver/webtop:ubuntu-xfce`)

The webtop project (its Dockerfile and support scripts) is GPL-3.0; the exact
terms are in linuxserver.io's own LICENSE and were not re-verified in this build
environment. The image is built on an Ubuntu base, so the packages it pulls in
keep their own licences (the Ubuntu mixed-FOSS set above). The following are
already present in the base image and are therefore shipped with it, not added by
CieloOS's Containerfile:

- **Chromium** — BSD-3-Clause.

### Added by `distro/images/desktop/Containerfile`

- **ONLYOFFICE Desktop Editors** (the `.deb` downloaded and installed) — AGPL-3.0
  (a commercial licence is also offered upstream).
- **Papirus icon theme** — GPL-3.0.
- **Orchis GTK theme** — GPL-3.0.
- **Inter font** — SIL Open Font License 1.1.
- **xdotool** — BSD-2-Clause.
- **scrot** — commonly MIT / BSD (the scrot source states a permissive licence; the
  exact terms are in the upstream LICENSE, which this environment could not fetch —
  confirm before relying on it).
- **AT-SPI** (`at-spi2-core`, `gir1.2-atspi-2.0`) — LGPL-2.1-or-later;
  `python3-gi` (PyGObject) is LGPL-2.1-or-later.
- **ffmpeg** — LGPL-2.1-or-later (the Ubuntu build; it may bundle GPL-licensed
  codecs, in which case the GPL applies to that combination).
- **locales, fonts** (`fonts-dejavu-core`, `fonts-liberation2`), **language packs**
  (`language-pack-*`), **XFCE/GTK theme bits** (`orchis-gtk-theme`,
  `papirus-icon-theme`, `breeze-cursor-theme`, `fonts-inter`, `plank`,
  `dconf-cli`), **keyboard utils** (`x11-xkb-utils`, `xkb-data`) — each from Ubuntu
  under its own licence (DejaVu is Bitstream Vera/DejaVu licence, Liberation is
  OFL-1.1, breeze-cursor-theme is LGPL-3.0, plank is GPL-3.0+, and the rest follow
  their Ubuntu package licences). These are redistributed as Ubuntu packages; the
  exact licence set is per package.

### `distro/images/console/Containerfile` contents

Base: **Ubuntu 24.04** (`docker.io/library/ubuntu:24.04`) — the mixed-FOSS Ubuntu
set above. Then installed:

- **ttyd** — MIT.
- **tmux** — ISC.
- **bash** — GPL-3.0-or-later.
- **ca-certificates** — MPL-2.0 (the bundled certificate data is public-domain
  source material).
- **procps** — GPL-2.0-or-later.
- **nano** — GPL-3.0-or-later.
- **less** — GPL-3.0-or-later.
- **git** — GPL-2.0-only (with a runtime-library exception).
- **curl** — MIT (its own permissive licence, derived from MIT/ISC).
- **jq** — MIT.
- **w3m** — MIT-style permissive (the w3m licence is a BSD/MIT-like permissive
  licence; confirm against the upstream source for the exact wording).
- **python3** — PSF Licence.
- **python3-pip** — MIT.
- **python3-openpyxl** — MIT.
- **python3-requests** — Apache-2.0.
- **python-docx** (pip) — MIT.
- **python-pptx** (pip) — MIT.

## .NET self-contained runtime

`distro/scripts/build-release.sh` publishes the runtime with
`dotnet publish --self-contained`, so the .NET runtime is carried in the bundle
and needs no installation on the target. The .NET runtime and SDK are **MIT**.
The runtime additionally bundles some third-party components under their own
licences; those are listed in the .NET `THIRD-PARTY-NOTICES` shipped with the
runtime, which takes precedence for those files.

## Frontend npm dependencies (`src/frontend`)

Direct dependencies, summarised by licence family (transitive dependencies of
these are resolved by npm and inherit their own licences):

- **MIT** — react, react-dom, @vitejs/plugin-react, vite, and the test/dev stack
  (@testing-library/jest-dom, @testing-library/react, @types/node, @types/react,
  @types/react-dom, jsdom, vitest).
- **ISC** — lucide-react.
- **Apache-2.0** — typescript.
