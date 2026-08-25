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

    // The password hash is deliberately NOT on PlatformUser: it has no business
    // travelling with an identity that gets serialised to the panel and the API.
    string? PasswordHashFor(Guid userId);
    void SetPasswordHash(Guid userId, string hash);

    // The language a person works in. A single-field write like the password
    // hash, rather than a whole-user upsert, so nothing else about the person can
    // be overwritten by a request that only meant to change one thing.
    void SetLanguage(Guid userId, string language);
    AgentProfile GetAgent(Guid id);
    void UpsertApproval(ApprovalRecord approval);
    ApprovalRecord GetApproval(Guid id);
    void AppendAudit(AuditEvent auditEvent);
    void SetSpreadsheet(SpreadsheetState spreadsheet);
    void SavePendingRequest(Guid approvalId, ToolRequest request);
    ToolRequest GetPendingRequest(Guid approvalId);
    ToolRequest? FindPendingRequest(Guid approvalId);
    RuntimePrincipal? FindPrincipalBySlug(string slug);

    // First-run ownership: create the first owner (+ their workspace and agent)
    // in one transaction. Returns false if an owner already exists — the
    // at-most-one-owner guard that makes a re-issued claim a no-op rather than a
    // second owner. Callers serialize concurrent claims (see ISetupService).
    bool CreateOwner(PlatformUser user, Workspace workspace, AgentProfile agent);

    // Add a further user (+ workspace + agent) after the first owner exists.
    // Returns false if the user OR agent slug is already taken (slugs are the
    // stable identity keys — a collision would break token auth and home volumes).
    // Callers serialize concurrent adds (see ISetupService).
    bool AddUser(PlatformUser user, Workspace workspace, AgentProfile agent);
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
    private readonly ISessionBackend? sessions;
    private readonly ISessionInputGrants? inputGrants;

    // Serializes every mutating operation in this single-process runtime, so
    // revision checks, approval resolution, and execution are atomic together.
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public AgentRuntime(IRuntimeStore store, IPolicyEngine policyEngine, ISandboxedToolExecutor executor, ISurfaceRegistry surfaces, ISessionBackend? sessions = null, ISessionInputGrants? inputGrants = null)
    {
        this.store = store;
        this.policyEngine = policyEngine;
        this.executor = executor;
        this.surfaces = surfaces;
        this.sessions = sessions;
        this.inputGrants = inputGrants;
    }

    public Task<ToolRequestResultDto> SubmitAsync(SubmitToolRequestDto dto, RuntimePrincipal principal, CancellationToken cancellationToken) =>
        SubmitAsync(dto, principal, expectedRevision: null, cancellationToken);

    public async Task<ToolRequestResultDto> SubmitAsync(SubmitToolRequestDto dto, RuntimePrincipal principal, long? expectedRevision, CancellationToken cancellationToken)
    {
        var actor = principal.Slug;
        var user = store.GetUser(dto.UserId);
        var agent = store.GetAgent(dto.AgentId);
        // When a human acts, it acts through (and on behalf of) its agent —
        // the dual-actor pair. An agent acting as itself has no second actor.
        var onBehalfOf = principal.Kind == PrincipalKind.Human ? agent.Slug : null;
        var request = new ToolRequest(Guid.NewGuid(), dto.UserId, dto.AgentId, dto.ToolName, dto.Operation, dto.Arguments, DateTimeOffset.UtcNow);

        // An agent principal may only act as itself; a human may act as an
        // agent it owns. This binds the request's agent to the caller instead
        // of trusting the client-supplied AgentId.
        if (principal.Kind == PrincipalKind.Agent && agent.Id != principal.Subject)
        {
            return Denied(request, user.Id, agent.Id, actor, "An agent may only act as itself.");
        }
        if (principal.Kind == PrincipalKind.Human && agent.OwnerUserId != principal.Subject)
        {
            return Denied(request, user.Id, agent.Id, actor, "A human may only act through agents it owns.");
        }

        // Manifest gates live here, at the single choke point every entry
        // path shares — the surface endpoint, the raw tool-request route, and
        // any future adapter all get identical enforcement (design law 1).
        if (surfaces.Find(dto.ToolName) is { } manifest && manifest.Commands.TryGetValue(dto.Operation, out var command))
        {
            if (command.RequiresHuman && principal.Kind != PrincipalKind.Human)
            {
                return Denied(request, user.Id, agent.Id, actor, "This command requires the human principal.");
            }

            if (!command.ExposedToAgent && principal.Kind == PrincipalKind.Agent)
            {
                return Denied(request, user.Id, agent.Id, actor, "This command is not exposed to agent principals.");
            }

            if (SurfaceInputValidator.Validate(command.Input, dto.Arguments) is { } validationError)
            {
                return Denied(request, user.Id, agent.Id, actor, validationError);
            }

            if (!SurfaceConditions.IsValidNow(command.ValidWhen, store))
            {
                return Denied(request, user.Id, agent.Id, actor, $"Command '{dto.Operation}' is not valid in the current surface state.");
            }
        }

        // Home/session ownership is enforced here, at the shared choke point, so
        // every entry path (surface command route, raw tool-request, any future
        // adapter) is covered — never only in one HTTP handler.
        if (dto.ToolName == "session")
        {
            if (dto.Operation == "create"
                && dto.Arguments.TryGetValue("owner", out var homeOwner)
                && !Ownership.CanAccessHome(principal, homeOwner, store))
            {
                return Denied(request, user.Id, agent.Id, actor, $"'{principal.Slug}' may not open a session over '{homeOwner}'.");
            }

            // Per-session ops (destroy, inhabit) are gated on the target
            // session's owner, resolved by id — the same choke point, so every
            // route is covered.
            if ((dto.Operation == "destroy" || dto.Operation == "inhabit")
                && dto.Arguments.TryGetValue("id", out var sessionId)
                && sessions is not null)
            {
                var target = (await sessions.ListAsync(cancellationToken))
                    .FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
                if (target is not null && !Ownership.CanAccessHome(principal, target.Owner, store))
                {
                    return Denied(request, user.Id, agent.Id, actor, $"'{principal.Slug}' may not {dto.Operation} a session owned by '{target.Owner}'.");
                }
            }
        }

        // Any surface that acts on a live session is gated on that session's
        // owner at this choke point, so an agent can only act on its own session
        // and a human only on a session it or its agents own. These ops fail
        // CLOSED: if the backend is missing or the session cannot be resolved,
        // deny rather than let an unowned action slip through — a desktop click on
        // someone else's screen is as owned as a keystroke into their console.
        //
        // Which surfaces those are comes from the MANIFEST (`targetsSession`), not
        // from a list here. The list version read `console or desktop or
        // session-input`; `browser` was added later, nobody extended it, and for a
        // while one user could drive another user's browser — the gated read
        // endpoints refused them while the bus did not. A missing declaration is
        // now a failing test rather than a silent hole.
        if (surfaces.Find(dto.ToolName) is { TargetsSession: true }
            && dto.Arguments.TryGetValue("id", out var sessionTarget))
        {
            if (sessions is null)
            {
                return Denied(request, user.Id, agent.Id, actor, $"{dto.ToolName} operations are unavailable: no session backend is configured.");
            }

            var target = (await sessions.ListAsync(cancellationToken))
                .FirstOrDefault(session => string.Equals(session.Id, sessionTarget, StringComparison.Ordinal));
            if (target is null)
            {
                return Denied(request, user.Id, agent.Id, actor, $"Session '{sessionTarget}' was not found.");
            }
            if (!Ownership.CanAccessHome(principal, target.Owner, store))
            {
                return Denied(request, user.Id, agent.Id, actor, $"'{principal.Slug}' may not operate the {dto.ToolName} of a session owned by '{target.Owner}'.");
            }
        }

        var evaluation = policyEngine.Evaluate(user, agent, request);

        // V0.6 per-session input grant: a live, human-issued lease upgrades desktop
        // typing/keys from RequireApproval to Allow — one-time consent, time-boxed,
        // instead of approving every keystroke. Ownership is still enforced above.
        if (evaluation.Decision == PolicyDecision.RequireApproval
            && dto.ToolName == "desktop" && dto.Operation is "type" or "key"
            && inputGrants is not null
            && dto.Arguments.TryGetValue("id", out var grantSession)
            && inputGrants.IsActive(grantSession, DateTimeOffset.UtcNow))
        {
            evaluation = new PolicyEvaluation(PolicyDecision.Allow,
                "Covered by an active per-session input grant.", evaluation.Evidence);
        }

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
                        return Denied(request, user.Id, agent.Id, actor, $"Executor rejected the request: {exception.Message}");
                    }

                    // A real input ledger: record the exact input the principal
                    // sent (console text, or desktop click coords / keystrokes),
                    // not just "an action happened".
                    var successDetail = evaluation.Reason;
                    if (dto.ToolName == "console" && dto.Operation == "type")
                    {
                        successDetail = $"input: {Truncate(dto.Arguments.GetValueOrDefault("text", ""))}";
                    }
                    else if (dto.ToolName == "desktop")
                    {
                        successDetail = DesktopLedger(dto);
                    }
                    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, $"{request.ToolName}.{request.Operation}", AuditOutcome.Success, successDetail, request.Id, actor, onBehalfOf));
                    return new ToolRequestResultDto(evaluation.Decision, evaluation.Reason, result, null, store.AuditEvents);

                case PolicyDecision.RequireApproval:
                    var requestHash = RequestHasher.Compute(request);
                    var approval = new ApprovalRecord(Guid.NewGuid(), request.Id, user.Id, ApprovalStatus.Pending, evaluation.Reason, DateTimeOffset.UtcNow, null, requestHash);
                    store.UpsertApproval(approval);
                    store.SavePendingRequest(approval.Id, request);
                    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, $"{request.ToolName}.{request.Operation}", AuditOutcome.PendingApproval, evaluation.Reason, request.Id, actor, onBehalfOf));
                    return new ToolRequestResultDto(evaluation.Decision, evaluation.Reason, null, approval, store.AuditEvents);

                default:
                    return Denied(request, user.Id, agent.Id, actor, evaluation.Reason);
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<ToolRequestResultDto> ResolveApprovalAsync(Guid approvalId, bool approved, string requestHash, RuntimePrincipal principal, long? observedRevision, CancellationToken cancellationToken)
    {
        var actor = principal.Slug;
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            var approval = store.GetApproval(approvalId);

            // Only the human who owns the request may resolve it. approval.UserId
            // is the owning human (set from the acting identity at submit time),
            // so one user cannot approve another user's agent's pending action.
            if (principal.Kind != PrincipalKind.Human || principal.Subject != approval.UserId)
            {
                throw new ApprovalOwnershipException("This approval belongs to another user.");
            }

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

            var onBehalfOf = request is not null ? store.GetAgent(request.AgentId).Slug : null;

            if (!approved)
            {
                var rejected = approval with { Status = ApprovalStatus.Rejected, ResolvedAt = DateTimeOffset.UtcNow };
                store.UpsertApproval(rejected);
                store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, approval.UserId, request?.AgentId, request is null ? "approval.reject" : $"{request.ToolName}.{request.Operation}", AuditOutcome.Blocked, "Human rejected approval request.", request?.Id ?? approval.ToolRequestId, actor, onBehalfOf));
                return new ToolRequestResultDto(PolicyDecision.Deny, "Human rejected approval request.", null, rejected, store.AuditEvents);
            }

            // Execute first, mark approved second: if execution fails the
            // approval stays pending and can be retried or rejected.
            var result = await executor.ExecuteAsync(request!, cancellationToken);
            var resolved = approval with { Status = ApprovalStatus.Approved, ResolvedAt = DateTimeOffset.UtcNow };
            store.UpsertApproval(resolved);
            // Approved is not the same as happened. This recorded Success for every
            // approval, whatever the executor returned — so a navigation the person
            // consented to and which was then refused downstream read, in the one
            // place people go to find out what occurred, as if it had worked. The
            // outcome now follows the execution, and the reason travels with it.
            store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, approval.UserId, request!.AgentId,
                $"{request.ToolName}.{request.Operation}",
                result.Executed ? AuditOutcome.Success : AuditOutcome.Blocked,
                result.Executed
                    ? $"Human approved request. {Truncate(result.Message, 160)}"
                    : $"Human approved it, but it did not take effect: {Truncate(result.Message, 160)}",
                request.Id, actor, onBehalfOf));
            return new ToolRequestResultDto(PolicyDecision.Allow, "Human approved request.", result, resolved, store.AuditEvents);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private static string Truncate(string value, int max = 200) =>
        value.Length <= max ? value : value[..max] + "…";

    // The desktop input ledger: what the principal actually did on the screen.
    private static string DesktopLedger(SubmitToolRequestDto dto) => dto.Operation switch
    {
        "click" or "double_click" =>
            $"{dto.Operation} ({dto.Arguments.GetValueOrDefault("x")}, {dto.Arguments.GetValueOrDefault("y")}) button {dto.Arguments.GetValueOrDefault("button", "1")}",
        "type" => $"type: {Truncate(dto.Arguments.GetValueOrDefault("text", ""))}",
        "key" => $"key: {dto.Arguments.GetValueOrDefault("keysym", "")}",
        _ => dto.Operation,
    };

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

public sealed class ApprovalOwnershipException : InvalidOperationException
{
    public ApprovalOwnershipException(string message) : base(message)
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
