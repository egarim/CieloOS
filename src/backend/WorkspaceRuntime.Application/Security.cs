using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// Backwards-compatible slug constants used by tests and by pre-identity call
// sites. With multi-user identity the audit principal is a real slug (e.g.
// "joche"); these remain valid slugs for the single-identity paths.
public static class RuntimePrincipals
{
    public const string Human = "human";
    public const string Agent = "agent";
}

public enum AccessLevel
{
    Public,
    AnyPrincipal,
    HumanOnly
}

// The single map from route to required principal. Reads are policed too:
// observation is a privileged operation, not a free channel.
public static class AccessPolicy
{
    public static AccessLevel Required(string path, string method)
    {
        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.Public;
        }

        // /api/inference/status doubles as the readiness probe that the VM's
        // agent-runtime.service and `workspace-agent status` call before any
        // token can exist on a fresh installation.
        //
        // /api/setup/* is the first-run claim: on a fresh, unclaimed machine no
        // token exists yet, so these must pass the auth gate. The claim's real
        // guard is in the handler (loopback-only + at-most-one-owner), not here.
        if (path == "/" || path == "/api/branding" || path == "/api/inference/status"
            || path == "/api/setup/status" || path == "/api/setup/claim")
        {
            return AccessLevel.Public;
        }

        if (path.StartsWith("/api/approvals/", StringComparison.OrdinalIgnoreCase)
            && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.HumanOnly;
        }

        // Owner (human) actions: inviting a teammate, and changing model providers
        // or defaults. Reads (GET) of these stay AnyPrincipal.
        var isPost = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);
        var isDelete = string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase);
        if (isPost && path == "/api/users")
        {
            return AccessLevel.HumanOnly;
        }
        if ((isPost || isDelete)
            && (path == "/api/models" || path.StartsWith("/api/models/", StringComparison.OrdinalIgnoreCase)))
        {
            return AccessLevel.HumanOnly;
        }

        return AccessLevel.AnyPrincipal;
    }
}

public static class PrincipalResolver
{
    public static RuntimePrincipal? BySlug(IReadOnlyList<PlatformUser> users, IReadOnlyList<AgentProfile> agents, string slug)
    {
        var user = users.FirstOrDefault(candidate => string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
        if (user is not null)
        {
            return new RuntimePrincipal(PrincipalKind.Human, user.Id, user.Slug, user.DisplayName);
        }

        var agent = agents.FirstOrDefault(candidate => string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
        if (agent is not null)
        {
            return new RuntimePrincipal(PrincipalKind.Agent, agent.Id, agent.Slug, agent.Name);
        }

        return null;
    }
}

public interface ITokenAuthenticator
{
    // Resolves a presented bearer token to a caller identity, or null. The
    // token cryptographically binds to a slug; the slug is then looked up
    // among the known users and agents.
    RuntimePrincipal? Authenticate(string bearerToken);

    // Deterministically mint the bearer token for a slug (does not touch disk).
    string Mint(string slug);

    // Mint AND persist the slug's 0600 token file, returning the token. Used when
    // an identity is created at runtime (the first-run claim) — the constructor's
    // startup pass has already run, so a new owner's token file must be written now.
    string IssueToken(string slug);
}

// Ownership: a human may act as, and inhabit, only the agents it owns; an
// agent is only ever itself. This is the boundary that makes "joche's agents"
// distinct from "yulia's agents".
public static class Ownership
{
    public static bool CanAccessHome(RuntimePrincipal principal, string homeSlug, IRuntimeStore store)
    {
        if (string.Equals(principal.Slug, homeSlug, StringComparison.Ordinal))
        {
            return true;
        }

        // A human may reach the homes of agents it owns.
        if (principal.Kind == PrincipalKind.Human)
        {
            var target = store.Agents.FirstOrDefault(agent => string.Equals(agent.Slug, homeSlug, StringComparison.Ordinal));
            return target is not null && target.OwnerUserId == principal.Subject;
        }

        return false;
    }

    // The USER a slug belongs to: a user slug maps to itself; an agent slug maps
    // to its owning user. This picks the per-owner shared workspace so a user and
    // all their agents share one `lunos-shared-<user>` space.
    public static string RootUserSlug(string slug, IRuntimeStore store)
    {
        if (store.Users.Any(user => string.Equals(user.Slug, slug, StringComparison.Ordinal)))
        {
            return slug;
        }

        var agent = store.Agents.FirstOrDefault(candidate => string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
        if (agent is not null)
        {
            var owner = store.Users.FirstOrDefault(user => user.Id == agent.OwnerUserId);
            if (owner is not null)
            {
                return owner.Slug;
            }
        }

        return slug;
    }
}
