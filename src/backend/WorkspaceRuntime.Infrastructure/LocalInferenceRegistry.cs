using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

public sealed class FileLocalInferenceRegistry : ILocalInferenceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string repositoryRoot;
    private readonly string configPath;

    public FileLocalInferenceRegistry(string repositoryRoot)
    {
        this.repositoryRoot = repositoryRoot;
        configPath = Path.Combine(repositoryRoot, "distro", "config", "local-inference.json");
    }

    public LocalInferenceStatus GetStatus()
    {
        if (!File.Exists(configPath))
        {
            return new LocalInferenceStatus(false, null, null, null, Array.Empty<LocalInferenceRegistryEntry>());
        }

        var config = ReadConfig();
        var registry = ReadRegistry(config);
        ValidateRegistry(registry);
        var activeProviderId = string.IsNullOrWhiteSpace(config.ActiveProviderId)
            ? registry.DefaultProviderId
            : config.ActiveProviderId;
        var activeProvider = ReadProvider(registry, activeProviderId);
        ValidateProvider(activeProviderId, activeProvider);

        return new LocalInferenceStatus(
            true,
            activeProviderId,
            config.Api.InternalBaseUrl,
            activeProvider,
            registry.Providers);
    }

    public LocalInferenceProviderProfile GetActiveProvider() =>
        GetStatus().ActiveProvider
        ?? throw new InvalidOperationException("Local inference is not configured.");

    private LocalInferenceConfig ReadConfig() =>
        ReadJson<LocalInferenceConfig>(configPath);

    private LocalInferenceRegistry ReadRegistry(LocalInferenceConfig config)
    {
        var registryPath = ResolveDistroPath(config.RegistryPath);
        return ReadJson<LocalInferenceRegistry>(registryPath);
    }

    private LocalInferenceProviderProfile ReadProvider(LocalInferenceRegistry registry, string providerId)
    {
        var entry = registry.Providers.SingleOrDefault(provider => provider.Id == providerId)
            ?? throw new InvalidOperationException($"Local inference provider '{providerId}' is not registered.");

        return ReadJson<LocalInferenceProviderProfile>(ResolveDistroPath(entry.Manifest));
    }

    private static void ValidateRegistry(LocalInferenceRegistry registry)
    {
        if (registry.Schema != 1)
        {
            throw new InvalidOperationException("Unsupported local inference registry schema.");
        }

        var duplicateId = registry.Providers
            .GroupBy(provider => provider.Id)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;

        if (!string.IsNullOrWhiteSpace(duplicateId))
        {
            throw new InvalidOperationException($"Local inference provider id '{duplicateId}' is duplicated.");
        }

        if (!registry.Providers.Any(provider => provider.Id == registry.DefaultProviderId))
        {
            throw new InvalidOperationException($"Default local inference provider '{registry.DefaultProviderId}' is not registered.");
        }
    }

    private static void ValidateProvider(string expectedProviderId, LocalInferenceProviderProfile provider)
    {
        if (provider.Schema != 1)
        {
            throw new InvalidOperationException($"Unsupported provider schema for '{expectedProviderId}'.");
        }

        if (!string.Equals(provider.ProviderId, expectedProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Provider manifest id '{provider.ProviderId}' does not match registry id '{expectedProviderId}'.");
        }

        if (!Uri.TryCreate(provider.Runtime.OpenAiCompatibleEndpoint, UriKind.Absolute, out var endpoint) ||
            !IsLoopbackHost(endpoint.Host))
        {
            throw new InvalidOperationException($"Provider '{expectedProviderId}' must expose a local OpenAI-compatible endpoint.");
        }

        if (!string.Equals(provider.Runtime.NetworkScope, "local-only", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Provider '{expectedProviderId}' must be local-only unless cloud fallback approval is implemented.");
        }

        if (string.IsNullOrWhiteSpace(provider.Runtime.Executable))
        {
            throw new InvalidOperationException($"Provider '{expectedProviderId}' is missing a runtime executable.");
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private string ResolveDistroPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            const string installRoot = "/opt/workspace-runtime/";
            if (path.StartsWith(installRoot, StringComparison.Ordinal))
            {
                return Path.Combine(repositoryRoot, "distro", path[installRoot.Length..]);
            }

            return path;
        }

        return Path.Combine(repositoryRoot, "distro", path);
    }

    private static T ReadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Local inference configuration file was not found.", path);
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Could not parse local inference configuration '{path}'.");
    }
}
