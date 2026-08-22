using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

public interface IInferenceProvider
{
    string Name { get; }
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken);
}

public sealed class EchoInferenceProvider : IInferenceProvider
{
    public string Name => "echo-local";

    public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken) =>
        Task.FromResult($"Echo provider received: {prompt}");
}

public interface ISandboxedToolExecutor
{
    Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken);
}

public interface IRuntimeStore
{
    IReadOnlyList<PlatformUser> Users { get; }
    IReadOnlyList<Workspace> Workspaces { get; }
    IReadOnlyList<AgentProfile> Agents { get; }
    IReadOnlyList<ApprovalRecord> Approvals { get; }
    IReadOnlyList<AuditEvent> AuditEvents { get; }
    SpreadsheetState Spreadsheet { get; }
    long SpreadsheetRevision { get; }
    PlatformUser GetUser(Guid id);
    AgentProfile GetAgent(Guid id);
    void UpsertApproval(ApprovalRecord approval);
    ApprovalRecord GetApproval(Guid id);
    void AppendAudit(AuditEvent auditEvent);
    void SetSpreadsheet(SpreadsheetState spreadsheet);
    void SavePendingRequest(Guid approvalId, ToolRequest request);
    ToolRequest GetPendingRequest(Guid approvalId);
    ToolRequest? FindPendingRequest(Guid approvalId);
}

public sealed record SubmitToolRequestDto(Guid UserId, Guid AgentId, string ToolName, string Operation, Dictionary<string, string> Arguments);

public sealed record ToolRequestResultDto(
    PolicyDecision Decision,
    string Reason,
    ToolExecutionResult? Execution,
    ApprovalRecord? Approval,
    IReadOnlyList<AuditEvent> AuditEvents);

public sealed class AgentRuntime
{
    private readonly IRuntimeStore store;
    private readonly IPolicyEngine policyEngine;
    private readonly ISandboxedToolExecutor executor;
    private readonly ISurfaceRegistry surfaces;

    // Serializes every mutating operation in this single-process runtime, so
    // revision checks, approval resolution, and execution are atomic together.
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public AgentRuntime(IRuntimeStore store, IPolicyEngine policyEngine, ISandboxedToolExecutor executor, ISurfaceRegistry surfaces)
    {
        this.store = store;
        this.policyEngine = policyEngine;
        this.executor = executor;
        this.surfaces = surfaces;
    }

    public Task<ToolRequestResultDto> SubmitAsync(SubmitToolRequestDto dto, string principal, CancellationToken cancellationToken) =>
        SubmitAsync(dto, principal, expectedRevision: null, cancellationToken);

    public async Task<ToolRequestResultDto> SubmitAsync(SubmitToolRequestDto dto, string principal, long? expectedRevision, CancellationToken cancellationToken)
    {
        var user = store.GetUser(dto.UserId);
        var agent = store.GetAgent(dto.AgentId);
        var request = new ToolRequest(Guid.NewGuid(), dto.UserId, dto.AgentId, dto.ToolName, dto.Operation, dto.Arguments, DateTimeOffset.UtcNow);

        // Manifest gates live here, at the single choke point every entry
        // path shares — the surface endpoint, the raw tool-request route, and
        // any future adapter all get identical enforcement (design law 1).
        if (surfaces.Find(dto.ToolName) is { } manifest && manifest.Commands.TryGetValue(dto.Operation, out var command))
        {
            if (command.RequiresHuman && principal != RuntimePrincipals.Human)
            {
                return Denied(request, user.Id, agent.Id, principal, "This command requires the human principal.");
            }

            if (!command.ExposedToAgent && principal == RuntimePrincipals.Agent)
            {
                return Denied(request, user.Id, agent.Id, principal, "This command is not exposed to agent principals.");
            }

            if (SurfaceInputValidator.Validate(command.Input, dto.Arguments) is { } validationError)
            {
                return Denied(request, user.Id, agent.Id, principal, validationError);
            }

            if (!SurfaceConditions.IsValidNow(command.ValidWhen, store))
            {
                return Denied(request, user.Id, agent.Id, principal, $"Command '{dto.Operation}' is not valid in the current surface state.");
            }
        }

        var evaluation = policyEngine.Evaluate(user, agent, request);

        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (expectedRevision is { } expected && expected != store.SpreadsheetRevision)
            {
                throw new RevisionMismatchException(store.SpreadsheetRevision);
            }

            switch (evaluation.Decision)
            {
                case PolicyDecision.Allow:
                    ToolExecutionResult result;
                    try
                    {
                        result = await executor.ExecuteAsync(request, cancellationToken);
                    }
                    catch (ArgumentException exception)
                    {
                        return Denied(request, user.Id, agent.Id, principal, $"Executor rejected the request: {exception.Message}");
                    }

                    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, $"{request.ToolName}.{request.Operation}", AuditOutcome.Success, evaluation.Reason, request.Id, principal));
                    return new ToolRequestResultDto(evaluation.Decision, evaluation.Reason, result, null, store.AuditEvents);

