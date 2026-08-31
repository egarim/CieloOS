using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public class VersioningTests
{
    [Fact]
    public void Undo_policy_snapshots_before_non_reversible_but_not_reversible()
    {
        var surfaces = TestRepository.Surfaces();
        var reversible = surfaces.Find("spreadsheet")!.Commands["set-cell"];
        var nonReversible = surfaces.Find("spreadsheet")!.Commands["clear"];

        Assert.False(UndoPolicy.ShouldSnapshot(reversible));
        Assert.True(UndoPolicy.ShouldSnapshot(nonReversible));
    }

    [Fact]
    public async Task In_memory_store_records_lists_and_restores_a_snapshot()
    {
        var store = new InMemoryVersionStore();
        var order = Guid.NewGuid();
        var recorded = await store.RecordBeforeAsync("joche", order, "console.type", CancellationToken.None);

        Assert.Equal("joche", recorded.OwnerSlug);
        Assert.Equal("console.type", recorded.Action);
        Assert.Contains(await store.ListAsync("joche", CancellationToken.None), s => s.Id == recorded.Id);
        Assert.True(await store.RestoreAsync("joche", recorded.Id, CancellationToken.None));
        Assert.False(await store.RestoreAsync("joche", Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Versioning_relaxes_non_reversible_to_allow_and_snapshots_before_it_runs()
    {
        var store = new InMemoryRuntimeStore();
        var versionStore = new InMemoryVersionStore();
        var runtime = new AgentRuntime(store, TestRepository.PolicyEngine(), new SpreadsheetSandboxExecutor(store),
            TestRepository.Surfaces(), versionStore: versionStore);
        var user = store.Users[0];
        var agent = store.Agents[0];

        // spreadsheet.clear is non-reversible and normally RequireApproval.
        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id, agent.Id, "spreadsheet", "clear", new Dictionary<string, string>()),
            TestRepository.AgentPrincipal(store), CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        var snapshots = await versionStore.ListAsync(user.Slug, CancellationToken.None);
        Assert.Contains(snapshots, s => s.Action == "spreadsheet.clear" && s.OwnerSlug == user.Slug);
    }

    [Fact]
    public async Task Without_a_version_store_non_reversible_stays_require_approval()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = new AgentRuntime(store, TestRepository.PolicyEngine(), new SpreadsheetSandboxExecutor(store),
            TestRepository.Surfaces());
        var user = store.Users[0];
        var agent = store.Agents[0];

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id, agent.Id, "spreadsheet", "clear", new Dictionary<string, string>()),
            TestRepository.AgentPrincipal(store), CancellationToken.None);

        Assert.Equal(PolicyDecision.RequireApproval, result.Decision);
    }
}
