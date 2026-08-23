using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Tests;

// The layered, capability-based model registry: a capability (chat / vision)
// resolves to a provider via the cascade agent -> user -> OS, and only a
// provider that actually SERVES the capability is eligible.
public class ModelRegistryTests
{
    private static readonly ProviderProfile Deepseek =
        new("deepseek", "DeepSeek", "openai-compatible", "https://api.deepseek.com", "deepseek-chat",
            new HashSet<string> { "chat" }, "cloud", "key");

    private static readonly ProviderProfile Mini =
        new("gpt-4.1-mini", "Azure gpt-4.1-mini", "azure-openai", "https://x/openai/v1", "gpt-4.1-mini",
            new HashSet<string> { "chat", "vision" }, "cloud", "key");

    private static readonly ProviderProfile Bonsai =
        new("local-bonsai", "Bonsai 4B", "local-llamacpp", "http://127.0.0.1:8080/v1", "bonsai",
            new HashSet<string> { "chat" }, "on-box", null);

    private static ModelRegistry Registry(IUserModelConfig? user = null) =>
        new(new[] { Deepseek, Mini, Bonsai },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["chat"] = "deepseek", ["vision"] = "gpt-4.1-mini" },
            user ?? new EmptyUserModelConfig());

    private static AgentProfile Agent(string provider) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "A", provider, new HashSet<string> { "console" }, "a-agent");

    [Fact]
    public void Agent_override_wins()
    {
        var resolved = Registry().Resolve("chat", Agent("gpt-4.1-mini"));
        Assert.Equal("gpt-4.1-mini", resolved!.Profile.Id);
        Assert.Equal("agent", resolved.Scope);
    }

    [Fact]
    public void Unknown_agent_provider_falls_to_the_os_default()
    {
        // Seeded agents carry InferenceProvider "local-inference" (not a provider id).
        var resolved = Registry().Resolve("chat", Agent("local-inference"));
        Assert.Equal("deepseek", resolved!.Profile.Id);
        Assert.Equal("os", resolved.Scope);
    }

    [Fact]
    public void User_default_beats_the_os_default()
    {
        var user = new FakeUser(("chat", "gpt-4.1-mini"));
        var resolved = Registry(user).Resolve("chat", Agent("local-inference"));
        Assert.Equal("gpt-4.1-mini", resolved!.Profile.Id);
        Assert.Equal("user", resolved.Scope);
    }

    [Fact]
    public void A_chat_only_provider_is_never_chosen_for_vision()
    {
        // The agent asks (indirectly) for vision while its provider is chat-only.
        var resolved = Registry().Resolve("vision", Agent("deepseek"));
        Assert.Equal("gpt-4.1-mini", resolved!.Profile.Id); // the only vision-capable one
    }

    [Fact]
    public void No_provider_for_a_capability_returns_null()
    {
        var registry = new ModelRegistry(new[] { Bonsai }, new Dictionary<string, string>(), new EmptyUserModelConfig());
        Assert.Null(registry.Resolve("vision", Agent("x")));
    }

    private sealed class FakeUser : IUserModelConfig
    {
        private readonly Dictionary<string, string> defaults;
        public FakeUser(params (string Capability, string Id)[] entries) =>
            defaults = entries.ToDictionary(entry => entry.Capability, entry => entry.Id);
        public string? DefaultProviderId(Guid userId, string capability) => defaults.GetValueOrDefault(capability);
    }
}
