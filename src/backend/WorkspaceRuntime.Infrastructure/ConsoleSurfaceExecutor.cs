using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// Routes the `console` surface's commands to the console backend, so an agent's
// keystrokes ride the same manifest-checked, ownership-gated, audited bus as
// every other action. Observation is a gated read (see the /console endpoint);
// this executor owns the state-changing half.
public sealed class ConsoleSurfaceExecutor : ISurfaceExecutor
{
    private readonly IConsoleBackend console;

    public ConsoleSurfaceExecutor(IConsoleBackend console)
    {
        this.console = console;
    }

    public string SurfaceId => "console";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case "type":
                var id = Required(request, "id");
                var text = request.Arguments.GetValueOrDefault("text", "");
                // Press Enter by default; a caller can hold the line open with
                // submit=false to build a command up in pieces before running it.
                var submit = !request.Arguments.TryGetValue("submit", out var flag)
                    || !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase);
                var result = await console.TypeAsync(id, text, submit, cancellationToken);
                return new ToolExecutionResult(result.Ok, result.Ok ? $"{result.Detail}\n{result.Screen}" : result.Detail, null);

            default:
                return new ToolExecutionResult(false, $"Console executor rejected unknown operation '{request.Operation}'.", null);
        }
    }

    public Task<EffectPreview> PreviewAsync(ToolRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new EffectPreview(
            true,
            $"Would type into console '{request.Arguments.GetValueOrDefault("id")}' and read the screen back.",
            Array.Empty<CellChange>()));

    private static string Required(ToolRequest request, string key) =>
        request.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument '{key}'.");
}
