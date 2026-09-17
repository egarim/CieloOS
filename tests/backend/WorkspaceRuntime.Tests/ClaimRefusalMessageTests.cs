using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// A refused claim has to tell the operator which rule they hit, because the two
// rules have opposite remedies: one says "go to the machine", the other says
// "you are at the machine, but a proxy is answering for you".
//
// These exist because the change that produced the second message reported, in
// its own notes, that it had already written them. It had not. The code was
// right; the claim about the tests was not, and nothing but a grep would have
// caught it — the suite still passed, at exactly the count it passed at before.
public sealed class ClaimRefusalMessageTests
{
    private static SetupService Fresh() =>
        new(new InMemoryRuntimeStore(seedDemo: false), new StubAuthenticator());

    [Fact]
    public void A_claim_from_a_named_terminator_names_the_terminator()
    {
        var result = Fresh().Claim("Someone", ClaimOrigin.NamedTerminator);

        Assert.Equal(ClaimOutcome.Forbidden, result.Outcome);
        // The operator's next action is a config file, so the message has to name
        // it. "from the machine itself" would send them to the machine they are
        // already on.
        Assert.Contains("Network__TlsTerminatedBy", result.Error);
        Assert.Contains("proxy", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_claim_from_off_the_machine_says_to_go_to_the_machine()
    {
        var result = Fresh().Claim("Someone", ClaimOrigin.OffMachine);

        Assert.Equal(ClaimOutcome.Forbidden, result.Outcome);
        Assert.Contains("machine itself", result.Error);
        // And must NOT mention the terminator, which would send somebody editing
        // a config file they do not have for a problem they do not have.
        Assert.DoesNotContain("Network__TlsTerminatedBy", result.Error);
    }

    [Fact]
    public void The_two_refusals_do_not_say_the_same_thing()
    {
        var terminator = Fresh().Claim("Someone", ClaimOrigin.NamedTerminator).Error;
        var offMachine = Fresh().Claim("Someone", ClaimOrigin.OffMachine).Error;

        Assert.NotEqual(terminator, offMachine);
    }

    [Fact]
    public void A_claim_from_the_machine_is_not_refused_at_all()
    {
        var result = Fresh().Claim("Someone", ClaimOrigin.OnMachine);

        Assert.Equal(ClaimOutcome.Ok, result.Outcome);
        Assert.Null(result.Error);
    }

    private sealed class StubAuthenticator : ITokenAuthenticator
    {
        public RuntimePrincipal? Authenticate(string bearerToken) => null;
        public string Mint(string slug) => $"{slug}:test";
        public string IssueToken(string slug) => Mint(slug);
    }
}
