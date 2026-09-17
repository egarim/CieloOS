using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// You give the claim a NAME and it derives the username you sign in with. That is
// fine until the derivation cannot spell your name — and for a script it cannot
// fold at all it derives nothing, so the claim is refused and the person has no
// way onto their own machine. Folding accents (see SlugTests) fixes Latin names
// and does nothing for Cyrillic or CJK.
//
// So: let the caller say what they want to be called.
public sealed class ChosenUsernameTests
{
    private static SetupService Fresh() =>
        new(new InMemoryRuntimeStore(seedDemo: false), new StubAuthenticator());

    // --- claiming ---

    [Fact]
    public void A_chosen_username_is_used_instead_of_the_derived_one()
    {
        var result = Fresh().Claim("José Ojeda", origin: ClaimOrigin.OnMachine, username: "joche");

        Assert.Equal(ClaimOutcome.Ok, result.Outcome);
        Assert.Equal("joche", result.Slug);
    }

    [Fact]
    public void A_name_no_script_can_fold_can_still_claim_by_choosing_one()
    {
        // Nothing transliterates Cyrillic here, so the derived slug is empty and
        // this claim is impossible today. It is the whole reason for the option.
        var derived = Fresh();
        Assert.Equal(ClaimOutcome.Invalid, derived.Claim("Вера Морозова", origin: ClaimOrigin.OnMachine).Outcome);

        var chosen = Fresh().Claim("Вера Морозова", origin: ClaimOrigin.OnMachine, username: "vera");

        Assert.Equal(ClaimOutcome.Ok, chosen.Outcome);
        Assert.Equal("vera", chosen.Slug);
    }

    [Theory]
    // A chosen username must already BE a slug. If Slug.Of would change it, then
    // the id stored and the id the caller believes they chose are different
    // strings, and every later lookup is a coin toss.
    [InlineData("Joche")]          // uppercase
    [InlineData("jo che")]         // space
    [InlineData("jo--che")]        // doubled dash
    [InlineData("-joche")]         // leading dash
    [InlineData("joche-")]         // trailing dash
    [InlineData("joche!")]         // punctuation
    [InlineData("josé")]           // the very thing we are avoiding
    public void A_username_that_is_not_already_a_slug_is_refused(string username)
    {
        var result = Fresh().Claim("Some Person", origin: ClaimOrigin.OnMachine, username: username);

        Assert.Equal(ClaimOutcome.Invalid, result.Outcome);
        Assert.Contains("username", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_over_long_chosen_username_is_refused_rather_than_truncated()
    {
        var tooLong = new string('a', Organizations.MaxUserSlug + 1);

        var result = Fresh().Claim("Some Person", origin: ClaimOrigin.OnMachine, username: tooLong);

        Assert.Equal(ClaimOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void An_over_long_DERIVED_username_is_refused_too()
    {
        // The claim never length-checked the owner's slug at all — AddUser did,
        // with a comment explaining that a truncated slug is a permanently wrong
        // home volume, and the owner was simply exempt from it.
        var longName = string.Join(" ", Enumerable.Repeat("Wilhelmina", 6));
        Assert.True(Slug.Of(longName).Length > Organizations.MaxUserSlug);

        var result = Fresh().Claim(longName, origin: ClaimOrigin.OnMachine);

        Assert.Equal(ClaimOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void Claiming_without_a_username_still_derives_one()
    {
        var result = Fresh().Claim("Ada Lovelace", origin: ClaimOrigin.OnMachine);

        Assert.Equal(ClaimOutcome.Ok, result.Outcome);
        Assert.Equal("ada-lovelace", result.Slug);
    }

    // --- adding a teammate ---

    [Fact]
    public void A_teammates_chosen_username_still_carries_the_organization_prefix()
    {
        var setup = Fresh();
        setup.Claim("Owner", origin: ClaimOrigin.OnMachine);

        var result = setup.AddUser("Вера Морозова", null, Organizations.FoundingSlug, username: "vera");

        Assert.Equal(AddUserOutcome.Ok, result.Outcome);
        // The prefix records how a user was minted and must survive a round trip
        // through Slug.Of, exactly as a derived one does.
        Assert.Equal($"{Organizations.FoundingSlug}-vera", result.Slug);
        Assert.Equal(result.Slug, Slug.Of(result.Slug!));
    }

    [Fact]
    public void A_teammates_username_is_validated_the_same_way()
    {
        var setup = Fresh();
        setup.Claim("Owner", origin: ClaimOrigin.OnMachine);

        var result = setup.AddUser("Someone", null, Organizations.FoundingSlug, username: "Not A Slug");

        Assert.Equal(AddUserOutcome.Invalid, result.Outcome);
    }

    // --- the predicate both of them lean on ---

    [Theory]
    [InlineData("joche", true)]
    [InlineData("jose-ojeda", true)]
    [InlineData("a1", true)]
    [InlineData("9", true)]
    [InlineData("Joche", false)]
    [InlineData("jo che", false)]
    [InlineData("jo--che", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData("", false)]
    [InlineData("josé", false)]
    public void IsWellFormed_accepts_exactly_what_survives_a_round_trip(string value, bool expected)
    {
        Assert.Equal(expected, Slug.IsWellFormed(value));
    }

    private sealed class StubAuthenticator : ITokenAuthenticator
    {
        public RuntimePrincipal? Authenticate(string bearerToken) => null;
        public string Mint(string slug) => $"{slug}:test";
        public string IssueToken(string slug) => Mint(slug);
    }
}
