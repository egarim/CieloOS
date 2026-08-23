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
    private readonly IReadOnlyDictionary<string, IConsoleAgentBrain> brains;
    private readonly IConsoleAgentBrain fallback;
    private readonly ILogger<ConsoleBrainRegistry> logger;

    public ConsoleBrainRegistry(IModelRegistry models, IConsoleAgentBrain fallback, ILogger<ConsoleBrainRegistry> logger)
    {
        this.models = models;
        this.fallback = fallback;
        this.logger = logger;

        var built = new Dictionary<string, IConsoleAgentBrain>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in models.Providers.Where(provider => provider.Serves("chat")))
        {
            built[provider.Id] = new ModelConsoleBrain(
                new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
                new ModelBrainOptions { BaseUrl = provider.BaseUrl, Model = provider.Model, ApiKey = provider.ApiKey ?? "" });
        }
        brains = built;
    }

    public BrainSelection Resolve(AgentProfile agent)
    {
        var resolved = models.Resolve("chat", agent);
        if (resolved is not null && brains.TryGetValue(resolved.Profile.Id, out var brain))
        {
            logger.LogInformation("Console brain: agent '{Agent}' -> chat provider '{Provider}' ({Scope}).",
                agent.Slug, resolved.Profile.Id, resolved.Scope);
            return new BrainSelection(resolved.Profile.Id, brain);
        }

        logger.LogInformation("Console brain: no chat provider for agent '{Agent}'; using the recipe fallback.", agent.Slug);
        return new BrainSelection("recipe", fallback);
    }

    public BrainSelection ResolveDefault()
    {
        var osId = models.OsDefault("chat");
        if (osId is not null && brains.TryGetValue(osId, out var brain))
        {
            return new BrainSelection(osId, brain);
        }
        foreach (var pair in brains)
        {
            return new BrainSelection(pair.Key, pair.Value);
        }
        return new BrainSelection("recipe", fallback);
    }
}
