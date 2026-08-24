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
    // cloudVisionAllowed reflects the session's "screenshot leaves the machine"
    // consent; a CLOUD vision provider is only wired as the fallback when it is
    // true (on-box vision needs no consent).
    IDesktopAgentBrain Resolve(AgentProfile agent, bool cloudVisionAllowed);
}

public sealed class DesktopBrainRegistry : IDesktopBrainRegistry
{
    private readonly IModelRegistry models;
    private readonly ILoggerFactory loggers;
    private readonly ConcurrentDictionary<string, IDesktopAgentBrain> cache = new();

    // Desktop runs spend on models exactly as console runs do — the vision path
    // especially, since a screenshot is a large prompt. Metering only the console
    // would have left an endpoint through which every ceiling could be bypassed.
    private readonly ITokenLedger? ledger;

    public DesktopBrainRegistry(IModelRegistry models, ILoggerFactory loggers, ITokenLedger? ledger = null)
    {
        this.models = models;
        this.loggers = loggers;
        this.ledger = ledger;
    }

    public IDesktopAgentBrain Resolve(AgentProfile agent, bool cloudVisionAllowed)
    {
        var chat = models.Resolve("chat", agent);
        var vision = models.Resolve("vision", agent);

        // "Screenshot leaves the machine" gate: a CLOUD vision provider is only
        // wired when the session has consented. Without consent the brain stays
        // AT-SPI-only, so nothing leaves the box by default.
        if (vision is not null
            && string.Equals(vision.Profile.Locality, "cloud", StringComparison.OrdinalIgnoreCase)
            && !cloudVisionAllowed)
        {
            vision = null;
        }

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

    private HttpClient Http(int seconds) =>
        ledger is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) }
            : new HttpClient(new TokenMeteringHandler(ledger, new HttpClientHandler())) { Timeout = TimeSpan.FromSeconds(seconds) };

    private static ModelBrainOptions Options(ProviderProfile profile) =>
        new()
        {
            BaseUrl = profile.BaseUrl,
            Model = profile.Model,
            ApiKey = profile.ApiKey ?? "",
            // The vision brain gets the VISION provider's identity here, which is
            // what makes a cloud vision call inside an on-box run countable.
            ProviderId = profile.Id,
            Locality = profile.Locality
        };
}
