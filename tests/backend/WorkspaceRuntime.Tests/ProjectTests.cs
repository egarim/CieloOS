using System.Text.RegularExpressions;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// Projects are records about work. The risk in them is not that a board renders
// badly — it is that "Yulia can see the project" quietly becomes "Yulia can see
// her teammate's home", or that one organization learns another exists.
public class ProjectTests
{
    // The guard that cannot be written any other way.
    //
    // The existing live cross-user 403 test would stay green through a completely
    // broken invariant here, because its fixtures contain no projects. So this is
    // source-level: the law is that nothing originating in a project row may reach
    // the home machinery, and the only way to check "nothing" is to look.
    [Fact]
    public void The_law_holds()
    {
        var forbidden = new Regex(
            @"CanAccessHome|IHomeBrowser|HomeVolumePrefix|SharedVolumePrefix|/api/home|/api/sessions|ListSharedAsync|RootUserSlug",
            RegexOptions.Compiled);

        var offences = new List<string>();
        foreach (var file in new[]
        {
            Path.Combine(TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Application", "ProjectRules.cs"),
            Path.Combine(TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Api", "ProjectApi.cs"),
        })
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                // Comments talk ABOUT the law; code has to obey it.
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (forbidden.IsMatch(line)) offences.Add($"{Path.GetFileName(file)}:{index + 1}: {line.Trim()}");
            }
        }

        // Exactly one: Ownership.RootUserSlug inside ProjectRules.ActingUser, which
        // resolves the CALLER to itself and never touches a slug read out of a row.
        Assert.True(offences.Count == 1,
            "A project file reaches into the home machinery:\n" + string.Join("\n", offences));
        Assert.Contains("RootUserSlug", offences[0], StringComparison.Ordinal);
        Assert.StartsWith("ProjectRules.cs", offences[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_is_invisible_outside_its_organization()
    {
        var world = World();
        var project = world.Store.CreateProject("acme-yulia", "acme", "September proposal");

        // Boris is in another organization. A project id is guessable, so the check
        // has to be membership and not the id.
        Assert.Null(world.Store.ReadProject("nova-boris", project.Id));
        Assert.Empty(world.Store.ListProjectsFor("nova-boris"));

        // And the rule says so independently of the store, because the route asks
        // the rule and the store re-filters — two checks, one for each way of being
        // wrong.
        Assert.False(ProjectRules.MaySee("nova-boris", project, Array.Empty<ProjectMember>(), world.Store));
    }

    [Fact]
    public void Someone_in_the_organization_still_has_to_be_on_the_project()
    {
        var world = World();
        var project = world.Store.CreateProject("acme-yulia", "acme", "September proposal");

        // Ana is in acme but not on this project.
        Assert.Null(world.Store.ReadProject("acme-ana", project.Id));

        world.Store.AddProjectMember(project.Id, "acme-ana");
        Assert.NotNull(world.Store.ReadProject("acme-ana", project.Id));
    }

    [Fact]
    public void A_member_from_another_organization_cannot_be_added()
    {
        var world = World();
        var project = world.Store.CreateProject("acme-yulia", "acme", "September proposal");

        // Otherwise a project becomes the way two organizations meet.
        Assert.False(ProjectRules.MayBeAdded("nova-boris", project, world.Store));
        Assert.True(ProjectRules.MayBeAdded("acme-ana", project, world.Store));
    }

    [Fact]
    public void The_lead_cannot_write_the_members_report()
    {
        var world = World();
        var project = world.Store.CreateProject("acme-yulia", "acme", "September proposal");
        world.Store.AddProjectMember(project.Id, "acme-ana");
        var task = world.Store.AddTask(project.Id, "acme-ana", "Draft the pricing section")!;

        // Progress is what the MEMBER reports. Made structural rather than
        // promised: the manager physically cannot author the member's word, so a
        // report always has one honest author.
        Assert.False(ProjectRules.MayReport("acme-yulia", task));
        Assert.True(ProjectRules.MayReport("acme-ana", task));

        // And the store refuses it too, not just the rule.
        Assert.Null(world.Store.Report("acme-yulia", task.Id, TaskState.Done, "all finished"));
        Assert.NotNull(world.Store.Report("acme-ana", task.Id, TaskState.Doing, "halfway"));
    }

    [Fact]
    public void Reports_accumulate_rather_than_overwrite()
    {
        var world = World();
        var project = world.Store.CreateProject("acme-yulia", "acme", "September proposal");
        world.Store.AddProjectMember(project.Id, "acme-ana");
        var task = world.Store.AddTask(project.Id, "acme-ana", "Draft the pricing section")!;

        world.Store.Report("acme-ana", task.Id, TaskState.Doing, "started");
        world.Store.Report("acme-ana", task.Id, TaskState.Blocked, "waiting on last year's numbers");
        world.Store.Report("acme-ana", task.Id, TaskState.Done, "sent it over");

        var trail = world.Store.ReadReports("acme-yulia", project.Id);

        // The lead gets a record of what was SAID, in order — which is the whole
        // reason this is a table and not a column. The alternative place that
        // history could live is the audit trail, and that is a record of what
        // someone's agent did inside their own home.
        Assert.Equal(3, trail.Count);
        Assert.Equal(new[] { TaskState.Doing, TaskState.Blocked, TaskState.Done }, trail.Select(report => report.State));
        Assert.Equal(new[] { 1L, 2L, 3L }, trail.Select(report => report.Sequence));

        // And the task carries the newest, so a board is one query.
        var refreshed = world.Store.ReadProject("acme-yulia", project.Id)!.Tasks.Single();
        Assert.Equal(TaskState.Done, refreshed.State);
        Assert.Equal("sent it over", refreshed.Note);
    }

    [Fact]
    public void Removing_a_member_removes_their_access_and_keeps_the_record()
    {
        var world = World();
        var project = world.Store.CreateProject("acme-yulia", "acme", "September proposal");
        world.Store.AddProjectMember(project.Id, "acme-ana");
        var task = world.Store.AddTask(project.Id, "acme-ana", "Draft the pricing section")!;
        world.Store.Report("acme-ana", task.Id, TaskState.Done, "sent it over");

        Assert.True(world.Store.RemoveProjectMember(project.Id, "acme-ana"));

        // Gone from her view...
        Assert.Null(world.Store.ReadProject("acme-ana", project.Id));
        // ...and still in the project's record. A history that vanishes when
        // somebody leaves is not a record.
        Assert.Single(world.Store.ReadReports("acme-yulia", project.Id));
        Assert.Single(world.Store.ReadProject("acme-yulia", project.Id)!.Tasks);
    }

    [Fact]
    public void An_agent_reaches_projects_only_as_its_owner()
    {
        var world = World();
        var project = world.Store.CreateProject("acme-yulia", "acme", "September proposal");
        world.Store.AddProjectMember(project.Id, "acme-ana");

        var agent = world.Store.Agents.Single(candidate => candidate.Slug == "acme-ana-agent");
        var principal = new RuntimePrincipal(PrincipalKind.Agent, agent.Id, agent.Slug, agent.Name);

        // Resolved to its owner, never taken from a parameter. An agent slug is
        // never a member of anything.
        Assert.Equal("acme-ana", ProjectRules.ActingUser(principal, world.Store));
        Assert.Empty(world.Store.ListProjectsFor(agent.Slug));
        Assert.Single(world.Store.ListProjectsFor(ProjectRules.ActingUser(principal, world.Store)));
    }

    [Fact]
    public void Project_routes_are_human_only_except_the_one_an_agent_needs()
    {
        // The prefix rule matters more than any single entry: the fall-through in
        // AccessPolicy is AnyPrincipal, so a project route added later is
        // agent-writable by omission unless the whole prefix is covered.
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/projects", "GET"));
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/projects", "POST"));
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/projects/" + Guid.NewGuid(), "GET"));
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/projects/tasks/" + Guid.NewGuid() + "/report", "POST"));
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/projects/something-invented-later", "POST"));

        // The single exception, and it is an EXACT match so nothing deeper opens.
        Assert.Equal(AccessLevel.AnyPrincipal, AccessPolicy.Required("/api/projects/mine", "GET"));
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/projects/mine/everything", "GET"));
    }

    private static (IRuntimeStore Store, ISetupService Setup) WorldTuple()
    {
        var store = new InMemoryRuntimeStore(seedDemo: false);
        var setup = new SetupService(store, new StubAuthenticator());
        setup.Claim("Joche", origin: ClaimOrigin.OnMachine);
        store.AddOrganization(new Organization(Guid.NewGuid(), "acme", "Acme", DateTimeOffset.UtcNow));
        store.AddOrganization(new Organization(Guid.NewGuid(), "nova", "Nova", DateTimeOffset.UtcNow));
        setup.AddUser("Yulia", null, "acme");
        setup.AddUser("Ana", null, "acme");
        setup.AddUser("Boris", null, "nova");
        return (store, setup);
    }

    private static TestWorld World()
    {
        var (store, setup) = WorldTuple();
        return new TestWorld(store, setup);
    }

    private sealed record TestWorld(IRuntimeStore Store, ISetupService Setup);

    private sealed class StubAuthenticator : ITokenAuthenticator
    {
        public RuntimePrincipal? Authenticate(string bearerToken) => null;
        public string Mint(string slug) => $"{slug}:token";
        public string IssueToken(string slug) => Mint(slug);
    }
}
