using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// The demo population, shared by both stores: two humans, each owning one
// agent. Slugs are the stable identity keys (home volume, audit, tokens).
internal static class RuntimeSeed
{
    public static IReadOnlyList<(PlatformUser User, Workspace Workspace, AgentProfile Agent)> People() => new[]
    {
        // Both in the founding organization, and neither slug carries its prefix:
        // they are the shape a machine has after an upgrade, where the people
        // predate organizations and keep the slugs their homes are named after.
        Person("11111111-1111-1111-1111-111111111111", "Joche", "joche", "22222222-2222-2222-2222-222222222221", "33333333-3333-3333-3333-333333333331", isMachineOwner: true),
        Person("11111111-1111-1111-1111-111111111112", "Yulia", "yulia", "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333332", isMachineOwner: false)
    };

    private static (PlatformUser, Workspace, AgentProfile) Person(
        string userId, string displayName, string slug, string workspaceId, string agentId, bool isMachineOwner)
    {
        var user = new PlatformUser(
            Guid.Parse(userId), displayName, $"{slug}@example.test", slug,
            Application.Organizations.FoundingSlug, isMachineOwner);
        var workspace = new Workspace(Guid.Parse(workspaceId), user.Id, $"{displayName}'s workspace");
        var agent = new AgentProfile(
            Guid.Parse(agentId),
            user.Id,
            workspace.Id,
            $"{displayName}'s Agent",
            // Empty = "no agent-level override": the model registry resolves this
            // agent's chat/vision through the user -> OS cascade. (The old
            // "local-inference" was a dead provider id that resolved to nothing.)
            "",
            OwnerDefaults.AgentTools,
            $"{slug}-agent");
        return (user, workspace, agent);
    }
}
