using Microsoft.Extensions.Logging;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// The brain chosen for a given agent's InferenceProvider.
public sealed record BrainSelection(string Provider, IConsoleAgentBrain Brain);

// Resolves an agent's InferenceProvider string to a console brain, so different
// agents can be driven by different model providers (e.g. DeepSeek or Azure
// OpenAI gpt-4.1-mini) on the same runtime. Unknown/unset providers fall back to
// the configured default provider, then to the deterministic recipe brain.
public interface IConsoleBrainRegistry
{
    BrainSelection Resolve(string? providerName);
    IReadOnlyCollection<string> ProviderNames { get; }
}

public sealed class ConsoleBrainRegistry : IConsoleBrainRegistry
{
    private readonly IReadOnlyDictionary<string, IConsoleAgentBrain> brains;
    private readonly IConsoleAgentBrain fallback;
    private readonly string defaultProvider;
    private readonly ILogger<ConsoleBrainRegistry> logger;

    public ConsoleBrainRegistry(
        IReadOnlyDictionary<string, IConsoleAgentBrain> brains,
        IConsoleAgentBrain fallback,
        string defaultProvider,
        ILogger<ConsoleBrainRegistry> logger)
    {
        this.brains = brains;
        this.fallback = fallback;
        this.defaultProvider = defaultProvider;
        this.logger = logger;
    }

    public IReadOnlyCollection<string> ProviderNames => brains.Keys.ToArray();

    public BrainSelection Resolve(string? providerName)
    {
        var requested = string.IsNullOrWhiteSpace(providerName) ? "" : providerName.Trim();

        if (brains.TryGetValue(requested, out var direct))
        {
            logger.LogInformation("Console brain: using provider '{Provider}'.", requested);
            return new BrainSelection(requested, direct);
        }

        if (!string.IsNullOrEmpty(defaultProvider) && brains.TryGetValue(defaultProvider, out var def))
        {
            logger.LogInformation(
                "Console brain: provider '{Requested}' not configured; using default '{Default}'.",
                requested, defaultProvider);
            return new BrainSelection(defaultProvider, def);
        }

        logger.LogInformation("Console brain: no model provider for '{Requested}'; using the recipe fallback.", requested);
        return new BrainSelection("recipe", fallback);
    }
}
