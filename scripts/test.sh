#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:${PATH}"

dotnet test

cd src/frontend
if [[ ! -d node_modules ]]; then
  npm install
fi
npm test
