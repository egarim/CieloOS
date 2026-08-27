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
    private readonly Dictionary<string, SpreadsheetState> spreadsheets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> spreadsheetRevisions = new(StringComparer.Ordinal);

    // Default seedDemo:true keeps every direct `new InMemoryRuntimeStore()` (the
    // unit-test fixtures) populated with the joche/yulia demo identities. A real,
    // provider-free install constructs it with seedDemo:false — an empty machine
    // whose first owner is created by the first-run claim.
    public InMemoryRuntimeStore(bool seedDemo = true)
    {
        if (!seedDemo)
        {
            return;
        }

        foreach (var (user, workspace, agent) in RuntimeSeed.People())
        {
            users.Add(user);
            workspaces.Add(workspace);
            agents.Add(agent);
        }

        spreadsheets[users[0].Slug] = new SpreadsheetState(new Dictionary<string, string>
        {
            ["A1"] = "12",
            ["A2"] = "30",
            ["B1"] = "Ready"
        });

        auditEvents.Add(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, users[0].Id, agents[0].Id, "runtime.seed", AuditOutcome.Success, "Seeded demo users."));
    }

    public IReadOnlyList<PlatformUser> Users => users;
    public IReadOnlyList<Workspace> Workspaces => workspaces;
    public IReadOnlyList<AgentProfile> Agents => agents;
    public IReadOnlyList<ApprovalRecord> Approvals => approvals.OrderByDescending(approval => approval.CreatedAt).ToList();
    public IReadOnlyList<AuditEvent> AuditEvents => auditEvents.OrderByDescending(auditEvent => auditEvent.OccurredAt).ToList();
    public SpreadsheetState GetSpreadsheet(string ownerSlug) =>
        spreadsheets.TryGetValue(ownerSlug, out var spreadsheet)
            ? spreadsheet
            : new SpreadsheetState(new Dictionary<string, string>());

    public long GetSpreadsheetRevision(string ownerSlug) =>
        spreadsheetRevisions.TryGetValue(ownerSlug, out var revision) ? revision : 0;

    // In-memory mode keeps passwords in memory too: it exists for tests and
    // ephemeral runs, where nothing survives a restart by design.
    private readonly Dictionary<Guid, string> passwords = new();

    public string? PasswordHashFor(Guid userId) => passwords.TryGetValue(userId, out var hash) ? hash : null;

    public void SetPasswordHash(Guid userId, string hash) => passwords[userId] = hash;

    public void SetLanguage(Guid userId, string language)
    {
        var index = users.FindIndex(user => user.Id == userId);
        if (index >= 0)
        {
            users[index] = users[index] with { Language = language };
        }
    }

    public PlatformUser GetUser(Guid id) => users.Single(user => user.Id == id);

    public AgentProfile GetAgent(Guid id) => agents.Single(agent => agent.Id == id);

    public ApprovalRecord GetApproval(Guid id) => approvals.Single(approval => approval.Id == id);

    public void UpsertApproval(ApprovalRecord approval)
    {
        approvals.RemoveAll(existing => existing.Id == approval.Id);
        approvals.Add(approval);
    }

    public void AppendAudit(AuditEvent auditEvent) => auditEvents.Add(auditEvent);

    public void SetSpreadsheet(string ownerSlug, SpreadsheetState spreadsheet)
    {
        spreadsheets[ownerSlug] = spreadsheet;
        spreadsheetRevisions[ownerSlug] = spreadsheetRevisions.TryGetValue(ownerSlug, out var revision)
            ? revision + 1
            : 1;
    }

    public void SavePendingRequest(Guid approvalId, ToolRequest request) => pendingRequests[approvalId] = request;

    public ToolRequest GetPendingRequest(Guid approvalId) =>
        pendingRequests.TryGetValue(approvalId, out var request)
            ? request
            : throw new InvalidOperationException("Pending request was not found.");

    public ToolRequest? FindPendingRequest(Guid approvalId) =>
        pendingRequests.TryGetValue(approvalId, out var request) ? request : null;

    public RuntimePrincipal? FindPrincipalBySlug(string slug) =>
        PrincipalResolver.BySlug(Users, Agents, slug);

    public bool CreateOwner(PlatformUser user, Workspace workspace, AgentProfile agent)
    {
        if (users.Count > 0)
        {
            return false;
        }

        users.Add(user);
        workspaces.Add(workspace);
        agents.Add(agent);
        auditEvents.Add(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, "owner.claim", AuditOutcome.Success, $"Claimed owner '{user.Slug}'."));
        return true;
    }

    public bool AddUser(PlatformUser user, Workspace workspace, AgentProfile agent)
    {
        if (users.Any(existing => existing.Slug == user.Slug) || agents.Any(existing => existing.Slug == agent.Slug))
        {
            return false;
        }

        users.Add(user);
        workspaces.Add(workspace);
        agents.Add(agent);
        auditEvents.Add(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, "user.add", AuditOutcome.Success, $"Added user '{user.Slug}'."));
        return true;
    }
}
