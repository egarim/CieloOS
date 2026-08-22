using Microsoft.EntityFrameworkCore;

namespace WorkspaceRuntime.Infrastructure;

public sealed class UserRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Slug { get; set; } = "";
}

public sealed class WorkspaceRow
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class AgentRow
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string InferenceProvider { get; set; } = "";
    public string GrantedToolsJson { get; set; } = "[]";
    public string Slug { get; set; } = "";
}

public sealed class ApprovalRow
{
    public Guid Id { get; set; }
    public Guid ToolRequestId { get; set; }
    public Guid UserId { get; set; }
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string RequestHash { get; set; } = "";
}

public sealed class AuditEventRow
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? UserId { get; set; }
    public Guid? AgentId { get; set; }
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Detail { get; set; } = "";
    public Guid? CorrelationId { get; set; }
    public string? Principal { get; set; }
    public string? OnBehalfOf { get; set; }
}

public sealed class PendingRequestRow
{
    public Guid ApprovalId { get; set; }
    public string RequestJson { get; set; } = "";
}

public sealed class SpreadsheetRow
{
    public int Id { get; set; }
    public string CellsJson { get; set; } = "{}";
    public long Revision { get; set; }
}

public sealed class RuntimeDbContext : DbContext
{
    public RuntimeDbContext(DbContextOptions<RuntimeDbContext> options) : base(options)
    {
    }

    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<WorkspaceRow> Workspaces => Set<WorkspaceRow>();
    public DbSet<AgentRow> Agents => Set<AgentRow>();
    public DbSet<ApprovalRow> Approvals => Set<ApprovalRow>();
    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();
    public DbSet<PendingRequestRow> PendingRequests => Set<PendingRequestRow>();
    public DbSet<SpreadsheetRow> Spreadsheets => Set<SpreadsheetRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserRow>().ToTable("runtime_users");
        modelBuilder.Entity<WorkspaceRow>().ToTable("runtime_workspaces");
        modelBuilder.Entity<AgentRow>().ToTable("runtime_agents");
        modelBuilder.Entity<ApprovalRow>().ToTable("runtime_approvals");
        modelBuilder.Entity<AuditEventRow>().ToTable("runtime_audit_events");
        modelBuilder.Entity<PendingRequestRow>().ToTable("runtime_pending_requests").HasKey(row => row.ApprovalId);
        modelBuilder.Entity<SpreadsheetRow>().ToTable("runtime_spreadsheets");
    }
}
