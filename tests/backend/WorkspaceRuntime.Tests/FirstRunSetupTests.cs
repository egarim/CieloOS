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
        Assert.Empty(store.Spreadsheet.Cells);
        Assert.Equal(0, store.SpreadsheetRevision);
    }

    [Fact]
    public void InMemory_with_demo_still_seeds_the_two_owners()
    {
        var store = new InMemoryRuntimeStore(); // default seedDemo:true

        Assert.Equal(2, store.Users.Count);
        Assert.Contains(store.Users, user => user.Slug == "joche");
        Assert.Equal("12", store.Spreadsheet.Cells["A1"]);
    }

    [Fact]
    public void Ef_without_demo_boots_empty_across_reopen_without_crashing()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lunos-firstrun-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            EfRuntimeStore Store() => new(new PooledDbContextFactory<RuntimeDbContext>(options), seedDemo: false);

            var first = Store();
            Assert.Empty(first.Users);
            Assert.Empty(first.Spreadsheet.Cells); // reading the singleton must not throw

            // A second boot with no users must NOT try to re-insert the singleton
            // (the bug the per-entity guard prevents): reading it still works.
            var second = Store();
            Assert.Empty(second.Users);
            Assert.Empty(second.Spreadsheet.Cells);
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

            var result = setup.Claim("Ada Lovelace", fromLoopback: true);

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
            var result = setup.Claim("Grace", fromLoopback: false);

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
            Assert.Equal(ClaimOutcome.Invalid, setup.Claim("   ", fromLoopback: true).Outcome);
            Assert.Equal(ClaimOutcome.Invalid, setup.Claim("!!!", fromLoopback: true).Outcome); // slugs to nothing
            Assert.False(setup.IsClaimed());
        });
    }

    [Fact]
    public void A_second_claim_does_not_create_a_second_owner()
    {
        Run((store, auth) =>
        {
            var setup = new SetupService(store, auth);
            Assert.Equal(ClaimOutcome.Ok, setup.Claim("First Owner", fromLoopback: true).Outcome);

            var second = setup.Claim("Second Owner", fromLoopback: true);
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
                results[index] = setup.Claim($"Owner {index}", fromLoopback: true));

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
            Assert.Equal(ClaimOutcome.AlreadyClaimed, setup.Claim("Interloper", fromLoopback: true).Outcome);
        }, seedDemo: true);
    }

    // --- the unconfigured brain ---

    [Fact]
    public async Task UnconfiguredBrain_ends_the_turn_with_an_honest_message()
    {
        var brain = new UnconfiguredBrain();
        var action = await brain.DecideAsync("do something", "", Array.Empty<string>(), 1, CancellationToken.None);

        Assert.True(action.Done);
        Assert.Null(action.Text); // types nothing
        Assert.Contains("No AI provider", action.Note);
    }

    // Runs the body against a fresh secrets dir + real authenticator, cleaning up.
    private static void Run(Action<IRuntimeStore, IdentityTokenAuthenticator> body, bool seedDemo = false)
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), $"lunos-firstrun-secrets-{Guid.NewGuid():N}");
        try
        {
            var store = new InMemoryRuntimeStore(seedDemo);
            var auth = new IdentityTokenAuthenticator(secretsDir, store);
            body(store, auth);
        }
        finally
        {
            if (Directory.Exists(secretsDir)) Directory.Delete(secretsDir, recursive: true);
        }
    }
}
