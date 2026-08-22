namespace WorkspaceRuntime.Domain;

public enum PolicyDecision
{
    Allow,
    Deny,
    RequireApproval
}

public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected
}

public enum AuditOutcome
{
    Success,
    Blocked,
    PendingApproval
}

// Slug is the stable lowercase identity key (^[a-z0-9-]{1,32}$): the home
// volume name, the audit attribution, and the bearer-token subject all key off
// it. A human's slug is their own; an agent's slug is its identity.
public sealed record PlatformUser(Guid Id, string DisplayName, string Email, string Slug);

public sealed record Workspace(Guid Id, Guid OwnerUserId, string Name);

public sealed record AgentProfile(
    Guid Id,
    Guid OwnerUserId,
    Guid WorkspaceId,
    string Name,
    string InferenceProvider,
    IReadOnlySet<string> GrantedTools,
    string Slug);

// A resolved caller identity: a human user or an agent, with the slug that
// keys its home, tokens, and audit attribution.
public enum PrincipalKind
{
    Human,
    Agent
}

public sealed record RuntimePrincipal(PrincipalKind Kind, Guid Subject, string Slug, string Display);

public sealed record ToolRequest(
    Guid Id,
    Guid UserId,
    Guid AgentId,
    string ToolName,
    string Operation,
    IReadOnlyDictionary<string, string> Arguments,
    DateTimeOffset CreatedAt);

public sealed record PolicyEvaluation(
    PolicyDecision Decision,
    string Reason,
    IReadOnlyDictionary<string, string> Evidence);

public sealed record ApprovalRecord(
    Guid Id,
    Guid ToolRequestId,
    Guid UserId,
    ApprovalStatus Status,
    string Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string RequestHash = "");

// Principal is the actor that drove this event; OnBehalfOf is the identity
// whose grants/seat were used, when different (dual-actor: "joche, on behalf of
// joche-agent"). For an agent acting as itself, OnBehalfOf is null.
public sealed record AuditEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? UserId,
    Guid? AgentId,
    string Action,
    AuditOutcome Outcome,
    string Detail,
    Guid? CorrelationId = null,
    string? Principal = null,
    string? OnBehalfOf = null);

public sealed record SpreadsheetCell(string Address, string Value);

public sealed record SpreadsheetState(IReadOnlyDictionary<string, string> Cells);

public sealed record ToolExecutionResult(
    bool Executed,
    string Message,
    SpreadsheetState? Spreadsheet);

public sealed record CellChange(string Address, string? Before, string? After);

public sealed record EffectPreview(
    bool Supported,
    string Summary,
    IReadOnlyList<CellChange> Changes);
