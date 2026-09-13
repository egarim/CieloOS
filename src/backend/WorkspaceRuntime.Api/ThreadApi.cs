using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

public static class ThreadApi
{
    // One constant body for both "no such thread" and "not yours". It deliberately
    // does NOT echo the requested id: the moment the two answers differ by even a
    // byte, an id becomes a probe for whether someone else's thread exists. Keeping
    // it constant is what lets the test assert the responses are identical rather
    // than assert they are identical-after-some-normalisation, which is a weaker
    // claim wearing the same words.
    private static readonly object NotFoundBody = new { error = "Thread not found." };

    public static void Map(WebApplication app)
    {
        // Threads are delegated work. Like sessions, the list is scoped to the
        // homes the caller can reach, and a single thread refuses reads from
        // other owners. A missing thread and another owner's thread are both
        // reported as 404 with the same body so ids cannot be used as probes.
        app.MapGet("/api/threads", (HttpContext context, IRuntimeStore store) =>
        {
            var caller = Caller(context);
            return Results.Ok(store.Threads.Where(thread =>
                Ownership.CanAccessHome(caller, thread.OwnerSlug, store)));
        });

        app.MapPost("/api/threads", (CreateThreadRequest? request, HttpContext context, IRuntimeStore store) =>
        {
            var caller = Caller(context);
            if (request is null || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "title and message are required." });
            }

            var thread = store.CreateThread(caller.Slug, request.Title.Trim(), request.Message.Trim());
            AppendAudit(store, caller, "thread.create", $"'{caller.Slug}' created thread '{thread.Id}'.");
            return Results.Created($"/api/threads/{thread.Id}", thread);
        });

        app.MapGet("/api/threads/{id:guid}", (Guid id, HttpContext context, IRuntimeStore store) =>
        {
            var caller = Caller(context);
            var detail = store.GetThread(id);
            if (detail is null || !Ownership.CanAccessHome(caller, detail.Thread.OwnerSlug, store))
            {
                return Results.NotFound(NotFoundBody);
            }

            return Results.Ok(new
            {
                detail.Thread.Id,
                detail.Thread.OwnerSlug,
                detail.Thread.Title,
                detail.Thread.State,
                detail.Thread.CreatedAt,
                detail.Thread.LastActivityAt,
                Messages = detail.Messages
            });
        });

        app.MapPost("/api/threads/{id:guid}/messages", (Guid id, AddThreadMessageRequest? request, HttpContext context, IRuntimeStore store) =>
        {
            var caller = Caller(context);
            var detail = store.GetThread(id);
            if (detail is null || !Ownership.CanAccessHome(caller, detail.Thread.OwnerSlug, store))
            {
                return Results.NotFound(NotFoundBody);
            }
            if (request is null || string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest(new { error = "text is required." });
            }

            // Authorship is a fact about the caller, never a claim in the body:
            // an agent cannot post as the person, and a person cannot post as
            // the agent.
            var role = caller.Kind == PrincipalKind.Human ? ThreadMessageRole.Person : ThreadMessageRole.Agent;
            var message = store.AppendThreadMessage(id, role, request.Text.Trim());
            AppendAudit(store, caller, "thread.message",
                $"'{caller.Slug}' added a {role.ToString().ToLowerInvariant()} message to thread '{id}'.");
            return Results.Created($"/api/threads/{id}/messages/{message.Id}", message);
        });
    }

    private static RuntimePrincipal Caller(HttpContext context) =>
        (RuntimePrincipal)context.Items["principal"]!;

    private static void AppendAudit(IRuntimeStore store, RuntimePrincipal caller, string action, string detail)
    {
        var userId = caller.Kind == PrincipalKind.Human ? caller.Subject : (Guid?)null;
        var agentId = caller.Kind == PrincipalKind.Agent ? caller.Subject : (Guid?)null;
        // Principal carries the acting slug. Every other audit path sets it and the
        // audit reads filter on it — an event with a null Principal is invisible to
        // the queries that matter, which is a worse failure than not recording it at
        // all, because the trail looks complete.
        store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, userId, agentId,
            action, AuditOutcome.Success, detail, Principal: caller.Slug));
    }
}