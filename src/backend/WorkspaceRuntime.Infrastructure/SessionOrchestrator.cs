using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

public sealed class SessionBackendOptions
{
    public string PodmanPath { get; init; } = "podman";
    // The desktop image the distro builds for THIS machine's architecture
    // (distro/images/desktop/Containerfile). The old default,
    // docker.io/accetto/ubuntu-vnc-xfce-g3, is published for amd64 only, so it could
    // never start on arm64; it also served noVNC on 6901 while the image we actually
    // build serves Selkies on 3000.
    public string Image { get; init; } = "localhost/lunos-desktop:latest";
    public int ViewportPort { get; init; } = 3000;
    public string NamePrefix { get; init; } = "lunos-session-";
    public string SessionLabel { get; init; } = "lunos.session=1";

    // The persistent home: one named volume per owner, mounted into every
    // session at the container's home path. The home is the durable primitive;
    // the container is a disposable view of it (docs/ai-native-ui.md D3).
    public string HomeVolumePrefix { get; init; } = "lunos-home-";
    public string HomePath { get; init; } = "/config";

    // The per-owner shared workspace: one volume per owner-user, mounted at
    // <home>/shared into that user's sessions AND all their agents' sessions.
    // This is where the agent leaves work for the user and the user leaves
    // inputs for the agent — distinct from the private home.
    public string SharedVolumePrefix { get; init; } = "lunos-shared-";

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
    // Where a desktop session should point its "CieloOS Chat" launcher. This is NOT
    // the panel's Chat:Url: from inside a rootless-podman session the host's loopback
    // is the container's own, so the URL must name the host (podman publishes it as
    // host.containers.internal). Empty disables the launcher.
    public string ChatUrl { get; init; } = "";
}

// The V0.4 session backend: one podman container per desktop session, driven
// through the same policy-checked bus as every other command. The production
// target (docs/ai-native-ui.md D3) swaps podman for Incus system containers;
// the command shape and policy path are identical, so only this executor moves.
public sealed class SessionOrchestrator : ISurfaceExecutor, ISessionBackend, IConsoleBackend, IDesktopBackend
{
    private readonly SessionBackendOptions options;

    // Maps a session-owner slug to the owner-USER slug whose shared workspace to
    // mount (a user maps to itself; an agent to its owner). Null disables the
    // shared mount (e.g. in tests). Injected because the orchestrator has no
    // identity store of its own.
    private readonly Func<string, string?>? resolveSharedOwner;

    // Maps a session-owner slug to that desk's profile (issue #15), so a .NET
    // developer's session starts from the developer image and an office desk from
    // the shared one. Null keeps the single-image behaviour, which is what tests
    // and a machine with no profiles want.
    private readonly Func<string, DeskProfile>? resolveDeskProfile;

    // Profile images are built lazily and a build takes minutes, so a build is a
    // background job with a status the panel can read, not something a request
    // waits on.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> imageBuilds = new(StringComparer.Ordinal);

