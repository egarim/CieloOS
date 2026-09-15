using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// Engine #2 onwards: somebody else's harness, running inside the owner's session
// container.
//
// What it is given is deliberately two URLs and a token. No provider key, no
// model name, no filesystem beyond the container it already lives in. The model
// endpoint decides the model; the MCP endpoint decides what it may do; the
// container decides what exists. An engine that ignores all three can still only
// damage one owner's home, because the container is the security boundary and MCP
// is only the policy one.
//
// Its reply is its stdout. That is not elegant, and it is what every one of these
// tools actually produces.
public sealed class ForeignProcessEngine : IAgentEngine
{
    private readonly EngineManifest manifest;
    private readonly IEngineProcess process;
    private readonly Func<EngineRun, EngineEndpoints> endpoints;

    public ForeignProcessEngine(EngineManifest manifest, IEngineProcess process, Func<EngineRun, EngineEndpoints> endpoints)
    {
        this.manifest = manifest;
        this.process = process;
        this.endpoints = endpoints;
    }

    public string Id => manifest.Id;

    public async Task<ConsoleLoopResult> RunAsync(
        EngineRun run,
        Func<ConsoleLoopStep, Task>? onStep,
        CancellationToken cancellationToken)
    {
        var wiring = endpoints(run);
        var invocation = new EngineInvocation(
            manifest.Invoke.Command,
            manifest.Invoke.Args.Select(argument => Fill(argument, run, wiring)).ToList(),
            (manifest.Invoke.Env ?? new Dictionary<string, string>())
                .ToDictionary(pair => pair.Key, pair => Fill(pair.Value, run, wiring), StringComparer.Ordinal));

        // One step, reported before the work rather than after, so the portal shows
        // the engine starting instead of a silent minute. A foreign harness does not
        // tell us what it is doing as it does it — the audit trail does, and that is
        // the record that counts.
        var starting = new ConsoleLoopStep(1, "", $"{manifest.DisplayName} {manifest.Version}", false, false, "handed the goal to the engine", "Allow", "Engine run started.");
        if (onStep is not null) await onStep(starting);

        var result = await process.RunAsync(run.SessionId, invocation, cancellationToken);

        if (result.ExitCode != 0)
        {
            // stderr is the engine's, and an engine may put anything in it — a
            // provider URL, a token it was given, a stack trace. The owner is told
            // that it failed and how far it got; the detail goes to the log, not
            // into a chat reply.
            var failed = new ConsoleLoopStep(2, "", null, false, true,
                $"{manifest.DisplayName} could not finish this. It stopped with exit code {result.ExitCode}.",
                "Stopped", $"Engine exited {result.ExitCode}.");
            return new ConsoleLoopResult(run.SessionId, run.Goal, false, $"Engine exited {result.ExitCode}.", new[] { starting, failed });
        }

        var reply = Reply(result.Stdout);
        var done = new ConsoleLoopStep(2, "", null, false, true, reply, "Done", "Engine finished.");
        if (onStep is not null) await onStep(done);
        return new ConsoleLoopResult(run.SessionId, run.Goal, true, "Engine finished.", new[] { starting, done });
    }

    // Engines print progress chatter on the way to an answer. OpenClaw prefixes its
    // own with [something] on its own line; those are its logs, not its reply, and
    // forwarding them to the owner as part of an answer would be noise at best.
    private static string Reply(string stdout)
    {
        var lines = stdout.Replace("\r\n", "\n").Split('\n')
            .Where(line => !line.TrimStart().StartsWith('['))
            .ToList();

        var reply = string.Join("\n", lines).Trim();
        return string.IsNullOrWhiteSpace(reply)
            ? "The engine finished without saying anything, which usually means it did not understand the request."
            : reply;
    }

    private static string Fill(string template, EngineRun run, EngineEndpoints wiring) =>
        template
            .Replace("{goal}", run.Goal, StringComparison.Ordinal)
            // An engine's idea of a session is its own, and it is NOT ours. Three
            // spike runs answered from a previous conversation's memory instead of
            // doing the work, because the key was reused. One CieloOS run is one
            // engine session, always.
            .Replace("{session}", $"cielo-{run.SessionId}-{run.AgentId:N}", StringComparison.Ordinal)
            .Replace("{modelUrl}", wiring.ModelUrl, StringComparison.Ordinal)
            .Replace("{mcpUrl}", wiring.McpUrl, StringComparison.Ordinal)
            .Replace("{token}", wiring.Token, StringComparison.Ordinal);
}
