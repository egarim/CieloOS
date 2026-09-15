using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The path nobody tested, which is why #49 survived three releases: an EXISTING
// database taking a NEW build. Every other schema test starts from nothing, and a
// fresh start is exactly the case that always worked — EnsureCreated builds today's
// model, so a new machine is correct by construction. The machine that already had
// a database got a binary expecting columns no migration had ever added, and died
// on its first query.
//
// These start from a database that already exists.
public class DatabaseUpgradeTests : IDisposable
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(), $"cielo-upgrade-{Guid.NewGuid():N}.db");

    private RuntimeDbContext Context()
    {
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new RuntimeDbContext(options);
    }

    private sealed class Factory : IDbContextFactory<RuntimeDbContext>
    {
        private readonly Func<RuntimeDbContext> make;
        public Factory(Func<RuntimeDbContext> make) => this.make = make;
        public RuntimeDbContext CreateDbContext() => make();
    }

    [Fact]
    public void A_database_left_at_an_older_migration_is_brought_up_to_date()
    {
        // Stop at the migration this machine was actually found sitting on: Login,
        // 24 August. Everything after it — the Language column, the audit session
        // id, the three thread migrations — is missing, exactly as on a real box.
        using (var context = Context())
        {
            var migrator = context.GetService<IMigrator>();
            migrator.Migrate("20260824131630_Login");
        }

        using (var context = Context())
        {
            Assert.DoesNotContain("20260825083408_UserLanguage", context.Database.GetAppliedMigrations());
        }

        // Starting the store is what an upgrade does. It must finish the job.
        var store = new EfRuntimeStore(new Factory(Context), seedDemo: false, databasePath);
        Assert.NotNull(store);

        using (var context = Context())
        {
            Assert.Empty(context.Database.GetPendingMigrations());
        }
    }

    // The guard for the NEXT #49 rather than the last one.
    //
    // Every EF store test constructs with ensureCreated: true, which builds the
    // schema from today's model and never calls Migrate(). So a new entity added to
    // RuntimeDbContext with no migration behind it leaves the whole suite green:
    // the tests build their schema from the model, and the model is right. The
    // first real query on a machine that already had a database is where it shows,
    // which is a customer's box and not this one.
    //
    // This asks EF the only question that catches it: does the model still match
    // the migrations? Adding a row class and forgetting to generate the migration
    // fails here, in the second it takes to run, instead of on an upgrade.
    [Fact]
    public void The_model_and_the_migrations_have_not_drifted_apart()
    {
        using var context = Context();
        Assert.False(
            context.Database.HasPendingModelChanges(),
            "RuntimeDbContext has changes with no migration behind them. Run: "
            + "dotnet ef migrations add <Name> --project src/backend/WorkspaceRuntime.Infrastructure. "
            + "Without it the suite stays green (every store test uses EnsureCreated) and every "
            + "machine that already has a database breaks on its first query after the upgrade.");
    }

    [Fact]
    public void The_column_that_crashed_the_upgrade_is_actually_queryable_afterwards()
    {
        using (var context = Context())
        {
            context.GetService<IMigrator>().Migrate("20260824131630_Login");
        }

        var store = new EfRuntimeStore(new Factory(Context), seedDemo: false, databasePath);

        // "SQLite Error 1: 'no such column: r.Language'" was the real crash, and it
        // came from reading users. Asserting the migration list is not the same
        // claim as asserting the query works, so do the query.
        Assert.Empty(store.Users);

        using var reader = Context();
        var columns = reader.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('runtime_users')")
            .ToList();
        Assert.Contains("Language", columns);
    }

    [Fact]
    public void A_schema_with_no_migration_history_is_refused_by_name_rather_than_by_accident()
    {
        // What a fresh install produced while EnsureCreated was the default: the
        // whole schema, and an empty history table. Migrating it blindly would try
        // to create tables that already exist and fail with something that reads
        // like corruption.
        using (var context = Context())
        {
            context.Database.EnsureCreated();
        }

        var failure = Assert.Throws<InvalidOperationException>(
            () => new EfRuntimeStore(new Factory(Context), seedDemo: false, databasePath));

        Assert.Contains("no migration history", failure.Message);
        // It must point at the issue and at what to do, not merely refuse.
        Assert.Contains("#49", failure.Message);
    }

    // The three tests above prove that migrating an old database WORKS. None of
    // them would have caught #49, because #49 was not that migration was broken —
    // it was that production never called it. EfRuntimeStore's own constructor has
    // always defaulted to Migrate(), so every test in this suite already took the
    // path that real machines did not.
    //
    // This is the guard for the actual defect: the wiring.
    [Fact]
    public void Production_migrates_unless_something_deliberately_asks_not_to()
    {
        var program = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Api", "Program.cs"));

        // EnsureCreated must be opt-IN. The original read
        //   !string.Equals(config["Database:EnsureCreated"], "false")
        // which is opt-OUT, so every machine that never set the variable — which is
        // all of them — silently skipped migrations forever.
        Assert.DoesNotContain("!string.Equals(builder.Configuration[\"Database:EnsureCreated\"]", program);
        Assert.Contains("string.Equals(builder.Configuration[\"Database:EnsureCreated\"], \"true\"", program);

        // And nothing we ship may turn it back on. A run.sh that exported
        // Database__EnsureCreated=true would restore the bug with the code correct.
        var root = TestRepository.Root();
        foreach (var script in new[]
        {
            Path.Combine(root, "distro", "run.sh"),
            Path.Combine(root, "distro", "config", "cielo.env.example"),
        })
        {
            if (!File.Exists(script)) continue;
            var text = File.ReadAllText(script);
            Assert.False(
                text.Contains("EnsureCreated=true", StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(script)} switches EnsureCreated back on, which disables migrations on every installed machine (#49).");
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in Directory.EnumerateFiles(
            Path.GetDirectoryName(databasePath)!,
            Path.GetFileNameWithoutExtension(databasePath) + "*"))
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
