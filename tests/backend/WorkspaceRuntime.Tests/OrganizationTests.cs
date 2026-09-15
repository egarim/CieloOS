using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// Several organizations on one machine, isolated from each other.
//
// The whole scheme rests on one idea: an organization is composed into the slug at
// MINT time, and after that nothing ever parses it back out. That is what makes
// `acme-maria` and `nova-maria` two different people with two different homes
// without a single edit to Ownership.CanAccessHome — and it is also the thing that
// quietly stops being true the first time somebody writes slug.Split('-')[0].
public class OrganizationTests
{
    [Fact]
    public void Two_organizations_can_each_have_a_maria()
    {
        var (setup, store) = Machine();
        setup.Claim("Joche", fromLoopback: true);
        store.AddOrganization(new Organization(Guid.NewGuid(), "acme", "Acme", DateTimeOffset.UtcNow));
        store.AddOrganization(new Organization(Guid.NewGuid(), "nova", "Nova", DateTimeOffset.UtcNow));

        var one = setup.AddUser("Maria", null, "acme");
        var two = setup.AddUser("Maria", null, "nova");

        Assert.Equal(AddUserOutcome.Ok, one.Outcome);
        Assert.Equal(AddUserOutcome.Ok, two.Outcome);
        Assert.Equal("acme-maria", one.Slug);
        Assert.Equal("nova-maria", two.Slug);

        // Different slugs mean different home volumes and different token files,
        // with no new isolation code anywhere. That is the entire point of doing it
        // this way rather than by adding a column to every check.
        Assert.NotEqual(one.Slug, two.Slug);
    }

    [Fact]
    public void One_organizations_person_cannot_reach_anothers_home()
    {
        var (setup, store) = Machine();
        setup.Claim("Joche", fromLoopback: true);
        store.AddOrganization(new Organization(Guid.NewGuid(), "acme", "Acme", DateTimeOffset.UtcNow));
        store.AddOrganization(new Organization(Guid.NewGuid(), "nova", "Nova", DateTimeOffset.UtcNow));
        setup.AddUser("Maria", null, "acme");
        setup.AddUser("Maria", null, "nova");

        var acme = Principal(store, "acme-maria");

        // CanAccessHome was not touched by any of this work, and does not need to
        // be: two different slugs is a string comparison it already makes. If a
        // later change ever DOES add an organization clause there, this is the test
        // that should be read again first — one added disjunct opens home download,
        // session inhabit, screenshots and another owner's audit trail at once.
        Assert.False(Ownership.CanAccessHome(acme, "nova-maria", store));
        Assert.True(Ownership.CanAccessHome(acme, "acme-maria", store));
    }

    [Fact]
    public void People_and_messaging_agree_about_who_exists()
    {
        var (setup, store) = Machine();
        setup.Claim("Joche", fromLoopback: true);
        store.AddOrganization(new Organization(Guid.NewGuid(), "acme", "Acme", DateTimeOffset.UtcNow));
        store.AddOrganization(new Organization(Guid.NewGuid(), "nova", "Nova", DateTimeOffset.UtcNow));
        setup.AddUser("Maria", null, "acme");
        setup.AddUser("Ana", null, "acme");
        setup.AddUser("Boris", null, "nova");

        var maria = store.Users.Single(user => user.Slug == "acme-maria");
        var visible = OrganizationRules.Visible(maria, store.Users).Select(user => user.Slug).ToList();

        Assert.Contains("acme-ana", visible);
        Assert.DoesNotContain("nova-boris", visible);
        Assert.DoesNotContain("joche", visible);

        // The directory and the messaging rule MUST agree. A directory that lists
        // someone you cannot then message is worse than either half being wrong on
        // its own: it confirms the person exists and then refuses, which is exactly
        // the enumeration the constant-404 refusal exists to prevent.
        var principal = Principal(store, "acme-maria");
        foreach (var user in store.Users.Where(user => user.Slug != "acme-maria"))
        {
            Assert.Equal(
                visible.Contains(user.Slug),
                MessageRules.MayConverseWith(user.Slug, principal, store));
        }
    }

    [Fact]
    public void The_machine_owner_sees_everyone_and_can_message_them()
    {
        var (setup, store) = Machine();
        setup.Claim("Joche", fromLoopback: true);
        store.AddOrganization(new Organization(Guid.NewGuid(), "acme", "Acme", DateTimeOffset.UtcNow));
        setup.AddUser("Maria", null, "acme");

        var owner = store.Users.Single(user => user.Slug == "joche");
        Assert.True(owner.IsMachineOwner);

        // An administrator who cannot see the machine they administer would just go
        // and read the database instead.
        Assert.Contains("acme-maria", OrganizationRules.Visible(owner, store.Users).Select(user => user.Slug));
        Assert.True(MessageRules.MayConverseWith("acme-maria", Principal(store, "joche"), store));
    }

