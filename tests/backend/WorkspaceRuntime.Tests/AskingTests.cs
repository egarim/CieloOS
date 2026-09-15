using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// #40, and the two benchmark tasks it decided.
//
// T3 gave the agent something underspecified and it spent its whole step budget
// guessing; OpenClaw asked, with three options. T5 said "clean up my folder" and
// the agent ran three distinct commands eight times before the step limit stopped
// it; OpenClaw inventoried, proposed, and stopped to ask. Neither deleted
// anything — but ours only because it never got far enough to be dangerous, which
// is not a safety property.
//
// So: a run may end by asking, and going in circles is noticed as circling rather
// than reported as running out of room.
public class AskingTests
{
    [Fact]
    public async Task A_run_can_end_by_asking()
    {
        var world = World();
        var loop = new ConsoleAgentLoop(world.Runtime, world.Console);

        var result = await loop.RunAsync(
            "joche-agent-abc", "summarise the quarter", maxSteps: 8,
            world.Principal, world.OwnerId, world.AgentId,
            new AskingBrain("Which quarter did you mean — Q2 or Q3? I can see files for both."),
            CancellationToken.None);

        Assert.True(result.Asked);
        Assert.False(result.Completed);
        Assert.Equal(ConsoleAgentLoop.AskedStopReason, result.StopReason);

        // The question is carried as the finishing note, which is what the reply
        // path reads. An asked run that arrived with no question would be worse
        // than not asking.
        Assert.Contains("Q2 or Q3", result.Steps.Last().Note);

        // And it asked INSTEAD of acting: nothing was typed at the console.
        Assert.Empty(world.Console.Typed);
    }

    [Fact]
    public async Task Asking_beats_done_when_a_model_says_both()
    {
        var world = World();
        var loop = new ConsoleAgentLoop(world.Runtime, world.Console);

        // Models do return both. The question is the more specific intent, and
        // treating it as Done would silently turn "I need to know X" into a reply.
        var result = await loop.RunAsync(
            "joche-agent-abc", "tidy up", maxSteps: 8, world.Principal, world.OwnerId, world.AgentId,
            new BothBrain(), CancellationToken.None);

        Assert.True(result.Asked);
        Assert.Contains("Which one", result.Steps.Last().Note);
    }

    [Fact]
    public async Task A_cycle_is_noticed_as_circling_not_as_running_out_of_room()
    {
        var world = World();
        var loop = new ConsoleAgentLoop(world.Runtime, world.Console);

        // Exactly T5's shape: three distinct commands, forever. `recent` holds the
        // last two, so it never saw this coming and the run used its whole budget.
        var result = await loop.RunAsync(
            "joche-agent-abc", "clean up my folder", maxSteps: 8, world.Principal, world.OwnerId, world.AgentId,
            new CyclingBrain("ls ~/shared", "cat ~/shared/notes.md", "python3 -c 'print(1)'"),
            CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("repeated a command", result.StopReason, StringComparison.OrdinalIgnoreCase);

        // Stopped on the second lap, well inside the budget — the point is that the
        // stop reason now tells the truth about why.
        Assert.DoesNotContain("step limit", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(world.Console.Typed.Count < 8, $"Typed {world.Console.Typed.Count} commands before noticing the cycle.");
    }

    [Fact]
    public async Task Ordinary_progress_is_not_mistaken_for_a_cycle()
    {
        var world = World();
        var loop = new ConsoleAgentLoop(world.Runtime, world.Console);

        // The failure mode of a repeat check is stopping work that was fine. Four
        // different commands, then done.
        var result = await loop.RunAsync(
            "joche-agent-abc", "do the thing", maxSteps: 8, world.Principal, world.OwnerId, world.AgentId,
            new ScriptedBrain("echo one", "echo two", "echo three", "echo four"),
            CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(4, world.Console.Typed.Count);
    }

    private sealed class AskingBrain : IConsoleAgentBrain
    {
        private readonly string question;
        public AskingBrain(string question) => this.question = question;
        public Task<ConsoleAgentAction> DecideAsync(string goal, string screen, IReadOnlyList<string> history, int step, CancellationToken cancellationToken) =>
            Task.FromResult(new ConsoleAgentAction(false, null, false, null, question));
    }

    private sealed class BothBrain : IConsoleAgentBrain
    {
        public Task<ConsoleAgentAction> DecideAsync(string goal, string screen, IReadOnlyList<string> history, int step, CancellationToken cancellationToken) =>
            Task.FromResult(new ConsoleAgentAction(true, null, false, "All done!", "Which one did you mean?"));
    }

    private sealed class CyclingBrain : IConsoleAgentBrain
    {
        private readonly string[] commands;
        public CyclingBrain(params string[] commands) => this.commands = commands;
        public Task<ConsoleAgentAction> DecideAsync(string goal, string screen, IReadOnlyList<string> history, int step, CancellationToken cancellationToken) =>
            Task.FromResult(new ConsoleAgentAction(false, commands[(step - 1) % commands.Length], true, "looking around"));
    }

    private sealed class ScriptedBrain : IConsoleAgentBrain
    {
        private readonly string[] commands;
        public ScriptedBrain(params string[] commands) => this.commands = commands;
        public Task<ConsoleAgentAction> DecideAsync(string goal, string screen, IReadOnlyList<string> history, int step, CancellationToken cancellationToken) =>
            Task.FromResult(step <= commands.Length
                ? new ConsoleAgentAction(false, commands[step - 1], true, "working")
                : new ConsoleAgentAction(true, null, false, "Finished."));
    }

    private static TestWorld World()
    {
        var store = new InMemoryRuntimeStore();
        var agent = store.Agents.Single(candidate => candidate.Slug == "joche-agent");
        var owner = store.Users.Single(candidate => candidate.Slug == "joche");
        var console = new FakeConsoleBackend();
        var runtime = new AgentRuntime(
            store, TestRepository.PolicyEngine(), new ConsoleSurfaceExecutor(console), TestRepository.Surfaces(),
            new FakeSessions(new DesktopSession("joche-agent-abc", "joche-agent", "agent-console", "running", 40000, "console")));
        return new TestWorld(
            runtime, console,
            new RuntimePrincipal(PrincipalKind.Agent, agent.Id, agent.Slug, agent.Name),
            owner.Id, agent.Id);
    }

    private sealed record TestWorld(
        AgentRuntime Runtime,
        FakeConsoleBackend Console,
        RuntimePrincipal Principal,
        Guid OwnerId,
        Guid AgentId);

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
