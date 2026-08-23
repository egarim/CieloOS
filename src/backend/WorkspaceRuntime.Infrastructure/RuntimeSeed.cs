using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// The demo population, shared by both stores: two humans, each owning one
// agent. Slugs are the stable identity keys (home volume, audit, tokens).
internal static class RuntimeSeed
{
    public static IReadOnlyList<(PlatformUser User, Workspace Workspace, AgentProfile Agent)> People() => new[]
    {
        Person("11111111-1111-1111-1111-111111111111", "Joche", "joche", "22222222-2222-2222-2222-222222222221", "33333333-3333-3333-3333-333333333331"),
        Person("11111111-1111-1111-1111-111111111112", "Yulia", "yulia", "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333332")
    };

    private static (PlatformUser, Workspace, AgentProfile) Person(string userId, string displayName, string slug, string workspaceId, string agentId)
    {
        var user = new PlatformUser(Guid.Parse(userId), displayName, $"{slug}@example.test", slug);
        var workspace = new Workspace(Guid.Parse(workspaceId), user.Id, $"{displayName}'s workspace");
        var agent = new AgentProfile(
            Guid.Parse(agentId),
            user.Id,
            workspace.Id,
            $"{displayName}'s Agent",
            "local-inference",
            new HashSet<string> { "spreadsheet", "session", "console", "desktop", "session-input" },
            $"{slug}-agent");
        return (user, workspace, agent);
    }
}
