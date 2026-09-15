using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// Who may talk to whom. This is the whole security model for messages, so it
// lives here rather than as a private helper beside the routes — a rule that can
// only be exercised by starting a web server is a rule that gets tested loosely,
// and this one has four distinct ways to be wrong.
public static class MessageRules
{
    /// <summary>
    ///   a person  may talk to any other PERSON on the machine, and to agents they own
    ///   an agent  may talk to the human that owns it, and to nobody else at all
    /// </summary>
    /// <remarks>
    ///   Every refusal is the same refusal. The caller answers 404 with one constant
    ///   body for all of them, so a slug cannot be used to discover who exists.
    /// </remarks>
    public static bool MayConverseWith(string slug, RuntimePrincipal caller, IRuntimeStore store)
    {
        // Nobody talks to themselves. Without this an agent would pass the owner
        // check below on a machine where RootUserSlug falls back to its own slug.
        if (string.Equals(slug, caller.Slug, StringComparison.Ordinal))
        {
            return false;
        }

        if (caller.Kind == PrincipalKind.Agent)
        {
            // An agent reporting to its own owner is not a consent moment — it is
            // the agent doing its job, which is why none of this needs an approval
            // prompt. An agent messaging a THIRD party would be one, and that is a
            // surface with a policy, not a wider rule here.
            var owner = Ownership.RootUserSlug(caller.Slug, store);
            return !string.Equals(owner, caller.Slug, StringComparison.Ordinal)
                && string.Equals(owner, slug, StringComparison.Ordinal);
        }

        if (store.Users.Any(user => string.Equals(user.Slug, slug, StringComparison.Ordinal)))
        {
            return true;
        }

        // Your own agent, so what it tells you is a conversation you can answer
        // rather than a notification you can only stare at. Ownership.CanAccessHome
        // is what stops this being "any agent on the machine".
        return store.Agents.Any(agent => string.Equals(agent.Slug, slug, StringComparison.Ordinal))
            && Ownership.CanAccessHome(caller, slug, store);
    }
}
