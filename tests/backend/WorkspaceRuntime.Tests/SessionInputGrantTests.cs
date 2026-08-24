using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The V0.6 per-session input grant: a human leases input on a session it owns,
// and while the lease is live desktop typing/keys become Allow instead of
// RequireApproval — one-time consent, time-boxed, only a human can issue it.
public class SessionInputGrantTests
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

    private static AgentRuntime Runtime(IRuntimeStore store, ISessionInputGrants grants, DesktopSession session) =>
        new(store, TestRepository.PolicyEngine(), new NoopExecutor(), TestRepository.Surfaces(),
            new FakeSessions(session), grants);

    [Fact]
    public async Task Typing_without_a_grant_requires_approval()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var grants = new InMemorySessionInputGrants();
        var runtime = Runtime(store, grants, new DesktopSession("joche-agent-abc", "joche-agent", "agent-desktop", "running", 40000, "desktop"));

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Id, jocheAgent.Id, "desktop", "type",
            new Dictionary<string, string> { ["id"] = "joche-agent-abc", ["text"] = "hi" }),
            Agent(store, "joche-agent"), CancellationToken.None);

        Assert.Equal(PolicyDecision.RequireApproval, result.Decision);
    }

    [Fact]
    public async Task A_live_input_grant_upgrades_typing_to_allow()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var grants = new InMemorySessionInputGrants();
        var runtime = Runtime(store, grants, new DesktopSession("joche-agent-abc", "joche-agent", "agent-desktop", "running", 40000, "desktop"));

        // The owner leases input on the session for 10 minutes.
        grants.Grant("joche-agent-abc", joche.Id, TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Id, jocheAgent.Id, "desktop", "type",
            new Dictionary<string, string> { ["id"] = "joche-agent-abc", ["text"] = "ls" }),
            Agent(store, "joche-agent"), CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.Contains("input grant", result.Reason);
        Assert.Contains(store.AuditEvents, e => e.Action == "desktop.type" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task An_agent_cannot_grant_itself_input()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var grants = new InMemorySessionInputGrants();
        var runtime = Runtime(store, grants, new DesktopSession("joche-agent-abc", "joche-agent", "agent-desktop", "running", 40000, "desktop"));

        // An agent principal tries to grant input — requiresHuman must deny it.
        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            joche.Id, jocheAgent.Id, "session-input", "grant",
            new Dictionary<string, string> { ["id"] = "joche-agent-abc", ["minutes"] = "10" }),
            Agent(store, "joche-agent"), CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.False(grants.IsActive("joche-agent-abc", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Grants_are_time_boxed_and_revocable()
    {
        var grants = new InMemorySessionInputGrants();
        var now = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid();

        grants.Grant("s1", user, TimeSpan.FromMinutes(10), now);
        Assert.True(grants.IsActive("s1", now));
        Assert.False(grants.IsActive("s1", now.AddMinutes(11))); // expired
        Assert.False(grants.IsActive("other", now));             // scoped to the session

        Assert.Equal(1, grants.Revoke("s1"));
        Assert.False(grants.IsActive("s1", now));                // revoked
    }

    [Fact]
    public void Vision_consent_is_time_boxed_and_revocable()
    {
        var consent = new InMemorySessionVisionConsent();
        var now = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid();

        consent.Grant("s1", user, TimeSpan.FromMinutes(10), now);
        Assert.True(consent.IsAllowed("s1", now));
        Assert.False(consent.IsAllowed("s1", now.AddMinutes(11))); // expired
        Assert.False(consent.IsAllowed("other", now));             // scoped to the session

        Assert.Equal(1, consent.Revoke("s1"));
        Assert.False(consent.IsAllowed("s1", now));                // revoked
    }

    private sealed class NoopExecutor : ISandboxedToolExecutor
    {
        public Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolExecutionResult(true, "noop", null));
    }

    private sealed class FakeSessions : ISessionBackend
    {
        private readonly IReadOnlyList<DesktopSession> list;
        public FakeSessions(params DesktopSession[] list) => this.list = list;
        public Task<IReadOnlyList<DesktopSession>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(list);
        // No podman here: a fake backend has every image it is asked about.
        public Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
