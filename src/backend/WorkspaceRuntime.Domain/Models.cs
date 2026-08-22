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

public sealed record PlatformUser(Guid Id, string DisplayName, string Email);

public sealed record Workspace(Guid Id, Guid OwnerUserId, string Name);

public sealed record AgentProfile(
    Guid Id,
    Guid OwnerUserId,
    Guid WorkspaceId,
    string Name,
    string InferenceProvider,
    IReadOnlySet<string> GrantedTools);

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

public sealed record AuditEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? UserId,
    Guid? AgentId,
    string Action,
    AuditOutcome Outcome,
    string Detail,
    Guid? CorrelationId = null,
    string? Principal = null);

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
