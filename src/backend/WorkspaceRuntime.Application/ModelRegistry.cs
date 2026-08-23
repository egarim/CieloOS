using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// A model provider tagged with the CAPABILITIES it can serve (chat / vision /
// embedding) and its LOCALITY (on-box / remote-self-hosted / cloud). The registry
// resolves a capability to a provider via a layered cascade — see model-config.md.
public sealed record ProviderProfile(
    string Id,
    string DisplayName,
    string Kind,                       // openai-compatible | azure-openai | local-llamacpp | ollama
    string BaseUrl,
    string Model,
    IReadOnlySet<string> Capabilities, // "chat", "vision", "embedding"
    string Locality,                   // "on-box" | "remote-self-hosted" | "cloud"
    string? ApiKey)                    // null for keyless/local providers
{
    public bool Serves(string capability) => Capabilities.Contains(capability);
}

// Which provider was chosen for a capability, and at which layer.
public sealed record ResolvedProvider(string Capability, ProviderProfile Profile, string Scope);

// Per-user model configuration (the "user" layer). Phase 1 ships an empty impl;
// the `models` surface (Phase 3) will let a user set their own providers/defaults.
public interface IUserModelConfig
{
    string? DefaultProviderId(Guid userId, string capability);
}

public sealed class EmptyUserModelConfig : IUserModelConfig
{
    public string? DefaultProviderId(Guid userId, string capability) => null;
}

public interface IModelRegistry
{
    // Resolve a provider for a capability via the cascade: the agent's own
    // InferenceProvider, then its owner's user default, then the OS default,
    // then any provider that can serve the capability. Null if none can.
    ResolvedProvider? Resolve(string capability, AgentProfile agent);
    IReadOnlyList<ProviderProfile> Providers { get; }
    string? OsDefault(string capability);
}

public sealed class ModelRegistry : IModelRegistry
{
    private readonly IReadOnlyDictionary<string, ProviderProfile> byId;
    private readonly IReadOnlyDictionary<string, string> osDefaults; // capability -> providerId
    private readonly IUserModelConfig userConfig;

    public ModelRegistry(
        IEnumerable<ProviderProfile> providers,
        IReadOnlyDictionary<string, string> osDefaults,
        IUserModelConfig userConfig)
    {
        byId = providers.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
        this.osDefaults = osDefaults;
        this.userConfig = userConfig;
    }

    public IReadOnlyList<ProviderProfile> Providers => byId.Values.ToArray();

    public string? OsDefault(string capability) => osDefaults.GetValueOrDefault(capability);

    public ResolvedProvider? Resolve(string capability, AgentProfile agent)
    {
        // 1. Agent override — the agent's own InferenceProvider, if it names a
        //    provider that can serve this capability.
        if (!string.IsNullOrWhiteSpace(agent.InferenceProvider)
            && byId.TryGetValue(agent.InferenceProvider, out var agentPick)
            && agentPick.Serves(capability))
        {
            return new ResolvedProvider(capability, agentPick, "agent");
        }

        // 2. User default — the owner's configured default for this capability.
        var userId = userConfig.DefaultProviderId(agent.OwnerUserId, capability);
        if (userId is not null && byId.TryGetValue(userId, out var userPick) && userPick.Serves(capability))
        {
            return new ResolvedProvider(capability, userPick, "user");
        }

        // 3. OS default.
        var osId = osDefaults.GetValueOrDefault(capability);
        if (osId is not null && byId.TryGetValue(osId, out var osPick) && osPick.Serves(capability))
        {
            return new ResolvedProvider(capability, osPick, "os");
        }

        // 4. Any provider that can serve the capability (deterministic by id).
        var any = byId.Values.Where(profile => profile.Serves(capability)).OrderBy(profile => profile.Id, StringComparer.Ordinal).FirstOrDefault();
        return any is null ? null : new ResolvedProvider(capability, any, "fallback");
    }
}
