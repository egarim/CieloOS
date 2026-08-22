using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public class PolicyAndRuntimeTests
{
    [Fact]
    public void Policy_allows_granted_spreadsheet_cell_write()
    {
        var store = new InMemoryRuntimeStore();
        var user = store.Users[0];
        var agent = store.Agents[0];
        var request = new ToolRequest(Guid.NewGuid(), user.Id, agent.Id, "spreadsheet", "set-cell", new Dictionary<string, string>(), DateTimeOffset.UtcNow);

        var evaluation = TestRepository.PolicyEngine().Evaluate(user, agent, request);

        Assert.Equal(PolicyDecision.Allow, evaluation.Decision);
        Assert.Equal("surface-manifest", evaluation.Evidence["rule"]);
    }

    [Fact]
    public void Policy_denies_unregistered_tool()
    {
        var store = new InMemoryRuntimeStore();
        var user = store.Users[0];
        var agent = store.Agents[0];
        var request = new ToolRequest(Guid.NewGuid(), user.Id, agent.Id, "email", "send", new Dictionary<string, string>(), DateTimeOffset.UtcNow);

        var evaluation = TestRepository.PolicyEngine().Evaluate(user, agent, request);

        Assert.Equal(PolicyDecision.Deny, evaluation.Decision);
    }

    [Fact]
    public void Policy_denies_unregistered_operation_on_granted_surface()
    {
        var store = new InMemoryRuntimeStore();
        var user = store.Users[0];
        var agent = store.Agents[0];
        var request = new ToolRequest(Guid.NewGuid(), user.Id, agent.Id, "spreadsheet", "drop-table", new Dictionary<string, string>(), DateTimeOffset.UtcNow);

        var evaluation = TestRepository.PolicyEngine().Evaluate(user, agent, request);

        Assert.Equal(PolicyDecision.Deny, evaluation.Decision);
        Assert.Equal("deny-by-default", evaluation.Evidence["rule"]);
    }

    [Fact]
    public void Policy_requires_approval_for_spreadsheet_clear()
    {
        var store = new InMemoryRuntimeStore();
        var user = store.Users[0];
        var agent = store.Agents[0];
        var request = new ToolRequest(Guid.NewGuid(), user.Id, agent.Id, "spreadsheet", "clear", new Dictionary<string, string>(), DateTimeOffset.UtcNow);

        var evaluation = TestRepository.PolicyEngine().Evaluate(user, agent, request);

        Assert.Equal(PolicyDecision.RequireApproval, evaluation.Decision);
    }

    [Fact]
    public async Task Runtime_executes_allowed_spreadsheet_operation_and_audits_it()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = CreateRuntime(store);
        var user = store.Users[0];
        var agent = store.Agents[0];

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id,
            agent.Id,
            "spreadsheet",
            "set-cell",
            new Dictionary<string, string> { ["address"] = "C1", ["value"] = "42" }), TestRepository.AgentPrincipal(store), CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.Equal("42", store.Spreadsheet.Cells["C1"]);
        Assert.Contains(store.AuditEvents, auditEvent =>
            auditEvent.Action == "spreadsheet.set-cell"
            && auditEvent.Outcome == AuditOutcome.Success
            && auditEvent.Principal == store.Agents[0].Slug
            && auditEvent.CorrelationId is not null);
    }

    [Fact]
    public async Task Runtime_creates_hash_bound_approval_then_executes_after_human_approval()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = CreateRuntime(store);
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
        Assert.NotEmpty(pending.Approval!.RequestHash);
        Assert.NotEmpty(store.Spreadsheet.Cells);

        var approved = await runtime.ResolveApprovalAsync(
            pending.Approval.Id, approved: true, pending.Approval.RequestHash, TestRepository.HumanPrincipal(store), null, CancellationToken.None);

        Assert.Equal(PolicyDecision.Allow, approved.Decision);
        Assert.Empty(store.Spreadsheet.Cells);
        Assert.Contains(store.Approvals, approval => approval.Id == pending.Approval.Id && approval.Status == ApprovalStatus.Approved);
    }

    [Fact]
    public async Task Approval_with_wrong_hash_is_rejected_and_stays_pending()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = CreateRuntime(store);
        var user = store.Users[0];
        var agent = store.Agents[0];

        var pending = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id,
            agent.Id,
            "spreadsheet",
            "clear",
            new Dictionary<string, string>()), TestRepository.AgentPrincipal(store), CancellationToken.None);

        await Assert.ThrowsAsync<StaleApprovalException>(() =>
            runtime.ResolveApprovalAsync(pending.Approval!.Id, approved: true, "not-the-real-hash", TestRepository.HumanPrincipal(store), null, CancellationToken.None));

        Assert.NotEmpty(store.Spreadsheet.Cells);
        Assert.Contains(store.Approvals, approval => approval.Id == pending.Approval!.Id && approval.Status == ApprovalStatus.Pending);
    }

    [Fact]
    public async Task Resolved_approval_cannot_be_resolved_again()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = CreateRuntime(store);
        var user = store.Users[0];
        var agent = store.Agents[0];

        var pending = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id,
            agent.Id,
            "spreadsheet",
            "clear",
            new Dictionary<string, string>()), TestRepository.AgentPrincipal(store), CancellationToken.None);
        await runtime.ResolveApprovalAsync(pending.Approval!.Id, approved: false, pending.Approval.RequestHash, TestRepository.HumanPrincipal(store), null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.ResolveApprovalAsync(pending.Approval.Id, approved: true, pending.Approval.RequestHash, TestRepository.HumanPrincipal(store), null, CancellationToken.None));
    }

    [Fact]
    public async Task Manifest_input_constraints_are_enforced_at_the_choke_point()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = CreateRuntime(store);
        var user = store.Users[0];
        var agent = store.Agents[0];

        // Address violates the manifest pattern ^[A-Za-z]+[0-9]+$ — this is the
        // raw bus path (no surface endpoint), so the gate must live in the runtime.
        var badAddress = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id, agent.Id, "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["address"] = "TOTALS ROW!!", ["value"] = "1" }), TestRepository.AgentPrincipal(store), CancellationToken.None);
        Assert.Equal(PolicyDecision.Deny, badAddress.Decision);
        Assert.DoesNotContain("TOTALS ROW!!", store.Spreadsheet.Cells.Keys);

        var tooLong = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id, agent.Id, "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["address"] = "C1", ["value"] = new string('x', 300) }), TestRepository.AgentPrincipal(store), CancellationToken.None);
        Assert.Equal(PolicyDecision.Deny, tooLong.Decision);

        var unknownKey = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id, agent.Id, "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["address"] = "C1", ["value"] = "1", ["extra"] = "x" }), TestRepository.AgentPrincipal(store), CancellationToken.None);
        Assert.Equal(PolicyDecision.Deny, unknownKey.Decision);

        Assert.Equal(3, store.AuditEvents.Count(auditEvent => auditEvent.Outcome == AuditOutcome.Blocked));
    }

    [Fact]
    public async Task Commands_invalid_in_the_current_state_are_denied_at_dispatch()
    {
        var store = new InMemoryRuntimeStore();
        store.SetSpreadsheet(new SpreadsheetState(new Dictionary<string, string>()));
        var runtime = CreateRuntime(store);
        var user = store.Users[0];
        var agent = store.Agents[0];

        var result = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id, agent.Id, "spreadsheet", "clear", new Dictionary<string, string>()), TestRepository.AgentPrincipal(store), CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("not valid", result.Reason);
    }

    [Fact]
    public async Task Approving_after_the_surface_moved_past_the_previewed_revision_is_stale()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = CreateRuntime(store);
        var user = store.Users[0];
        var agent = store.Agents[0];

        var pending = await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id, agent.Id, "spreadsheet", "clear", new Dictionary<string, string>()), TestRepository.AgentPrincipal(store), CancellationToken.None);
        var observedRevision = store.SpreadsheetRevision;

        await runtime.SubmitAsync(new SubmitToolRequestDto(
            user.Id, agent.Id, "spreadsheet", "set-cell",
            new Dictionary<string, string> { ["address"] = "Z9", ["value"] = "1" }), TestRepository.AgentPrincipal(store), CancellationToken.None);

        await Assert.ThrowsAsync<StaleApprovalException>(() =>
            runtime.ResolveApprovalAsync(pending.Approval!.Id, approved: true, pending.Approval.RequestHash, TestRepository.HumanPrincipal(store), observedRevision, CancellationToken.None));

        Assert.Contains(store.Approvals, approval => approval.Id == pending.Approval!.Id && approval.Status == ApprovalStatus.Pending);
    }

    [Fact]
    public async Task Revision_precondition_is_checked_atomically_with_execution()
    {
        var store = new InMemoryRuntimeStore();
        var runtime = CreateRuntime(store);
        var user = store.Users[0];
        var agent = store.Agents[0];

        var stale = await Assert.ThrowsAsync<RevisionMismatchException>(() =>
            runtime.SubmitAsync(new SubmitToolRequestDto(
                user.Id, agent.Id, "spreadsheet", "set-cell",
                new Dictionary<string, string> { ["address"] = "C1", ["value"] = "1" }), TestRepository.AgentPrincipal(store), expectedRevision: 99, CancellationToken.None));

        Assert.Equal(store.SpreadsheetRevision, stale.CurrentRevision);
    }

    private static AgentRuntime CreateRuntime(IRuntimeStore store) =>
        new(store, TestRepository.PolicyEngine(), new SpreadsheetSandboxExecutor(store), TestRepository.Surfaces());
}
