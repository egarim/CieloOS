using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// Who may see and change a project. Rows only.
//
// THE LAW, and it is checkable by grep: no value originating in a project row may
// reach Ownership.CanAccessHome, an /api/home/* or /api/sessions/* path,
// IHomeBrowser, or a podman volume name — and no project row stores a path.
// Project membership grants a list of RECORDS, never a home.
//
// That is the whole reason this is a separate predicate rather than another clause
// in CanAccessHome. One added disjunct there would simultaneously open cross-user
// home download, session inhabit, desktop screenshots and another owner's audit
// trail — and the live cross-user 403 test would stay green while it happened,
// because its fixtures have no projects in them.
//
// Every function here returns a bool about rows. Exactly one returns a slug, and
// it returns the CALLER's own — never a slug read out of a project.
//
// Beside MessageRules for the reason MessageRules gives for its own existence: a
// rule that can only be exercised by starting a web server is a rule that gets
// tested loosely, and this one has several distinct ways to be wrong.
public static class ProjectRules
{
    // An agent has no membership of its own.
    //
    // Ownership.CanAccessHome returns false for an agent principal even toward its
    // own owner, so an agent can only reach project data by resolving itself to its
    // owner here first — the move /api/shared/* already makes. Any design where an
    // agent reaches a project by naming a slug is wrong by construction.
    public static string ActingUser(RuntimePrincipal caller, IRuntimeStore store) =>
        Ownership.RootUserSlug(caller.Slug, store);

    public static bool SameOrganization(string actingSlug, string orgSlug, IRuntimeStore store)
    {
        var user = store.Users.FirstOrDefault(candidate =>
            string.Equals(candidate.Slug, actingSlug, StringComparison.Ordinal));
        return user is not null && string.Equals(user.OrgSlug, orgSlug, StringComparison.Ordinal);
    }

    // Two clauses, both required, ORGANIZATION FIRST so a mismatch short-circuits
    // before membership is even consulted. Membership of a project in another
    // organization should be impossible; if it ever exists — a bad migration, a
    // direct database edit — it must not be what decides this.
    public static bool MaySee(string actingSlug, Project project, IReadOnlyList<ProjectMember> members, IRuntimeStore store) =>
        SameOrganization(actingSlug, project.OrgSlug, store)
        && (string.Equals(project.LeadSlug, actingSlug, StringComparison.Ordinal)
            || members.Any(member => string.Equals(member.MemberSlug, actingSlug, StringComparison.Ordinal)));

    public static bool MayLead(string actingSlug, Project project, IRuntimeStore store) =>
        SameOrganization(actingSlug, project.OrgSlug, store)
        && string.Equals(project.LeadSlug, actingSlug, StringComparison.Ordinal);

    // The assignee, and ONLY the assignee. The lead cannot write it.
    //
    // This is the owner's decision about progress made structural rather than
    // promised: progress is what the member reports, so the manager physically
    // cannot author the member's report. It also means a report always has one
    // honest author, which is what makes the trail worth reading.
    public static bool MayReport(string actingSlug, ProjectTask task) =>
        string.Equals(task.AssigneeSlug, actingSlug, StringComparison.Ordinal);

    // A candidate has to exist, be a person rather than an agent, and be in the
    // project's organization. The last clause is what stops a project becoming a
    // way to introduce two organizations to each other.
    public static bool MayBeAdded(string candidateSlug, Project project, IRuntimeStore store)
    {
        var candidate = store.Users.FirstOrDefault(user =>
            string.Equals(user.Slug, candidateSlug, StringComparison.Ordinal));
        return candidate is not null
            && string.Equals(candidate.OrgSlug, project.OrgSlug, StringComparison.Ordinal);
    }
}
