using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The loop drives the console through the SAME policy-checked bus a human uses:
// each decided keystroke batch is a `console.type`, ownership-gated and audited.
public class ConsoleLoopTests
{
    private static RuntimePrincipal Agent(IRuntimeStore store, string slug)
    {
        var agent = store.Agents.Single(candidate => candidate.Slug == slug);
        return new RuntimePrincipal(PrincipalKind.Agent, agent.Id, agent.Slug, agent.Name);
    }

    [Fact]
    public async Task The_loop_types_through_the_bus_until_the_brain_says_done()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var console = new FakeConsoleBackend();
        var runtime = new AgentRuntime(
            store, TestRepository.PolicyEngine(), new ConsoleSurfaceExecutor(console), TestRepository.Surfaces(),
            new FakeSessions(new DesktopSession("joche-agent-abc", "joche-agent", "agent-console", "running", 40000, "console")));
        var loop = new ConsoleAgentLoop(runtime, console);

        var result = await loop.RunAsync(
            "joche-agent-abc", "note the task", maxSteps: 6,
            Agent(store, "joche-agent"), joche.Id, jocheAgent.Id, new RecipeConsoleBrain(), CancellationToken.None);

        Assert.True(result.Completed);
        // The recipe's two commands were actually typed via the backend...
        Assert.Contains(console.Typed, text => text.Contains("AGENT_LOG.md"));
        Assert.Equal(2, console.Typed.Count);
        // ...and each landed on the audit trail as a console.type with its text.
        Assert.Equal(2, store.AuditEvents.Count(auditEvent => auditEvent.Action == "console.type"));
        Assert.Contains(store.AuditEvents, auditEvent =>
            auditEvent.Action == "console.type" && auditEvent.Detail.Contains("AGENT_LOG.md"));
    }

    [Fact]
    public async Task The_loop_stops_when_a_keystroke_is_denied_by_ownership()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var console = new FakeConsoleBackend();
        // The session belongs to yulia's agent; joche's agent must not drive it.
        var runtime = new AgentRuntime(
            store, TestRepository.PolicyEngine(), new ConsoleSurfaceExecutor(console), TestRepository.Surfaces(),
            new FakeSessions(new DesktopSession("yulia-agent-xyz", "yulia-agent", "agent-console", "running", 40000, "console")));
        var loop = new ConsoleAgentLoop(runtime, console);

        var result = await loop.RunAsync(
            "yulia-agent-xyz", "snoop", maxSteps: 6,
            Agent(store, "joche-agent"), joche.Id, jocheAgent.Id, new RecipeConsoleBrain(), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("Blocked", result.StopReason);
        // Nothing was actually typed into the container.
        Assert.Empty(console.Typed);
        Assert.Contains(result.Steps, step => step.Decision == "Deny");
    }

    [Fact]
    public async Task The_loop_stops_when_the_agent_repeats_a_command()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var console = new FakeConsoleBackend();
        var runtime = new AgentRuntime(
            store, TestRepository.PolicyEngine(), new ConsoleSurfaceExecutor(console), TestRepository.Surfaces(),
            new FakeSessions(new DesktopSession("joche-agent-abc", "joche-agent", "agent-console", "running", 40000, "console")));
        var loop = new ConsoleAgentLoop(runtime, console);

        // A brain that keeps typing the same command and never says done.
        var result = await loop.RunAsync(
            "joche-agent-abc", "loop forever", maxSteps: 8,
            Agent(store, "joche-agent"), joche.Id, jocheAgent.Id, new StuckBrain("ls -la"), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("repeated", result.StopReason);
        // It ran the command once, then caught the repeat — not eight times.
        Assert.Single(console.Typed);
    }

    private sealed class StuckBrain : IConsoleAgentBrain
    {
        private readonly string command;
        public StuckBrain(string command) => this.command = command;
        public Task<ConsoleAgentAction> DecideAsync(string goal, string screen, IReadOnlyList<string> history, int step, CancellationToken cancellationToken) =>
            Task.FromResult(new ConsoleAgentAction(false, command, true, "stuck"));
    }

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
    }
}
