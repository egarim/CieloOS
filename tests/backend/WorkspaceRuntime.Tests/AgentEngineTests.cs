using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The seam that lets something other than our own loop do the work
// (docs/agent-engines.md). The interesting failure is not that an engine misbehaves
// — it is that the seam quietly stops being the way through, and an endpoint goes
// back to driving the loop directly. That reads as a harmless simplification in a
// diff and takes the policy decision, the metering and the engine attribution with
// it.
public class AgentEngineTests
{
    private static string Program() => File.ReadAllText(Path.Combine(
        TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Api", "Program.cs"));

    [Fact]
    public void Endpoints_drive_the_engine_and_not_the_loop()
    {
        var program = Program();

        // ConsoleAgentLoop stays registered — CieloConsoleEngine is built from it.
        Assert.Contains("builder.Services.AddSingleton<ConsoleAgentLoop>();", program);
        Assert.Contains("builder.Services.AddSingleton<IAgentEngine, CieloConsoleEngine>();", program);

        // ...and that registration is the ONLY place it is named. An endpoint that
        // takes it as a handler parameter is driving the loop directly again.
        Assert.Equal(1, program.Split("ConsoleAgentLoop").Length - 1);

        // All three console call sites go through the engine.
        Assert.Equal(3, program.Split("engine.RunAsync(").Length - 1);

        // DesktopAgentLoop is deliberately NOT behind this seam yet: a desktop run
        // resolves a second, vision brain under separate consent, so it is its own
        // conversion rather than a line in this one. Stated here so the single
        // remaining direct `loop.RunAsync(` is a decision on the record instead of
        // something this guard quietly tolerates.
        var direct = program.Split("loop.RunAsync(").Length - 1;
        Assert.True(direct == 1, $"Expected exactly one direct loop call (the desktop one); found {direct}.");
        Assert.Contains("DesktopAgentLoop loop", program);
    }

    [Fact]
    public void An_engine_run_carries_no_model_and_no_key()
    {
        // Which model a run uses is the host's decision. The moment an engine is
        // handed a model or a credential, "the benchmark pinned the model" becomes a
        // claim about a config file rather than a fact about the run, and a foreign
        // engine can bill somebody else's budget.
        var fields = typeof(EngineRun).GetProperties().Select(property => property.Name).ToList();

        Assert.Contains("SessionId", fields);
        Assert.Contains("Principal", fields);
        Assert.Contains("AgentId", fields);
        Assert.DoesNotContain(fields, name => name.Contains("Model", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Brain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_engine_is_identifiable_in_the_audit()
    {
        // An audit trail that cannot say which engine acted answers nothing once
        // there is more than one.
        Assert.NotNull(typeof(IAgentEngine).GetProperty(nameof(IAgentEngine.Id)));
        Assert.Equal("cielo-console", WorkspaceRuntime.Infrastructure.CieloConsoleEngine.EngineId);
    }

    // A brain registry that always hands back the deterministic recipe, and a model
    // registry that knows no providers — so the run is real and unmetered, which is
    // exactly what a recipe run has always been.
    private sealed class RecipeBrains : IConsoleBrainRegistry
    {
        public BrainSelection Resolve(AgentProfile agent) => new("recipe", new RecipeConsoleBrain());
        public BrainSelection ResolveDefault() => Resolve(null!);
    }

    private sealed class NoProviders : IModelRegistry
    {
        public ResolvedProvider? Resolve(string capability, AgentProfile agent) => null;
        public IReadOnlyList<ProviderProfile> Providers => Array.Empty<ProviderProfile>();
        public string? OsDefault(string capability) => null;
    }

    // The guards above check the seam is THERE. This checks work still comes out of
    // it: the same audited console.type commands the loop produced before, now
    // reached through the engine instead of by calling the loop directly.
    [Fact]
    public async Task The_engine_does_the_same_audited_work_the_loop_did()
    {
        var store = new InMemoryRuntimeStore();
        var agent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var owner = store.Users.Single(candidate => candidate.Slug == "joche");
        var console = new FakeConsoleBackend();
        var runtime = new AgentRuntime(
            store, TestRepository.PolicyEngine(), new ConsoleSurfaceExecutor(console), TestRepository.Surfaces(),
            new FakeSessions(new DesktopSession("joche-agent-abc", "joche-agent", "agent-console", "running", 40000, "console")));

        var engine = new CieloConsoleEngine(
            new ConsoleAgentLoop(runtime, console), new RecipeBrains(), store, new NoProviders());

        var steps = new List<ConsoleLoopStep>();
        var result = await engine.RunAsync(
            new EngineRun("joche-agent-abc", "note the task", 6,
                new RuntimePrincipal(PrincipalKind.Agent, agent.Id, agent.Slug, agent.Name), owner.Id, agent.Id),
            onStep: step => { steps.Add(step); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, console.Typed.Count);
        Assert.Equal(2, store.AuditEvents.Count(auditEvent => auditEvent.Action == "console.type"));
        // Progress still streams; the portal shows the agent working from this.
        Assert.NotEmpty(steps);
    }

    // Copies of the console-loop fakes, kept local like every other test file here:
    // a shared fixture is one edit away from changing what a dozen tests believe.
    private sealed class FakeConsoleBackend : IConsoleBackend
    {
        public List<string> Typed { get; } = new();

        public Task<ConsoleView> CaptureAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(new ConsoleView(sessionId, "root@host:~#", true, null));

        public Task<ConsoleActionResult> TypeAsync(string sessionId, string text, bool submit, CancellationToken cancellationToken)
        {
            Typed.Add(text);
            return Task.FromResult(new ConsoleActionResult(true, "root@host:~#", "ok"));
        }
    }

    private sealed class FakeSessions : ISessionBackend
    {
        private readonly IReadOnlyList<DesktopSession> list;
        public FakeSessions(params DesktopSession[] list) => this.list = list;
        public Task<IReadOnlyList<DesktopSession>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(list);
        public Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
