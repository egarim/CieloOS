using Microsoft.EntityFrameworkCore;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The eight tests brief 02a listed and the reply did not contain. Both stores are
// driven from one [Theory] wherever the behaviour is shared, because the two
// implementations agreeing is part of what is being tested — a test that only
// exercises InMemoryInviteStore proves nothing about the store a real machine runs.
//
// The EF store is built the way DatabaseUpgradeTests builds one: a temp SQLite
// file, a DbContextOptionsBuilder, and a private IDbContextFactory wrapper.
public class InviteStoreTests : IDisposable
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(), $"cielo-invites-{Guid.NewGuid():N}.db");

    public InviteStoreTests()
    {
        // The schema has to exist before the first store call, and nothing else
        // here creates it: every EF test in this project owns its own file, so
        // there is no shared fixture that would have done it. Without this the
        // failure is "no such table: runtime_invites", which reads like a missing
        // migration rather than a missing line in the test.
        using var context = Context();
        context.Database.EnsureCreated();
    }

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

    private IInviteStore Store(string kind) => kind switch
    {
        "memory" => new InMemoryInviteStore(),
        "ef" => new EfInviteStore(new Factory(Context)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown store")
    };

    public static TheoryData<string> BothStores => new() { "memory", "ef" };

    // 1. The code is returned once and only its hash is kept. A test that only
    // checked round-tripping would pass on a store that saved the secret in plain
    // text, so this asserts the stored value DIFFERS from the returned code.
    [Theory]
    [MemberData(nameof(BothStores))]
    public void A_created_invite_is_live_and_the_stored_hash_is_not_the_code(string kind)
    {
        var store = Store(kind);
        var userId = Guid.NewGuid();
        var (invite, code) = store.Create(userId, Guid.NewGuid(), TimeSpan.FromDays(7));

        Assert.True(invite.IsLive(DateTimeOffset.UtcNow));
        Assert.Equal("live", invite.State(DateTimeOffset.UtcNow));

        // The code is what the person pastes; it must carry the visible prefix so
        // the auth gate can tell it from an API key without trying both.
        Assert.StartsWith(CredentialFormat.InvitePrefix, code);

        var stored = Assert.Single(store.All());
        Assert.NotEqual(code, stored.Id.ToString());
        Assert.NotEqual(code, stored.RedeemedFrom);
        Assert.NotEqual(code, stored.FirstPreviewedFrom);

        // The only place the code could be hiding is the hash column, and it must
        // not be there in the clear.
        Assert.DoesNotContain(code, stored.ToString(), StringComparison.Ordinal);
    }

    // 2. The compare-and-swap. Exactly one caller wins. For InMemoryInviteStore
    // this is the only thing standing behind the claim that a lock makes it
    // atomic; for EfInviteStore it is the observable outcome the design promises.
    [Theory]
    [MemberData(nameof(BothStores))]
    public void Spend_has_exactly_one_winner_under_a_race(string kind)
    {
        var store = Store(kind);
        var (invite, _) = store.Create(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromDays(7));

        const int threads = 8;
        var barrier = new Barrier(threads);
        var results = new bool[threads];
        var workers = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            var index = i;
            workers[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                results[index] = store.Spend(invite.Id, DateTimeOffset.UtcNow, $"thread-{index}");
            });
        }

        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) worker.Join();

        Assert.Equal(1, results.Count(won => won));
        Assert.Equal(threads - 1, results.Count(won => !won));

        var row = Assert.Single(store.All());
        Assert.NotNull(row.RedeemedAt);
        Assert.Equal("used", row.State(DateTimeOffset.UtcNow));
    }

    // 3. Spending, revoking and superseding each move State to the right word and
    // IsLive to false.
    [Theory]
    [MemberData(nameof(BothStores))]
    public void Each_terminal_transition_moves_state_and_clears_liveness(string kind)
    {
        var now = DateTimeOffset.UtcNow;

        var spent = Store(kind);
        var (spentInvite, _) = spent.Create(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromDays(7));
        Assert.True(spent.Spend(spentInvite.Id, now, "test"));
        // By id, not Assert.Single: the three stores in this test are three
        // objects but ONE SQLite file, so All() accumulates across them. Asserting
        // a single row passed on the in-memory store and failed on the EF one for
        // a reason that had nothing to do with what is being tested.
        var spentRow = spent.All().Single(row => row.Id == spentInvite.Id);
        Assert.Equal("used", spentRow.State(now));
        Assert.False(spentRow.IsLive(now));

        var revoked = Store(kind);
        var (revokedInvite, _) = revoked.Create(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromDays(7));
        Assert.True(revoked.Revoke(revokedInvite.Id));
        var revokedRow = revoked.All().Single(row => row.Id == revokedInvite.Id);
        Assert.Equal("revoked", revokedRow.State(now));
        Assert.False(revokedRow.IsLive(now));

        var superseded = Store(kind);
        var userId = Guid.NewGuid();
        var (supersededInvite, _) = superseded.Create(userId, Guid.NewGuid(), TimeSpan.FromDays(7));
        Assert.Equal(1, superseded.SupersedeLiveFor(userId));
        var supersededRow = superseded.All().Single(row => row.Id == supersededInvite.Id);
        Assert.Equal("superseded", supersededRow.State(now));
        Assert.False(supersededRow.IsLive(now));
    }

    // 4. Expiry is the passage of time, not an update. Nothing is written.
    [Theory]
    [MemberData(nameof(BothStores))]
    public void An_expired_invite_reports_expired_with_nothing_written(string kind)
    {
        var store = Store(kind);
        // A negative lifetime is the only way to reach an already-expired row
        // without waiting, and neither store rejects one.
        var (invite, _) = store.Create(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromSeconds(-1));

        var now = DateTimeOffset.UtcNow;
        Assert.Equal("expired", invite.State(now));
        Assert.False(invite.IsLive(now));

        var row = Assert.Single(store.All());
        Assert.Equal("expired", row.State(now));
        Assert.False(row.IsLive(now));
        Assert.Null(row.RedeemedAt);
        Assert.Null(row.RevokedAt);
        Assert.Null(row.SupersededAt);
    }

    // 5. State reports the FIRST true thing. A row that is both superseded and
    // expired says "superseded", because "it was replaced" is the sentence that
    // tells the person what to do next.
    [Theory]
    [MemberData(nameof(BothStores))]
    public void State_reports_superseded_before_expired(string kind)
    {
        var store = Store(kind);
        var userId = Guid.NewGuid();
        var (invite, _) = store.Create(userId, Guid.NewGuid(), TimeSpan.FromSeconds(-1));

        // SupersedeLiveFor only touches live rows, so an already-expired row is
        // not superseded by it. Build the both-true row directly instead.
        var both = invite with { SupersededAt = DateTimeOffset.UtcNow };
        Assert.Equal("superseded", both.State(DateTimeOffset.UtcNow));
        Assert.False(both.IsLive(DateTimeOffset.UtcNow));

        // And the store's own view of the row agrees once it is superseded while
        // still live, then allowed to expire.
        var live = Store(kind);
        var (liveInvite, _) = live.Create(userId, Guid.NewGuid(), TimeSpan.FromDays(7));
        Assert.Equal(1, live.SupersedeLiveFor(userId));
        var row = live.All().Single(entry => entry.Id == liveInvite.Id);
        Assert.Equal("superseded", row.State(DateTimeOffset.UtcNow));
        Assert.Equal("superseded", row.State(DateTimeOffset.UtcNow.AddDays(30)));
    }

    // 6. Resolve returns dead rows rather than null, so a handler can say "already
    // used" instead of "no such link".
    [Theory]
    [MemberData(nameof(BothStores))]
    public void Resolve_returns_dead_rows_rather_than_null(string kind)
    {
        var store = Store(kind);
        var (invite, code) = store.Create(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromDays(7));
        Assert.True(store.Spend(invite.Id, DateTimeOffset.UtcNow, "test"));

        var resolved = store.Resolve(code, DateTimeOffset.UtcNow);
        Assert.NotNull(resolved);
        Assert.Equal("used", resolved!.State(DateTimeOffset.UtcNow));
        Assert.False(resolved.IsLive(DateTimeOffset.UtcNow));
    }

    // 7. SupersedeLiveFor supersedes only live rows for that user and returns the
    // count — not rows for other users, not rows already dead.
    [Theory]
    [MemberData(nameof(BothStores))]
    public void SupersedeLiveFor_touches_only_live_rows_for_that_user(string kind)
    {
        var store = Store(kind);
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        var (live, _) = store.Create(mine, Guid.NewGuid(), TimeSpan.FromDays(7));
        var (dead, _) = store.Create(mine, Guid.NewGuid(), TimeSpan.FromDays(7));
        var (other, _) = store.Create(theirs, Guid.NewGuid(), TimeSpan.FromDays(7));
        Assert.True(store.Revoke(dead.Id));

        var count = store.SupersedeLiveFor(mine);
        Assert.Equal(1, count);

        var rows = store.All();
        var liveRow = rows.Single(row => row.Id == live.Id);
        var deadRow = rows.Single(row => row.Id == dead.Id);
        var otherRow = rows.Single(row => row.Id == other.Id);

        Assert.NotNull(liveRow.SupersededAt);
        Assert.Equal("superseded", liveRow.State(DateTimeOffset.UtcNow));

        Assert.Null(deadRow.SupersededAt);
        Assert.Equal("revoked", deadRow.State(DateTimeOffset.UtcNow));

        Assert.Null(otherRow.SupersededAt);
        Assert.Equal("live", otherRow.State(DateTimeOffset.UtcNow));
    }

    // 8. All of it survives a round trip through EfInviteStore, not only the
    // in-memory one. This is the test that would have caught a store that kept
    // the code in memory and lost it on the next context.
    [Fact]
    public void The_whole_lifecycle_survives_a_round_trip_through_the_ef_store()
    {
        var store = Store("ef");
        var userId = Guid.NewGuid();
        var (invite, code) = store.Create(userId, Guid.NewGuid(), TimeSpan.FromDays(7));

        var resolved = store.Resolve(code, DateTimeOffset.UtcNow);
        Assert.NotNull(resolved);
        Assert.Equal(invite.Id, resolved!.Id);
        Assert.Equal("live", resolved.State(DateTimeOffset.UtcNow));

        Assert.True(store.Spend(invite.Id, DateTimeOffset.UtcNow, "round-trip"));

        var after = store.Resolve(code, DateTimeOffset.UtcNow);
        Assert.NotNull(after);
        Assert.Equal("used", after!.State(DateTimeOffset.UtcNow));
        Assert.False(after.IsLive(DateTimeOffset.UtcNow));

        var row = Assert.Single(store.All());
        Assert.Equal("used", row.State(DateTimeOffset.UtcNow));
        Assert.Equal("round-trip", row.RedeemedFrom);
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
