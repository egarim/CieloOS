#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
backend() { dotnet test WorkspaceRuntime.slnx --nologo -v minimal; }
frontend() (
  cd src/frontend
  npm ci --no-audit --no-fund
  npm run build
  npm test
  for page in index.html portal.html; do
    test -f "dist/$page" || { echo "dist/$page was not built" >&2; exit 1; }
  done
  compgen -G 'dist/assets/portal-*.js' >/dev/null || { echo "Missing portal bundle" >&2; exit 1; }
  compgen -G 'dist/assets/main-*.js' >/dev/null || { echo "Missing admin bundle" >&2; exit 1; }
)
preflight() {
  bash tests/preflight-smoke.sh
  bash tests/preflight-cpu-fixtures.sh
  bash tests/preflight-conflicts.sh
}
case "${1:-all}" in
  backend) backend ;;
  frontend) frontend ;;
  preflight) preflight ;;
  all) backend; frontend; preflight ;;
  *) echo "Usage: bash scripts/ci/test.sh [backend|frontend|preflight|all]" >&2; exit 2 ;;
esac
