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

// A provider a human adds at runtime (the models surface), before it has an id.
public sealed record ProviderDraft(
    string DisplayName,
    string Kind,
    string BaseUrl,
    string Model,
    IReadOnlySet<string> Capabilities,
    string Locality,
    string? ApiKey);

// The persisted, MUTABLE layer of providers + OS-default overrides: what the panel
// adds at runtime, on top of the immutable startup providers built from config.
// Kept 0600 (it holds API keys). The registry reads it live, so a newly-added
// provider is usable without a restart.
public interface IProviderConfigStore
{
    IReadOnlyList<ProviderProfile> All();
    ProviderProfile Add(ProviderDraft draft);
    bool Remove(string id);
    IReadOnlyDictionary<string, string> OsDefaults();     // capability -> providerId (overrides)
    void SetOsDefault(string capability, string providerId);
}

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
    private readonly IReadOnlyList<ProviderProfile> startup;
    private readonly IReadOnlyDictionary<string, string> startupOsDefaults; // capability -> providerId
    private readonly IUserModelConfig userConfig;
    private readonly IProviderConfigStore? dynamicStore;

    public ModelRegistry(
        IEnumerable<ProviderProfile> providers,
        IReadOnlyDictionary<string, string> osDefaults,
        IUserModelConfig userConfig,
        IProviderConfigStore? dynamicStore = null)
    {
        startup = providers.ToArray();
        startupOsDefaults = osDefaults;
        this.userConfig = userConfig;
        this.dynamicStore = dynamicStore;
    }

    // Startup providers overlaid with the runtime-added ones (dynamic wins on an id
    // clash), rebuilt each call so a just-added provider is immediately visible.
    private Dictionary<string, ProviderProfile> ById()
    {
        var byId = new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in startup)
        {
            byId[profile.Id] = profile;
        }
        if (dynamicStore is not null)
        {
            foreach (var profile in dynamicStore.All())
            {
                byId[profile.Id] = profile;
            }
        }
        return byId;
    }

    public IReadOnlyList<ProviderProfile> Providers => ById().Values.ToArray();

    public string? OsDefault(string capability) =>
        dynamicStore?.OsDefaults().GetValueOrDefault(capability) ?? startupOsDefaults.GetValueOrDefault(capability);

    public ResolvedProvider? Resolve(string capability, AgentProfile agent)
    {
        var byId = ById();

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

        // 3. OS default (runtime override wins over the startup default).
        var osId = OsDefault(capability);
        if (osId is not null && byId.TryGetValue(osId, out var osPick) && osPick.Serves(capability))
        {
            return new ResolvedProvider(capability, osPick, "os");
        }

        // 4. Any provider that can serve the capability (deterministic by id).
        var any = byId.Values.Where(profile => profile.Serves(capability)).OrderBy(profile => profile.Id, StringComparer.Ordinal).FirstOrDefault();
        return any is null ? null : new ResolvedProvider(capability, any, "fallback");
    }
}
