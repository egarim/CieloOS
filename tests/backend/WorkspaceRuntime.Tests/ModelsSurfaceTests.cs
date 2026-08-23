using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The models surface: a human adds a provider at runtime; it persists (0600) and
// the registry resolves it live, with no restart. API keys stay in the store.
public sealed class ModelsSurfaceTests
{
    private static AgentProfile Agent() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "A", "", new HashSet<string>(), "a-agent");

    private static ProviderDraft Draft(string name, string capability = "chat", string? key = "k") =>
        new(name, "openai-compatible", "https://api.deepseek.com", "deepseek-chat",
            new HashSet<string> { capability }, "cloud", key);

    [Fact]
    public void Provider_store_persists_providers_and_defaults_across_reopen()
    {
        WithSecrets(dir =>
        {
            var store = new ProviderConfigStore(dir);
            var added = store.Add(Draft("My DeepSeek", key: "sk-secret"));
            Assert.Equal("my-deepseek", added.Id);
            store.SetOsDefault("chat", added.Id);

            var reopened = new ProviderConfigStore(dir);
            var all = reopened.All();
            Assert.Single(all);
            Assert.Equal("sk-secret", all[0].ApiKey);          // the key persists (0600 file)
            Assert.Equal(added.Id, reopened.OsDefaults()["chat"]);
        });
    }

    [Fact]
    public void Ids_are_deduplicated()
    {
        WithSecrets(dir =>
        {
            var store = new ProviderConfigStore(dir);
            Assert.Equal("acme", store.Add(Draft("Acme")).Id);
            Assert.Equal("acme-2", store.Add(Draft("Acme")).Id);
        });
    }

    [Fact]
    public void Removing_a_provider_drops_it_and_clears_its_default()
    {
        WithSecrets(dir =>
        {
            var store = new ProviderConfigStore(dir);
            var provider = store.Add(Draft("Temp"));
            store.SetOsDefault("chat", provider.Id);

            Assert.True(store.Remove(provider.Id));
            Assert.Empty(store.All());
            Assert.False(store.OsDefaults().ContainsKey("chat"));
            Assert.False(store.Remove(provider.Id)); // already gone
        });
    }

    [Fact]
    public void Registry_resolves_a_runtime_added_provider_without_a_restart()
    {
        WithSecrets(dir =>
        {
            var dynamicStore = new ProviderConfigStore(dir);
            var registry = new ModelRegistry(
                Array.Empty<ProviderProfile>(), new Dictionary<string, string>(), new EmptyUserModelConfig(), dynamicStore);
            var agent = Agent();

            Assert.Null(registry.Resolve("chat", agent)); // provider-free

            var provider = dynamicStore.Add(Draft("Runtime Chat"));
            var resolved = registry.Resolve("chat", agent);
            Assert.NotNull(resolved);
            Assert.Equal(provider.Id, resolved!.Profile.Id);

            dynamicStore.SetOsDefault("chat", provider.Id);
            Assert.Equal(provider.Id, registry.OsDefault("chat"));
        });
    }

    [Fact]
    public void A_dynamic_provider_overrides_a_startup_provider_with_the_same_id()
    {
        WithSecrets(dir =>
        {
            var startup = new[]
            {
                new ProviderProfile("deepseek", "Startup DS", "openai-compatible", "https://old", "old-model",
                    new HashSet<string> { "chat" }, "cloud", "oldkey")
            };
            var dynamicStore = new ProviderConfigStore(dir);
            var registry = new ModelRegistry(
                startup, new Dictionary<string, string> { ["chat"] = "deepseek" }, new EmptyUserModelConfig(), dynamicStore);

            Assert.Single(registry.Providers, profile => profile.Id == "deepseek" && profile.Model == "old-model");

            // DisplayName "DeepSeek" slugs to id "deepseek" — it overrides the startup one.
            dynamicStore.Add(new ProviderDraft("DeepSeek", "openai-compatible", "https://new", "new-model",
                new HashSet<string> { "chat" }, "cloud", "newkey"));

            Assert.Single(registry.Providers, profile => profile.Id == "deepseek" && profile.Model == "new-model");
        });
    }

    private static void WithSecrets(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lunos-models-{Guid.NewGuid():N}");
        try
        {
            body(dir);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
