using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

// People talking to each other on this machine.
//
// Deliberately NOT built on threads, and deliberately not a surface.
//
// A thread is delegated work: one owner, their agents, scoped by
// Ownership.CanAccessHome. A direct message crosses exactly the boundary that
// scoping exists to enforce, so it needs its own rule — both ends are people, and
// only those two may read it.
//
// Not a surface because nothing here is an agent's to do. The only communication
// the product needs is a person with their own agent (that is the chat, and it is
// a thread) and a person with another person (this). The moment an agent should be
// able to send a message on its owner's behalf, this becomes a surface with a
// RequireApproval policy — "may I send this?" is one of the three consent moments
// the design brief names — and that will also need the policy engine to be able to
// say "ask when the agent does it, not when the person does". It cannot today:
// ManifestPolicyEngine.Evaluate never sees the acting principal, so one decision
// covers both, and a person would be made to approve their own messages.
public static class MessageApi
{
    // One body for "no such person" and "not someone you can message", for the
    // reason ThreadApi uses one for threads: if the two answers differ by a byte,
    // a slug becomes a probe for who exists on this machine.
    private static readonly object NoSuchPerson = new { error = "No such person." };

    public static void Map(WebApplication app)
    {
        // Who you have talked to, plus who you could. A messenger needs a
        // directory, and this one is every other PERSON on the machine — never an
        // agent, which is not something you message, and never yourself.
        app.MapGet("/api/messages", (HttpContext context, IRuntimeStore store) =>
        {
            var caller = Caller(context);
            return Results.Ok(new
            {
                Conversations = store.ListConversations(caller.Slug),
                People = store.Users
                    .Where(user => !string.Equals(user.Slug, caller.Slug, StringComparison.Ordinal))
                    .OrderBy(user => user.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(user => new { user.Slug, user.DisplayName })
            });
        });

        app.MapGet("/api/messages/{slug}", (string slug, HttpContext context, IRuntimeStore store) =>
        {
            var caller = Caller(context);
            if (!IsMessageablePerson(slug, caller, store))
            {
                return Results.NotFound(NoSuchPerson);
            }

            // Opening the conversation is what marks it read. Doing it on a GET is
            // deliberate: the alternative is a second call the client must remember
            // to make, and a client that forgets leaves the other person looking
            // permanently unread.
            store.MarkConversationRead(caller.Slug, slug);

            return Results.Ok(new
            {
                WithSlug = slug,
                Messages = store.ReadConversation(caller.Slug, slug)
            });
        });

        app.MapPost("/api/messages/{slug}", (string slug, SendMessageRequest? request, HttpContext context, IRuntimeStore store) =>
        {
            var caller = Caller(context);
            if (!IsMessageablePerson(slug, caller, store))
            {
                return Results.NotFound(NoSuchPerson);
            }
            if (request is null || string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest(new { error = "text is required." });
            }

            // Bounded here rather than trusted from the browser. Nothing else
            // limits it, and a megabyte of text would be stored and then sent to
            // someone else's screen.
            var text = request.Text.Trim();
            if (text.Length > 4000)
            {
                return Results.BadRequest(new { error = "A message may be at most 4000 characters." });
            }

            // The sender is the caller, never a field in the body. Otherwise the
            // first thing anyone would do is send a message as somebody else.
            var message = store.SendDirectMessage(caller.Slug, slug, text);
            AppendAudit(store, caller, "message.send", $"'{caller.Slug}' messaged '{slug}'.");
            return Results.Created($"/api/messages/{slug}", message);
        });
    }

    // A person, not an agent, not yourself, and someone who actually exists. All
    // three failures answer identically.
    private static bool IsMessageablePerson(string slug, RuntimePrincipal caller, IRuntimeStore store) =>
        !string.Equals(slug, caller.Slug, StringComparison.Ordinal)
        && store.Users.Any(user => string.Equals(user.Slug, slug, StringComparison.Ordinal));

    private static RuntimePrincipal Caller(HttpContext context) =>
        (RuntimePrincipal)context.Items["principal"]!;

    private static void AppendAudit(IRuntimeStore store, RuntimePrincipal caller, string action, string detail)
    {
        var userId = caller.Kind == PrincipalKind.Human ? caller.Subject : (Guid?)null;
        // The message TEXT is not in the detail. An audit trail an administrator
        // can read must not turn into a transcript of everyone's private messages —
        // that it happened is the auditable fact, not what was said.
        store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, userId, null,
            action, AuditOutcome.Success, detail, Principal: caller.Slug));
    }
}

public sealed record SendMessageRequest(string? Text);
