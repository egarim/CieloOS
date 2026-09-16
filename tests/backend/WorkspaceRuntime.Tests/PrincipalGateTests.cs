using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Tests;

// The authorisation decision itself, which until now lived inside the request
// pipeline where no test could reach it.
//
// That is not a stylistic complaint. OwnerOnly shipped as a slug comparison with no
// second factor, and on a test machine a bare identity token — the contents of a
// 0600 file, deterministic and eternal — created an organization and then a person
// inside it, from a different machine, over the network. Both routes are OwnerOnly.
// The whole suite was green the entire time, because the suite could not see the
// four `if` blocks that made the decision.
public class PrincipalGateTests
{
    // THE regression. If this ever goes back to None, a leaked token is an owner
    // again and nothing else in the product will notice.
    [Fact]
    public void An_identity_token_cannot_reach_an_owner_route()
    {
        var refusal = PrincipalGate.Check(
            AccessLevel.OwnerOnly,
            PrincipalKind.Human,
            isMachineOwner: true,   // it really is the owner's token
            hasSession: false,      // but no password was ever proved
            isApiKey: false);

        Assert.Equal(PrincipalRefusal.NeedsSession, refusal);
    }

    [Fact]
    public void The_owner_with_a_real_session_is_allowed()
    {
        Assert.Equal(PrincipalRefusal.None, PrincipalGate.Check(
            AccessLevel.OwnerOnly, PrincipalKind.Human,
            isMachineOwner: true, hasSession: true, isApiKey: false));
    }

    // Order matters for honesty as much as for security: somebody holding the
    // owner's token is not "not the owner", and telling them so would send them
    // looking for the wrong problem.
    [Fact]
    public void The_owner_without_a_session_is_told_to_sign_in_not_that_they_are_not_the_owner()
    {
        var refusal = PrincipalGate.Check(
            AccessLevel.OwnerOnly, PrincipalKind.Human,
            isMachineOwner: true, hasSession: false, isApiKey: false);

        Assert.Equal(PrincipalRefusal.NeedsSession, refusal);
        Assert.Contains("password", PrincipalGate.Explain(refusal));
        Assert.DoesNotContain("Only the owner", PrincipalGate.Explain(refusal));
    }

    [Fact]
    public void A_signed_in_teammate_is_still_not_the_owner()
    {
        Assert.Equal(PrincipalRefusal.NotOwner, PrincipalGate.Check(
            AccessLevel.OwnerOnly, PrincipalKind.Human,
            isMachineOwner: false, hasSession: true, isApiKey: false));
    }

    // An API key authenticates AS a person, so it would pass the Human check. It is
    // refused separately, and on owner routes it can never get that far now — but
    // the rule has to hold on HumanOnly too, which is where credentials are minted.
    [Fact]
    public void An_api_key_cannot_do_human_only_work()
    {
        Assert.Equal(PrincipalRefusal.ApiKeyRefused, PrincipalGate.Check(
            AccessLevel.HumanOnly, PrincipalKind.Human,
            isMachineOwner: false, hasSession: true, isApiKey: true));
    }

    [Fact]
    public void An_agent_cannot_do_human_only_work()
    {
        Assert.Equal(PrincipalRefusal.NotHuman, PrincipalGate.Check(
            AccessLevel.HumanOnly, PrincipalKind.Agent,
            isMachineOwner: false, hasSession: true, isApiKey: false));
    }

    // The new session requirement must not leak downwards. An agent doing ordinary
    // agent work holds a token and has no session by construction; if this ever
    // returns a refusal, every agent on every machine stops working at once.
    [Theory]
    [InlineData(AccessLevel.Public)]
    [InlineData(AccessLevel.AnyPrincipal)]
    public void Ordinary_routes_do_not_require_a_session(AccessLevel level)
    {
        Assert.Equal(PrincipalRefusal.None, PrincipalGate.Check(
            level, PrincipalKind.Agent,
            isMachineOwner: false, hasSession: false, isApiKey: false));

        Assert.Equal(PrincipalRefusal.None, PrincipalGate.Check(
            level, PrincipalKind.Human,
            isMachineOwner: false, hasSession: false, isApiKey: true));
    }

    // Every refusal has to say something. A blank body on a 403 is how a caller
    // ends up guessing, and the reason someone is refused is never a secret here —
    // they already authenticated.
    [Theory]
    [InlineData(PrincipalRefusal.NeedsSession)]
    [InlineData(PrincipalRefusal.NotOwner)]
    [InlineData(PrincipalRefusal.NotHuman)]
    [InlineData(PrincipalRefusal.ApiKeyRefused)]
    public void Every_refusal_explains_itself(PrincipalRefusal refusal)
    {
        Assert.False(string.IsNullOrWhiteSpace(PrincipalGate.Explain(refusal)));
    }
}
