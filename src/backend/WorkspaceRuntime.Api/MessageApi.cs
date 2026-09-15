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
// Two kinds of sender arrive here, and that is the point: another PERSON on this
// machine, or YOUR OWN AGENT. This is the inbox — the one place you find out that
// something was said to you, whether a colleague said it or the agent did when it
// finished a job you were not watching.
//
// An agent may message exactly one person: the human that owns it. Not other
// people, not other agents, not another owner's agent. That single rule is why
// none of this needs an approval prompt — an agent reporting to its own owner is
// not a consent moment, it is the agent doing its job. An agent messaging a THIRD
// party would be ("may I send this?"), and that is the day this becomes a surface
// with a RequireApproval policy. It would also need the policy engine to
// distinguish who is acting, which it cannot today: ManifestPolicyEngine.Evaluate
// never sees the principal, so one decision would cover the person too and they
// would be made to approve their own messages.
//
// The directory stays human-only. Who else exists on this machine is not something
// an agent needs, and an agent that cannot enumerate people cannot pick a new
// target for anything.
public static class MessageApi
{
    // One body for "no such person" and "not someone you can message", for the
    // reason ThreadApi uses one for threads: if the two answers differ by a byte,
    // a slug becomes a probe for who exists on this machine.
    private static readonly object NoSuchPerson = new { error = "No such person." };

    public static void Map(WebApplication app)
    {
        // Who you have talked to, plus who you could: every other person IN YOUR
        // ORGANIZATION, and your own agent. The agent belongs in this list because
        // it starts conversations with you — when a job you were not watching
        // finishes, this is where you find out — and a reply has to go somewhere.
        //
        // The organization filter has to match MayConverseWith exactly. A directory
        // that lists someone you cannot then message is a worse bug than either
        // half alone: it tells you the person exists and then refuses, which is the
        // enumeration the constant-404 refusal exists to prevent.
        app.MapGet("/api/messages", (HttpContext context, IRuntimeStore store) =>
        {
            var caller = Caller(context);
            var self = store.Users.FirstOrDefault(user => string.Equals(user.Slug, caller.Slug, StringComparison.Ordinal));
            var people = (self is null ? Enumerable.Empty<PlatformUser>() : OrganizationRules.Visible(self, store.Users))
                .Where(user => !string.Equals(user.Slug, caller.Slug, StringComparison.Ordinal))
                .Select(user => new { Slug = user.Slug, DisplayName = user.DisplayName, IsAgent = false })
                .Concat(store.Agents
                    .Where(agent => Ownership.CanAccessHome(caller, agent.Slug, store))
                    .Select(agent => new { Slug = agent.Slug, DisplayName = agent.Name, IsAgent = true }))
                .OrderBy(person => person.IsAgent)
                .ThenBy(person => person.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return Results.Ok(new
            {
                Conversations = store.ListConversations(caller.Slug),
                People = people
            });
        });

        app.MapGet("/api/messages/{slug}", (string slug, HttpContext context, IRuntimeStore store) =>
        {
            var caller = Caller(context);
            if (!MessageRules.MayConverseWith(slug, caller, store))
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
            if (!MessageRules.MayConverseWith(slug, caller, store))
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

    private static RuntimePrincipal Caller(HttpContext context) =>
        (RuntimePrincipal)context.Items["principal"]!;

    private static void AppendAudit(IRuntimeStore store, RuntimePrincipal caller, string action, string detail)
    {
        var userId = caller.Kind == PrincipalKind.Human ? caller.Subject : (Guid?)null;
        var agentId = caller.Kind == PrincipalKind.Agent ? caller.Subject : (Guid?)null;
        // The message TEXT is not in the detail. An audit trail an administrator
        // can read must not turn into a transcript of everyone's private messages —
        // that it happened is the auditable fact, not what was said.
        store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, userId, agentId,
            action, AuditOutcome.Success, detail, Principal: caller.Slug));
    }
}

public sealed record SendMessageRequest(string? Text);
