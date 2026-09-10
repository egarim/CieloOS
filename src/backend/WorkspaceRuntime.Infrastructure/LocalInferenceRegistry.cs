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

    private readonly string payloadRoot;
    private readonly string configPath;

    public FileLocalInferenceRegistry(string repositoryRoot)
    {
        // The same payload sits at two depths. A git checkout keeps it under
        // distro/; a run.sh bundle and an install.sh installation flatten that away,
        // so config/ and models/ land beside the binary. Locate the config first and
        // resolve everything it points at against the SAME root — this class was the
        // last one still assuming a checkout, which is why /api/inference/status
        // reported "not configured" on every installed layout.
        var checkoutRoot = Path.Combine(repositoryRoot, "distro");
        payloadRoot = File.Exists(Path.Combine(checkoutRoot, "config", "local-inference.json"))
            ? checkoutRoot
            : repositoryRoot;
        configPath = Path.Combine(payloadRoot, "config", "local-inference.json");
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
            // registryPath is written as an absolute install path. Rebase the known
            // install roots onto wherever the payload actually is, so the shipped
            // config works unchanged in a checkout, a bundle and an installation.
            foreach (var installRoot in new[] { "/opt/workspace-runtime/", "/opt/cielo/" })
            {
                if (path.StartsWith(installRoot, StringComparison.Ordinal))
                {
                    return Path.Combine(payloadRoot, path[installRoot.Length..]);
                }
            }

            return path;
        }

        return Path.Combine(payloadRoot, path);
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
