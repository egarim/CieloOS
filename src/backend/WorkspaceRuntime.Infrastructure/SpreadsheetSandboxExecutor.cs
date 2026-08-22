using System.Globalization;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

public sealed class SpreadsheetSandboxExecutor : ISurfaceExecutor
{
    public static readonly IReadOnlySet<string> SupportedOperations =
        new HashSet<string>(StringComparer.Ordinal) { "set-cell", "sum", "clear" };

    public string SurfaceId => "spreadsheet";

    private readonly IRuntimeStore store;

    public SpreadsheetSandboxExecutor(IRuntimeStore store)
    {
        this.store = store;
    }

    public Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        if (!TryCompute(request, out var cells, out var error))
        {
            return Task.FromResult(new ToolExecutionResult(false, error!, store.Spreadsheet));
        }

        var spreadsheet = new SpreadsheetState(cells!);
        store.SetSpreadsheet(spreadsheet);
        return Task.FromResult(new ToolExecutionResult(true, $"Executed spreadsheet.{request.Operation} in sandbox.", spreadsheet));
    }

    // Dry runs share the exact computation with execution and never commit,
    // so the preview a human approves is the change that will actually happen.
    public Task<EffectPreview> PreviewAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        if (!TryCompute(request, out var cells, out var error))
        {
            return Task.FromResult(new EffectPreview(false, error!, Array.Empty<CellChange>()));
        }

        var before = store.Spreadsheet.Cells;
        var changes = new List<CellChange>();
        foreach (var address in before.Keys.Union(cells!.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.Ordinal))
        {
            var oldValue = before.TryGetValue(address, out var previous) ? previous : null;
            var newValue = cells.TryGetValue(address, out var next) ? next : null;
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                changes.Add(new CellChange(address.ToUpperInvariant(), oldValue, newValue));
            }
        }

        var summary = changes.Count == 0
            ? "No cells would change."
            : $"{changes.Count} cell{(changes.Count == 1 ? "" : "s")} would change.";
        return Task.FromResult(new EffectPreview(true, summary, changes));
    }

    private bool TryCompute(ToolRequest request, out Dictionary<string, string>? result, out string? error)
    {
        result = null;
        error = null;

        if (request.ToolName != "spreadsheet")
        {
            error = "Executor sandbox rejected unknown tool.";
            return false;
        }

        var cells = new Dictionary<string, string>(store.Spreadsheet.Cells, StringComparer.OrdinalIgnoreCase);
        switch (request.Operation)
        {
            case "set-cell":
                var address = Required(request, "address").ToUpperInvariant();
                var value = Required(request, "value");
                cells[address] = value;
                break;

            case "sum":
                var source = Required(request, "source").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var target = Required(request, "target").ToUpperInvariant();
                var sum = source.Select(cell => decimal.TryParse(cells.GetValueOrDefault(cell.ToUpperInvariant()), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0).Sum();
                cells[target] = sum.ToString(CultureInfo.InvariantCulture);
                break;

            case "clear":
                cells.Clear();
                break;

            default:
                error = "Executor sandbox rejected unknown operation.";
                return false;
        }

        result = cells;
        return true;
    }

    private static string Required(ToolRequest request, string key) =>
        request.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument '{key}'.");
}