                case PolicyDecision.RequireApproval:
                    var requestHash = RequestHasher.Compute(request);
                    var approval = new ApprovalRecord(Guid.NewGuid(), request.Id, user.Id, ApprovalStatus.Pending, evaluation.Reason, DateTimeOffset.UtcNow, null, requestHash);
                    store.UpsertApproval(approval);
                    store.SavePendingRequest(approval.Id, request);
                    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, $"{request.ToolName}.{request.Operation}", AuditOutcome.PendingApproval, evaluation.Reason, request.Id, principal));
                    return new ToolRequestResultDto(evaluation.Decision, evaluation.Reason, null, approval, store.AuditEvents);

                default:
                    return Denied(request, user.Id, agent.Id, principal, evaluation.Reason);
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<ToolRequestResultDto> ResolveApprovalAsync(Guid approvalId, bool approved, string requestHash, string principal, long? observedRevision, CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            var approval = store.GetApproval(approvalId);
            if (approval.Status != ApprovalStatus.Pending)
            {
                throw new InvalidOperationException("The approval has already been resolved.");
            }

            if (!string.Equals(approval.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new StaleApprovalException("The approval hash does not match the pending request. Re-read the approval before resolving it.");
            }

            // The consent the human gave was to a previewed effect. If the
            // surface moved since they previewed it, the effect they approved
            // is no longer the effect that would run.
            if (approved && observedRevision is { } seen && seen != store.SpreadsheetRevision)
            {
                throw new StaleApprovalException("The workspace changed since this approval was previewed. Re-read the approval before resolving it.");
            }

            var request = store.FindPendingRequest(approvalId);
            if (request is null && approved)
            {
                throw new InvalidOperationException("The pending request for this approval is missing; it can only be rejected.");
            }

            if (!approved)
            {
                var rejected = approval with { Status = ApprovalStatus.Rejected, ResolvedAt = DateTimeOffset.UtcNow };
                store.UpsertApproval(rejected);
                store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, approval.UserId, request?.AgentId, request is null ? "approval.reject" : $"{request.ToolName}.{request.Operation}", AuditOutcome.Blocked, "Human rejected approval request.", request?.Id ?? approval.ToolRequestId, principal));
                return new ToolRequestResultDto(PolicyDecision.Deny, "Human rejected approval request.", null, rejected, store.AuditEvents);
            }

            // Execute first, mark approved second: if execution fails the
            // approval stays pending and can be retried or rejected.
            var result = await executor.ExecuteAsync(request!, cancellationToken);
            var resolved = approval with { Status = ApprovalStatus.Approved, ResolvedAt = DateTimeOffset.UtcNow };
            store.UpsertApproval(resolved);
            store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, approval.UserId, request!.AgentId, $"{request.ToolName}.{request.Operation}", AuditOutcome.Success, "Human approved request.", request.Id, principal));
            return new ToolRequestResultDto(PolicyDecision.Allow, "Human approved request.", result, resolved, store.AuditEvents);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private ToolRequestResultDto Denied(ToolRequest request, Guid userId, Guid agentId, string principal, string reason)
    {
        store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, userId, agentId, $"{request.ToolName}.{request.Operation}", AuditOutcome.Blocked, reason, request.Id, principal));
        return new ToolRequestResultDto(PolicyDecision.Deny, reason, null, null, store.AuditEvents);
    }
}

public sealed class StaleApprovalException : InvalidOperationException
{
    public StaleApprovalException(string message) : base(message)
    {
    }
}

public sealed class RevisionMismatchException : InvalidOperationException
{
    public RevisionMismatchException(long currentRevision)
        : base("Revision mismatch: the surface changed since it was observed.")
    {
        CurrentRevision = currentRevision;
    }

    public long CurrentRevision { get; }
}
