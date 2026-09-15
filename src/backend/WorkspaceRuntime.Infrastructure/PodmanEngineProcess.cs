using System.Diagnostics;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// Runs an engine's command inside a session's container, with `podman exec`.
//
// The container is the security boundary, not this class: whatever the engine
// does, it does as that session's user in that session's home. What this adds is
// that the engine is handed its two endpoints and nothing else, and that the
// arguments cross as a real argv rather than through a shell — no quoting, so no
// quoting bug, so no injection through a goal somebody typed.
public sealed class PodmanEngineProcess : IEngineProcess
{
    private readonly string podmanPath;
    private readonly Func<string, string> containerOf;

    public PodmanEngineProcess(string podmanPath, Func<string, string> containerOf)
    {
        this.podmanPath = podmanPath;
        this.containerOf = containerOf;
    }

    public async Task<EngineProcessResult> RunAsync(string sessionId, EngineInvocation invocation, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = podmanPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("exec");
        foreach (var pair in invocation.Env)
        {
            // Through podman's own --env, never an inline `KEY=value cmd`, which
            // would need a shell and would put the token on a command line every
            // process in the container can read in /proc.
            startInfo.ArgumentList.Add("--env");
            startInfo.ArgumentList.Add($"{pair.Key}={pair.Value}");
        }

        startInfo.ArgumentList.Add(containerOf(sessionId));
        startInfo.ArgumentList.Add(invocation.Command);
        foreach (var argument in invocation.Args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return new EngineProcessResult(127, "", $"Could not launch podman ('{podmanPath}'): {exception.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A cancelled run must not leave the engine running in the container:
            // it still holds the MCP token and can still act. Killing the exec is
            // what actually ends the run — this is the mistake OpenClaw's own
            // cancelled tool call made in the other direction, where the work landed
            // after the caller had stopped waiting.
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        return new EngineProcessResult(process.ExitCode, await stdout, await stderr);
    }
}
