using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// The chat brain chosen for an agent, and the provider id behind it.
public sealed record BrainSelection(string Provider, IConsoleAgentBrain Brain);

// Resolves an agent's CHAT brain through the layered model registry (agent ->
// user -> OS), caching one ModelConsoleBrain per chat-capable provider. This is
// the console/`/v1/agent` half of the capability-based provider system; the
// desktop vision path resolves the same registry for the "vision" capability.
public interface IConsoleBrainRegistry
{
    BrainSelection Resolve(AgentProfile agent);
    BrainSelection ResolveDefault();
}

public sealed class ConsoleBrainRegistry : IConsoleBrainRegistry
{
    private readonly IModelRegistry models;
    private readonly IConsoleAgentBrain fallback;
    private readonly ILogger<ConsoleBrainRegistry> logger;
    // Brains are built lazily, keyed by provider id, so a provider added at
    // runtime (the models surface) gets a brain on first use — no restart. Ids
    // are unique per provider, so a cached brain never goes stale under its id.
    private readonly ConcurrentDictionary<string, IConsoleAgentBrain> brains = new(StringComparer.OrdinalIgnoreCase);

    public ConsoleBrainRegistry(IModelRegistry models, IConsoleAgentBrain fallback, ILogger<ConsoleBrainRegistry> logger)
    {
        this.models = models;
        this.fallback = fallback;
        this.logger = logger;
    }

    private IConsoleAgentBrain BrainFor(ProviderProfile provider) =>
        brains.GetOrAdd(provider.Id, _ => new ModelConsoleBrain(
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            new ModelBrainOptions { BaseUrl = provider.BaseUrl, Model = provider.Model, ApiKey = provider.ApiKey ?? "" }));

    public BrainSelection Resolve(AgentProfile agent)
    {
        var resolved = models.Resolve("chat", agent);
        if (resolved is not null)
        {
            logger.LogInformation("Console brain: agent '{Agent}' -> chat provider '{Provider}' ({Scope}).",
                agent.Slug, resolved.Profile.Id, resolved.Scope);
            return new BrainSelection(resolved.Profile.Id, BrainFor(resolved.Profile));
        }

        logger.LogInformation("Console brain: no chat provider for agent '{Agent}'; using the fallback brain.", agent.Slug);
        return new BrainSelection("recipe", fallback);
    }

    public BrainSelection ResolveDefault()
    {
        var osId = models.OsDefault("chat");
        var provider = osId is not null
            ? models.Providers.FirstOrDefault(candidate => string.Equals(candidate.Id, osId, StringComparison.OrdinalIgnoreCase) && candidate.Serves("chat"))
            : null;
        provider ??= models.Providers.FirstOrDefault(candidate => candidate.Serves("chat"));
        return provider is not null
            ? new BrainSelection(provider.Id, BrainFor(provider))
            : new BrainSelection("recipe", fallback);
    }
}
