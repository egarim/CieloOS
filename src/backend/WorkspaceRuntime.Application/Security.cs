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
    HumanOnly,

    // The machine owner alone. A level rather than a check inside each handler, so
    // it is enforced in the middleware beside the others — a handler-side check has
    // to be remembered by the next person who adds a route, and the fall-through
    // here is AnyPrincipal, which fails open.
    OwnerOnly
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

        // Normalise ONCE, here, rather than asking every rule below to remember.
        // ASP.NET routes case-insensitively and treats a trailing slash as the same
        // route, so "/API/USERS" and "/api/users/" both reach the endpoint that
        // "/api/users" guards. Every `==` rule below is case-sensitive, so without
        // this they matched nothing and fell through to AnyPrincipal — which for a
        // HumanOnly route means an agent token could invite a teammate or rewire the
        // model providers. Public rules failed the safe way (closed); the human-only
        // ones failed open. A single normalisation cannot be forgotten by whoever
        // writes the next rule.
        path = path.ToLowerInvariant();
        if (path.Length > 1 && path.EndsWith('/'))
        {
            path = path.TrimEnd('/');
        }

        // /api/inference/status is public because `workspace-agent status` can
        // run before any token exists on a fresh installation. It reports
        // provider readiness only, never identity-bearing data.
        //
        // /api/setup/* is the first-run claim: on a fresh, unclaimed machine no
        // token exists yet, so these must pass the auth gate. The claim's real
        // guard is in the handler (loopback-only + at-most-one-owner), not here.
        // /api/desk-profiles is public for the same reason as the claim itself:
        // the first-run wizard has to offer the choice of desk before any token
        // exists. It returns names and readiness, nothing about who lives here.
        // /api/auth/login is public for the obvious reason: it is what you use
        // when you have no session yet. Its own guard is the password.
        if (path == "/" || path == "/api/branding" || path == "/api/inference/status"
            || path == "/api/setup/status" || path == "/api/setup/claim"
            || path == "/api/desk-profiles" || path == "/api/auth/login"
            // Which languages this machine has been translated into. Public for the
            // same reason the desk list is: the sign-in screen has to offer it
            // before anyone has signed in, and it says nothing about who lives here.
            || path == "/api/languages")
        {
            return AccessLevel.Public;
        }

        if (path.StartsWith("/api/approvals/", StringComparison.OrdinalIgnoreCase)
            && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.HumanOnly;
        }

        // A scripted demo can drive a desktop and move the mouse, so only the
        // person at the panel may start one. The handler still applies the same
        // session ownership check as the read routes.
        if (path.StartsWith("/api/examples/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/run", StringComparison.OrdinalIgnoreCase)
            && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.HumanOnly;
        }

        // What an AGENT may know about its owner's project work: this one exact
        // path, and nothing else under the prefix. Listed FIRST so the prefix rule
        // below cannot accidentally swallow it, and kept to an exact match so
        // /api/projects/mine/anything is not also open.
        //
        // Delete these four lines and an agent has no project access at all.
        if (path == "/api/projects/mine")
        {
            return AccessLevel.AnyPrincipal;
        }

        // Everything else about projects, on EVERY verb including reads. A rule for
        // the whole prefix rather than a list of routes, because the fall-through at
        // the end of this function is AnyPrincipal: a project route added in six
        // months would otherwise be agent-writable by omission, and nothing would
        // fail to say so.
        if (path == "/api/projects"
            || path.StartsWith("/api/projects/", StringComparison.Ordinal))
        {
            return AccessLevel.HumanOnly;
        }

        // Creating people and organizations is the machine owner's alone.
        //
        // This was HumanOnly, which meant any signed-in person could invite another
        // user onto the box — not into a project, onto the MACHINE, with a home
        // volume and a token. With organizations that is also the power to put
        // somebody inside an organization they were not meant to see.
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && (path == "/api/users" || path == "/api/organizations"))
        {
            return AccessLevel.OwnerOnly;
        }

        // Moving a person between organizations changes who can see them.
        if (path.StartsWith("/api/users/", StringComparison.Ordinal)
            && path.EndsWith("/organization", StringComparison.Ordinal))
        {
            return AccessLevel.OwnerOnly;
        }

        // Who else lives on this machine is not something an agent needs.
        //
        // This read was AnyPrincipal, and GET /api/users takes no HttpContext and
        // applies no projection — so any agent token could enumerate every person
        // here with their email. That flatly contradicted the rule three lines of
        // comment below it already claim to hold for the message directory: "an
        // agent that cannot enumerate people cannot pick a new target for
        // anything." It could not, through one door, and could through another.
        //
        // Found while mapping organizations: org membership hung off PlatformUser
        // would have been readable by every agent token on the machine the day it
        // shipped.
        if (path == "/api/users" || path == "/api/organizations")
        {
            return AccessLevel.HumanOnly;
        }

        // What engines exist and whether they may be installed is an owner question.
        // Unlike the model providers below, the READ is human-only too: the list is
        // a menu of other agent harnesses, and an agent has no use for one.
        if (path == "/api/engines")
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
        // Building a desk image costs gigabytes of disk and a long download, so it
        // is an owner's decision, not something an agent can set off.
        // Sessions and keys belong to the person, so these are human-only: an
        // agent must not be able to mint a credential or end a session.
        // READING them is human-only too: the list is an inventory of a person's
        // credentials and where they are signed in, and an agent token or an API
        // key handing that over is the same disclosure as letting it change them.
        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
            || path == "/api/keys" || path.StartsWith("/api/keys/", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.HumanOnly;
        }
        if (isPost && path == "/api/usage/limits")
        {
            return AccessLevel.HumanOnly;
        }
        if (isPost && path.StartsWith("/api/desk-profiles/", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.HumanOnly;
        }
        if ((isPost || isDelete)
            && (path == "/api/models" || path.StartsWith("/api/models/", StringComparison.OrdinalIgnoreCase)))
        {
            return AccessLevel.HumanOnly;
        }

        // Threads are delegated work. Starting one is a person's act, so an
        // agent cannot invent its own assignment; once a thread exists, the
        // agent must be able to speak in it (with the role forced from the
        // caller by the handler).
        if (isPost && path == "/api/threads")
        {
            return AccessLevel.HumanOnly;
        }
        if (isPost && path.StartsWith("/api/threads/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.AnyPrincipal;
        }

        // The message DIRECTORY is human-only: who else exists on this machine is
        // not something an agent needs, and an agent that cannot enumerate people
        // cannot pick a new target for anything.
        if (path == "/api/messages")
        {
            return AccessLevel.HumanOnly;
        }
        // A single conversation admits agents, because an agent messaging its OWNER
        // is how you find out that a job you were not watching has finished. The
        // handler is what limits an agent to exactly that one counterpart; this
        // level only decides whether an agent token gets through the door at all.
        if (path.StartsWith("/api/messages/", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.AnyPrincipal;
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
// Why a principal was refused, so the middleware can say which without owning the
// decision.
public enum PrincipalRefusal
{
    None,
    NeedsSession,
    NotOwner,
    NotHuman,
    ApiKeyRefused,
}

// The authorisation decision, out of the request pipeline and into a function.
//
// It lived as four sequential `if` blocks inside the middleware, which is not
// reachable from a test — there is no TestServer in this solution — and so was
// covered by exactly none of the suite. That mattered: OwnerOnly was a slug
// comparison with no second factor, and a leaked identity token could create an
// organization and a person inside it from another machine. Four hundred and
// nineteen tests stayed green through all of it, because none of them could see
// this code. A rule nothing can test is a rule nobody is checking.
//
// Order is load-bearing and matches what the middleware did: the session check
// comes before the owner check so somebody holding a valid owner token is told to
// sign in rather than told they are not the owner, which would be a lie.
public static class PrincipalGate
{
    public static PrincipalRefusal Check(
        AccessLevel level,
        PrincipalKind kind,
        bool isMachineOwner,
        bool hasSession,
        bool isApiKey)
    {
        var elevated = level == AccessLevel.HumanOnly || level == AccessLevel.OwnerOnly;

        if (level == AccessLevel.OwnerOnly && !hasSession)
        {
            return PrincipalRefusal.NeedsSession;
        }

        if (level == AccessLevel.OwnerOnly && !isMachineOwner)
        {
            return PrincipalRefusal.NotOwner;
        }

        if (elevated && kind != PrincipalKind.Human)
        {
            return PrincipalRefusal.NotHuman;
        }

        if (elevated && isApiKey)
        {
            return PrincipalRefusal.ApiKeyRefused;
        }

        return PrincipalRefusal.None;
    }

    public static string Explain(PrincipalRefusal refusal) => refusal switch
    {
        PrincipalRefusal.NeedsSession =>
            "Sign in with your password for this. An identity token or API key is not enough for owner actions.",
        PrincipalRefusal.NotOwner => "Only the owner of this machine can do that.",
        PrincipalRefusal.NotHuman => "This operation requires a human principal.",
        PrincipalRefusal.ApiKeyRefused =>
            "An API key cannot do this. Sign in as yourself for credential and owner actions.",
        _ => "",
    };
}

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

    // A caller may read the spreadsheet of the owner group it belongs to. The
    // sheet is keyed by RootUserSlug — a user and its agents share one — so the
    // caller's own group needs no check. An explicit `owner` is honoured only
    // when it names that same group; otherwise null, so a caller can never read
    // another owner group's sheet.
    public static string? ReadableSpreadsheetOwner(RuntimePrincipal caller, string? requestedOwner, IRuntimeStore store)
    {
        var myRoot = RootUserSlug(caller.Slug, store);
        if (string.IsNullOrWhiteSpace(requestedOwner))
        {
            return myRoot;
        }

        return string.Equals(requestedOwner, myRoot, StringComparison.Ordinal) ? myRoot : null;
    }
}
