#!/usr/bin/env bash
# Run the Lun.Os web panel (Vite) on the Mac, proxying /api to the runtime
# running INSIDE the VM. The VM's runtime listens on 5150 and is reached over an
# SSH tunnel (127.0.0.1:5150 -> VM:5150), so we point the proxy there rather than
# at 5148 (the autoinstall-baked runtime) or a local dotnet build.
set -euo pipefail
export PATH="/opt/homebrew/bin:${PATH}"
export BACKEND_PORT="${BACKEND_PORT:-5150}"
cd "$(dirname "$0")/../src/frontend"
exec npm run dev