    public SessionOrchestrator(
        SessionBackendOptions options,
        Func<string, string?>? resolveSharedOwner = null,
        Func<string, DeskProfile>? resolveDeskProfile = null)
    {
        this.options = options;
        this.resolveSharedOwner = resolveSharedOwner;
        this.resolveDeskProfile = resolveDeskProfile;
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
        // The console is the same lightweight terminal for every desk; only the
        // desktop carries a toolchain, so only the desktop varies by desk profile.
        var desk = resolveDeskProfile?.Invoke(owner) ?? DeskProfiles.Default;
        var image = isConsole ? options.ConsoleImage : desk.Image;
        var containerPort = isConsole ? options.ConsolePort : options.ViewportPort;
        var homePath = isConsole ? options.ConsoleHomePath : options.HomePath;

        // The per-owner home volume is created once and outlives every session,
        // and is the SAME volume whether viewed as a console or a desktop.
        await RunPodmanAsync(new[] { "volume", "create", homeVolume }, cancellationToken);

        var runArguments = new List<string>
        {
            "run", "-d",
            // Survive a host reboot: podman-restart.service brings back anything with a
            // restart policy, and "unless-stopped" means a session the owner explicitly
            // destroyed stays destroyed.
            "--restart", "unless-stopped",
            "--name", name,
            "--label", options.SessionLabel,
            "--label", $"lunos.owner={owner}",
            "--label", $"lunos.profile={profile}",
            "--label", $"lunos.kind={(isConsole ? "console" : "desktop")}",
            "-v", $"{homeVolume}:{homePath}"
        };

        // Mount the owner's shared workspace at <home>/shared, so a user and all
        // their agents meet in one place (nested under the home mount — podman
        // orders mounts by destination depth).
        var sharedOwner = resolveSharedOwner?.Invoke(owner);
        if (!string.IsNullOrEmpty(sharedOwner))
        {
            var sharedVolume = options.SharedVolumePrefix + sharedOwner;
            await RunPodmanAsync(new[] { "volume", "create", sharedVolume }, cancellationToken);
            runArguments.Add("-v");
            runArguments.Add($"{sharedVolume}:{homePath}/shared");
        }

        runArguments.Add("-p");
        runArguments.Add($"127.0.0.1::{containerPort}");
        runArguments.Add(image);

        // A missing image is the expected state right after an --offline/--skip-images
        // install, while cielo-session-images.service is still building. Say that,
        // rather than surfacing raw podman stderr about an unknown image.
        // Only for localhost/ tags: those can never be pulled, so a miss is fatal and
        // worth explaining. A remote reference is left to podman, which pulls it on run.
        var localOnlyImage = image.StartsWith("localhost/", StringComparison.Ordinal);
        var imagePresent = localOnlyImage
            ? await RunPodmanAsync(new[] { "image", "exists", image }, cancellationToken)
            : (ExitCode: 0, Stdout: "", Stderr: "");
        if (imagePresent.ExitCode != 0)
        {
            // A desk profile's own image is built on demand, so "not there yet" is
            // an ordinary state with a specific remedy — say which one, rather than
            // pointing at the session-image builder that does not build it.
            if (!isConsole && desk.NeedsOwnImage)
            {
                var status = imageBuilds.TryGetValue(desk.Id, out var state) ? state : "not built";
                return new ToolExecutionResult(false,
                    $"The {desk.Label} desk image is {status}. It is built on demand because it is large; " +
                    "start it from the panel's desk list, or run " +
                    $"/usr/local/bin/cielo-build-desk-image {desk.Id}.", null);
            }

            return new ToolExecutionResult(false,
                $"The {(isConsole ? "console" : "desktop")} session image '{image}' is not available yet. If this machine was just " +
                "installed it is still building (systemctl status cielo-session-images); otherwise build " +
                "it with /usr/local/bin/cielo-build-session-images.", null);
        }

        var run = await RunPodmanAsync(runArguments.ToArray(), cancellationToken);

        if (run.ExitCode != 0)
        {
            return new ToolExecutionResult(false, $"Failed to start {profile} session: {run.Stderr.Trim()}", null);
        }

        if (!isConsole)
        {
            // Give the desktop its launchers (menu + desktop icon) and the shared
            // workspace directory. Best-effort: never fail the session over a launcher.
            try
            {
                await ProvisionDesktopLaunchersAsync(name, cancellationToken);
            }
            catch
            {
                // ignored
            }
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

    public async Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken) =>
        (await RunPodmanAsync(new[] { "image", "exists", image }, cancellationToken)).ExitCode == 0;

    public string BuildStatus(string profileId) =>
        imageBuilds.TryGetValue(profileId, out var status) ? status : "";

    // Starts a profile's image build and returns immediately: these take minutes
    // and gigabytes, so the caller gets a status to poll rather than a request that
    // hangs. Output goes to a log file, because a build that failed at 03:00 is
    // only debuggable if it said why.
    public bool StartImageBuild(DeskProfile profile, string imagesRoot, string? buildArgument = null)
    {
        if (!profile.NeedsOwnImage)
        {
            return false;
        }

        // One build at a time per profile; a second click must not start a second
        // multi-gigabyte build over the top of the first.
        if (!imageBuilds.TryAdd(profile.Id, "building"))
        {
            return imageBuilds[profile.Id] == "building";
        }

        var context = Path.Combine(imagesRoot, profile.Id);
        var log = Path.Combine(Path.GetTempPath(), $"cielo-desk-image-{profile.Id}.log");

        _ = Task.Run(async () =>
        {
            try
            {
                var arguments = new List<string> { "build", "-t", profile.Image };
                if (!string.IsNullOrEmpty(buildArgument))
                {
                    arguments.Add("--build-arg");
                    arguments.Add(buildArgument);
                }
                arguments.Add(context);

                var result = await RunPodmanAsync(arguments.ToArray(), CancellationToken.None);
                await File.WriteAllTextAsync(log, result.Stdout + Environment.NewLine + result.Stderr);
                imageBuilds[profile.Id] = result.ExitCode == 0 ? "built" : $"failed (see {log})";
            }
            catch (Exception exception)
            {
                imageBuilds[profile.Id] = $"failed: {exception.Message}";
            }
        });

        return true;
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

        // Observe-after-act, WAITING FOR THE COMMAND TO FINISH rather than
        // guessing a fixed delay: poll until the shell is the foreground process
        // again (the prompt returned) and stays that way, bounded by a max wait.
        // A fixed short delay races slow commands (a web search, an install, a
        // build) and captures a blank or half-rendered screen.
        var screen = string.Empty;
        var readbackFailed = string.Empty;
        var idleStreak = 0;
        var sawBusy = false;
        for (var i = 0; i < 90; i++)
        {
            await Task.Delay(200, cancellationToken);
            var poll = await RunPodmanAsync(new[] { "exec", name, "bash", "-c",
                $"tmux display-message -p -t {options.ConsoleTmuxSession} '#{{pane_current_command}}'; echo '<<<L>>>'; tmux capture-pane -p -t {options.ConsoleTmuxSession}" }, cancellationToken);
            if (poll.ExitCode != 0) { readbackFailed = poll.Stderr.Trim(); break; }

            var split = poll.Stdout.Split("<<<L>>>", 2);
            var foreground = split[0].Trim();
            if (split.Length > 1) { screen = split[1].Trim('\n'); }

            var idle = foreground is "bash" or "-bash" or "sh" or "zsh";
            if (idle) { idleStreak++; } else { idleStreak = 0; sawBusy = true; }
            if (idleStreak >= 2 && (sawBusy || i >= 4)) { break; }
        }

        var detail = submit ? "Typed and submitted." : "Typed.";
        if (!string.IsNullOrEmpty(readbackFailed))
        {
            detail += $" (screen readback unavailable: {readbackFailed})";
        }
        return new ConsoleActionResult(true, screen, detail);
    }

    // --- Desktop backend: observe with scrot, act with xdotool, over podman exec.
    // The desktop containers run an X server on :1; input/keys cross to podman as
    // single argv entries (no host shell), so there is no host-side injection, and
    // the blast radius is that session's container + home. ---
    private const string DesktopDisplayEnv = "DISPLAY=:1";

    public async Task<DesktopShot> ScreenshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await NotDesktopAsync(sessionId, cancellationToken) is { } bad)
        {
            return new DesktopShot(sessionId, Array.Empty<byte>(), 0, 0, false, bad.Detail);
        }

