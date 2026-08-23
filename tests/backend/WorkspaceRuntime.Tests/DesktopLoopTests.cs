using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The desktop loop drives a session through the SAME policy-checked bus a human
// uses: it grounds on AT-SPI element ids (exact centers) with a vision-pixel
// fallback, and every click/keystroke is a `desktop.*`, ownership-gated + audited.
public class DesktopLoopTests
{
    private static RuntimePrincipal Agent(IRuntimeStore store, string slug)
    {
        var agent = store.Agents.Single(candidate => candidate.Slug == slug);
        return new RuntimePrincipal(PrincipalKind.Agent, agent.Id, agent.Slug, agent.Name);
    }

    private static (AgentRuntime Runtime, DesktopAgentLoop Loop, FakeDesktopBackend Backend) Build(
        IRuntimeStore store, DesktopSession session, params DesktopElement[] elements)
    {
        var backend = new FakeDesktopBackend(elements);
        var runtime = new AgentRuntime(
            store, TestRepository.PolicyEngine(), new DesktopSurfaceExecutor(backend), TestRepository.Surfaces(),
            new FakeSessions(session));
        return (runtime, new DesktopAgentLoop(runtime, backend), backend);
    }

    [Fact]
    public async Task The_loop_grounds_on_an_element_id_and_clicks_its_exact_center()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var (_, loop, backend) = Build(store,
            new DesktopSession("joche-agent-abc", "joche-agent", "agent-desktop", "running", 40000, "desktop"),
            new DesktopElement(3, "toggle button", "Applications", 0, 0, 100, 20)); // center (50, 10)

        var brain = new ScriptedDesktopBrain(
            new DesktopAgentAction(false, "click", 3, null, null, null, null, "open the menu"),
            new DesktopAgentAction(true, "done", null, null, null, null, null, "done"));

        var result = await loop.RunAsync("joche-agent-abc", "open the applications menu", 5,
            Agent(store, "joche-agent"), joche.Id, jocheAgent.Id, brain, CancellationToken.None);

        Assert.True(result.Completed);
        // The element id was resolved to its EXACT center and clicked there.
        Assert.Contains((50, 10), backend.Clicks);
        // ...and it landed on the audit trail as a desktop.click with those coords.
        Assert.Contains(store.AuditEvents, e => e.Action == "desktop.click" && e.Detail.Contains("(50, 10)"));
    }

    [Fact]
    public async Task The_loop_falls_back_to_vision_pixels_when_the_target_is_not_an_element()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        var (_, loop, backend) = Build(store,
            new DesktopSession("joche-agent-abc", "joche-agent", "agent-desktop", "running", 40000, "desktop"));

        var brain = new ScriptedDesktopBrain(
            new DesktopAgentAction(false, "click", null, 200, 300, null, null, "click the desktop icon"),
            new DesktopAgentAction(true, "done", null, null, null, null, null, "done"));

        var result = await loop.RunAsync("joche-agent-abc", "click the home icon", 5,
            Agent(store, "joche-agent"), joche.Id, jocheAgent.Id, brain, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Contains((200, 300), backend.Clicks);
    }

    [Fact]
    public async Task The_loop_stops_when_a_click_is_denied_by_ownership()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var joche = store.Users.Single(candidate => candidate.Slug == "joche");
        // Session belongs to yulia's agent; joche's agent must not drive it.
        var (_, loop, backend) = Build(store,
            new DesktopSession("yulia-agent-xyz", "yulia-agent", "agent-desktop", "running", 40000, "desktop"),
            new DesktopElement(0, "push button", "Secret", 0, 0, 40, 20));

        var brain = new ScriptedDesktopBrain(
            new DesktopAgentAction(false, "click", 0, null, null, null, null, "snoop"));

        var result = await loop.RunAsync("yulia-agent-xyz", "snoop", 5,
            Agent(store, "joche-agent"), joche.Id, jocheAgent.Id, brain, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("Deny", result.StopReason);
        Assert.Empty(backend.Clicks);
        Assert.Contains(result.Steps, step => step.Decision == "Deny");
    }

    private sealed class ScriptedDesktopBrain : IDesktopAgentBrain
    {
        private readonly Queue<DesktopAgentAction> actions;
        public ScriptedDesktopBrain(params DesktopAgentAction[] actions) => this.actions = new Queue<DesktopAgentAction>(actions);
        public Task<DesktopAgentAction> DecideAsync(
            string goal, IReadOnlyList<DesktopElement> elements, byte[]? screenshotPng,
            int screenWidth, int screenHeight, IReadOnlyList<string> history, int step, CancellationToken cancellationToken) =>
            Task.FromResult(actions.Count > 0
                ? actions.Dequeue()
                : new DesktopAgentAction(true, "done", null, null, null, null, null, "exhausted"));
    }

    private sealed class FakeDesktopBackend : IDesktopBackend
    {
        private readonly IReadOnlyList<DesktopElement> elements;
        public List<(int X, int Y)> Clicks { get; } = new();
        public List<string> Typed { get; } = new();

        public FakeDesktopBackend(IReadOnlyList<DesktopElement> elements) => this.elements = elements;

        public Task<DesktopShot> ScreenshotAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(new DesktopShot(sessionId, new byte[] { 1, 2, 3 }, 1000, 800, true, null));

        public Task<DesktopElements> ElementsAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(new DesktopElements(sessionId, elements, true, null));

        public Task<DesktopActionResult> ClickAsync(string sessionId, int x, int y, int button, int repeat, CancellationToken cancellationToken)
        {
            Clicks.Add((x, y));
            return Task.FromResult(new DesktopActionResult(true, $"clicked ({x},{y})"));
        }

        public Task<DesktopActionResult> TypeTextAsync(string sessionId, string text, CancellationToken cancellationToken)
        {
            Typed.Add(text);
            return Task.FromResult(new DesktopActionResult(true, "typed"));
        }

        public Task<DesktopActionResult> KeyAsync(string sessionId, string keysym, CancellationToken cancellationToken) =>
            Task.FromResult(new DesktopActionResult(true, $"key {keysym}"));
    }

    private sealed class FakeSessions : ISessionBackend
    {
        private readonly IReadOnlyList<DesktopSession> list;
        public FakeSessions(params DesktopSession[] list) => this.list = list;
        public Task<IReadOnlyList<DesktopSession>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(list);
    }
}
