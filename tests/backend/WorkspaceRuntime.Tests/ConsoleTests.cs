using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The agent's hands on its own console session ride the same choke point as
// every other action: an agent may only type into a session it owns, a human
// only through sessions it or its agents own, and every keystroke is audited.
public class ConsoleTests
{
    private static RuntimePrincipal Human(IRuntimeStore store, string slug)
    {
        var user = store.Users.Single(candidate => candidate.Slug == slug);
        return new RuntimePrincipal(PrincipalKind.Human, user.Id, user.Slug, user.DisplayName);
    }

    private static RuntimePrincipal Agent(IRuntimeStore store, string slug)
    {
        var agent = store.Agents.Single(candidate => candidate.Slug == slug);
        return new RuntimePrincipal(PrincipalKind.Agent, agent.Id, agent.Slug, agent.Name);
    }

    private static AgentRuntime Runtime(IRuntimeStore store, params DesktopSession[] sessions) =>
        new(store, TestRepository.PolicyEngine(), new NoopExecutor(), TestRepository.Surfaces(), new FakeSessions(sessions));

    [Fact]
    public async Task An_agent_may_not_type_into_another_users_console()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var yuliasConsole = new DesktopSession("yulia-agent-abc", "yulia-agent", "agent-console", "running", 40000, "console");
        var runtime = Runtime(store, yuliasConsole);

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Id, jocheAgent.Id, "console", "type",
            new Dictionary<string, string> { ["id"] = "yulia-agent-abc", ["text"] = "rm -rf /" }),
            Agent(store, "joche-agent"), CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("may not operate the console of a session owned by 'yulia-agent'", result.Reason);
    }

    [Fact]
    public async Task An_agent_may_type_into_its_own_console_and_it_is_audited()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var ownConsole = new DesktopSession("joche-agent-abc", "joche-agent", "agent-console", "running", 40000, "console");
        var runtime = Runtime(store, ownConsole);

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Id, jocheAgent.Id, "console", "type",
            new Dictionary<string, string> { ["id"] = "joche-agent-abc", ["text"] = "ls -la" }),
            Agent(store, "joche-agent"), CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        // The agent acted as itself: principal is the agent, no second actor.
        Assert.Contains(store.AuditEvents, auditEvent =>
            auditEvent.Action == "console.type"
            && auditEvent.Principal == "joche-agent"
            && auditEvent.OnBehalfOf == null);
    }

    [Fact]
    public async Task A_human_typing_into_its_agents_console_is_audited_with_dual_actor()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = Human(store, "joche");
        var agentConsole = new DesktopSession("joche-agent-abc", "joche-agent", "agent-console", "running", 40000, "console");
        var runtime = Runtime(store, agentConsole);

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Subject, jocheAgent.Id, "console", "type",
            new Dictionary<string, string> { ["id"] = "joche-agent-abc", ["text"] = "whoami" }),
            joche, CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.Contains(store.AuditEvents, auditEvent =>
            auditEvent.Action == "console.type"
            && auditEvent.Principal == "joche"
            && auditEvent.OnBehalfOf == "joche-agent");
    }

    [Fact]
    public async Task Typing_without_text_is_rejected_by_the_manifest()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var ownConsole = new DesktopSession("joche-agent-abc", "joche-agent", "agent-console", "running", 40000, "console");
        var runtime = Runtime(store, ownConsole);

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Id, jocheAgent.Id, "console", "type",
            new Dictionary<string, string> { ["id"] = "joche-agent-abc" }),
            Agent(store, "joche-agent"), CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public async Task Typing_into_an_unknown_session_fails_closed()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        // No sessions exist; the console gate must deny rather than fall through.
        var runtime = Runtime(store);

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Id, jocheAgent.Id, "console", "type",
            new Dictionary<string, string> { ["id"] = "ghost-session", ["text"] = "ls" }),
            Agent(store, "joche-agent"), CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("was not found", result.Reason);
    }

    private sealed class NoopExecutor : ISandboxedToolExecutor
    {
        public Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolExecutionResult(true, "noop", null));
    }

    private sealed class FakeSessions : ISessionBackend
    {
        private readonly IReadOnlyList<DesktopSession> list;
        public FakeSessions(IReadOnlyList<DesktopSession> list) => this.list = list;
        public Task<IReadOnlyList<DesktopSession>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(list);
    }
}
