using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public class AccessPolicyTests
{
    [Theory]
    [InlineData("/", "GET", AccessLevel.Public)]
    [InlineData("/api/branding", "GET", AccessLevel.Public)]
    [InlineData("/api/inference/status", "GET", AccessLevel.Public)]
    [InlineData("/api/setup/status", "GET", AccessLevel.Public)]
    [InlineData("/api/setup/claim", "POST", AccessLevel.Public)]
    // The claim wizard offers the desk choice before any token exists.
    [InlineData("/api/desk-profiles", "GET", AccessLevel.Public)]
    // Building one costs gigabytes, so it is the owner's call, not an agent's.
    [InlineData("/api/desk-profiles/dotnet/build", "POST", AccessLevel.HumanOnly)]
    // Anyone may read the bill (an agent that cannot see its budget cannot explain
    // why it stopped); only an owner may change a ceiling.
    [InlineData("/api/usage", "GET", AccessLevel.AnyPrincipal)]
    [InlineData("/api/usage/limits", "POST", AccessLevel.HumanOnly)]
    // Signing in is public — it is what you use when you have no session. The
    // rest of auth, and every key operation, is human-only: an agent must never
    // mint a credential or end a person's session.
    [InlineData("/api/auth/login", "POST", AccessLevel.Public)]
    [InlineData("/api/auth/logout", "POST", AccessLevel.HumanOnly)]
    [InlineData("/api/auth/password", "POST", AccessLevel.HumanOnly)]
    [InlineData("/api/keys", "GET", AccessLevel.HumanOnly)]
    [InlineData("/api/keys", "POST", AccessLevel.HumanOnly)]
    [InlineData("/api/keys/00000000-0000-0000-0000-000000000001", "DELETE", AccessLevel.HumanOnly)]
    [InlineData("/api/users", "GET", AccessLevel.AnyPrincipal)]
    [InlineData("/api/audit-events", "GET", AccessLevel.AnyPrincipal)]
    [InlineData("/api/surfaces/spreadsheet/state", "GET", AccessLevel.AnyPrincipal)]
    [InlineData("/api/surfaces/spreadsheet/commands/set-cell", "POST", AccessLevel.AnyPrincipal)]
    [InlineData("/api/tool-requests", "POST", AccessLevel.AnyPrincipal)]
    [InlineData("/api/approvals", "GET", AccessLevel.AnyPrincipal)]
    [InlineData("/api/approvals/00000000-0000-0000-0000-000000000001/approve", "POST", AccessLevel.HumanOnly)]
    [InlineData("/api/approvals/00000000-0000-0000-0000-000000000001/reject", "POST", AccessLevel.HumanOnly)]
    [InlineData("/v1/chat/completions", "POST", AccessLevel.AnyPrincipal)]
    public void Route_access_levels_are_as_declared(string path, string method, AccessLevel expected)
    {
        Assert.Equal(expected, AccessPolicy.Required(path, method));
    }

    [Fact]
    public void Tokens_resolve_to_the_right_identity_and_kind()
    {
        Run(secretsDir =>
        {
            var store = new InMemoryRuntimeStore();
            var auth = new IdentityTokenAuthenticator(secretsDir, store);

            var joche = auth.Authenticate(File.ReadAllText(Path.Combine(secretsDir, "joche.token")).Trim());
            Assert.NotNull(joche);
            Assert.Equal("joche", joche!.Slug);
            Assert.Equal(PrincipalKind.Human, joche.Kind);

            var jocheAgent = auth.Authenticate(File.ReadAllText(Path.Combine(secretsDir, "joche-agent.token")).Trim());
            Assert.NotNull(jocheAgent);
            Assert.Equal("joche-agent", jocheAgent!.Slug);
            Assert.Equal(PrincipalKind.Agent, jocheAgent.Kind);
        });
    }

    [Fact]
    public void Garbage_and_forged_tokens_are_rejected()
    {
        Run(secretsDir =>
        {
            var store = new InMemoryRuntimeStore();
            var auth = new IdentityTokenAuthenticator(secretsDir, store);

            Assert.Null(auth.Authenticate("not-a-token"));
            Assert.Null(auth.Authenticate(""));
            // Right shape, wrong signature.
            Assert.Null(auth.Authenticate("joche:deadbeef"));
            // A validly-signed token for a slug that is not a known identity.
            Assert.Null(auth.Authenticate(auth.Mint("ghost")));
        });
    }

    [Fact]
    public void A_token_cannot_be_replayed_for_a_different_slug()
    {
        Run(secretsDir =>
        {
            var store = new InMemoryRuntimeStore();
            var auth = new IdentityTokenAuthenticator(secretsDir, store);

            // Take joche's signature and paste it after yulia's slug — rejected.
            var jocheToken = auth.Mint("joche");
            var signature = jocheToken.Split(':', 2)[1];
            Assert.Null(auth.Authenticate($"yulia:{signature}"));
        });
    }

    [Fact]
    public void Signing_key_survives_a_restart_so_tokens_stay_valid()
    {
        Run(secretsDir =>
        {
            _ = new IdentityTokenAuthenticator(secretsDir, new InMemoryRuntimeStore());
            var token = File.ReadAllText(Path.Combine(secretsDir, "joche.token")).Trim();

            var reopened = new IdentityTokenAuthenticator(secretsDir, new InMemoryRuntimeStore());
            Assert.Equal("joche", reopened.Authenticate(token)!.Slug);
        });
    }

    private static void Run(Action<string> body)
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), $"workspace-runtime-secrets-{Guid.NewGuid():N}");
        try
        {
            body(secretsDir);
        }
        finally
        {
            Directory.Delete(secretsDir, recursive: true);
        }
    }
}
