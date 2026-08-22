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

    private static AgentRuntime Runtime(IRuntimeStore store) =>
        new(store, TestRepository.PolicyEngine(), new SpreadsheetSandboxExecutor(store), TestRepository.Surfaces());
}
