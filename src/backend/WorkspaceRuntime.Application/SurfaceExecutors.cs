using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// A surface executor owns one surface id and provides both execution and
// dry-run preview for it. The router dispatches by ToolName so every surface
// shares the one policy-checked bus.
public interface ISurfaceExecutor : ISandboxedToolExecutor, IDryRunToolExecutor
{
    string SurfaceId { get; }
}

public sealed class SurfaceExecutorRouter : ISandboxedToolExecutor, IDryRunToolExecutor
{
    private readonly IReadOnlyDictionary<string, ISurfaceExecutor> executors;

    public SurfaceExecutorRouter(IEnumerable<ISurfaceExecutor> executors)
    {
        this.executors = executors.ToDictionary(executor => executor.SurfaceId, StringComparer.Ordinal);
    }

    public Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken) =>
        executors.TryGetValue(request.ToolName, out var executor)
            ? executor.ExecuteAsync(request, cancellationToken)
            : Task.FromResult(new ToolExecutionResult(false, $"No executor is registered for surface '{request.ToolName}'.", null));

    public Task<EffectPreview> PreviewAsync(ToolRequest request, CancellationToken cancellationToken) =>
        executors.TryGetValue(request.ToolName, out var executor)
            ? executor.PreviewAsync(request, cancellationToken)
            : Task.FromResult(new EffectPreview(false, $"No preview is available for surface '{request.ToolName}'.", Array.Empty<CellChange>()));
}

// A running session, as reported by the session backend. Kind is "console"
// or "desktop" — two views of the same per-owner home.
public sealed record DesktopSession(
    string Id,
    string Owner,
    string Profile,
    string Status,
    int ViewportPort,
    string Kind = "desktop");

public interface ISessionBackend
{
    Task<IReadOnlyList<DesktopSession>> ListAsync(CancellationToken cancellationToken);

    // Whether a desk profile's image is on this machine yet. Profile images are
    // built lazily (issue #15) — installing a multi-gigabyte developer image on
    // every machine to serve one office user is the wrong default — so the panel
    // needs to say "not built yet" instead of offering a desk that cannot start.
    Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken);
}

// What an agent (or a watching human) sees on a console session's screen, and
// the result of acting on it. Screen is the current terminal contents.
public sealed record ConsoleView(string SessionId, string Screen, bool Available, string? Detail = null);

public sealed record ConsoleActionResult(bool Ok, string Screen, string Detail);

// The agent's eyes and hands on its own console session: observe the terminal,
// and type into it. Backed by tmux capture-pane / send-keys over the session
// container, so a human inhabiting the same tmux sees the agent act live. Every
// state-changing use rides the policy-checked bus (surface "console"); observe
// is a gated read, like the home browser.
public interface IConsoleBackend
{
    Task<ConsoleView> CaptureAsync(string sessionId, CancellationToken cancellationToken);
    Task<ConsoleActionResult> TypeAsync(string sessionId, string text, bool submit, CancellationToken cancellationToken);
}
