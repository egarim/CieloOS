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

// A running desktop session, as reported by the session backend.
public sealed record DesktopSession(
    string Id,
    string Owner,
    string Profile,
    string Status,
    int ViewportPort);

public interface ISessionBackend
{
    Task<IReadOnlyList<DesktopSession>> ListAsync(CancellationToken cancellationToken);
}
