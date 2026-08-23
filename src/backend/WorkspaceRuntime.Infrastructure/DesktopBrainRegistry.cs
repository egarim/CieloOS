using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// Resolves an agent's DESKTOP brain through the layered model registry: a `chat`
// provider drives AT-SPI text grounding (the default, on-box-capable), and a
// `vision` provider — if the agent/user/OS has one — is the screenshot fallback.
// The two are assembled into a HybridDesktopBrain (AT-SPI first, vision only when
// the tree can't locate the target). Cached per (chat, vision) provider pair.
public interface IDesktopBrainRegistry
{
    IDesktopAgentBrain Resolve(AgentProfile agent);
}

public sealed class DesktopBrainRegistry : IDesktopBrainRegistry
{
    private readonly IModelRegistry models;
    private readonly ILoggerFactory loggers;
    private readonly ConcurrentDictionary<string, IDesktopAgentBrain> cache = new();

    public DesktopBrainRegistry(IModelRegistry models, ILoggerFactory loggers)
    {
        this.models = models;
        this.loggers = loggers;
    }

    public IDesktopAgentBrain Resolve(AgentProfile agent)
    {
        var chat = models.Resolve("chat", agent);
        var vision = models.Resolve("vision", agent);
        var key = $"{chat?.Profile.Id ?? "none"}|{vision?.Profile.Id ?? "none"}";
        return cache.GetOrAdd(key, _ =>
        {
            if (chat is null)
            {
                // No chat provider at all — cannot ground; report cleanly.
                return new NoVisionDesktopBrain();
            }
            var textBrain = new ModelDesktopBrain(Http(60), Options(chat.Profile), useVision: false);
            IDesktopAgentBrain? visionBrain = vision is null
                ? null
                : new ModelDesktopBrain(Http(90), Options(vision.Profile), useVision: true);
            return new HybridDesktopBrain(textBrain, visionBrain, loggers.CreateLogger<HybridDesktopBrain>());
        });
    }

    private static HttpClient Http(int seconds) => new() { Timeout = TimeSpan.FromSeconds(seconds) };

    private static ModelBrainOptions Options(ProviderProfile profile) =>
        new() { BaseUrl = profile.BaseUrl, Model = profile.Model, ApiKey = profile.ApiKey ?? "" };
}
