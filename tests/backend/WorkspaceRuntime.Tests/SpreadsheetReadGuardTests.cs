using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The shared spreadsheet is keyed by the owner group a caller belongs to (a user
// and all its agents resolve to the same RootUserSlug and share one sheet). A
// caller may read that sheet; it may never read another owner group's.
public class SpreadsheetReadGuardTests
{
    private static RuntimePrincipal Human(IRuntimeStore store, string slug)
    {
        var user = store.Users.Single(candidate => candidate.Slug == slug);
        return new RuntimePrincipal(PrincipalKind.Human, user.Id, user.Slug, user.DisplayName);
    }

    private static RuntimePrincipal Agent(IRuntimeStore store, string slug)
    {
        var agent = store.Agents.Single(candidate => candidate.Slug == slug);
        return new RuntimePrincipal(PrincipalKind.Agent, agent.Id, agent.Slug, agent.Name);
    }

    [Fact]
    public void An_agent_reads_the_sheet_of_the_owner_group_it_belongs_to()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = Agent(store, "joche-agent");

        // No explicit owner: the caller's own group, even for an agent (whose
        // RootUserSlug is its owner's slug, not its own).
        Assert.Equal("joche", Ownership.ReadableSpreadsheetOwner(jocheAgent, null, store));

        // The seeded store only has a sheet for joche; the agent resolves to it.
        var sheet = store.GetSpreadsheet(Ownership.ReadableSpreadsheetOwner(jocheAgent, null, store)!);
        Assert.True(sheet.Cells.ContainsKey("A1"));

        // Naming its own owner group explicitly is the same sheet.
        Assert.Equal("joche", Ownership.ReadableSpreadsheetOwner(jocheAgent, "joche", store));
    }

    [Fact]
    public void A_principal_cannot_read_another_owner_groups_sheet()
    {
        var store = new InMemoryRuntimeStore();
        var jocheAgent = Agent(store, "joche-agent");
        var joche = Human(store, "joche");

        // An agent may not reach yulia's group; a user may not either.
        Assert.Null(Ownership.ReadableSpreadsheetOwner(jocheAgent, "yulia", store));
        Assert.Null(Ownership.ReadableSpreadsheetOwner(joche, "yulia", store));
        Assert.Null(Ownership.ReadableSpreadsheetOwner(joche, "yulia-agent", store));
    }
}
