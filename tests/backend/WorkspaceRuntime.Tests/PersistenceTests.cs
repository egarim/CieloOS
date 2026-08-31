using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"workspace-runtime-tests-{Guid.NewGuid():N}.db");

    private EfRuntimeStore CreateStore()
    {
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new EfRuntimeStore(new PooledDbContextFactory<RuntimeDbContext>(options), ensureCreated: true);
    }

    [Fact]
    public void Store_seeds_demo_data_once()
    {
        var first = CreateStore();
        var second = CreateStore();

        Assert.Equal(2, second.Users.Count);
        Assert.Equal(2, second.Workspaces.Count);
        Assert.Equal(2, second.Agents.Count);
        Assert.Contains(second.Users, user => user.Slug == "joche");
        Assert.Contains(second.Users, user => user.Slug == "yulia");
        Assert.Equal("12", second.GetSpreadsheet(second.Users[0].Slug).Cells["A1"]);
        // Seeded exactly once despite reopening — no duplicate seed rows.
        Assert.Single(second.AuditEvents, auditEvent => auditEvent.Action == "runtime.seed");
        _ = first;
    }

    [Fact]
    public async Task Executed_operations_survive_a_restart()
    {
        var store = CreateStore();
        var runtime = new AgentRuntime(store, TestRepository.PolicyEngine(), new SpreadsheetSandboxExecutor(store), TestRepository.Surfaces());
        var user = store.Users[0];
        var agent = store.Agents[0];

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id,
            agent.Id,
            "spreadsheet",
            "set-cell",
            new Dictionary<string, string> { ["address"] = "C1", ["value"] = "42" }), TestRepository.AgentPrincipal(store), CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, result.Decision);

        var reopened = CreateStore();
        Assert.Equal("42", reopened.GetSpreadsheet(reopened.Users[0].Slug).Cells["C1"]);
        Assert.Equal(1, reopened.GetSpreadsheetRevision(reopened.Users[0].Slug));
        Assert.Contains(reopened.AuditEvents, auditEvent => auditEvent.Action == "spreadsheet.set-cell" && auditEvent.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Pending_approval_survives_a_restart_and_can_be_resolved()
    {
        var store = CreateStore();
        var runtime = new AgentRuntime(store, TestRepository.PolicyEngine(), new SpreadsheetSandboxExecutor(store), TestRepository.Surfaces());
        var user = store.Users[0];
        var agent = store.Agents[0];

        var pending = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id,
            agent.Id,
            "spreadsheet",
            "clear",
            new Dictionary<string, string>()), TestRepository.AgentPrincipal(store), CancellationToken.None);
        Assert.Equal(PolicyDecision.RequireApproval, pending.Decision);
        Assert.NotNull(pending.Approval);

        var reopened = CreateStore();
        var reopenedRuntime = new AgentRuntime(reopened, TestRepository.PolicyEngine(), new SpreadsheetSandboxExecutor(reopened), TestRepository.Surfaces());

        Assert.Contains(reopened.Approvals, approval =>
            approval.Id == pending.Approval!.Id
            && approval.Status == ApprovalStatus.Pending
            && approval.RequestHash == pending.Approval.RequestHash);

        var approved = await reopenedRuntime.ResolveApprovalAsync(
            pending.Approval!.Id, approved: true, pending.Approval.RequestHash, TestRepository.HumanPrincipal(store), null, CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, approved.Decision);
        Assert.Empty(reopened.GetSpreadsheet(reopened.Users[0].Slug).Cells);
        Assert.Contains(reopened.Approvals, approval => approval.Id == pending.Approval.Id && approval.Status == ApprovalStatus.Approved);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }
}
