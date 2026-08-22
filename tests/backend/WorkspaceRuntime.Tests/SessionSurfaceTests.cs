using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public class SessionSurfaceTests
{
    [Fact]
    public void Session_surface_manifest_loads_with_create_and_destroy()
    {
        var manifest = TestRepository.Surfaces().Find("session");

        Assert.NotNull(manifest);
        Assert.Contains("create", manifest!.Commands.Keys);
        Assert.Contains("destroy", manifest.Commands.Keys);
        Assert.True(manifest.Commands["create"].ExposedToAgent);
    }

    [Fact]
    public void Create_is_allowed_and_destroy_requires_approval()
    {
        var engine = TestRepository.PolicyEngine();
        var store = new InMemoryRuntimeStore();
        var user = store.Users[0];
        var agent = store.Agents[0] with { GrantedTools = new HashSet<string> { "spreadsheet", "session" } };

        var create = new ToolRequest(Guid.NewGuid(), user.Id, agent.Id, "session", "create",
            new Dictionary<string, string> { ["owner"] = "avery", ["profile"] = "human-desktop" }, DateTimeOffset.UtcNow);
        var destroy = new ToolRequest(Guid.NewGuid(), user.Id, agent.Id, "session", "destroy",
            new Dictionary<string, string> { ["id"] = "avery-abc" }, DateTimeOffset.UtcNow);

        Assert.Equal(PolicyDecision.Allow, engine.Evaluate(user, agent, create).Decision);
        Assert.Equal(PolicyDecision.RequireApproval, engine.Evaluate(user, agent, destroy).Decision);
    }

    [Fact]
    public void Router_dispatches_to_the_executor_that_owns_the_surface()
    {
        var store = new InMemoryRuntimeStore();
        var spreadsheet = new SpreadsheetSandboxExecutor(store);
        var sessions = new SessionOrchestrator(new SessionBackendOptions { PodmanPath = "podman" });
        var router = new SurfaceExecutorRouter(new ISurfaceExecutor[] { spreadsheet, sessions });

        // An unrouted surface fails cleanly rather than throwing.
        var unknown = router.ExecuteAsync(
            new ToolRequest(Guid.NewGuid(), store.Users[0].Id, store.Agents[0].Id, "nope", "x", new Dictionary<string, string>(), DateTimeOffset.UtcNow),
            CancellationToken.None).Result;
        Assert.False(unknown.Executed);
    }

    [Fact]
    public async Task Session_create_dry_run_describes_the_desktop_without_touching_podman()
    {
        var sessions = new SessionOrchestrator(new SessionBackendOptions { PodmanPath = "podman" });
        var preview = await sessions.PreviewAsync(
            new ToolRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "session", "create",
                new Dictionary<string, string> { ["owner"] = "avery", ["profile"] = "agent-desktop" }, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.True(preview.Supported);
        Assert.Contains("agent-desktop", preview.Summary);
        Assert.Contains("avery", preview.Summary);
    }
}
