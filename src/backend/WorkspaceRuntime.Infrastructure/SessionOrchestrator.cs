using System.Diagnostics;
using System.Text.Json;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

public sealed class SessionBackendOptions
{
    public string PodmanPath { get; init; } = "podman";
    public string Image { get; init; } = "docker.io/accetto/ubuntu-vnc-xfce-g3:latest";
    public int ViewportPort { get; init; } = 6901;
    public string NamePrefix { get; init; } = "lunos-session-";
    public string SessionLabel { get; init; } = "lunos.session=1";

    // The persistent home: one named volume per owner, mounted into every
    // session at the container's home path. The home is the durable primitive;
    // the container is a disposable view of it (docs/ai-native-ui.md D3).
    public string HomeVolumePrefix { get; init; } = "lunos-home-";
    public string HomePath { get; init; } = "/config";

    // The console kind: a lightweight ttyd+tmux terminal over the same home,
    // the default agent session. Far smaller than the desktop image, and
    // tmux gives shadow/become attach semantics for free.
    public string ConsoleImage { get; init; } = "localhost/lunos-console:latest";
    public int ConsolePort { get; init; } = 7681;
    public string ConsoleHomePath { get; init; } = "/root";

    // The persistent tmux session inside a console container. The image starts
    // it detached at boot and ttyd attaches to the same name, so the agent
    // (via podman exec) and any human (via ttyd) share one live screen.
    public string ConsoleTmuxSession { get; init; } = "main";
}

// The V0.4 session backend: one podman container per desktop session, driven
// through the same policy-checked bus as every other command. The production
// target (docs/ai-native-ui.md D3) swaps podman for Incus system containers;
// the command shape and policy path are identical, so only this executor moves.
public sealed class SessionOrchestrator : ISurfaceExecutor, ISessionBackend, IConsoleBackend
{
    private readonly SessionBackendOptions options;

    public SessionOrchestrator(SessionBackendOptions options)
    {
        this.options = options;
    }

    public string SurfaceId => "session";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case "create":
                return await CreateAsync(
                    Required(request, "owner"),
                    Required(request, "profile"),
                    cancellationToken);

            case "destroy":
                return await DestroyAsync(Required(request, "id"), cancellationToken);

            case "inhabit":
                return await InhabitAsync(Required(request, "id"), Required(request, "mode"), cancellationToken);

