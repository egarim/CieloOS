using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Tests;

// Running somebody else's harness here. The interesting assertions are about what
// it is NOT given, and about the two mistakes the spike actually made.
public class ForeignEngineTests
{
    [Fact]
    public void The_shipped_openclaw_manifest_says_what_the_spike_proved()
    {
        var engine = new FileEngineCatalog(TestRepository.Root()).Find("openclaw");
        Assert.NotNull(engine);

        // Pinned, not "latest". Installing the newest thing from a panel is a
        // supply-chain decision made by whoever happened to click.
        Assert.Contains("@", engine!.Install.Package, StringComparison.Ordinal);

        // No network. The whole containment argument rests on this line.
        Assert.Equal("none", engine.Network);

        var configure = string.Join("\n", engine.Install.Configure ?? Array.Empty<string>());

        // The absolute allowlist is what actually removes its shell; the profile
        // alone does not.
        Assert.Contains("tools.allow", configure, StringComparison.Ordinal);

        // SSE opens with a GET and gets a 405 from our server. Measured.
        Assert.Contains("--transport streamable-http", configure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_engine_gets_two_urls_and_a_token_and_nothing_else()
    {
        var process = new RecordingProcess("done");
        var engine = Engine(process);

        await engine.RunAsync(Run("make a csv"), onStep: null, CancellationToken.None);

        var env = process.Last!.Env;
        var arguments = string.Join(" ", process.Last.Args);

        // No provider key and no provider URL reach the engine. If either did, the
        // container could be wide open and it would still be able to bill someone.
        Assert.DoesNotContain(env, pair => pair.Value.Contains("api.deepseek.com", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(env, pair => pair.Key.Contains("API_KEY", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("sk-", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_run_is_a_new_engine_session()
    {
        var process = new RecordingProcess("done");
        var engine = Engine(process);

        await engine.RunAsync(Run("first"), onStep: null, CancellationToken.None);
        var first = process.Last!.Args.ToList();
        await engine.RunAsync(Run("second"), onStep: null, CancellationToken.None);
        var second = process.Last!.Args.ToList();

        // An engine's idea of a session is its own. Reusing a key made three spike
        // runs answer from a previous conversation's memory and never call a tool —
        // the most convincing wrong answers of the exercise, because nothing in them
        // looked wrong.
        var firstKey = first[first.IndexOf("--session-key") + 1];
        var secondKey = second[second.IndexOf("--session-key") + 1];
        Assert.NotEqual(firstKey, secondKey);
    }

    [Fact]
    public async Task The_engines_own_log_lines_are_not_part_of_its_reply()
    {
        var process = new RecordingProcess(
            "[bundle-mcp] tool \"cielo.write_file\" registered as \"cielo__cielo-write_file\"\n"
            + "[provider-transport-fetch] start provider=cielo\n"
            + "Saved colours.csv to your shared workspace.\n"
            + "[agents/agent-command] run 389d ended with stopReason=stop");

        var result = await Engine(process).RunAsync(Run("save a csv"), onStep: null, CancellationToken.None);

        var reply = result.Steps.Last().Note!;
        Assert.Equal("Saved colours.csv to your shared workspace.", reply);
        Assert.DoesNotContain("bundle-mcp", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("stopReason", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_engine_does_not_leak_its_stderr_into_the_reply()
    {
        var process = new RecordingProcess("", exitCode: 1,
            stderr: "Error: connect ECONNREFUSED https://api.deepseek.com  Authorization: Bearer sk-secret");

        var result = await Engine(process).RunAsync(Run("anything"), onStep: null, CancellationToken.None);

        Assert.False(result.Completed);
        var reply = result.Steps.Last().Note!;
        Assert.DoesNotContain("sk-secret", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("deepseek", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not finish", reply, StringComparison.OrdinalIgnoreCase);
    }

    private static ForeignProcessEngine Engine(IEngineProcess process) =>
        new(
            new FileEngineCatalog(TestRepository.Root()).Find("openclaw")!,
            process,
            _ => new EngineEndpoints("http://127.0.0.1:8931/engine/v1", "http://127.0.0.1:8931/mcp", "run-token"));

    private static EngineRun Run(string goal) =>
        new("session-1", goal, 8,
            new RuntimePrincipal(PrincipalKind.Agent, Guid.NewGuid(), "joche-agent", "Joche's Agent"),
            Guid.NewGuid(), Guid.NewGuid());

    private sealed class RecordingProcess : IEngineProcess
    {
        private readonly string stdout;
        private readonly string stderr;
        private readonly int exitCode;

        public RecordingProcess(string stdout, int exitCode = 0, string stderr = "")
        {
            this.stdout = stdout;
            this.exitCode = exitCode;
            this.stderr = stderr;
        }

        public EngineInvocation? Last { get; private set; }

        public Task<EngineProcessResult> RunAsync(string sessionId, EngineInvocation invocation, CancellationToken cancellationToken)
        {
            Last = invocation;
            return Task.FromResult(new EngineProcessResult(exitCode, stdout, stderr));
        }
    }
}
