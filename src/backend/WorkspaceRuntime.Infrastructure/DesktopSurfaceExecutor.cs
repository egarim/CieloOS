using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// Routes the `desktop` surface's input commands to the desktop backend, so an
// agent's pointer/keyboard actions ride the same manifest-checked, ownership-
// gated, audited bus as `console.type`. Observation is a gated read (the
// /screenshot endpoint); this executor owns the state-changing half.
public sealed class DesktopSurfaceExecutor : ISurfaceExecutor
{
    private readonly IDesktopBackend desktop;

    public DesktopSurfaceExecutor(IDesktopBackend desktop)
    {
        this.desktop = desktop;
    }

    public string SurfaceId => "desktop";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var id = Required(request, "id");
        switch (request.Operation)
        {
            case "click":
            case "double_click":
            {
                var x = Int(request, "x");
                var y = Int(request, "y");
                var button = request.Arguments.TryGetValue("button", out var b) && int.TryParse(b, out var bi) ? bi : 1;
                var repeat = request.Operation == "double_click" ? 2 : 1;
                var result = await desktop.ClickAsync(id, x, y, button, repeat, cancellationToken);
                return new ToolExecutionResult(result.Ok, result.Detail, null);
            }

            case "type":
            {
                var text = request.Arguments.GetValueOrDefault("text", "");
                // Bound the payload as the console does — reject rather than truncate.
                if (text.Length > 4096)
                {
                    return new ToolExecutionResult(false, "Desktop input exceeds the 4096-character limit.", null);
                }
                var result = await desktop.TypeTextAsync(id, text, cancellationToken);
                return new ToolExecutionResult(result.Ok, result.Detail, null);
            }

            case "key":
            {
                var keysym = Required(request, "keysym");
                var result = await desktop.KeyAsync(id, keysym, cancellationToken);
                return new ToolExecutionResult(result.Ok, result.Detail, null);
            }

            default:
                return new ToolExecutionResult(false, $"Desktop executor rejected unknown operation '{request.Operation}'.", null);
        }
    }

    public Task<EffectPreview> PreviewAsync(ToolRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new EffectPreview(
            true,
            $"Would send desktop '{request.Operation}' to session '{request.Arguments.GetValueOrDefault("id")}'.",
            Array.Empty<CellChange>()));

    private static int Int(ToolRequest request, string key) =>
        request.Arguments.TryGetValue(key, out var value) && int.TryParse(value, out var number)
            ? number
            : throw new ArgumentException($"Argument '{key}' must be an integer.");

    private static string Required(ToolRequest request, string key) =>
        request.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument '{key}'.");
}
