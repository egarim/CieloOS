using System.Text;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// Shared defaults for a newly-created owner, so the first-run claim grants its
// agent exactly the surfaces a demo owner's agent has. Lives in Application as
// the single source of truth (RuntimeSeed in Infrastructure references it too).
public static class OwnerDefaults
{
    public static HashSet<string> AgentTools => new()
    {
        "spreadsheet", "session", "console", "desktop", "session-input"
    };
}

// A conservative slug: lowercase, ASCII alphanumerics kept, every other run
// collapsed to a single '-', edges trimmed. "José Peña" -> "jos-pe-a". Shared by
// identity creation and the provider store so ids are formed one way.
public static class Slug
{
    public static string Of(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}

public enum ClaimOutcome
{
    Ok,             // owner created; Slug + Token are set
    AlreadyClaimed, // an owner already exists (409)
    Forbidden,      // not from loopback (403)
    Invalid         // empty/unusable name (400)
}

public sealed record ClaimResult(ClaimOutcome Outcome, string? Slug = null, string? Token = null, string? Error = null);

// First-run setup: is this machine claimed, and (if not) claim it for the first
// owner. Claiming is allowed ONLY from loopback while unclaimed — the structural
// replacement for a setup token: only someone on the box (a local browser, the
// SSH tunnel the panel already uses, or the CLI) can create the owner. A single
// in-process lock makes concurrent claims single-winner; the runtime is one
// process, so that lock plus the store's at-most-one-owner recheck is authoritative.
public enum AddUserOutcome
{
    Ok,       // user created; Slug + Token are set
    Invalid,  // empty/unusable name (400)
    Conflict  // the slug is already taken (409)
}

public sealed record AddUserResult(AddUserOutcome Outcome, string? Slug = null, string? Token = null, string? Error = null);

public interface ISetupService
{
    bool IsClaimed();

    // The slug of the first owner, or null on an unclaimed box. On-box services
    // (the chat UI) need it to find whose token to act as; it is never returned
    // to a remote caller.
    string? OwnerSlug();
    ClaimResult Claim(string? name, bool fromLoopback);
    // Add a further user AFTER the first owner (an existing owner invites a
    // teammate). Authorization is at the endpoint (human principal); this creates
    // the identity + agent + token. Single-owner today; this is the multi-user seam.
    AddUserResult AddUser(string? name);
}

public sealed class SetupService : ISetupService
{
    private readonly IRuntimeStore store;
    private readonly ITokenAuthenticator authenticator;
    private readonly object gate = new();

    public SetupService(IRuntimeStore store, ITokenAuthenticator authenticator)
    {
        this.store = store;
        this.authenticator = authenticator;
    }

    // A machine with any user is claimed. This doubles as the demo predicate: a
    // demo image (seeded joche/yulia) is already "claimed", so its setup wizard
    // never appears — exactly right.
    public bool IsClaimed() => store.Users.Count > 0;

    public string? OwnerSlug() => store.Users.FirstOrDefault()?.Slug;

    public ClaimResult Claim(string? name, bool fromLoopback)
    {
        if (!fromLoopback)
        {
            return new ClaimResult(ClaimOutcome.Forbidden,
                Error: "Setup can only be claimed from the machine itself (localhost). Open the panel on the box or over an SSH tunnel.");
        }

        var displayName = (name ?? "").Trim();
        if (displayName.Length == 0)
        {
            return new ClaimResult(ClaimOutcome.Invalid, Error: "A non-empty owner name is required.");
        }

        var slug = Slug.Of(displayName);
        if (slug.Length == 0)
        {
            return new ClaimResult(ClaimOutcome.Invalid, Error: "The name must contain at least one letter or digit.");
        }

        lock (gate)
        {
            if (store.Users.Count > 0)
            {
                return new ClaimResult(ClaimOutcome.AlreadyClaimed, Error: "This machine already has an owner.");
            }

            var (user, workspace, agent) = BuildIdentity(displayName, slug);
            if (!store.CreateOwner(user, workspace, agent))
            {
                return new ClaimResult(ClaimOutcome.AlreadyClaimed, Error: "This machine already has an owner.");
            }

            var token = authenticator.IssueToken(slug);
            authenticator.IssueToken(agent.Slug); // the agent identity's token file too
            return new ClaimResult(ClaimOutcome.Ok, slug, token);
        }
    }

    public AddUserResult AddUser(string? name)
    {
        var displayName = (name ?? "").Trim();
        if (displayName.Length == 0)
        {
            return new AddUserResult(AddUserOutcome.Invalid, Error: "A non-empty name is required.");
        }

        var slug = Slug.Of(displayName);
        if (slug.Length == 0)
        {
            return new AddUserResult(AddUserOutcome.Invalid, Error: "The name must contain at least one letter or digit.");
        }

        lock (gate)
        {
            var (user, workspace, agent) = BuildIdentity(displayName, slug);
            // store.AddUser rejects a duplicate user/agent slug inside the same
            // transaction; the lock serializes all identity creation.
            if (!store.AddUser(user, workspace, agent))
            {
                return new AddUserResult(AddUserOutcome.Conflict, Error: $"The name '{displayName}' is already taken (slug '{slug}').");
            }

            var token = authenticator.IssueToken(slug);
            authenticator.IssueToken(agent.Slug);
            return new AddUserResult(AddUserOutcome.Ok, slug, token);
        }
    }

    // One user + their workspace + their agent, with the agent granted the full
    // owner tool set and no provider override (resolves via the registry cascade).
    private static (PlatformUser user, Workspace workspace, AgentProfile agent) BuildIdentity(string displayName, string slug)
    {
        var user = new PlatformUser(Guid.NewGuid(), displayName, $"{slug}@lunos.local", slug);
        var workspace = new Workspace(Guid.NewGuid(), user.Id, $"{displayName}'s workspace");
        var agent = new AgentProfile(
            Guid.NewGuid(), user.Id, workspace.Id, $"{displayName}'s Agent",
            "", OwnerDefaults.AgentTools, $"{slug}-agent");
        return (user, workspace, agent);
    }
}
