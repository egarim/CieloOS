using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

public interface IPolicyEngine
{
    PolicyEvaluation Evaluate(PlatformUser user, AgentProfile agent, ToolRequest request);
}

// The former DefaultPolicyEngine hardcoded surface rules in C# while the JSON
// manifests declared policy nothing enforced. ManifestPolicyEngine (SurfaceContracts.cs)
// is now the only policy source of truth; there is deliberately no code-defined fallback.
