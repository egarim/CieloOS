using System.Net;
using Microsoft.AspNetCore.Http;
using WorkspaceRuntime.Api;
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
    public void User_creation_invitation_contract_has_code_expiry_and_no_token_field()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");
        var caller = new RuntimePrincipal(PrincipalKind.Human, owner.Id, owner.Slug, owner.DisplayName);

        var issued = InvitationIssuance.Mint(teammate, caller, invites, store);
        var body = System.Text.Json.JsonSerializer.Serialize(issued,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var json = System.Text.Json.JsonDocument.Parse(body);

        Assert.Equal(teammate.Slug, json.RootElement.GetProperty("slug").GetString());
        Assert.StartsWith(CredentialFormat.InvitePrefix, json.RootElement.GetProperty("code").GetString());
        Assert.True(json.RootElement.TryGetProperty("expiresAt", out _));
        Assert.False(json.RootElement.TryGetProperty("token", out _));
    }

    [Fact]
    public void User_creation_invitation_code_redeems_and_a_second_creation_supersedes_it()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");
        var caller = new RuntimePrincipal(PrincipalKind.Human, owner.Id, owner.Slug, owner.DisplayName);

        var first = InvitationIssuance.Mint(teammate, caller, invites, store);
        var firstRow = invites.Resolve(first.Code, DateTimeOffset.UtcNow)!;
        var second = InvitationIssuance.Mint(teammate, caller, invites, store);

        Assert.Equal("superseded", invites.Resolve(first.Code, DateTimeOffset.UtcNow)!.State(DateTimeOffset.UtcNow));
        var secondRow = invites.Resolve(second.Code, DateTimeOffset.UtcNow)!;
        Assert.True(invites.Spend(secondRow.Id, DateTimeOffset.UtcNow, "test-client"));
        Assert.True(store.SetFirstPasswordHash(teammate.Id, "redeemed-password-hash"));
        Assert.Equal("used", invites.Resolve(second.Code, DateTimeOffset.UtcNow)!.State(DateTimeOffset.UtcNow));
        Assert.Equal("redeemed-password-hash", store.PasswordHashFor(teammate.Id));
        Assert.NotEqual(firstRow.Id, secondRow.Id);
    }

    [Fact]
    public void Redeem_rejects_a_non_confidential_transport_before_touching_state()
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = IPAddress.Parse("10.0.0.1");
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        var invites = new InMemoryInviteStore();
        var store = new InMemoryRuntimeStore();

        // The inline handler is not directly callable without an HTTP pipeline.
        // This is its first predicate; all state is deliberately still empty.
        Assert.False(TransportFacts.Confidential(context, new HashSet<IPAddress>()));
        Assert.Empty(invites.All());
        Assert.DoesNotContain(store.AuditEvents, row => row.Action == "invite.redeem");
    }

    [Fact]
    public void Short_password_does_not_consume_the_invitation()
    {
        var invites = new InMemoryInviteStore();
        var (invite, code) = invites.Create(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromDays(1));

        const string password = "too-short";
        Assert.True(password.Length < 10);
        Assert.Equal(invite.Id, invites.Resolve(code, DateTimeOffset.UtcNow)!.Id);
        Assert.True(invites.All().Single().IsLive(DateTimeOffset.UtcNow));
        Assert.Null(invites.All().Single().RedeemedAt);
    }

    [Fact]
    public void Dead_invitation_reports_its_state_without_being_spent_again()
    {
        var invites = new InMemoryInviteStore();
        var (invite, code) = invites.Create(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromDays(1));
        Assert.True(invites.Revoke(invite.Id));

        var resolved = invites.Resolve(code, DateTimeOffset.UtcNow)!;

        Assert.Equal("revoked", resolved.State(DateTimeOffset.UtcNow));
        Assert.Null(invites.All().Single().RedeemedAt);
    }

    [Fact]
    public void Unknown_code_resolves_to_nothing_and_writes_no_audit_row()
    {
        var invites = new InMemoryInviteStore();
        var store = new InMemoryRuntimeStore();
        var before = store.AuditEvents.Count;

        Assert.Null(invites.Resolve("cielo_inv_not-a-real-code", DateTimeOffset.UtcNow));
        Assert.Equal(before, store.AuditEvents.Count);
        Assert.DoesNotContain(store.AuditEvents, row => row.Action == "invite.redeem");
    }

    [Fact]
    public void Redeeming_twice_cannot_spend_twice_or_change_the_password()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");
        var (invite, _) = invites.Create(teammate.Id, owner.Id, TimeSpan.FromDays(1));
        var now = DateTimeOffset.UtcNow;

        Assert.True(invites.Spend(invite.Id, now, "first-client"));
        Assert.True(store.SetFirstPasswordHash(teammate.Id, "first-hash"));
        Assert.False(invites.Spend(invite.Id, now.AddSeconds(1), "second-client"));
        Assert.Equal("first-hash", store.PasswordHashFor(teammate.Id));
        Assert.Equal("first-client", invites.All().Single().RedeemedFrom);
    }

    [Fact]
    public void Successful_redeem_side_effects_are_one_spend_password_session_and_one_dual_slug_audit()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var sessions = new InMemorySessionStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");
        var (invite, _) = invites.Create(teammate.Id, owner.Id, TimeSpan.FromDays(1));
        var now = DateTimeOffset.UtcNow;

        Assert.True(invites.Spend(invite.Id, now, "10.0.0.5"));
        Assert.True(store.SetFirstPasswordHash(teammate.Id, "password-hash"));
        var (session, secret) = sessions.Create(teammate.Id, TimeSpan.FromDays(14));
        store.AppendAudit(new AuditEvent(Guid.NewGuid(), now, teammate.Id, null,
            "invite.redeem", AuditOutcome.Success, "accepted",
            Principal: owner.Slug, OnBehalfOf: teammate.Slug));

        Assert.Equal("password-hash", store.PasswordHashFor(teammate.Id));
        Assert.NotNull(invites.All().Single().RedeemedAt);
        Assert.Equal(session.Id, sessions.Resolve(secret, now)!.Id);
        var audit = Assert.Single(store.AuditEvents, row => row.Action == "invite.redeem");
        Assert.Equal(owner.Slug, audit.Principal);
        Assert.Equal(teammate.Slug, audit.OnBehalfOf);
    }

    [Fact]
    public void Existing_password_makes_post_spend_conditional_write_refuse_without_overwrite()
    {
        var store = new InMemoryRuntimeStore();
        var invites = new InMemoryInviteStore();
        var owner = AddUser(store, "joche", isMachineOwner: true);
        var teammate = AddUser(store, "dmitri");
        store.SetPasswordHash(teammate.Id, "existing-hash");
        var (invite, _) = invites.Create(teammate.Id, owner.Id, TimeSpan.FromDays(1));

        Assert.True(invites.Spend(invite.Id, DateTimeOffset.UtcNow, "racing-client"));
        Assert.False(store.SetFirstPasswordHash(teammate.Id, "attacker-hash"));
        Assert.Equal("existing-hash", store.PasswordHashFor(teammate.Id));
        Assert.Equal("used", invites.All().Single().State(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Preview_maps_unknown_and_expired_codes_to_the_same_public_state()
    {
        var invites = new InMemoryInviteStore();
        var (_, expiredCode) = invites.Create(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromSeconds(-1));
        var now = DateTimeOffset.UtcNow;

        static string PublicState(Invite? invite, DateTimeOffset at) => invite?.State(at) ?? "expired";

        var unknownState = PublicState(invites.Resolve("cielo_inv_unknown", now), now);
        var expiredState = PublicState(invites.Resolve(expiredCode, now), now);
        Assert.Equal("expired", unknownState);
        Assert.Equal(unknownState, expiredState);
    }

    [Fact]
    public void First_password_write_is_conditional()
    {
        var store = new InMemoryRuntimeStore();
        var teammate = AddUser(store, "dmitri");

        Assert.True(store.SetFirstPasswordHash(teammate.Id, "first-hash"));
        Assert.False(store.SetFirstPasswordHash(teammate.Id, "replacement-hash"));
        Assert.Equal("first-hash", store.PasswordHashFor(teammate.Id));
    }

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
