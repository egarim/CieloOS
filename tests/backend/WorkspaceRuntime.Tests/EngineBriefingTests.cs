using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Tests;

// What a hosted engine is told it can do, against what it can actually do.
//
// The gap these cover is not hypothetical. A live instance was asked for its tool
// list and answered with fifteen tools, eleven of which require an "id" naming the
// session to act in. The engine was never given that id, and was told instead to use
// "websearch, python3, the files in ~ and ~/shared" — four things openclaw.engine.json
// deliberately removes by setting tools.allow to an absolute ["cielo__*"]. Three
// usable tools out of fifteen, and a prompt describing a machine it was not on.
public class EngineBriefingTests
{
    private const string Session = "joche-agent-81663b3b";

    [Fact]
    public void A_foreign_engine_is_told_the_session_id_it_has_to_pass()
    {
        var retargeted = EngineBriefing.Retarget("Do the thing. " + EngineBriefing.NativeTail, Session);

        Assert.Contains(Session, retargeted);
    }

    // The specific failure that shipped: the tail naming tools a hosted engine does
    // not have must not survive into what it reads.
    [Fact]
    public void The_native_tail_does_not_survive_retargeting()
    {
        var retargeted = EngineBriefing.Retarget("Do the thing. " + EngineBriefing.NativeTail, Session);

        Assert.DoesNotContain(EngineBriefing.NativeTail, retargeted);
        Assert.DoesNotContain("python3", retargeted);
        Assert.DoesNotContain("websearch", retargeted);
    }

    [Fact]
    public void The_owners_actual_request_is_left_alone()
    {
        var goal = "Make me a spreadsheet of microcontrollers. " + EngineBriefing.NativeTail + "Finish up.";

        var retargeted = EngineBriefing.Retarget(goal, Session);

        Assert.Contains("Make me a spreadsheet of microcontrollers.", retargeted);
        Assert.Contains("Finish up.", retargeted);
    }

    // A goal that never carried the native tail — a direct agent-run, say — still
    // needs the id, because the tools still require it. Doing nothing here would be
    // the quiet version of the original bug.
    [Fact]
    public void A_goal_without_the_native_tail_still_gets_the_session_id()
    {
        var retargeted = EngineBriefing.Retarget("Just answer this.", Session);

        Assert.Contains("Just answer this.", retargeted);
        Assert.Contains(Session, retargeted);
    }

    // Retargeting twice must not stack two briefings: a run that somehow passed
    // through the adapter twice would otherwise hand the engine two session ids and
    // no way to choose.
    [Fact]
    public void Retargeting_is_idempotent_in_what_it_promises()
    {
        var once = EngineBriefing.Retarget("Do the thing. " + EngineBriefing.NativeTail, Session);
        var twice = EngineBriefing.Retarget(once, Session);

        Assert.Equal(1, Occurrences(twice, "Its id is"));
    }

    // The native loop's own tail must keep describing the native loop. If this ever
    // starts naming MCP tools, the console agent is the one being lied to.
    [Fact]
    public void The_native_tail_still_describes_the_native_loop()
    {
        Assert.Contains("python3", EngineBriefing.NativeTail);
        Assert.Contains("~/shared", EngineBriefing.NativeTail);
        Assert.DoesNotContain("MCP", EngineBriefing.NativeTail);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}

// The adapter, not just the string helper. EngineBriefing being correct is worth
// nothing if ForeignProcessEngine never calls it, and that wiring is exactly what
// was missing — the session id existed on EngineRun the whole time and simply never
// reached the text handed to the engine. IEngineProcess is an interface, so the
// argument list an engine would really be launched with can be captured and read.
public class ForeignEngineBriefingTests
{
    private sealed class CapturingProcess : IEngineProcess
    {
        public EngineInvocation? Seen { get; private set; }

        public Task<EngineProcessResult> RunAsync(string sessionId, EngineInvocation invocation, CancellationToken cancellationToken)
        {
            Seen = invocation;
            return Task.FromResult(new EngineProcessResult(0, "done", ""));
        }
    }

    private static EngineManifest Manifest() => new(
        Schema: 1, Kind: "engine", Id: "openclaw", DisplayName: "OpenClaw", Version: "2026.9.4",
        Install: new EngineInstall("npm", "openclaw@2026.9.4", null, new[] { "openclaw config set tools.profile minimal" }),
        // The real manifest's shape: the goal travels as an argument after -m.
        Invoke: new EngineInvoke("openclaw", new[] { "agent", "--session-key", "{session}", "-m", "{goal}" }, null),
        Network: "none");

    [Fact]
    public async Task The_engine_is_launched_with_a_goal_naming_its_session()
    {
        var process = new CapturingProcess();
        var engine = new ForeignProcessEngine(
            Manifest(), process,
            _ => new EngineEndpoints("http://model", "http://mcp", "token"));

        var principal = new RuntimePrincipal(PrincipalKind.Agent, Guid.NewGuid(), "joche-agent", "Joche's Agent");
        var run = new EngineRun("joche-agent-81663b3b", "Find me a part. " + EngineBriefing.NativeTail,
            MaxSteps: 6, Principal: principal, UserId: Guid.NewGuid(), AgentId: Guid.NewGuid());

        await engine.RunAsync(run, onStep: null, CancellationToken.None);

        Assert.NotNull(process.Seen);
        var goalArgument = process.Seen!.Args.Last();

        // The id eleven of the fifteen MCP tools require.
        Assert.Contains("joche-agent-81663b3b", goalArgument);

        // And not a word about the four capabilities the allowlist removes.
        Assert.DoesNotContain("python3", goalArgument);
        Assert.DoesNotContain("websearch", goalArgument);

        // The owner's actual request survives all of it.
        Assert.Contains("Find me a part.", goalArgument);
    }
}