    [Fact]
    public void Moving_a_person_changes_the_column_and_not_their_slug()
    {
        var (setup, store) = Machine();
        setup.Claim("Joche", fromLoopback: true);
        store.AddOrganization(new Organization(Guid.NewGuid(), "acme", "Acme", DateTimeOffset.UtcNow));
        store.AddOrganization(new Organization(Guid.NewGuid(), "nova", "Nova", DateTimeOffset.UtcNow));
        setup.AddUser("Maria", null, "acme");
        setup.AddUser("Boris", null, "nova");

        Assert.True(store.SetUserOrganization("acme-maria", "nova"));

        var maria = store.Users.Single(user => user.Slug == "acme-maria");

        // Her slug still says acme, and she is in nova. This is the case that breaks
        // any code which reads the organization out of the prefix, and the reason
        // moving somebody is possible at all: her home volume, token file, audit
        // history and spreadsheet are keyed on the slug, and none of them moved.
        Assert.Equal("acme-maria", maria.Slug);
        Assert.Equal("nova", maria.OrgSlug);
        Assert.True(MessageRules.MayConverseWith("nova-boris", Principal(store, "acme-maria"), store));
    }

    [Fact]
    public void A_name_that_would_not_fit_is_refused_rather_than_truncated()
    {
        var (setup, store) = Machine();
        setup.Claim("Joche", fromLoopback: true);
        store.AddOrganization(new Organization(Guid.NewGuid(), "engineering", "Engineering", DateTimeOffset.UtcNow));

        var result = setup.AddUser("Alexandra Konstantinovna", null, "engineering");

        // A truncated slug is a permanently wrong home volume name, which is not
        // something to discover later.
        Assert.Equal(AddUserOutcome.Invalid, result.Outcome);
        Assert.Contains("shorter", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(store.Users, user => user.Slug.StartsWith("engineering-alexandra", StringComparison.Ordinal));
    }

    [Fact]
    public void A_user_cannot_be_minted_into_an_organization_that_does_not_exist()
    {
        var (setup, store) = Machine();
        setup.Claim("Joche", fromLoopback: true);

        var result = setup.AddUser("Maria", null, "ghost");

        // Otherwise they would exist in an organization nobody is in, able to see
        // nobody and be seen by nobody, which reads as a broken account rather than
        // as a mistake somebody made.
        Assert.Equal(AddUserOutcome.Invalid, result.Outcome);
        Assert.Single(store.Users);
    }

    [Fact]
    public void Creating_people_and_organizations_is_the_owners_alone()
    {
        Assert.Equal(AccessLevel.OwnerOnly, AccessPolicy.Required("/api/users", "POST"));
        Assert.Equal(AccessLevel.OwnerOnly, AccessPolicy.Required("/api/organizations", "POST"));
        Assert.Equal(AccessLevel.OwnerOnly, AccessPolicy.Required("/api/users/acme-maria/organization", "POST"));

        // Reading is human-only rather than owner-only: a person needs the people
        // list to message a colleague. An agent does not.
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/organizations", "GET"));
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/users", "GET"));
    }

    // The test that decides whether a machine with people already on it survives
    // the upgrade. Everything else here runs against a machine built by this build.
    [Fact]
    public void An_existing_machine_keeps_its_people_and_gains_an_owner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cielo-orgs-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite($"Data Source={path}").Options;
        RuntimeDbContext Context() => new(options);

        try
        {
            // A database as it stood BEFORE organizations existed.
            using (var context = Context())
            {
                context.GetService<IMigrator>().Migrate("20260915063249_DirectMessages");
                context.Database.ExecuteSqlRaw(
                    "INSERT INTO runtime_users (Id, DisplayName, Email, Slug, DeskProfile, Language, PasswordHash) VALUES "
                    + "('11111111-1111-1111-1111-111111111111', 'Joche', 'j@x.test', 'joche', 'office', 'en', ''),"
                    + "('11111111-1111-1111-1111-111111111112', 'Yulia', 'y@x.test', 'yulia', 'office', 'en', '')");
            }

            using (var context = Context())
            {
                context.Database.Migrate();
            }

            using (var context = Context())
            {
                var users = context.Users.AsNoTracking().OrderBy(user => user.Slug).ToList();

                // Their slugs are untouched — which is why no podman volume, token
                // file, audit principal or spreadsheet key had to move.
                Assert.Equal(new[] { "joche", "yulia" }, users.Select(user => user.Slug));

                // Both in a real organization that actually exists. Left at the
                // column default they would be in '', an organization with no row,
                // and would be unable to see anyone — including each other.
                Assert.All(users, user => Assert.Equal("main", user.OrgSlug));
                Assert.Single(context.Organizations, organization => organization.Slug == "main");

                // And EXACTLY ONE machine owner. With none, nobody could ever create
                // a user or an organization again: the box would come up, serve
                // every page, and be quietly bricked.
                Assert.Single(users.Where(user => user.IsMachineOwner));
            }
        }
        finally
        {
            SqliteConnectionCleanup();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    private static void SqliteConnectionCleanup() =>
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private static RuntimePrincipal Principal(IRuntimeStore store, string slug)
    {
        var user = store.Users.Single(candidate => candidate.Slug == slug);
        return new RuntimePrincipal(PrincipalKind.Human, user.Id, user.Slug, user.DisplayName);
    }

    private static (ISetupService Setup, IRuntimeStore Store) Machine()
    {
        var store = new InMemoryRuntimeStore(seedDemo: false);
        return (new SetupService(store, new StubAuthenticator()), store);
    }

    private sealed class StubAuthenticator : ITokenAuthenticator
    {
        public RuntimePrincipal? Authenticate(string bearerToken) => null;
        public string Mint(string slug) => $"{slug}:token";
        public string IssueToken(string slug) => Mint(slug);
    }
}
