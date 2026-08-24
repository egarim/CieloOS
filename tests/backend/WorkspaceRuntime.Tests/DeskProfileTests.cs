using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public class DeskProfileTests
{
    [Fact]
    public void Office_is_the_default_and_needs_no_image_of_its_own()
    {
        // The default has to be the desk that already exists: a machine that
        // upgrades must not suddenly need a multi-gigabyte image to open a session.
        Assert.Equal("office", DeskProfiles.Default.Id);
        Assert.Equal(DeskProfiles.SharedDesktopImage, DeskProfiles.Default.Image);
        Assert.False(DeskProfiles.Default.NeedsOwnImage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("does-not-exist")]
    public void An_unknown_profile_resolves_to_the_default_rather_than_failing(string? id)
    {
        // A desk is more useful than an error: a profile removed in a later
        // release, or a name from a newer panel, must still open a session.
        Assert.Equal(DeskProfiles.Default.Id, DeskProfiles.Resolve(id).Id);
    }

    [Fact]
    public void A_developer_desk_has_its_own_image()
    {
        var dotnet = DeskProfiles.Resolve("dotnet");

        Assert.True(dotnet.NeedsOwnImage);
        Assert.NotEqual(DeskProfiles.SharedDesktopImage, dotnet.Image);
        // The build context directory and the image tag are derived from the id in
        // two different places (installer script and runtime), so they have to agree.
        Assert.Equal($"localhost/cielo-desk-{dotnet.Id}:latest", dotnet.Image);
    }

    [Fact]
    public void Creating_a_user_records_the_desk_they_were_given()
    {
        // seedDemo: false — a seeded store already has an owner, and this test is about
        // what claiming records.
        var store = new InMemoryRuntimeStore(seedDemo: false);
        var setup = new SetupService(store, new StubAuthenticator());

        var claimed = setup.Claim("Joche", fromLoopback: true, deskProfile: "dotnet");
        Assert.Equal(ClaimOutcome.Ok, claimed.Outcome);

        var owner = store.Users.Single(user => user.Slug == "joche");
        Assert.Equal("dotnet", owner.DeskProfile);

        // And a teammate created without one gets the office desk, not an empty
        // string that only happens to resolve correctly.
        Assert.Equal(AddUserOutcome.Ok, setup.AddUser("Yulia").Outcome);
        Assert.Equal("office", store.Users.Single(user => user.Slug == "yulia").DeskProfile);
    }

    private sealed class StubAuthenticator : ITokenAuthenticator
    {
        public RuntimePrincipal? Authenticate(string bearerToken) => null;
        public string Mint(string slug) => $"{slug}:test";
        public string IssueToken(string slug) => Mint(slug);
    }
}
