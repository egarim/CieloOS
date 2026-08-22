You are implementing V0.1 of a brand-new project currently branded "Lun.Os".

This is a real implementation task. Build a working local vertical slice, not a design document.

Non-negotiable architecture constraints:
- The product brand is provisional. Keep "Lun.Os", "Lun", and "LunOS" confined to branding config, documentation, page titles, and visible UI copy only.
- Do not use Lun, LunOS, or Lun.Os in stable technical namespaces, service names, API route roots, database table prefixes, protocol names, package names, or durable identifiers.
- Use neutral technical names. Prefer "WorkspaceRuntime" for .NET namespaces and project naming.
- Use C#/.NET ASP.NET Core for the backend.
- Use React + TypeScript + Vite for the frontend.
- Use PostgreSQL as the intended database. For local tests/dev, it is acceptable to support SQLite fallback or EF Core in-memory if documented.
- Include a multi-user-aware domain model.
- Include an agent runtime with pluggable inference providers.
- Include a policy engine with Allow, Deny, and RequireApproval decisions.
- Include structured tools.
- Include a sandboxed executor abstraction.
- Include an audit log.
- Include an approval flow.
- Include a spreadsheet demo tool/workflow.
- Include backend tests, and frontend unit tests if practical.
- Produce a working local dev command and useful documentation.

Vertical slice:
1. A backend API that can:
   - expose current branding from config;
   - list users/workspaces/agents;
   - submit a structured agent tool request;
   - evaluate policy;
   - execute allowed spreadsheet operations through a sandboxed executor;
   - create approval records when policy requires approval;
   - approve/reject pending approvals;
   - write audit events for all important operations;
   - expose audit events and spreadsheet demo state.
2. A frontend app that can:
   - load branding from the backend;
   - show users/workspaces/agents;
   - submit sample spreadsheet actions;
   - show policy decisions;
   - approve pending requests;
   - show audit events;
   - visibly use the brand name only from branding config.
3. Tests that cover:
   - policy Allow/Deny/RequireApproval behavior;
   - approval flow;
   - spreadsheet execution;
   - audit logging;
   - rename-safety guard, such as a script/test that fails if forbidden brand terms appear in stable backend namespaces or project identifiers.

Recommended structure:
- src/backend/WorkspaceRuntime.Api
- src/backend/WorkspaceRuntime.Domain
- src/backend/WorkspaceRuntime.Application
- src/backend/WorkspaceRuntime.Infrastructure
- tests/backend/WorkspaceRuntime.Tests
- src/frontend
- config/branding.json
- docs/architecture.md
- docs/local-dev.md

Implementation notes:
- Keep the first version intentionally small and understandable.
- Seed demo data on startup.
- Use OpenAPI/Swagger if easy.
- The backend should run with a simple command such as `dotnet run --project src/backend/WorkspaceRuntime.Api`.
- The frontend should run with `npm install && npm run dev` from `src/frontend`.
- Add root-level scripts or documentation for running both.
- Prefer standard libraries and common frameworks over hand-rolled infrastructure.
- Commit nothing unless explicitly asked.

Please implement the files in this repository now.
