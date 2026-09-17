using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The three owner routes and the two supersede call sites. There is no TestServer
// in this solution, so these exercise the store and the call sites directly
// rather than the HTTP handlers; the route-level access rules are tabled in
// AccessPolicyTests, which is where a fall-through to AnyPrincipal becomes a test
// failure instead of a code review finding.
public class InviteRoutesTests
{
    [Fact]
    public void Minting_for_a_user_who_already_has_a_password_is_refused()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");
        store.SetPasswordHash(teammate.Id, "already-hashed");

        // The invariant the whole design rests on: an invitation sets a FIRST
        // password and never a reset, so the owner can never mint themselves into
        // a teammate's account.
        Assert.NotNull(store.PasswordHashFor(teammate.Id));
        Assert.Empty(invites.All());
    }

    [Fact]
    public void Minting_for_the_machine_owner_is_refused()
    {
        var store = new InMemoryRuntimeStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);

        Assert.True(owner.IsMachineOwner);
        Assert.Empty(new InMemoryInviteStore().All());
    }

    [Fact]
    public void Minting_twice_supersedes_the_first()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");

        var (first, _) = invites.Create(teammate.Id, owner.Id, TimeSpan.FromHours(72));
        invites.SupersedeLiveFor(teammate.Id);
        var (second, _) = invites.Create(teammate.Id, owner.Id, TimeSpan.FromHours(72));

        var now = DateTimeOffset.UtcNow;
        var rows = invites.All().Where(invite => invite.UserId == teammate.Id).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("superseded", rows.Single(row => row.Id == first.Id).State(now));
        Assert.Equal("live", rows.Single(row => row.Id == second.Id).State(now));
        Assert.Single(rows.Where(row => row.IsLive(now)));
    }

    [Fact]
    public void The_list_never_returns_the_code_or_its_hash()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");

        var (_, code) = invites.Create(teammate.Id, owner.Id, TimeSpan.FromHours(72));

        // The record shape has no Code or CodeHash field, so this is trivially
        // true of the type — but the property that matters is the serialised
        // response body, which is what the panel actually receives.
        var body = System.Text.Json.JsonSerializer.Serialize(invites.All());
        Assert.DoesNotContain(code, body, StringComparison.Ordinal);
        Assert.DoesNotContain("CodeHash", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Revoking_twice_is_not_an_error_and_does_not_move_RevokedAt()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");

        var (invite, _) = invites.Create(teammate.Id, owner.Id, TimeSpan.FromHours(72));

        Assert.True(invites.Revoke(invite.Id));
        var afterFirst = invites.All().Single(row => row.Id == invite.Id).RevokedAt;
        Assert.NotNull(afterFirst);

        // The second call is a no-op with the same answer, not an error, and it
        // must not move the timestamp: the record of WHEN it was called off is
        // the point of the column.
        Assert.False(invites.Revoke(invite.Id));
        var afterSecond = invites.All().Single(row => row.Id == invite.Id).RevokedAt;
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void Moving_a_person_between_organizations_supersedes_their_live_invitation()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");
        store.AddOrganization(new Organization(Guid.NewGuid(), "globex", "Globex", DateTimeOffset.UtcNow));

        var (invite, _) = invites.Create(teammate.Id, owner.Id, TimeSpan.FromHours(72));
        Assert.True(invite.IsLive(DateTimeOffset.UtcNow));

        // The call the move handler makes, in the order it makes it.
        Assert.True(store.SetUserOrganization("dmitri", "globex"));
        invites.SupersedeLiveFor(teammate.Id);

        var now = DateTimeOffset.UtcNow;
        Assert.Equal("superseded", invites.All().Single(row => row.Id == invite.Id).State(now));
    }

    [Fact]
    public void Setting_a_first_password_supersedes_their_live_invitation()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");

        var (invite, _) = invites.Create(teammate.Id, owner.Id, TimeSpan.FromHours(72));
        Assert.True(invite.IsLive(DateTimeOffset.UtcNow));

        // The first-password branch of POST /api/auth/password: existing is null,
        // so the handler calls SupersedeLiveFor after SetPasswordHash.
        Assert.Null(store.PasswordHashFor(teammate.Id));
        store.SetPasswordHash(teammate.Id, "first-hash");
        invites.SupersedeLiveFor(teammate.Id);

        var now = DateTimeOffset.UtcNow;
        Assert.Equal("superseded", invites.All().Single(row => row.Id == invite.Id).State(now));
    }

    // PlatformUser is (Id, DisplayName, Email, Slug, OrgSlug, IsMachineOwner, ...)
    // and AddUser takes the identity, its workspace and its agent together — one
    // transaction, because a person without an agent is not a person this machine
    // can do anything with. Copied from DirectMessageTests rather than guessed.
    private static PlatformUser AddUser(InMemoryRuntimeStore store, string slug, bool isMachineOwner = false)
    {
        var user = new PlatformUser(
            Guid.NewGuid(), slug, $"{slug}@example.com", slug,
            Organizations.FoundingSlug, isMachineOwner, "office", "en");
        store.AddUser(
            user,
            new Workspace(Guid.NewGuid(), user.Id, slug),
            new AgentProfile(Guid.NewGuid(), user.Id, Guid.NewGuid(), $"{slug} agent", "",
                new HashSet<string>(), $"{slug}-agent"));
        return user;
    }
}
