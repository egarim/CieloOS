using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

public sealed class EfRuntimeStore : IRuntimeStore
{
    private readonly IDbContextFactory<RuntimeDbContext> contextFactory;

    public EfRuntimeStore(IDbContextFactory<RuntimeDbContext> contextFactory)
    {
        this.contextFactory = contextFactory;
        EnsureCreatedAndSeeded();
    }

    private void EnsureCreatedAndSeeded()
    {
        using var context = contextFactory.CreateDbContext();
        context.Database.Migrate();

        if (context.Users.Any())
        {
            return;
        }

        PlatformUser? firstUser = null;
        AgentProfile? firstAgent = null;
        foreach (var (user, workspace, agent) in RuntimeSeed.People())
        {
            firstUser ??= user;
            firstAgent ??= agent;
            context.Users.Add(new UserRow { Id = user.Id, DisplayName = user.DisplayName, Email = user.Email, Slug = user.Slug });
            context.Workspaces.Add(new WorkspaceRow { Id = workspace.Id, OwnerUserId = workspace.OwnerUserId, Name = workspace.Name });
            context.Agents.Add(new AgentRow
            {
                Id = agent.Id,
                OwnerUserId = agent.OwnerUserId,
                WorkspaceId = agent.WorkspaceId,
                Name = agent.Name,
                InferenceProvider = agent.InferenceProvider,
                GrantedToolsJson = JsonSerializer.Serialize(agent.GrantedTools),
                Slug = agent.Slug
            });
        }

        context.AuditEvents.Add(ToRow(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, firstUser!.Id, firstAgent!.Id, "runtime.seed", AuditOutcome.Success, "Seeded demo users.")));
        context.Spreadsheets.Add(new SpreadsheetRow
        {
            Id = 1,
            CellsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["A1"] = "12",
                ["A2"] = "30",
                ["B1"] = "Ready"
            })
        });
        context.SaveChanges();
    }

    public IReadOnlyList<PlatformUser> Users
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.Users.AsNoTracking().Select(row => new PlatformUser(row.Id, row.DisplayName, row.Email, row.Slug)).ToList();
        }
    }

    public IReadOnlyList<Workspace> Workspaces
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.Workspaces.AsNoTracking().Select(row => new Workspace(row.Id, row.OwnerUserId, row.Name)).ToList();
        }
    }

    public IReadOnlyList<AgentProfile> Agents
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.Agents.AsNoTracking().AsEnumerable().Select(ToAgent).ToList();
        }
    }

    public IReadOnlyList<ApprovalRecord> Approvals
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.Approvals.AsNoTracking().AsEnumerable().Select(ToApproval).OrderByDescending(approval => approval.CreatedAt).ToList();
        }
    }

    public IReadOnlyList<AuditEvent> AuditEvents
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.AuditEvents.AsNoTracking().AsEnumerable().Select(ToAudit).OrderByDescending(auditEvent => auditEvent.OccurredAt).ToList();
        }
    }

    public SpreadsheetState Spreadsheet
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            var row = context.Spreadsheets.AsNoTracking().Single(sheet => sheet.Id == 1);
            return new SpreadsheetState(JsonSerializer.Deserialize<Dictionary<string, string>>(row.CellsJson) ?? new Dictionary<string, string>());
        }
    }

    public long SpreadsheetRevision
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.Spreadsheets.AsNoTracking().Single(sheet => sheet.Id == 1).Revision;
        }
    }

    public PlatformUser GetUser(Guid id)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Users.AsNoTracking().Single(user => user.Id == id);
        return new PlatformUser(row.Id, row.DisplayName, row.Email, row.Slug);
    }

    public AgentProfile GetAgent(Guid id)
    {
        using var context = contextFactory.CreateDbContext();
        return ToAgent(context.Agents.AsNoTracking().Single(agent => agent.Id == id));
    }

    public ApprovalRecord GetApproval(Guid id)
    {
        using var context = contextFactory.CreateDbContext();
        return ToApproval(context.Approvals.AsNoTracking().Single(approval => approval.Id == id));
    }

    public void UpsertApproval(ApprovalRecord approval)
    {
        using var context = contextFactory.CreateDbContext();
        var existing = context.Approvals.SingleOrDefault(row => row.Id == approval.Id);
        if (existing is null)
        {
            existing = new ApprovalRow { Id = approval.Id };
            context.Approvals.Add(existing);
        }

        existing.ToolRequestId = approval.ToolRequestId;
        existing.UserId = approval.UserId;
        existing.Status = approval.Status.ToString();
        existing.Reason = approval.Reason;
        existing.CreatedAt = approval.CreatedAt;
        existing.ResolvedAt = approval.ResolvedAt;
        existing.RequestHash = approval.RequestHash;
        context.SaveChanges();
    }

    public void AppendAudit(AuditEvent auditEvent)
    {
        using var context = contextFactory.CreateDbContext();
        context.AuditEvents.Add(ToRow(auditEvent));
        context.SaveChanges();
    }

    public void SetSpreadsheet(SpreadsheetState spreadsheet)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Spreadsheets.Single(sheet => sheet.Id == 1);
        row.CellsJson = JsonSerializer.Serialize(spreadsheet.Cells);
        row.Revision++;
        context.SaveChanges();
    }

    public void SavePendingRequest(Guid approvalId, ToolRequest request)
    {
        using var context = contextFactory.CreateDbContext();
        var existing = context.PendingRequests.SingleOrDefault(row => row.ApprovalId == approvalId);
        if (existing is null)
        {
            existing = new PendingRequestRow { ApprovalId = approvalId };
            context.PendingRequests.Add(existing);
        }

        existing.RequestJson = JsonSerializer.Serialize(new StoredToolRequest(
            request.Id, request.UserId, request.AgentId, request.ToolName, request.Operation,
            new Dictionary<string, string>(request.Arguments), request.CreatedAt));
        context.SaveChanges();
    }

    public ToolRequest GetPendingRequest(Guid approvalId) =>
        FindPendingRequest(approvalId)
            ?? throw new InvalidOperationException("Pending request was not found.");

    public ToolRequest? FindPendingRequest(Guid approvalId)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.PendingRequests.AsNoTracking().SingleOrDefault(pending => pending.ApprovalId == approvalId);
        if (row is null)
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<StoredToolRequest>(row.RequestJson)
            ?? throw new InvalidOperationException("Pending request payload was unreadable.");
        return new ToolRequest(stored.Id, stored.UserId, stored.AgentId, stored.ToolName, stored.Operation, stored.Arguments, stored.CreatedAt);
    }

    public RuntimePrincipal? FindPrincipalBySlug(string slug) =>
        PrincipalResolver.BySlug(Users, Agents, slug);

    private static AgentProfile ToAgent(AgentRow row) => new(
        row.Id,
        row.OwnerUserId,
        row.WorkspaceId,
        row.Name,
        row.InferenceProvider,
        JsonSerializer.Deserialize<HashSet<string>>(row.GrantedToolsJson) ?? new HashSet<string>(),
        row.Slug);

    private static ApprovalRecord ToApproval(ApprovalRow row) => new(
        row.Id,
        row.ToolRequestId,
        row.UserId,
        Enum.Parse<ApprovalStatus>(row.Status),
        row.Reason,
        row.CreatedAt,
        row.ResolvedAt,
        row.RequestHash);

    private static AuditEvent ToAudit(AuditEventRow row) => new(
        row.Id,
        row.OccurredAt,
        row.UserId,
        row.AgentId,
        row.Action,
        Enum.Parse<AuditOutcome>(row.Outcome),
        row.Detail,
        row.CorrelationId,
        row.Principal,
        row.OnBehalfOf);

    private static AuditEventRow ToRow(AuditEvent auditEvent) => new()
    {
        Id = auditEvent.Id,
        OccurredAt = auditEvent.OccurredAt,
        UserId = auditEvent.UserId,
        AgentId = auditEvent.AgentId,
        Action = auditEvent.Action,
        Outcome = auditEvent.Outcome.ToString(),
        Detail = auditEvent.Detail,
        CorrelationId = auditEvent.CorrelationId,
        Principal = auditEvent.Principal,
        OnBehalfOf = auditEvent.OnBehalfOf
    };

    private sealed record StoredToolRequest(
        Guid Id,
        Guid UserId,
        Guid AgentId,
        string ToolName,
        string Operation,
        Dictionary<string, string> Arguments,
        DateTimeOffset CreatedAt);
}
