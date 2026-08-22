using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

internal static class TestRepository
{
    public static RuntimePrincipal AgentPrincipal(IRuntimeStore store)
    {
        var agent = store.Agents[0];
        return new RuntimePrincipal(PrincipalKind.Agent, agent.Id, agent.Slug, agent.Name);
    }

    public static RuntimePrincipal HumanPrincipal(IRuntimeStore store)
    {
        var user = store.Users[0];
        return new RuntimePrincipal(PrincipalKind.Human, user.Id, user.Slug, user.DisplayName);
    }

    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "WorkspaceRuntime.sln")) &&
               !File.Exists(Path.Combine(directory.FullName, "WorkspaceRuntime.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    public static ISurfaceRegistry Surfaces() => new FileSurfaceRegistry(Root());

    public static IPolicyEngine PolicyEngine() => new ManifestPolicyEngine(Surfaces());
}