        var name = options.NamePrefix + sessionId;
        var capture = await RunPodmanAsync(new[] { "exec", "-e", DesktopDisplayEnv, name, "scrot", "-o", "/tmp/_wr_shot.png" }, cancellationToken);
        if (capture.ExitCode != 0)
        {
            return new DesktopShot(sessionId, Array.Empty<byte>(), 0, 0, false, $"Could not capture screen: {capture.Stderr.Trim()}");
        }

        // Read the PNG back as base64 so it survives the text stdout pipe intact.
        var encoded = await RunPodmanAsync(new[] { "exec", name, "base64", "-w0", "/tmp/_wr_shot.png" }, cancellationToken);
        if (encoded.ExitCode != 0)
        {
            return new DesktopShot(sessionId, Array.Empty<byte>(), 0, 0, false, $"Could not read screenshot: {encoded.Stderr.Trim()}");
        }

        byte[] png;
        try
        {
            png = Convert.FromBase64String(encoded.Stdout.Trim());
        }
        catch (FormatException)
        {
            return new DesktopShot(sessionId, Array.Empty<byte>(), 0, 0, false, "Screenshot was not valid image data.");
        }

        var (width, height) = PngSize(png);
        return new DesktopShot(sessionId, png, width, height, true, null);
    }

    // AT-SPI-first perception: run the in-container accessibility reader (lunos-atspi,
    // as the desktop user so it reaches the session a11y bus) and parse its JSON.
    public async Task<DesktopElements> ElementsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await NotDesktopAsync(sessionId, cancellationToken) is { } bad)
        {
            return new DesktopElements(sessionId, Array.Empty<DesktopElement>(), false, bad.Detail);
        }

        var name = options.NamePrefix + sessionId;
        var read = await RunPodmanAsync(new[] { "exec", "-u", "abc", name, "lunos-atspi", "all" }, cancellationToken);
        if (read.ExitCode != 0)
        {
            return new DesktopElements(sessionId, Array.Empty<DesktopElement>(), false, $"Accessibility read failed: {read.Stderr.Trim()}");
        }

        try
        {
            var elements = JsonSerializer.Deserialize<List<DesktopElement>>(
                read.Stdout, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new List<DesktopElement>();
            return new DesktopElements(sessionId, elements, true, null);
        }
        catch (JsonException)
        {
            return new DesktopElements(sessionId, Array.Empty<DesktopElement>(), false, "Accessibility output was not valid JSON.");
        }
    }

    public async Task<DesktopActionResult> ClickAsync(string sessionId, int x, int y, int button, int repeat, CancellationToken cancellationToken)
    {
        if (await NotDesktopAsync(sessionId, cancellationToken) is { } bad)
        {
            return bad;
        }

        var name = options.NamePrefix + sessionId;
        var args = repeat > 1
            ? new[] { "exec", "-e", DesktopDisplayEnv, name, "xdotool", "mousemove", x.ToString(), y.ToString(), "click", "--repeat", repeat.ToString(), button.ToString() }
            : new[] { "exec", "-e", DesktopDisplayEnv, name, "xdotool", "mousemove", x.ToString(), y.ToString(), "click", button.ToString() };
        var result = await RunPodmanAsync(args, cancellationToken);
        return result.ExitCode == 0
            ? new DesktopActionResult(true, $"Clicked ({x}, {y}) button {button}{(repeat > 1 ? $" x{repeat}" : "")}.")
            : new DesktopActionResult(false, $"Click failed: {result.Stderr.Trim()}");
    }

    public async Task<DesktopActionResult> TypeTextAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        if (await NotDesktopAsync(sessionId, cancellationToken) is { } bad)
        {
            return bad;
        }

        var name = options.NamePrefix + sessionId;
        var result = await RunPodmanAsync(new[] { "exec", "-e", DesktopDisplayEnv, name, "xdotool", "type", "--clearmodifiers", "--", text }, cancellationToken);
        return result.ExitCode == 0
            ? new DesktopActionResult(true, $"Typed {text.Length} character(s).")
            : new DesktopActionResult(false, $"Type failed: {result.Stderr.Trim()}");
    }

    public async Task<DesktopActionResult> KeyAsync(string sessionId, string keysym, CancellationToken cancellationToken)
    {
        if (await NotDesktopAsync(sessionId, cancellationToken) is { } bad)
        {
            return bad;
        }

        var name = options.NamePrefix + sessionId;
        // `--` terminates options so the backend is argv-safe regardless of the manifest.
        var result = await RunPodmanAsync(new[] { "exec", "-e", DesktopDisplayEnv, name, "xdotool", "key", "--clearmodifiers", "--", keysym }, cancellationToken);
        return result.ExitCode == 0
            ? new DesktopActionResult(true, $"Pressed key '{keysym}'.")
            : new DesktopActionResult(false, $"Key failed: {result.Stderr.Trim()}");
    }

    // Returns a failure result if the session is missing or not a desktop; null when OK.
    private async Task<DesktopActionResult?> NotDesktopAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = (await ListAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == sessionId);
        if (session is null)
        {
            return new DesktopActionResult(false, "Session not found.");
        }
        if (!string.Equals(session.Kind, "desktop", StringComparison.Ordinal))
        {
            return new DesktopActionResult(false, "Only desktop sessions accept pointer/keyboard input.");
        }
        return null;
    }

    // Width/height live big-endian in the PNG IHDR: 8-byte signature, then
    // length(4)+"IHDR"(4), then width at offset 16, height at offset 20.
    private static (int Width, int Height) PngSize(byte[] png)
    {
        if (png.Length < 24)
        {
            return (0, 0);
        }
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (width, height);
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

    // The chat launcher: a chromium app-mode window on the OS's chat UI. --no-sandbox
    // is required because chromium's own sandbox cannot nest inside the rootless
    // container; the container is the isolation boundary here.
    private const string ChatLauncherDesktop = """
[Desktop Entry]
Version=1.0
Type=Application
Name=CieloOS Chat
Comment=Chat with your agent
Exec=chromium "--app=CHAT_URL_PLACEHOLDER" --no-sandbox --no-first-run --no-default-browser-check
Icon=user-available
Terminal=false
Categories=Network;
""";

    // Writes the launcher into the desktop's home (base64 to sidestep all shell
    // quoting) as the container user, so it shows in the Applications menu and on
    // the Desktop.
    private async Task ProvisionDesktopLaunchersAsync(string name, CancellationToken cancellationToken)
    {
        // The shared workspace must exist even with no launchers: it is how the
        // owner and the agent exchange files.
        await RunPodmanAsync(new[] { "exec", "-u", "abc", name, "bash", "-c",
            "mkdir -p \"$HOME/.local/share/applications\" \"$HOME/Desktop\" \"$HOME/shared\"" }, cancellationToken);

        // A desktop icon for the chat UI, when the OS knows where its chat lives.
        if (!string.IsNullOrWhiteSpace(options.ChatUrl))
        {
            var chatLauncher = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                ChatLauncherDesktop.Replace("CHAT_URL_PLACEHOLDER", EscapeExecArgument(options.ChatUrl)).ReplaceLineEndings("\n")));
            var chatCommand =
                $"echo {chatLauncher} | base64 -d > \"$HOME/.local/share/applications/cielo-chat.desktop\"; " +
                $"echo {chatLauncher} | base64 -d > \"$HOME/Desktop/CieloOS-Chat.desktop\"; " +
                "chmod +x \"$HOME/Desktop/CieloOS-Chat.desktop\"";
            await RunPodmanAsync(new[] { "exec", "-u", "abc", name, "bash", "-c", chatCommand }, cancellationToken);
        }
    }

    // Desktop Entry Exec quoting: the URL sits inside a double-quoted argument, so a
    // backslash, quote, dollar or backtick has to be escaped there, and a literal percent
    // must be doubled because % introduces a field code. Without this, a URL carrying
    // ?, &, a space or a percent escape splits the argument or is silently rewritten.
    private static string EscapeExecArgument(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("$", "\\$")
        .Replace("`", "\\`")
        .Replace("%", "%%");

    private static string Required(ToolRequest request, string key) =>
        request.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument '{key}'.");
}
