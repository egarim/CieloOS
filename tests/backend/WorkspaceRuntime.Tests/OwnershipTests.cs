using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public class OwnershipTests
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

    [Fact]
    public void A_human_can_reach_only_its_own_home_and_its_agents_homes()
    {
        var store = new InMemoryRuntimeStore();
        var joche = Human(store, "joche");

        Assert.True(Ownership.CanAccessHome(joche, "joche", store));
        Assert.True(Ownership.CanAccessHome(joche, "joche-agent", store));
        Assert.False(Ownership.CanAccessHome(joche, "yulia", store));
        Assert.False(Ownership.CanAccessHome(joche, "yulia-agent", store));
    }

    [Fact]
    public void An_agent_can_reach_only_its_own_home()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = Agent(store, "joche-agent");

        Assert.True(Ownership.CanAccessHome(jocheAgent, "joche-agent", store));
        Assert.False(Ownership.CanAccessHome(jocheAgent, "joche", store));
        Assert.False(Ownership.CanAccessHome(jocheAgent, "yulia-agent", store));
    }

    [Fact]
    public async Task An_agent_may_not_act_as_another_agent()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = Runtime(store);
        var jocheAgentPrincipal = Agent(store, "joche-agent");
        var yuliaAgent = store.Agents.Single(candidate => candidate.Slug == "yulia-agent");
        var yulia = store.Users.Single(candidate => candidate.Slug == "yulia");

        // joche's agent tries to act as yulia's agent.
        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            yulia.Id, yuliaAgent.Id, "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["address"] = "C1", ["value"] = "1" }), jocheAgentPrincipal, CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("only act as itself", result.Reason);
    }

    [Fact]
    public async Task A_human_may_not_act_through_an_agent_it_does_not_own()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = Runtime(store);
        var joche = Human(store, "joche");
        var yuliaAgent = store.Agents.Single(candidate => candidate.Slug == "yulia-agent");
        var yulia = store.Users.Single(candidate => candidate.Slug == "yulia");

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            yulia.Id, yuliaAgent.Id, "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["address"] = "C1", ["value"] = "1" }), joche, CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("agents it owns", result.Reason);
    }

    [Fact]
    public async Task A_human_may_act_through_its_own_agent()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = Runtime(store);
        var joche = Human(store, "joche");
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Subject, jocheAgent.Id, "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["address"] = "C1", ["value"] = "7" }), joche, CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        // The audit records the human who acted, not the agent.
        Assert.Contains(store.AuditEvents, auditEvent => auditEvent.Action == "spreadsheet.set-cell" && auditEvent.Principal == "joche");
    }

    [Fact]
    public async Task A_human_may_not_open_a_session_over_another_users_home_on_the_raw_bus()
    {
        // Regression: the ownership gate must live at the choke point, so the
        // raw tool-request path enforces it too — not only the HTTP handler.
        var store = new InMemoryRuntimeStore();
        var runtime = Runtime(store);
        var joche = Human(store, "joche");
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Subject, jocheAgent.Id, "session", "create",
            new Dictionary<string, string> { ["owner"] = "yulia", ["profile"] = "agent-console" }), joche, CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("may not open a session over 'yulia'", result.Reason);
    }

    [Fact]
    public async Task A_human_may_open_a_session_over_its_own_agents_home()
    {
        var store = new InMemoryRuntimeStore();
        // No real podman here; the executor is irrelevant because the ownership
        // check runs before execution. Use a no-op session backend.
        var runtime = new AgentRuntime(store, TestRepository.PolicyEngine(), new NoopExecutor(), TestRepository.Surfaces());
        var joche = Human(store, "joche");
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Subject, jocheAgent.Id, "session", "create",
            new Dictionary<string, string> { ["owner"] = "joche-agent", ["profile"] = "agent-console" }), joche, CancellationToken.None);

        Assert.NotEqual(PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public async Task A_user_may_not_destroy_a_session_owned_by_another()
    {
        var store = new InMemoryRuntimeStore();
        var joche = Human(store, "joche");
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var yuliasSession = new DesktopSession("yulia-abc", "yulia", "agent-console", "running", 40000, "console");
        var runtime = new AgentRuntime(store, TestRepository.PolicyEngine(), new NoopExecutor(), TestRepository.Surfaces(),
            new FakeSessions(new[] { yuliasSession }));

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Subject, jocheAgent.Id, "session", "destroy",
            new Dictionary<string, string> { ["id"] = "yulia-abc" }), joche, CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("may not destroy a session owned by 'yulia'", result.Reason);
    }

    [Fact]
    public async Task A_human_may_not_resolve_another_users_approval()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = Runtime(store);
        var jocheAgent = Agent(store, "joche-agent");
        var yulia = Human(store, "yulia");
        var joche = Human(store, "joche");
        var jocheAgentProfile = store.Agents.Single(candidate => candidate.Slug == "joche-agent");

        // joche's agent submits an approval-gated action; the approval is owned by joche.
        var pending = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Subject, jocheAgentProfile.Id, "spreadsheet", "clear", new Dictionary<string, string>()), jocheAgent, CancellationToken.None);
        Assert.Equal(PolicyDecision.RequireApproval, pending.Decision);

        // yulia may not resolve it.
        await Assert.ThrowsAsync<ApprovalOwnershipException>(() =>
            runtime.ResolveApprovalAsync(pending.Approval!.Id, approved: true, pending.Approval.RequestHash, yulia, null, CancellationToken.None));

        // joche (the owner) may.
        var approved = await runtime.ResolveApprovalAsync(pending.Approval!.Id, approved: true, pending.Approval.RequestHash, joche, null, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, approved.Decision);
    }

    [Fact]
    public async Task A_human_inhabiting_its_own_agent_is_allowed_and_audited_with_dual_actor()
    {
        var store = new InMemoryRuntimeStore();
        var joche = Human(store, "joche");
        var jocheAgentProfile = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var jocheSession = new DesktopSession("joche-agent-abc", "joche-agent", "agent-console", "running", 40000, "console");
        var runtime = new AgentRuntime(store, TestRepository.PolicyEngine(), new NoopExecutor(), TestRepository.Surfaces(),
            new FakeSessions(new[] { jocheSession }));

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Subject, jocheAgentProfile.Id, "session", "inhabit",
            new Dictionary<string, string> { ["id"] = "joche-agent-abc", ["mode"] = "become" }), joche, CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        // Dual-actor: the human acted, on behalf of the agent.
        Assert.Contains(store.AuditEvents, auditEvent =>
            auditEvent.Action == "session.inhabit"
            && auditEvent.Principal == "joche"
            && auditEvent.OnBehalfOf == "joche-agent"
            && auditEvent.SessionId == "joche-agent-abc");
    }

    [Fact]
    public async Task A_human_may_not_inhabit_another_users_session()
    {
        var store = new InMemoryRuntimeStore();
        var joche = Human(store, "joche");
        var jocheAgentProfile = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var yuliaSession = new DesktopSession("yulia-abc", "yulia", "agent-console", "running", 40000, "console");
        var runtime = new AgentRuntime(store, TestRepository.PolicyEngine(), new NoopExecutor(), TestRepository.Surfaces(),
            new FakeSessions(new[] { yuliaSession }));

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Subject, jocheAgentProfile.Id, "session", "inhabit",
            new Dictionary<string, string> { ["id"] = "yulia-abc", ["mode"] = "shadow" }), joche, CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("may not inhabit a session owned by 'yulia'", result.Reason);
    }

    [Fact]
    public void The_shared_workspace_owner_is_the_root_user_for_both_a_user_and_its_agent()
    {
        var store = new InMemoryRuntimeStore();

        // A user maps to itself; its agent maps to the same user; a stranger slug
        // passes through unchanged (fail-safe, no cross-user shared mount).
        Assert.Equal("joche", Ownership.RootUserSlug("joche", store));
        Assert.Equal("joche", Ownership.RootUserSlug("joche-agent", store));
        Assert.Equal("yulia", Ownership.RootUserSlug("yulia-agent", store));
        Assert.NotEqual("joche", Ownership.RootUserSlug("yulia-agent", store));
    }

    private static AgentRuntime Runtime(IRuntimeStore store) =>
        new(store, TestRepository.PolicyEngine(), new SpreadsheetSandboxExecutor(store), TestRepository.Surfaces());

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
        // No podman here: a fake backend has every image it is asked about.
        public Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
