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
public interface ISetupService
{
    bool IsClaimed();
    ClaimResult Claim(string? name, bool fromLoopback);
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

        var slug = Slugify(displayName);
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

            var user = new PlatformUser(Guid.NewGuid(), displayName, $"{slug}@lunos.local", slug);
            var workspace = new Workspace(Guid.NewGuid(), user.Id, $"{displayName}'s workspace");
            var agentSlug = $"{slug}-agent";
            var agent = new AgentProfile(
                Guid.NewGuid(), user.Id, workspace.Id, $"{displayName}'s Agent",
                // No agent-level provider override — resolves through the model
                // registry cascade (user -> OS). With no provider configured the
                // agent gets the UnconfiguredBrain, not a connection error.
                "", OwnerDefaults.AgentTools, agentSlug);

            if (!store.CreateOwner(user, workspace, agent))
            {
                return new ClaimResult(ClaimOutcome.AlreadyClaimed, Error: "This machine already has an owner.");
            }

            var token = authenticator.IssueToken(slug);
            // The agent identity gets its token file too, mirroring the seed path.
            authenticator.IssueToken(agentSlug);
            return new ClaimResult(ClaimOutcome.Ok, slug, token);
        }
    }

    // A conservative slug: lowercase, ASCII alphanumerics kept, every other run
    // collapsed to a single '-', edges trimmed. "José Peña" -> "jos-pe-a".
    private static string Slugify(string value)
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
