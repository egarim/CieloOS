using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public sealed class SuspensionTests
{
    [Fact]
    public void Both_routes_are_owner_only()
    {
        Assert.Equal(AccessLevel.OwnerOnly, AccessPolicy.Required("/api/users/yulia/suspend", "POST"));
        Assert.Equal(AccessLevel.OwnerOnly, AccessPolicy.Required("/api/users/yulia/unsuspend", "POST"));
    }

    [Fact]
    public void Suspending_refuses_the_machine_owner()
    {
        var fixture = Fixture.Create();
        var result = Suspension.Suspend(fixture.Owner.Slug, fixture.Store, fixture.Sessions,
            fixture.Keys, fixture.Invites, fixture.Owner.Id);

        Assert.Equal(SuspensionResult.MachineOwner, result);
        Assert.Null(fixture.Store.GetUser(fixture.Owner.Id).SuspendedAt);
        Assert.DoesNotContain(fixture.Store.AuditEvents, row => row.Action == "user.suspend");
    }

    [Fact]
    public void Suspended_person_is_refused_with_session_api_key_and_identity_token()
    {
        var fixture = Fixture.Create();
        fixture.Store.SetSuspendedAt(fixture.User.Id, DateTimeOffset.UtcNow);
        var (_, sessionSecret) = fixture.Sessions.Create(fixture.User.Id, TimeSpan.FromDays(1));
        var (_, keySecret) = fixture.Keys.Create(fixture.User.Id, "integration", null);
        var session = Assert.IsType<PanelSession>(fixture.Sessions.Resolve(sessionSecret, DateTimeOffset.UtcNow));
        var key = Assert.IsType<ApiKey>(fixture.Keys.Resolve(keySecret, DateTimeOffset.UtcNow));
        var principals = new[]
        {
            new RuntimePrincipal(PrincipalKind.Human, session.UserId, fixture.User.Slug, fixture.User.DisplayName),
            new RuntimePrincipal(PrincipalKind.Human, key.OwnerUserId, fixture.User.Slug, fixture.User.DisplayName),
            // The legacy identity-token authenticator resolves to this same
            // principal shape; unlike the two credentials above, it cannot be
            // revoked, so only the account-column gate can refuse it.
            new RuntimePrincipal(PrincipalKind.Human, fixture.User.Id, fixture.User.Slug, fixture.User.DisplayName)
        };

        Assert.All(principals, principal => Assert.True(Suspension.IsSuspended(fixture.Store, principal)));
        Assert.Contains("suspended", Suspension.RefusalMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suspending_revokes_sessions_and_keys()
    {
        var fixture = Fixture.Create();
        var (_, sessionSecret) = fixture.Sessions.Create(fixture.User.Id, TimeSpan.FromDays(1));
        var (_, keySecret) = fixture.Keys.Create(fixture.User.Id, "laptop", null);

        Assert.Equal(SuspensionResult.Success, Suspension.Suspend(fixture.User.Slug, fixture.Store,
            fixture.Sessions, fixture.Keys, fixture.Invites, fixture.Owner.Id));
        Assert.Null(fixture.Sessions.Resolve(sessionSecret, DateTimeOffset.UtcNow));
        Assert.Null(fixture.Keys.Resolve(keySecret, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Suspending_supersedes_a_live_invitation()
    {
        var fixture = Fixture.Create();
        var (invite, _) = fixture.Invites.Create(fixture.User.Id, fixture.Owner.Id, TimeSpan.FromDays(1));

        Suspension.Suspend(fixture.User.Slug, fixture.Store, fixture.Sessions,
            fixture.Keys, fixture.Invites, fixture.Owner.Id);

        Assert.Equal("superseded", fixture.Invites.All().Single(row => row.Id == invite.Id).State(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Unsuspending_restores_access_but_not_old_sessions_or_keys()
    {
        var fixture = Fixture.Create();
        var (_, sessionSecret) = fixture.Sessions.Create(fixture.User.Id, TimeSpan.FromDays(1));
        var (_, keySecret) = fixture.Keys.Create(fixture.User.Id, "laptop", null);
        Suspension.Suspend(fixture.User.Slug, fixture.Store, fixture.Sessions,
            fixture.Keys, fixture.Invites, fixture.Owner.Id);

        Assert.Equal(SuspensionResult.Success, Suspension.Unsuspend(fixture.User.Slug, fixture.Store, fixture.Owner.Id));
        Assert.False(Suspension.IsSuspended(fixture.Store,
            new RuntimePrincipal(PrincipalKind.Human, fixture.User.Id, fixture.User.Slug, fixture.User.DisplayName)));
        Assert.Null(fixture.Sessions.Resolve(sessionSecret, DateTimeOffset.UtcNow));
        Assert.Null(fixture.Keys.Resolve(keySecret, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Password_change_revokes_keys_as_well_as_sessions()
    {
        var fixture = Fixture.Create();
        var (_, sessionSecret) = fixture.Sessions.Create(fixture.User.Id, TimeSpan.FromDays(1));
        var (_, keySecret) = fixture.Keys.Create(fixture.User.Id, "lost-laptop", null);

        var revoked = PasswordCredentialRotation.RevokeAll(fixture.User.Id, fixture.Sessions, fixture.Keys);

        Assert.Equal((1, 1), revoked);
        Assert.Null(fixture.Sessions.Resolve(sessionSecret, DateTimeOffset.UtcNow));
        Assert.Null(fixture.Keys.Resolve(keySecret, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Suspension_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"suspension-{Guid.NewGuid():N}.db");
        try
        {
            EfRuntimeStore Open()
            {
                var options = new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite($"Data Source={path}").Options;
                return new EfRuntimeStore(new PooledDbContextFactory<RuntimeDbContext>(options), ensureCreated: true);
            }

            var first = Open();
            var user = first.Users.Single(candidate => !candidate.IsMachineOwner);
            Assert.True(first.SetSuspendedAt(user.Id, DateTimeOffset.UtcNow));

            var reopened = Open();
            Assert.NotNull(reopened.GetUser(user.Id).SuspendedAt);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed record Fixture(
        InMemoryRuntimeStore Store, InMemorySessionStore Sessions, InMemoryApiKeyStore Keys,
        InMemoryInviteStore Invites, PlatformUser Owner, PlatformUser User)
    {
        public static Fixture Create()
        {
            var store = new InMemoryRuntimeStore();
            return new Fixture(store, new InMemorySessionStore(), new InMemoryApiKeyStore(),
                new InMemoryInviteStore(),
                store.Users.Single(user => user.IsMachineOwner),
                store.Users.Single(user => !user.IsMachineOwner));
        }
    }
}
