using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

public sealed class InMemoryRuntimeStore : IRuntimeStore
{
    private readonly List<PlatformUser> users = new();
    private readonly List<Workspace> workspaces = new();
    private readonly List<AgentProfile> agents = new();
    private readonly List<ApprovalRecord> approvals = new();
    private readonly List<AuditEvent> auditEvents = new();
    private readonly Dictionary<Guid, ToolRequest> pendingRequests = new();
    private long spreadsheetRevision;
    private SpreadsheetState spreadsheet = new(new Dictionary<string, string>
    {
        ["A1"] = "12",
        ["A2"] = "30",
        ["B1"] = "Ready"
    });

    public InMemoryRuntimeStore()
    {
        var user = new PlatformUser(Guid.Parse("10000000-0000-0000-0000-000000000001"), "Avery Stone", "avery@example.test");
        var workspace = new Workspace(Guid.Parse("20000000-0000-0000-0000-000000000001"), user.Id, "Office demo workspace");
        var agent = new AgentProfile(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            user.Id,
            workspace.Id,
            "Office Agent",
            "local-inference",
            new HashSet<string> { "spreadsheet", "session" });

        users.Add(user);
        workspaces.Add(workspace);
        agents.Add(agent);
        auditEvents.Add(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, "runtime.seed", AuditOutcome.Success, "Seeded demo workspace."));
    }

    public IReadOnlyList<PlatformUser> Users => users;
    public IReadOnlyList<Workspace> Workspaces => workspaces;
    public IReadOnlyList<AgentProfile> Agents => agents;
    public IReadOnlyList<ApprovalRecord> Approvals => approvals.OrderByDescending(approval => approval.CreatedAt).ToList();
    public IReadOnlyList<AuditEvent> AuditEvents => auditEvents.OrderByDescending(auditEvent => auditEvent.OccurredAt).ToList();
    public SpreadsheetState Spreadsheet => spreadsheet;
    public long SpreadsheetRevision => spreadsheetRevision;

    public PlatformUser GetUser(Guid id) => users.Single(user => user.Id == id);

    public AgentProfile GetAgent(Guid id) => agents.Single(agent => agent.Id == id);

    public ApprovalRecord GetApproval(Guid id) => approvals.Single(approval => approval.Id == id);

    public void UpsertApproval(ApprovalRecord approval)
    {
        approvals.RemoveAll(existing => existing.Id == approval.Id);
        approvals.Add(approval);
    }

    public void AppendAudit(AuditEvent auditEvent) => auditEvents.Add(auditEvent);

    public void SetSpreadsheet(SpreadsheetState spreadsheet)
    {
        this.spreadsheet = spreadsheet;
        spreadsheetRevision++;
    }

    public void SavePendingRequest(Guid approvalId, ToolRequest request) => pendingRequests[approvalId] = request;

    public ToolRequest GetPendingRequest(Guid approvalId) =>
        pendingRequests.TryGetValue(approvalId, out var request)
            ? request
            : throw new InvalidOperationException("Pending request was not found.");

    public ToolRequest? FindPendingRequest(Guid approvalId) =>
        pendingRequests.TryGetValue(approvalId, out var request) ? request : null;
}