            default:
                return new ToolExecutionResult(false, $"Session executor rejected unknown operation '{request.Operation}'.", null);
        }
    }

    public Task<EffectPreview> PreviewAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var summary = request.Operation switch
        {
            "create" => $"Would create an isolated {request.Arguments.GetValueOrDefault("profile")} session for '{request.Arguments.GetValueOrDefault("owner")}', over that owner's persistent home.",
            "destroy" => $"Would destroy session '{request.Arguments.GetValueOrDefault("id")}' and discard its running state (the home volume persists).",
            "inhabit" => $"Would take a seat at session '{request.Arguments.GetValueOrDefault("id")}' as '{request.Arguments.GetValueOrDefault("mode")}', recorded on behalf of its agent.",
            _ => "Unknown session operation."
        };
        return Task.FromResult(new EffectPreview(true, summary, Array.Empty<CellChange>()));
    }

    private async Task<ToolExecutionResult> CreateAsync(string owner, string profile, CancellationToken cancellationToken)
    {
        var id = $"{owner}-{Guid.NewGuid():N}"[..Math.Min(owner.Length + 9, 40)];
        var name = options.NamePrefix + id;
        var homeVolume = options.HomeVolumePrefix + owner;

        var isConsole = profile.Contains("console", StringComparison.Ordinal);
        var image = isConsole ? options.ConsoleImage : options.Image;
        var containerPort = isConsole ? options.ConsolePort : options.ViewportPort;
        var homePath = isConsole ? options.ConsoleHomePath : options.HomePath;

        // The per-owner home volume is created once and outlives every session,
        // and is the SAME volume whether viewed as a console or a desktop.
        await RunPodmanAsync(new[] { "volume", "create", homeVolume }, cancellationToken);

        var run = await RunPodmanAsync(new[]
        {
            "run", "-d",
            "--name", name,
            "--label", options.SessionLabel,
            "--label", $"lunos.owner={owner}",
            "--label", $"lunos.profile={profile}",
            "--label", $"lunos.kind={(isConsole ? "console" : "desktop")}",
            "-v", $"{homeVolume}:{homePath}",
            "-p", $"127.0.0.1::{containerPort}",
            image
        }, cancellationToken);

        if (run.ExitCode != 0)
        {
            return new ToolExecutionResult(false, $"Failed to start {profile} session: {run.Stderr.Trim()}", null);
        }

        var port = await ReadViewportPortAsync(name, containerPort, cancellationToken);
        var portText = port is null ? "pending" : port.Value.ToString();
        var kind = isConsole ? "console" : "desktop";
        return new ToolExecutionResult(true, $"Started {profile} {kind} '{id}' (viewport 127.0.0.1:{portText}).", null);
    }

    // Inhabiting does not change the container; it is the governed, audited act
    // of a human taking a seat at an owned agent's session. It resolves the
    // live viewport so the human can open it. (Keystroke-level read-only for
    // "shadow" and agent-suspend for "become" arrive with the session gateway,
    // docs/ai-native-ui.md V0.6; today this is the accountable grant.)
    private async Task<ToolExecutionResult> InhabitAsync(string id, string mode, CancellationToken cancellationToken)
    {
        var session = (await ListAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == id);
        if (session is null)
        {
            return new ToolExecutionResult(false, $"Session '{id}' was not found.", null);
        }

        if (session.Status != "running")
        {
            return new ToolExecutionResult(false, $"Session '{id}' is not running ({session.Status}).", null);
        }

        return new ToolExecutionResult(true, $"Inhabiting {session.Kind} '{id}' ({mode}) at viewport 127.0.0.1:{session.ViewportPort}.", null);
    }

    private async Task<ToolExecutionResult> DestroyAsync(string id, CancellationToken cancellationToken)
    {
        var name = options.NamePrefix + id;
        var remove = await RunPodmanAsync(new[] { "rm", "-f", name }, cancellationToken);
        return remove.ExitCode == 0
            ? new ToolExecutionResult(true, $"Destroyed session '{id}'.", null)
            : new ToolExecutionResult(false, $"Failed to destroy session '{id}': {remove.Stderr.Trim()}", null);
    }

    public async Task<IReadOnlyList<DesktopSession>> ListAsync(CancellationToken cancellationToken)
    {
        var list = await RunPodmanAsync(new[]
        {
            "ps", "-a", "--filter", $"label={options.SessionLabel}", "--format", "json"
        }, cancellationToken);

        if (list.ExitCode != 0 || string.IsNullOrWhiteSpace(list.Stdout))
        {
            return Array.Empty<DesktopSession>();
        }

        var sessions = new List<DesktopSession>();
        using var document = JsonDocument.Parse(list.Stdout);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = element.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array && names.GetArrayLength() > 0
                ? names[0].GetString() ?? ""
                : "";
            if (!name.StartsWith(options.NamePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var labels = element.TryGetProperty("Labels", out var labelElement) ? labelElement : default;
            var status = element.TryGetProperty("State", out var state) ? state.GetString() ?? "unknown" : "unknown";
            var port = ExtractHostPort(element);

            sessions.Add(new DesktopSession(
                name[options.NamePrefix.Length..],
                LabelOrDefault(labels, "lunos.owner", "unknown"),
                LabelOrDefault(labels, "lunos.profile", "unknown"),
                status,
                port,
                LabelOrDefault(labels, "lunos.kind", "desktop")));
        }

        return sessions;
    }

    // Observe: read the console's current screen. tmux capture-pane returns the
    // visible pane text — exactly what a human at ttyd would see.
    public async Task<ConsoleView> CaptureAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = (await ListAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == sessionId);
        if (session is null)
        {
            return new ConsoleView(sessionId, "", false, "Session not found.");
        }
        if (!string.Equals(session.Kind, "console", StringComparison.Ordinal))
        {
            return new ConsoleView(sessionId, "", false, "Only console sessions expose a readable screen.");
        }

        var name = options.NamePrefix + sessionId;
        var capture = await RunPodmanAsync(new[] { "exec", name, "tmux", "capture-pane", "-p", "-t", options.ConsoleTmuxSession }, cancellationToken);
        return capture.ExitCode == 0
            ? new ConsoleView(sessionId, capture.Stdout.TrimEnd('\n'), true, null)
            : new ConsoleView(sessionId, "", false, $"Could not read console: {capture.Stderr.Trim()}");
    }

    // Act: type into the console. `-l` sends the text literally (shell
    // metacharacters are typed, not read by tmux as key names); the text crosses
    // to podman as a single argv entry with no host shell, so there is no
    // host-side injection. Blast radius is the agent's own container + home.
    public async Task<ConsoleActionResult> TypeAsync(string sessionId, string text, bool submit, CancellationToken cancellationToken)
    {
        var session = (await ListAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == sessionId);
        if (session is null)
        {
            return new ConsoleActionResult(false, "", "Session not found.");
        }
        if (!string.Equals(session.Kind, "console", StringComparison.Ordinal))
        {
            return new ConsoleActionResult(false, "", "Only console sessions accept input.");
        }

        var name = options.NamePrefix + sessionId;
        var typed = await RunPodmanAsync(new[] { "exec", name, "tmux", "send-keys", "-t", options.ConsoleTmuxSession, "-l", text }, cancellationToken);
        if (typed.ExitCode != 0)
        {
            return new ConsoleActionResult(false, "", $"Could not type: {typed.Stderr.Trim()}");
        }

        if (submit)
        {
            var enter = await RunPodmanAsync(new[] { "exec", name, "tmux", "send-keys", "-t", options.ConsoleTmuxSession, "Enter" }, cancellationToken);
            if (enter.ExitCode != 0)
            {
                return new ConsoleActionResult(false, "", $"Could not submit: {enter.Stderr.Trim()}");
            }
        }

        // Let the shell render, then read the screen back so the caller sees the
        // effect of what it just typed (observe-after-act).
        await Task.Delay(300, cancellationToken);
        var view = await CaptureAsync(sessionId, cancellationToken);
        return new ConsoleActionResult(true, view.Screen, submit ? "Typed and submitted." : "Typed.");
    }

    private static string LabelOrDefault(JsonElement labels, string key, string fallback) =>
        labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(key, out var value)
            ? value.GetString() ?? fallback
            : fallback;

    private static int ExtractHostPort(JsonElement container)
    {
        if (container.TryGetProperty("Ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
        {
            foreach (var mapping in ports.EnumerateArray())
            {
                if (mapping.TryGetProperty("host_port", out var hostPort) && hostPort.TryGetInt32(out var value) && value > 0)
                {
                    return value;
                }
            }
        }

        return 0;
    }

    private async Task<int?> ReadViewportPortAsync(string name, int containerPort, CancellationToken cancellationToken)
    {
        var result = await RunPodmanAsync(new[] { "port", name, containerPort.ToString() }, cancellationToken);
        if (result.ExitCode != 0)
        {
            return null;
        }

        // Output like "127.0.0.1:43521" (possibly multiple lines).
        var firstLine = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var portText = firstLine?.Split(':').LastOrDefault();
        return int.TryParse(portText, out var port) ? port : null;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunPodmanAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.PodmanPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
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
            return (127, "", $"Could not launch podman ('{options.PodmanPath}'): {exception.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string Required(ToolRequest request, string key) =>
        request.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument '{key}'.");
}
