using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public class SurfaceContractTests
{
    [Fact]
    public void Spreadsheet_surface_manifest_loads_and_is_well_formed()
    {
        var registry = TestRepository.Surfaces();
        var manifest = registry.Find("spreadsheet");

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.Schema);
        Assert.Equal("surface", manifest.Kind);
        Assert.True(manifest.State.Revisioned);
        Assert.Contains("set-cell", manifest.Commands.Keys);
        Assert.Contains("sum", manifest.Commands.Keys);
        Assert.Contains("clear", manifest.Commands.Keys);
    }

    // The drift guard: every command a manifest declares must be an operation
    // the executor actually implements. A manifest that promises more than the
    // executor delivers is a lie the whole contract depends on preventing.
    [Fact]
    public void Every_manifest_command_is_implemented_by_the_executor()
    {
        var registry = TestRepository.Surfaces();
        var manifest = registry.Find("spreadsheet")!;

        foreach (var command in manifest.Commands.Keys)
        {
            Assert.Contains(command, SpreadsheetSandboxExecutor.SupportedOperations);
        }
    }

    [Fact]
    public void Request_hash_is_deterministic_and_argument_order_independent()
    {
        var a = new ToolRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["address"] = "C1", ["value"] = "42" }, DateTimeOffset.UtcNow);
        var b = new ToolRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["value"] = "42", ["address"] = "C1" }, DateTimeOffset.UtcNow);
        var c = new ToolRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["address"] = "C1", ["value"] = "43" }, DateTimeOffset.UtcNow);

        Assert.Equal(RequestHasher.Compute(a), RequestHasher.Compute(b));
        Assert.NotEqual(RequestHasher.Compute(a), RequestHasher.Compute(c));
    }

    [Fact]
    public async Task Dry_run_preview_shows_the_change_without_committing_it()
    {
        var store = new InMemoryRuntimeStore();
        var executor = new SpreadsheetSandboxExecutor(store);
        var request = new ToolRequest(Guid.NewGuid(), store.Users[0].Id, store.Agents[0].Id, "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["address"] = "A1", ["value"] = "99" }, DateTimeOffset.UtcNow);

        var preview = await executor.PreviewAsync(request, CancellationToken.None);

        Assert.True(preview.Supported);
        var change = Assert.Single(preview.Changes);
        Assert.Equal("A1", change.Address);
        Assert.Equal("12", change.Before);
        Assert.Equal("99", change.After);
        Assert.Equal("12", store.Spreadsheet.Cells["A1"]);
        Assert.Equal(0, store.SpreadsheetRevision);
    }

    [Fact]
    public async Task Clear_preview_lists_every_removed_cell()
    {
        var store = new InMemoryRuntimeStore();
        var executor = new SpreadsheetSandboxExecutor(store);
        var request = new ToolRequest(Guid.NewGuid(), store.Users[0].Id, store.Agents[0].Id, "spreadsheet", "clear",
            new Dictionary<string, string>(), DateTimeOffset.UtcNow);

        var preview = await executor.PreviewAsync(request, CancellationToken.None);

        Assert.True(preview.Supported);
        Assert.Equal(store.Spreadsheet.Cells.Count, preview.Changes.Count);
        Assert.All(preview.Changes, change => Assert.Null(change.After));
    }

    [Fact]
    public void Revision_increments_on_every_committed_change()
    {
        var store = new InMemoryRuntimeStore();
        Assert.Equal(0, store.SpreadsheetRevision);

        store.SetSpreadsheet(new SpreadsheetState(new Dictionary<string, string> { ["A1"] = "1" }));
        store.SetSpreadsheet(new SpreadsheetState(new Dictionary<string, string> { ["A1"] = "2" }));

        Assert.Equal(2, store.SpreadsheetRevision);
    }
}
