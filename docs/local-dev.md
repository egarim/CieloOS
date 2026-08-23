# Local Development

## Prerequisites

- .NET SDK 10 or later
- Node.js and npm

## Backend

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

The Vite dev server proxies `/api` calls to `http://127.0.0.1:5148` by default. Set `BACKEND_PORT` before starting both processes to use another port — useful when a CieloOS VM is running, because the VM forwards its runtime API to host port 5148:

```bash
BACKEND_PORT=5149 ./scripts/dev.sh
```

## Authentication

The runtime has two principals backed by file tokens created on first run under `.data/secrets/` (or `Auth:SecretsPath`; the VM uses `/var/lib/workspace-runtime/secrets`):

- `human.token` — required for approving or rejecting approvals; paste it into the panel's session screen. `./scripts/dev.sh` prints it when the backend starts.
- `agent.token` — the agent service principal; `workspace-agent` reads it from `WORKSPACE_RUNTIME_TOKEN`, `WORKSPACE_RUNTIME_TOKEN_FILE`, the VM path, or `./.data/secrets/agent.token`.

Every route except `/`, `/api/branding`, and the `/api/inference/status` readiness probe requires `Authorization: Bearer <token>`. Approval verbs additionally require the human principal, so an agent can never approve its own requests. Approvals bind to a SHA-256 hash of the exact pending request plus the surface revision the human previewed; resolving with a stale hash or after the surface moved returns 409. Manifest gates (`requiresHuman`, `exposedToAgent`, input `pattern`/`maxLength`, `validWhen`) are enforced inside the runtime itself, so every entry path — surface commands, raw tool requests, future adapters — passes the same checks, and all mutations are serialized through one gate.

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

The runtime persists to SQLite by default at `.data/workspace-runtime.db` under the repository root (created and seeded on first run). Configuration lives in `appsettings.json` under `Database`:

- `Provider`: `sqlite` (default), `postgres`, or `memory`
- `SqlitePath`: optional explicit SQLite file path
- `PostgresConnection`: required when `Provider` is `postgres`

The schema is managed with EF Core migrations under `src/backend/WorkspaceRuntime.Infrastructure/Migrations/`, applied automatically on startup. Add a migration with `dotnet ef migrations add <Name>` from the Infrastructure project (a design-time SQLite factory is included). Databases created by the earlier `EnsureCreated` prototype predate the migration history table — delete `.data/` once when upgrading. Pending approvals and their tool requests survive restarts: an approval created before a restart can be approved and executed after it.

## Tests

```bash
dotnet test
cd src/frontend
npm test
```
