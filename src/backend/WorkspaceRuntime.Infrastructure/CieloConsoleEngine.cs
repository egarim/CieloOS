using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// Engine #1: the loop CieloOS has always had, now behind the seam rather than
// welded to the endpoints.
//
// It resolves its own brain and its own billing identity from the agent, which is
// the point of the seam: the caller asks for a goal to be pursued and does not
// decide how, or with which model. Every call site used to do both, identically,
// which is how the choice would have drifted apart the moment there were two
// engines.
public sealed class CieloConsoleEngine : IAgentEngine
{
    public const string EngineId = "cielo-console";

    private readonly ConsoleAgentLoop loop;
    private readonly IConsoleBrainRegistry brains;
    private readonly IRuntimeStore store;
    private readonly IModelRegistry models;

    public CieloConsoleEngine(ConsoleAgentLoop loop, IConsoleBrainRegistry brains, IRuntimeStore store, IModelRegistry models)
    {
        this.loop = loop;
        this.brains = brains;
        this.store = store;
        this.models = models;
    }

    public string Id => EngineId;

    public Task<ConsoleLoopResult> RunAsync(
        EngineRun run,
        Func<ConsoleLoopStep, Task>? onStep,
        CancellationToken cancellationToken)
    {
        var selection = brains.Resolve(store.GetAgent(run.AgentId));
        return loop.RunAsync(
            run.SessionId,
            run.Goal,
            run.MaxSteps,
            run.Principal,
            run.UserId,
            run.AgentId,
            selection.Brain,
            cancellationToken,
            onStep,
            Billed(selection.Provider));
    }

    // Null when the provider behind the brain is not one the registry knows —
    // the run still happens, it is simply not metered, which is what the fallback
    // and recipe brains have always done.
    private ModelIdentity? Billed(string providerId)
    {
        var provider = models.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        return provider is null ? null : new ModelIdentity(provider.Id, provider.Model, provider.Locality);
    }
}
