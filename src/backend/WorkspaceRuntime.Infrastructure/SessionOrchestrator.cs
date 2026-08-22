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
}

// The V0.4 session backend: one podman container per desktop session, driven
// through the same policy-checked bus as every other command. The production
// target (docs/ai-native-ui.md D3) swaps podman for Incus system containers;
// the command shape and policy path are identical, so only this executor moves.
public sealed class SessionOrchestrator : ISurfaceExecutor, ISessionBackend
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

            default:
                return new ToolExecutionResult(false, $"Session executor rejected unknown operation '{request.Operation}'.", null);
        }
    }

    public Task<EffectPreview> PreviewAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var summary = request.Operation switch
        {
            "create" => $"Would create an isolated {request.Arguments.GetValueOrDefault("profile")} desktop for '{request.Arguments.GetValueOrDefault("owner")}' from image {options.Image}.",
            "destroy" => $"Would destroy desktop session '{request.Arguments.GetValueOrDefault("id")}' and discard its running state.",
            _ => "Unknown session operation."
        };
        return Task.FromResult(new EffectPreview(true, summary, Array.Empty<CellChange>()));
    }

    private async Task<ToolExecutionResult> CreateAsync(string owner, string profile, CancellationToken cancellationToken)
    {
        var id = $"{owner}-{Guid.NewGuid():N}"[..Math.Min(owner.Length + 9, 40)];
        var name = options.NamePrefix + id;
        var homeVolume = options.HomeVolumePrefix + owner;

        // The per-owner home volume is created once and outlives every session.
        await RunPodmanAsync(new[] { "volume", "create", homeVolume }, cancellationToken);

        var run = await RunPodmanAsync(new[]
        {
            "run", "-d",
            "--name", name,
            "--label", options.SessionLabel,
            "--label", $"lunos.owner={owner}",
            "--label", $"lunos.profile={profile}",
            "-v", $"{homeVolume}:{options.HomePath}",
            "-p", $"127.0.0.1::{options.ViewportPort}",
            options.Image
        }, cancellationToken);

        if (run.ExitCode != 0)
        {
            return new ToolExecutionResult(false, $"Failed to start desktop container: {run.Stderr.Trim()}", null);
        }

        var port = await ReadViewportPortAsync(name, cancellationToken);
        var portText = port is null ? "pending" : port.Value.ToString();
        return new ToolExecutionResult(true, $"Started {profile} desktop '{id}' (viewport 127.0.0.1:{portText}).", null);
    }

    private async Task<ToolExecutionResult> DestroyAsync(string id, CancellationToken cancellationToken)
    {
        var name = options.NamePrefix + id;
        var remove = await RunPodmanAsync(new[] { "rm", "-f", name }, cancellationToken);
        return remove.ExitCode == 0
            ? new ToolExecutionResult(true, $"Destroyed desktop session '{id}'.", null)
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
                port));
        }

        return sessions;
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

    private async Task<int?> ReadViewportPortAsync(string name, CancellationToken cancellationToken)
    {
        var result = await RunPodmanAsync(new[] { "port", name, options.ViewportPort.ToString() }, cancellationToken);
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
