using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// Phase A of the first-run setup: a provider-free machine boots with no users,
// its first owner is created by a loopback-gated single-winner claim, and asking
// an unconfigured agent to think returns an honest message rather than an error.
public sealed class FirstRunSetupTests
{
    // --- provider-free store boots cleanly ---

    [Fact]
    public void InMemory_without_demo_has_no_users_but_a_readable_spreadsheet()
    {
        var store = new InMemoryRuntimeStore(seedDemo: false);

        Assert.Empty(store.Users);
        Assert.Empty(store.Agents);
        // The control plane reads the spreadsheet on nearly every operation; it
        // must exist (and be empty) even with the demo population gated off.
        Assert.Empty(store.GetSpreadsheet("").Cells);
        Assert.Equal(0, store.GetSpreadsheetRevision(""));
    }

    [Fact]
    public void InMemory_with_demo_still_seeds_the_two_owners()
    {
        var store = new InMemoryRuntimeStore(); // default seedDemo:true

        Assert.Equal(2, store.Users.Count);
        Assert.Contains(store.Users, user => user.Slug == "joche");
        Assert.Equal("12", store.GetSpreadsheet(store.Users[0].Slug).Cells["A1"]);
    }

    [Fact]
    public void Ef_without_demo_boots_empty_across_reopen_without_crashing()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lunos-firstrun-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            EfRuntimeStore Store() => new(new PooledDbContextFactory<RuntimeDbContext>(options), seedDemo: false, ensureCreated: true);

            var first = Store();
            Assert.Empty(first.Users);
            Assert.Empty(first.GetSpreadsheet("").Cells); // reading an unclaimed sheet must not throw

            // A second boot with no users must NOT try to re-insert the singleton
            // (the bug the per-entity guard prevents): reading it still works.
            var second = Store();
            Assert.Empty(second.Users);
            Assert.Empty(second.GetSpreadsheet("").Cells);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    // --- the claim ---

    [Fact]
    public void Claim_from_loopback_creates_the_owner_agent_and_token()
    {
        Run((store, auth) =>
        {
            var setup = new SetupService(store, auth);
            Assert.False(setup.IsClaimed());

            var result = setup.Claim("Ada Lovelace", origin: ClaimOrigin.OnMachine);

            Assert.Equal(ClaimOutcome.Ok, result.Outcome);
            Assert.Equal("ada-lovelace", result.Slug);
            Assert.True(setup.IsClaimed());

            // Owner + their agent exist, with the full owner tool grant.
            var owner = Assert.Single(store.Users);
            Assert.Equal("ada-lovelace", owner.Slug);
            var agent = Assert.Single(store.Agents);
            Assert.Equal("ada-lovelace-agent", agent.Slug);
            Assert.Equal(OwnerDefaults.AgentTools, agent.GrantedTools);
            Assert.Equal("", agent.InferenceProvider);

            // The returned token authenticates as the new owner.
            var principal = auth.Authenticate(result.Token!);
            Assert.NotNull(principal);
            Assert.Equal("ada-lovelace", principal!.Slug);
            Assert.Equal(PrincipalKind.Human, principal.Kind);
        });
    }

    [Fact]
    public void Claim_is_refused_from_a_non_loopback_caller()
    {
        Run((store, auth) =>
        {
            var setup = new SetupService(store, auth);
            var result = setup.Claim("Grace", origin: ClaimOrigin.OffMachine);

            Assert.Equal(ClaimOutcome.Forbidden, result.Outcome);
            Assert.False(setup.IsClaimed());
            Assert.Empty(store.Users);
        });
    }

    [Fact]
    public void An_empty_name_is_rejected()
    {
        Run((store, auth) =>
        {
            var setup = new SetupService(store, auth);
            Assert.Equal(ClaimOutcome.Invalid, setup.Claim("   ", origin: ClaimOrigin.OnMachine).Outcome);
            Assert.Equal(ClaimOutcome.Invalid, setup.Claim("!!!", origin: ClaimOrigin.OnMachine).Outcome); // slugs to nothing
            Assert.False(setup.IsClaimed());
        });
    }

    [Fact]
    public void A_second_claim_does_not_create_a_second_owner()
    {
        Run((store, auth) =>
        {
            var setup = new SetupService(store, auth);
            Assert.Equal(ClaimOutcome.Ok, setup.Claim("First Owner", origin: ClaimOrigin.OnMachine).Outcome);

            var second = setup.Claim("Second Owner", origin: ClaimOrigin.OnMachine);
            Assert.Equal(ClaimOutcome.AlreadyClaimed, second.Outcome);
            Assert.Single(store.Users);
            Assert.Equal("first-owner", store.Users[0].Slug);
        });
    }

