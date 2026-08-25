using Microsoft.EntityFrameworkCore;

namespace WorkspaceRuntime.Infrastructure;

public sealed class UserRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Slug { get; set; } = "";
    // Rows written before desk profiles existed read as "office" — the desk they
    // have always had.
    public string DeskProfile { get; set; } = "office";

    // BCP-47. Defaulted so every user created before languages existed reads as
    // English rather than as unset.
    public string Language { get; set; } = "en";
    // Empty until a password is set. Existing installs upgrade with no password:
    // they can still sign in with their identity token, and are asked to set one.
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset? PasswordSetAt { get; set; }
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
    public DbSet<TokenUsageRow> TokenUsage => Set<TokenUsageRow>();
    public DbSet<TokenLimitRow> TokenLimits => Set<TokenLimitRow>();
    public DbSet<SessionRow> Sessions => Set<SessionRow>();
    public DbSet<ApiKeyRow> ApiKeys => Set<ApiKeyRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserRow>().ToTable("runtime_users");
        modelBuilder.Entity<WorkspaceRow>().ToTable("runtime_workspaces");
        modelBuilder.Entity<AgentRow>().ToTable("runtime_agents");
        modelBuilder.Entity<ApprovalRow>().ToTable("runtime_approvals");
        modelBuilder.Entity<AuditEventRow>().ToTable("runtime_audit_events");
        modelBuilder.Entity<PendingRequestRow>().ToTable("runtime_pending_requests").HasKey(row => row.ApprovalId);
        modelBuilder.Entity<SpreadsheetRow>().ToTable("runtime_spreadsheets");
        modelBuilder.Entity<TokenUsageRow>().ToTable("runtime_token_usage");
        // One limit per (scope, subject): "the cap for this agent" is a single
        // fact, and a second row for the same subject would just be ambiguous.
        modelBuilder.Entity<TokenLimitRow>().ToTable("runtime_token_limits").HasKey(row => new { row.Scope, row.Subject });
        // A credential is looked up BY ITS HASH on every request, so that column
        // is indexed and unique — a duplicate would mean two sessions sharing one
        // secret, which should be impossible rather than merely unlikely.
        modelBuilder.Entity<SessionRow>().ToTable("runtime_sessions")
            .HasIndex(row => row.SecretHash).IsUnique();
        modelBuilder.Entity<ApiKeyRow>().ToTable("runtime_api_keys")
            .HasIndex(row => row.SecretHash).IsUnique();
    }
}

// Append-only: one row per model call, never updated. Spend is summed on read.
// At this volume that is exact and simple; if a machine ever makes enough calls
// for the sum to hurt, the fix is a monthly rollup, not a mutable counter.
public sealed class TokenUsageRow
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    // The calendar month this belongs to, as yyyy-MM in UTC. Stored rather than
    // derived because SQLite cannot compare a DateTimeOffset in a query — and it
    // makes the monthly window a fact in the schema instead of an assumption in
    // the code that reads it.
    public string MonthKey { get; set; } = "";
    // UTC ticks, so "most recent first" is a database sort. SQLite stores a
    // DateTimeOffset as text it cannot order meaningfully, and sorting in memory
    // means reading an append-only table in full to show ten rows.
    public long OccurredAtTicks { get; set; }
    public Guid UserId { get; set; }
    public Guid AgentId { get; set; }
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Locality { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
}

public sealed class TokenLimitRow
{
    public string Scope { get; set; } = "";
    public string Subject { get; set; } = "";
    public long MonthlyTokens { get; set; }
}

// A browser session. The secret itself is never stored — only its hash — so a
// copy of this table is a list of who is signed in, not a way to become them.
public sealed class SessionRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string SecretHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    // Ticks alongside the timestamp, because SQLite cannot order a
    // DateTimeOffset in a query (the same lesson as the usage ledger).
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

// A named credential for a program — the thing that means an integration never
// has to hold a person's own credential (issue #9, point 6).
public sealed class ApiKeyRow
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public long CreatedAtTicks { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
