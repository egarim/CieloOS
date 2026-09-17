using System.Globalization;
using System.Text;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// Shared defaults for a newly-created owner, so the first-run claim grants its
// agent exactly the surfaces a demo owner's agent has. Lives in Application as
// the single source of truth (RuntimeSeed in Infrastructure references it too).
// The organization a machine gets when its owner claims it without naming one.
//
// A real slug, not an empty string. OrgSlug is the only authority on which
// organization a person is in, and "" as a sentinel for "the founding one" is a
// special case every later reader has to learn — including the one who writes the
// next isolation predicate and reads "" as "unset".
public static class Organizations
{
    public const string FoundingSlug = "main";

    // A composed slug has to leave room for "-agent" inside the 40 characters a
    // podman object name and a session id share.
    public const int MaxUserSlug = 31;

    // The organization half of that, leaving room for a person's name after it.
    public const int MaxOrgSlug = 12;
}

public static class OwnerDefaults
{
    public static HashSet<string> AgentTools => new()
    {
        "spreadsheet", "session", "console", "desktop", "session-input", "browser", "recorder"
    };
}

// A slug a person can actually type: lowercase, accents folded onto the letter
// they sit on, every other run collapsed to a single '-', edges trimmed. Shared by
// identity creation and the provider store so ids are formed one way.
//
// This used to keep only ASCII a-z0-9 and collapse everything else, which did not
// transliterate an accented letter — it DELETED it and left a dash in the hole:
//
//     "José Ojeda"  ->  "jos-ojeda"          (not "jose-ojeda")
//     "Ángel Ruiz"  ->  "ngel-ruiz"          (the first letter simply gone)
//     "Søren"       ->  "s-ren"
//
// and the slug is the username, so that is what the person then had to type to
// sign in. The old comment documented "José Peña" -> "jos-pe-a" as if it were a
// design decision rather than a name being mangled.
public static class Slug
{
    // Letters whose mark is part of the glyph rather than a combining character,
    // so NFD leaves them whole and stripping marks does not reach them.
    private static readonly Dictionary<char, string> LatinFolds = new()
    {
        ['ø'] = "o", ['ł'] = "l", ['ß'] = "ss", ['æ'] = "ae", ['œ'] = "oe",
        ['đ'] = "d", ['ð'] = "d", ['þ'] = "th", ['ħ'] = "h", ['ı'] = "i",
    };

