# Local Development

## Prerequisites

- .NET SDK 10 or later
- Node.js and npm

## Quick start

`./scripts/dev.sh` runs everything: it starts the backend on `http://127.0.0.1:5148` and the Vite panel on `http://127.0.0.1:5173`, then prints the owner's session token once one exists. On a fresh checkout there are no users yet, so the panel opens on the claim wizard (see [Authentication](#authentication)).

## Backend

To run just the backend:

```bash
dotnet run --project src/backend/WorkspaceRuntime.Api --urls http://127.0.0.1:5148
```

Useful endpoints:

- `GET /api/branding`
- `GET /api/users`
- `GET /api/workspaces`
- `GET /api/agents`
- `GET /api/spreadsheet`
- `GET /api/inference/status`
- `POST /api/inference/chat`
- `POST /v1/chat/completions`
- `POST /api/tool-requests`
- `GET /api/approvals`
- `POST /api/approvals/{approvalId}/approve`
- `POST /api/approvals/{approvalId}/reject`
- `GET /api/audit-events`

## Frontend

```bash
cd src/frontend
npm install
npm run dev
```

The Vite dev server listens on `http://127.0.0.1:5173` and proxies `/api` calls to `http://127.0.0.1:5148` by default. Set `BACKEND_PORT` before starting both processes to use another port — useful when a CieloOS VM is running, because the VM forwards its runtime API to host port 5148:

```bash
BACKEND_PORT=5149 ./scripts/dev.sh
```

## Authentication

First run is a **claim**, not a seeded login. A fresh runtime has no users, so opening the panel shows the "Claim this machine" wizard. Claiming is loopback-only by design (a local browser or an SSH tunnel — nobody on the network can claim your box) and single-winner; it creates the first owner plus their agent and mints a bearer token you paste into the panel's session screen.

- `GET /api/setup/status` → `{ "claimed": <bool> }`
- `POST /api/setup/claim` with `{ "name": "..." }` → `{ slug, token }`. Public so a token-less machine can reach it, but guarded in-handler: `403` off loopback, `409` once an owner exists.

Tokens are per-identity files under `.data/secrets/` (override with `Auth:SecretsPath`; the VM uses `/var/lib/workspace-runtime/secrets`), each written `0600` when the identity is claimed or added: a user's token is `<slug>.token`, their agent's is `<slug>-agent.token`. Paste a user token into the panel to sign in. The agent principal (`workspace-agent`) reads its token from `WORKSPACE_RUNTIME_TOKEN`, `WORKSPACE_RUNTIME_TOKEN_FILE`, the VM path, or `./.data/secrets/agent.token`.

**Demo seed (opt-in).** The `joche`/`yulia` demo identities are no longer seeded by default — a clean install is provider-free with no users. Set `LUNOS_DEMO=1` (or `Runtime:SeedDemo=true`) before starting to seed them:

```bash
LUNOS_DEMO=1 ./scripts/dev.sh
```

The machine then counts as already claimed (the wizard is skipped) and `./scripts/dev.sh` prints joche's session token, with yulia's at `.data/secrets/yulia.token`.

Every route except `/`, `/api/branding`, the `/api/inference/status` readiness probe, and the two `/api/setup/*` endpoints above requires `Authorization: Bearer <token>`. Human-only verbs — approving/rejecting approvals, inviting a teammate (`POST /api/users`), and changing providers or defaults (`POST`/`DELETE /api/models`) — reject an agent token, so an agent can never approve its own requests or rewire the OS. Approvals bind to a SHA-256 hash of the exact pending request plus the surface revision the human previewed; resolving with a stale hash or after the surface moved returns 409. Manifest gates (`requiresHuman`, `exposedToAgent`, input `pattern`/`maxLength`, `validWhen`) are enforced inside the runtime itself, so every entry path — surface commands, raw tool requests, future adapters — passes the same checks, and all mutations are serialized through one gate.

## Surfaces

The spreadsheet is the first contract surface (`surfaces/spreadsheet.surface.json`, schema 2):

- `GET /api/surfaces` — registry
- `GET /api/surfaces/{id}/manifest` — the parsed contract
- `GET /api/surfaces/{id}/state` — revisioned state with an ETag (304 on If-None-Match)
- `GET /api/surfaces/{id}/commands` — the currently-valid commands (progressive disclosure; at most 8)
- `POST /api/surfaces/{id}/commands/{name}` — dispatch with `{input, dryRun?, idempotencyKey?, expectedRevision?}`
- `GET /api/events` — server-sent events on state and approval changes

The panel renders its buttons from the commands endpoint and dispatches through the same bus the agent CLI uses (`workspace-agent observe` / `workspace-agent do`). A conformance test fails if any frontend mutation bypasses it.

## Persistence

The runtime persists to SQLite by default at `.data/workspace-runtime.db` under the repository root (created on first run; the demo identities are only added when `LUNOS_DEMO=1`). Configuration lives in `appsettings.json` under `Database`:

- `Provider`: `sqlite` (default), `postgres`, or `memory`
- `SqlitePath`: optional explicit SQLite file path
- `PostgresConnection`: required when `Provider` is `postgres`

The schema is managed with EF Core migrations under `src/backend/WorkspaceRuntime.Infrastructure/Migrations/`, applied automatically on startup. Add a migration with `dotnet ef migrations add <Name>` from the Infrastructure project (a design-time SQLite factory is included). Databases created by the earlier `EnsureCreated` prototype predate the migration history table — delete `.data/` once when upgrading. Pending approvals and their tool requests survive restarts: an approval created before a restart can be approved and executed after it.

## Tests

`./scripts/test.sh` runs both suites — `dotnet test`, then the panel's `npm test` (vitest) in `src/frontend`. To run them separately:

```bash
dotnet test
cd src/frontend
npm test
```
