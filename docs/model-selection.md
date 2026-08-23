# Model Selection

## Model Boundary

CieloOS should not depend directly on a single model vendor. The OS owns a neutral local inference API and a provider registry. Models are replaceable profiles.

```text
Agent Runtime
  -> Local Inference API
  -> Provider Registry
  -> Active Provider Profile
  -> Model Runtime
```

The current default can change without renaming services, changing policy schemas, or rewriting agent tools.

In V0.1 the stable local inference API is hosted by the ASP.NET backend at `http://127.0.0.1:5148/v1`. The distro keeps `local-inference.service` as the future service boundary so the router can be split out later.

## First Default

The first CieloOS local model integration should use PrismML Bonsai:

```text
Model: prism-ml/Ternary-Bonsai-4B-gguf
Vendor: Prism ML, Inc.
Base model: Qwen3-4B
Format: GGUF Q2_0 ternary
Runtime: PrismML llama.cpp fork, pinned commit
Approximate packed size: 1.07 GB
License: Apache-2.0
```

## Why

CieloOS needs a small model that is agentic, not merely conversational. PrismML describes Bonsai 27B as supporting multi-step reasoning, tool calling, agentic workflows, and multimodal understanding for local devices, and publishes Bonsai 4B/8B/1.7B variants for smaller footprints.

The 4B ternary model is the right first default:

- small enough to bundle in the Bonsai ISO;
- more capable than a tiny 1.7B default;
- likely practical on ordinary CPU-only machines;
- based on Qwen3-4B, which is already a strong small agentic base;
- Apache-2.0 license.

PrismML documents that this ternary Q2_0 format requires its fork until support lands in mainline `llama.cpp`. The provider profile therefore pins the fork revision and the exact model artifact digest. The runtime manager verifies both boundaries before service activation.

## Editions

```text
Core ISO
  No model weights bundled
  Can install Bonsai later

Bonsai ISO
  Bundles prism-ml/Ternary-Bonsai-4B-gguf
  Local agent runtime works from first boot

Bonsai Pro Pack
  Optional PrismML Bonsai 8B or 27B profile
  For stronger machines
```

## Replacement Profiles

The repo includes `qwen3-4b` as a fallback provider profile to prove the abstraction. Future profiles can add Gemma, Llama, Phi, local enterprise gateways, or cloud fallback providers.

Provider profiles must declare:

- model source and license;
- runtime engine;
- OpenAI-compatible endpoint;
- approximate size;
- hardware requirements;
- capabilities such as tool use, reasoning, vision, and document analysis.

Runtime lifecycle fields are structured, not shell strings:

```json
{
  "engine": "llama.cpp",
  "openAiCompatibleEndpoint": "http://127.0.0.1:8080/v1",
  "executable": "llama-server",
  "args": ["-m", "/opt/workspace-runtime/models/provider/model.gguf", "-c", "4096"],
  "healthPath": "/health",
  "readinessTimeoutSeconds": 60,
  "networkScope": "local-only",
  "pullPolicy": "if-missing"
}
```

The optional `model.artifact` and `runtime.source` objects provide the HTTPS URL, SHA-256 digest, source repository, and full Git revision used by `workspace-model`. Providers can use another runtime manager later without changing `workspace-agent` or the stable API.

To replace the default model:

1. Add a new provider manifest under `distro/models/providers/`.
2. Add it to `distro/models/registry.json`.
3. Set `defaultProviderId` and `activeProviderId` to the new provider id.
4. Keep the endpoint local-only unless cloud fallback approval is implemented.

For the active profile, `sudo workspace-model install` performs the one-time runtime and model setup. `workspace-agent status` reports actual endpoint readiness, and `workspace-agent` starts interactive local chat.

## Ownership

Bonsai is PrismML's model family. CieloOS should describe this as an integration, keep attribution clear, and verify redistribution requirements before shipping public ISO images with bundled weights.
