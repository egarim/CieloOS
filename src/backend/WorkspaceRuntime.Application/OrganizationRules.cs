using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// Who may know that whom exists, once a machine has more than one organization on
// it.
//
// Deliberately NOT a clause added to Ownership.CanAccessHome. That function answers
// a question about a HOME — a slug-named filesystem, a session stream, an audit
// trail — and it gates about twenty-five routes. One added disjunct there would
// open cross-user home download, session inhabit, desktop screenshots and another
// owner's audit trail all at once, and the live isolation test would stay green
// because its fixtures have no organizations in them.
//
// This answers a different question, and it is the only question organizations get
// to answer. Nothing here produces a slug that is then handed to CanAccessHome.
//
// Same shape as MessageRules for the same reason: a pure static function, so the
// rule can be tested without a web server and read without one.
public static class OrganizationRules
{
    // The authority is the COLUMN, never the slug.
    //
    // A slug like "acme-maria" records how that user was minted. A user moved
    // between organizations keeps the old prefix and belongs to the new
    // organization, and the two users who predate organizations have no prefix at
    // all. Anything that parsed the organization back out of a slug would be
    // confidently wrong about exactly those people — which is why nothing does.
    public static bool SameOrganization(PlatformUser one, PlatformUser other) =>
        string.Equals(one.OrgSlug, other.OrgSlug, StringComparison.Ordinal);

    // May the caller know this person exists?
    //
    // The machine owner may see everybody, because they create the organizations
    // and the users, and an administrator who cannot see the machine they
    // administer would simply go and read the database instead.
    //
    // ONE predicate, deliberately, because there are two callers — the people
    // directory and the messaging rule — and a directory that lists someone you
    // cannot then message is worse than either half being wrong alone: it confirms
    // the person exists and then refuses, which is precisely the enumeration that
    // the constant-404 refusal exists to prevent.
    public static bool MaySee(PlatformUser caller, PlatformUser other) =>
        caller.IsMachineOwner || SameOrganization(caller, other);

    // Everyone the caller may know exists.
    public static IEnumerable<PlatformUser> Visible(PlatformUser caller, IEnumerable<PlatformUser> everyone) =>
        everyone.Where(other => MaySee(caller, other));
}
