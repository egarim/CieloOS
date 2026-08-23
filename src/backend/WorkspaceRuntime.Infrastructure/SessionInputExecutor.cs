using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// Executes the `session-input` surface: a human GRANTS or REVOKES a time-boxed
// input lease on a desktop session (the V0.6 per-session input grant). Both
// commands are requiresHuman (an agent can never grant itself input) and
// ownership-gated at the choke point. While a grant is live, AgentRuntime
// upgrades desktop.type/key from RequireApproval to Allow.
public sealed class SessionInputExecutor : ISurfaceExecutor
{
    private const int DefaultMinutes = 10;
    private const int MaxMinutes = 120;

    private readonly ISessionInputGrants grants;
    private readonly ISessionVisionConsent visionConsent;

    public SessionInputExecutor(ISessionInputGrants grants, ISessionVisionConsent visionConsent)
    {
        this.grants = grants;
        this.visionConsent = visionConsent;
    }

    public string SurfaceId => "session-input";

    public Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var sessionId = Required(request, "id");
        switch (request.Operation)
        {
            case "grant":
                var grant = grants.Grant(sessionId, request.UserId, TimeSpan.FromMinutes(Minutes(request)), DateTimeOffset.UtcNow);
                return Task.FromResult(new ToolExecutionResult(true,
                    $"Input on session '{sessionId}' granted for {Minutes(request)} minute(s), until {grant.ExpiresAt:u}.", null));

            case "revoke":
                var removed = grants.Revoke(sessionId);
                return Task.FromResult(new ToolExecutionResult(true,
                    removed > 0 ? $"Input grant on '{sessionId}' revoked." : $"No active input grant on '{sessionId}'.", null));

            case "grant-vision":
                var consent = visionConsent.Grant(sessionId, request.UserId, TimeSpan.FromMinutes(Minutes(request)), DateTimeOffset.UtcNow);
                return Task.FromResult(new ToolExecutionResult(true,
                    $"Cloud vision (screenshots may leave the machine) allowed for session '{sessionId}' for {Minutes(request)} minute(s), until {consent.ExpiresAt:u}.", null));

            case "revoke-vision":
                var revokedVision = visionConsent.Revoke(sessionId);
                return Task.FromResult(new ToolExecutionResult(true,
                    revokedVision > 0 ? $"Cloud vision consent on '{sessionId}' revoked." : $"No active cloud vision consent on '{sessionId}'.", null));

            default:
                return Task.FromResult(new ToolExecutionResult(false, $"Session-input executor rejected unknown operation '{request.Operation}'.", null));
        }
    }

    private static int Minutes(ToolRequest request) =>
        request.Arguments.TryGetValue("minutes", out var raw) && int.TryParse(raw, out var parsed)
            ? Math.Clamp(parsed, 1, MaxMinutes)
            : DefaultMinutes;

    public Task<EffectPreview> PreviewAsync(ToolRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new EffectPreview(true,
            $"Would {request.Operation} the input lease on session '{request.Arguments.GetValueOrDefault("id")}'.",
            Array.Empty<CellChange>()));

    private static string Required(ToolRequest request, string key) =>
        request.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument '{key}'.");
}