    public static string Of(string value)
    {
        // Decompose first, so an accent becomes a separate combining mark we can
        // drop while KEEPING the letter it sat on. Malformed UTF-16 makes
        // Normalize throw, and a name typed into a claim form is user input, so
        // fall back to the raw string rather than turning a bad name into a 500.
        string decomposed;
        try
        {
            decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            decomposed = value.ToLowerInvariant();
        }

        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var character in decomposed)
        {
            // The accent itself, now detached. Drop it without starting a dash:
            // its letter is already in the builder.
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            string? kept = null;
            if (LatinFolds.TryGetValue(character, out var folded))
            {
                kept = folded;
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                kept = character.ToString();
            }

            if (kept is not null)
            {
                builder.Append(kept);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        // A script we cannot fold comes back EMPTY, never as a row of dashes.
        // Empty is what callers check to say "that name will not do"; "----" is a
        // username nobody can guess and a collision between two different people.
        return builder.ToString().Trim('-');
    }

    // May a caller hand us this as a username? Only if it already IS a slug.
    //
    // Anything Slug.Of would change is refused rather than quietly corrected: if
    // we stored Of(value) while the caller believed they chose value, the id in
    // the database and the id they were told are different strings, and every
    // later lookup — the home volume, the agent name, the token file — is a coin
    // toss between the two.
    public static bool IsWellFormed(string? value)
    {
        var candidate = value ?? "";
        return candidate.Length > 0 && Of(candidate) == candidate;
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

// Turning "what they typed" into "the username we will store", for the claim and
// for adding a teammate, which must not drift apart: they mint the same kind of
// identity and a difference between them is a difference nobody would look for.
public static class UsernameChoice
{
    public const string Rule =
        "A username may use lowercase letters, digits and single dashes, and cannot start or end with one.";

    // Returns the slug to use, or null with a reason. `derivedFrom` is the display
    // name to fall back to when no username was chosen.
    public static string? Resolve(string? chosen, string derivedFrom, int maxLength, out string? error)
    {
        var requested = (chosen ?? "").Trim();
        string slug;

        if (requested.Length > 0)
        {
            if (!Slug.IsWellFormed(requested))
            {
                var suggestion = Slug.Of(requested);
                error = suggestion.Length > 0
                    ? $"'{requested}' is not a usable username. {Rule} Did you mean '{suggestion}'?"
                    : $"'{requested}' is not a usable username. {Rule}";
                return null;
            }

            slug = requested;
        }
        else
        {
            slug = Slug.Of(derivedFrom);
            if (slug.Length == 0)
            {
                // A name written in a script nothing here folds. Say what to do
                // rather than "the name must contain a letter or digit", which is
                // false — it is full of them, we just cannot spell them.
                error = $"A username could not be made from '{derivedFrom}'. Choose one instead. {Rule}";
                return null;
            }
        }

        if (slug.Length > maxLength)
        {
            // Refused, never truncated: a truncated slug is a permanently wrong
            // home volume, and two people can truncate to the same one.
            error = $"'{slug}' is {slug.Length} characters and the limit is {maxLength}. Choose a shorter username.";
            return null;
        }

        error = null;
        return slug;
    }
}

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
    // username is what the person will type to sign in. Omitted, it is derived
    // from the display name — which is fine for a Latin name and impossible for a
    // script nothing here folds, so it has to be sayable.
    ClaimResult Claim(string? name, bool fromLoopback, string? deskProfile = null, string? organizationName = null, string? username = null);
    // Add a further user AFTER the first owner (an existing owner invites a
    // teammate). Authorization is at the endpoint (human principal); this creates
    // the identity + agent + token. Single-owner today; this is the multi-user seam.
    // orgSlug names an EXISTING organization, and has no default: a user with no
    // organization is not a user in none of them, it is a user in whichever one
    // the empty string happens to be.
    AddUserResult AddUser(string? name, string? deskProfile, string orgSlug, string? username = null);
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

    // Nothing in the database records WHO claimed the box — every user row looks
    // alike — and the order an EF query returns rows in is not defined, so taking
    // "the first" would be a guess a teammate could win, and an on-box service
    // would then act as them. So answer only when the answer cannot be a guess:
    // one human means one possible owner. With several, say nothing and let the
    // caller refuse (or be told explicitly, via CHAT_OWNER). A real owner column
    // belongs with the login work in #9.
    public string? OwnerSlug() => store.Users.Count == 1 ? store.Users[0].Slug : null;

    public ClaimResult Claim(string? name, bool fromLoopback, string? deskProfile = null, string? organizationName = null, string? username = null)
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

        // The owner keeps a BARE slug (no organization prefix), so the whole
        // budget is theirs. This was not length-checked at all until now: AddUser
        // refused an over-long slug with a comment about permanently wrong home
        // volumes, and the owner — the one account every machine has — was exempt.
        var slug = UsernameChoice.Resolve(username, displayName, Organizations.MaxUserSlug, out var slugError);
        if (slug is null)
        {
            return new ClaimResult(ClaimOutcome.Invalid, Error: slugError);
        }

        lock (gate)
        {
            if (store.Users.Count > 0)
            {
                return new ClaimResult(ClaimOutcome.AlreadyClaimed, Error: "This machine already has an owner.");
            }

            // The founding organization, created with the owner because every user
            // needs one and the owner is the first. Its display name is renameable;
            // its slug is not, so it stays short and neutral when unnamed.
            var orgName = (organizationName ?? "").Trim();
            var orgSlug = orgName.Length > 0 ? Slug.Of(orgName) : Organizations.FoundingSlug;
            if (orgSlug.Length == 0)
            {
                orgSlug = Organizations.FoundingSlug;
                orgName = "";
            }

            store.AddOrganization(new Organization(
                Guid.NewGuid(), orgSlug, orgName.Length > 0 ? orgName : "Main", DateTimeOffset.UtcNow));

            // The founder keeps a BARE slug, with no organization prefix. A prefix
            // records how a user was minted, not where they belong — OrgSlug is the
            // only authority on the second question — so the owner reads as "joche"
            // rather than "main-joche" forever, and can still be moved to another
            // organization later by changing one column.
            var (user, workspace, agent) = BuildIdentity(displayName, slug, deskProfile, orgSlug, isMachineOwner: true);
            if (!store.CreateOwner(user, workspace, agent))
            {
                return new ClaimResult(ClaimOutcome.AlreadyClaimed, Error: "This machine already has an owner.");
            }

            var token = authenticator.IssueToken(slug);
            authenticator.IssueToken(agent.Slug); // the agent identity's token file too
            return new ClaimResult(ClaimOutcome.Ok, slug, token);
        }
    }

    public AddUserResult AddUser(string? name, string? deskProfile, string orgSlug, string? username = null)
    {
        var displayName = (name ?? "").Trim();
        if (displayName.Length == 0)
        {
            return new AddUserResult(AddUserOutcome.Invalid, Error: "A non-empty name is required.");
        }

        // The org prefix is added below, so the person-half is what has room for
        // it: the budget here is the total minus "<org>-".
        var personBudget = Organizations.MaxUserSlug - orgSlug.Length - 1;
        var personSlug = UsernameChoice.Resolve(username, displayName, Math.Max(personBudget, 1), out var slugError);
        if (personSlug is null)
        {
            return new AddUserResult(AddUserOutcome.Invalid, Error: slugError);
        }

        if (store.FindOrganization(orgSlug) is null)
        {
            return new AddUserResult(AddUserOutcome.Invalid, Error: "That organization does not exist on this machine.");
        }

        // The minting rule. Composed with '-' and not '_' because Slug.Of collapses
        // every non-alphanumeric run to '-', so '-' is the only separator that
        // survives a round trip through the codebase's own slug function:
        // Slug.Of("acme_maria") is "acme-maria", and a composed slug that CHANGES
        // when it passes through Slug.Of is a slug that can quietly become another
        // organization's.
        var slug = $"{orgSlug}-{personSlug}";
        if (slug.Length > Organizations.MaxUserSlug)
        {
            return new AddUserResult(AddUserOutcome.Invalid,
                Error: $"'{displayName}' in '{orgSlug}' makes a {slug.Length}-character name and the limit is "
                     + $"{Organizations.MaxUserSlug}. Use a shorter name or a shorter organization slug. It is not "
                     + "truncated on purpose: a truncated slug is a permanently wrong home volume.");
        }

        lock (gate)
        {
            var (user, workspace, agent) = BuildIdentity(displayName, slug, deskProfile, orgSlug, isMachineOwner: false);
            // store.AddUser rejects a duplicate user/agent slug inside the same
            // transaction; the lock serializes all identity creation.
            if (!store.AddUser(user, workspace, agent))
            {
                return new AddUserResult(AddUserOutcome.Conflict, Error: $"The name '{displayName}' is already taken in '{orgSlug}' (slug '{slug}').");
            }

            var token = authenticator.IssueToken(slug);
            authenticator.IssueToken(agent.Slug);
            return new AddUserResult(AddUserOutcome.Ok, slug, token);
        }
    }

    // One user + their workspace + their agent, with the agent granted the full
    // owner tool set and no provider override (resolves via the registry cascade).
    private static (PlatformUser user, Workspace workspace, AgentProfile agent) BuildIdentity(
        string displayName, string slug, string? deskProfile, string orgSlug, bool isMachineOwner)
    {
        // An unknown id resolves to the default rather than failing: a desk is
        // more useful than an error, and the profile only decides what is
        // installed, never who the person is.
        var profile = DeskProfiles.Resolve(deskProfile);
        var user = new PlatformUser(
            Guid.NewGuid(), displayName, $"{slug}@lunos.local", slug, orgSlug, isMachineOwner, profile.Id);
        var workspace = new Workspace(Guid.NewGuid(), user.Id, $"{displayName}'s workspace");
        var agent = new AgentProfile(
            Guid.NewGuid(), user.Id, workspace.Id, $"{displayName}'s Agent",
            "", profile.AgentTools.ToHashSet(), $"{slug}-agent");
        return (user, workspace, agent);
    }
}
