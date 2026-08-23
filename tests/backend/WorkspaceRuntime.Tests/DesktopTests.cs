using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The agent's hands on its own desktop session ride the same choke point as the
// console: an agent may only click/type on a session it owns, a human only
// through sessions it or its agents own, and every pointer/keystroke is audited
// with its exact coordinates/text (the desktop input ledger).
public class DesktopTests
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
    public async Task An_agent_may_not_click_on_another_users_desktop()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var yuliasDesktop = new DesktopSession("yulia-agent-abc", "yulia-agent", "agent-desktop", "running", 40000, "desktop");
        var runtime = Runtime(store, yuliasDesktop);

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Id, jocheAgent.Id, "desktop", "click",
            new Dictionary<string, string> { ["id"] = "yulia-agent-abc", ["x"] = "10", ["y"] = "10" }),
            Agent(store, "joche-agent"), CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("may not operate the desktop of a session owned by 'yulia-agent'", result.Reason);
    }

    [Fact]
    public async Task An_agent_may_click_its_own_desktop_and_the_coords_are_audited()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var ownDesktop = new DesktopSession("joche-agent-abc", "joche-agent", "agent-desktop", "running", 40000, "desktop");
        var runtime = Runtime(store, ownDesktop);

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Id, jocheAgent.Id, "desktop", "click",
            new Dictionary<string, string> { ["id"] = "joche-agent-abc", ["x"] = "100", ["y"] = "200" }),
            Agent(store, "joche-agent"), CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.Contains(store.AuditEvents, auditEvent =>
            auditEvent.Action == "desktop.click"
            && auditEvent.Principal == "joche-agent"
            && auditEvent.OnBehalfOf == null
            && auditEvent.Detail.Contains("(100, 200)"));
    }

    [Fact]
    public async Task A_human_typing_on_its_agents_desktop_is_audited_with_dual_actor()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = Human(store, "joche");
        var agentDesktop = new DesktopSession("joche-agent-abc", "joche-agent", "agent-desktop", "running", 40000, "desktop");
        var runtime = Runtime(store, agentDesktop);

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Subject, jocheAgent.Id, "desktop", "type",
            new Dictionary<string, string> { ["id"] = "joche-agent-abc", ["text"] = "hello" }),
            joche, CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.Contains(store.AuditEvents, auditEvent =>
            auditEvent.Action == "desktop.type"
            && auditEvent.Principal == "joche"
            && auditEvent.OnBehalfOf == "joche-agent"
            && auditEvent.Detail.Contains("hello"));
    }

    [Fact]
    public async Task Clicking_without_coordinates_is_rejected_by_the_manifest()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var ownDesktop = new DesktopSession("joche-agent-abc", "joche-agent", "agent-desktop", "running", 40000, "desktop");
        var runtime = Runtime(store, ownDesktop);

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Id, jocheAgent.Id, "desktop", "click",
            new Dictionary<string, string> { ["id"] = "joche-agent-abc" }),
            Agent(store, "joche-agent"), CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
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