    [Fact]
    public void Concurrent_claims_yield_exactly_one_owner()
    {
        Run((store, auth) =>
        {
            var setup = new SetupService(store, auth);
            var results = new ClaimResult[16];

            Parallel.For(0, results.Length, index =>
                results[index] = setup.Claim($"Owner {index}", origin: ClaimOrigin.OnMachine));

            Assert.Single(results, r => r.Outcome == ClaimOutcome.Ok);
            Assert.Equal(results.Length - 1, results.Count(r => r.Outcome == ClaimOutcome.AlreadyClaimed));
            Assert.Single(store.Users);
        });
    }

    [Fact]
    public void A_demo_machine_is_already_claimed()
    {
        Run((store, auth) =>
        {
            var setup = new SetupService(store, auth);
            Assert.True(setup.IsClaimed());
            Assert.Equal(ClaimOutcome.AlreadyClaimed, setup.Claim("Interloper", origin: ClaimOrigin.OnMachine).Outcome);
        }, seedDemo: true);
    }

    // --- add teammate (multi-user) ---

    [Fact]
    public void AddUser_creates_a_second_identity_with_its_own_token()
    {
        Run((store, auth, secretsDir) =>
        {
            var setup = new SetupService(store, auth);
            setup.Claim("Owner One", origin: ClaimOrigin.OnMachine);

            var result = setup.AddUser("Grace Hopper", null, Organizations.FoundingSlug);

            Assert.Equal(AddUserOutcome.Ok, result.Outcome);
            // Composed from the organization and the person, which is what lets
            // two organizations each have a Grace Hopper on one machine.
            Assert.Equal("main-grace-hopper", result.Slug);
            Assert.Equal(2, store.Users.Count);
            // The agent slug follows the user slug, prefix and all — it has its own
            // home volume and its own token file, both named after it.
            Assert.Contains(store.Agents, agent => agent.Slug == "main-grace-hopper-agent");
            Assert.Equal("main-grace-hopper", auth.Authenticate(result.Token!)!.Slug);
            var tokenPath = Path.Combine(secretsDir, "main-grace-hopper.token");
            Assert.True(File.Exists(tokenPath));
            Assert.Equal(result.Token, File.ReadAllText(tokenPath).Trim());
        });
    }

    [Fact]
    public void AddUser_rejects_a_taken_name()
    {
        Run((store, auth) =>
        {
            var setup = new SetupService(store, auth);
            setup.Claim("Grace Hopper", origin: ClaimOrigin.OnMachine);

            // The owner claimed as "Grace Hopper" and kept the BARE slug
            // "grace-hopper", so a teammate of the same name mints as
            // "main-grace-hopper" and no longer collides with them. Which is a
            // behaviour change worth having a test say out loud.
            var namesake = setup.AddUser("Grace Hopper", null, Organizations.FoundingSlug);
            Assert.Equal(AddUserOutcome.Ok, namesake.Outcome);
            Assert.Equal("main-grace-hopper", namesake.Slug);

            // A second one in the SAME organization is still a conflict.
            var duplicate = setup.AddUser("Grace Hopper", null, Organizations.FoundingSlug);
            Assert.Equal(AddUserOutcome.Conflict, duplicate.Outcome);
            Assert.Equal(2, store.Users.Count);
        });
    }

    [Fact]
    public void AddUser_rejects_an_empty_name()
    {
        Run((store, auth) =>
        {
            var setup = new SetupService(store, auth);
            setup.Claim("Owner", origin: ClaimOrigin.OnMachine);
            Assert.Equal(AddUserOutcome.Invalid, setup.AddUser("   ", null, Organizations.FoundingSlug).Outcome);
        });
    }

    // --- the unconfigured brain ---

    [Fact]
    public async Task UnconfiguredBrain_ends_the_turn_with_an_honest_message()
    {
        var brain = new UnconfiguredBrain();
        var action = await brain.DecideAsync("do something", "", Array.Empty<string>(), 1, 6, CancellationToken.None);

        Assert.True(action.Done);
        Assert.Null(action.Text); // types nothing
        Assert.Contains("No AI provider", action.Note);
    }

    // Runs the body against a fresh secrets dir + real authenticator, cleaning up.
    private static void Run(Action<IRuntimeStore, IdentityTokenAuthenticator> body, bool seedDemo = false)
        => Run((store, auth, _) => body(store, auth), seedDemo);

    private static void Run(Action<IRuntimeStore, IdentityTokenAuthenticator, string> body, bool seedDemo = false)
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), $"lunos-firstrun-secrets-{Guid.NewGuid():N}");
        try
        {
            var store = new InMemoryRuntimeStore(seedDemo);
            var auth = new IdentityTokenAuthenticator(secretsDir, store);
            body(store, auth, secretsDir);
        }
        finally
        {
            if (Directory.Exists(secretsDir)) Directory.Delete(secretsDir, recursive: true);
        }
    }
}
