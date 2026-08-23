# Architecture

The product is currently branded CieloOS, but brand terms are treated as presentation data. Stable backend namespaces, project files, API routes, and durable identifiers use neutral `WorkspaceRuntime` naming.

## Runtime path

```text
User
  -> Agent
  -> ToolRequest
  -> PolicyEngine
      -> Allow
      -> Deny
      -> RequireApproval
  -> SandboxedToolExecutor
  -> AuditEvent
```

## Domain model

- `PlatformUser`: a human account.
- `Workspace`: a persistent user-owned workspace.
- `AgentProfile`: an agent security principal with explicit tool grants.
- `ToolRequest`: a structured operation submitted by an agent.
- `PolicyEvaluation`: the policy decision and reason.
- `ApprovalRecord`: pending or resolved human approval.
- `AuditEvent`: append-only operational history.

## Persistence

The runtime persists through EF Core behind `IRuntimeStore`: SQLite by default (created and migrated on startup), PostgreSQL via `Database:Provider`, and an in-memory store for tests. The domain and application layers stay isolated behind the interface, so storage choices never move brand or policy logic into infrastructure code. See `docs/local-dev.md` for configuration.

## Distro Layer

The distro layer lives under `distro/`. It starts as an Ubuntu autoinstall profile with neutral services:

- `agent-runtime.service`
- `agent-policy.service`
- `agent-executor.service`
- `local-inference.service`

These names are intentionally brand-neutral. The visible product name remains controlled by branding config.

## Inference providers

`IInferenceProvider` is intentionally small in V0.1. The seeded provider is `echo-local`, but the runtime shape allows future providers for local models, hosted APIs, or organization-specific gateways.

The distro layer uses a local inference registry rather than hard-coding one model provider. `distro/models/registry.json` selects an active provider, and `distro/config/local-inference.json` exposes a stable internal API to the rest of the OS.

## Bonsai

Bonsai is PrismML's local inference model family. CieloOS can integrate Bonsai as the first small offline model tier, but should not present Bonsai as a CieloOS-owned model or couple the OS architecture to PrismML.

The first target is `prism-ml/Ternary-Bonsai-4B-gguf` through `llama.cpp`. The reason is simple: it is very small, practical on ordinary machines, and built around intelligence density. PrismML describes Bonsai as targeting multi-step reasoning, tool calling, agentic workflows, and local-device use.

Initial behavior:

- expose an OpenAI-compatible local endpoint on `127.0.0.1:8080/v1`;
- bundle weights in the Bonsai ISO edition;
- deny network access by default;
- support CPU fallback;
- allow GPU acceleration when available;
- require approval for high-risk tools such as email sending, external network access, and credential access.

Replacement rule:

```text
Agent runtime -> local inference API -> selected provider profile -> model runtime
```

Only the selected provider profile should know whether the model is PrismML Bonsai, Qwen, Gemma, Llama, Phi, or a future local provider.

## Rename safety

The brand lives in `config/branding.json` and visible UI copy. `RenameSafetyTests` scans stable backend source and project files for forbidden brand terms in namespaces and identifiers.
