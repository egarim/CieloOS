using WorkspaceRuntime.Application;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public class AccessPolicyTests
{
    [Theory]
    [InlineData("/", "GET", AccessLevel.Public)]
    [InlineData("/api/branding", "GET", AccessLevel.Public)]
    [InlineData("/api/inference/status", "GET", AccessLevel.Public)]
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
    public void Tokens_authenticate_to_distinct_principals_and_garbage_is_rejected()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), $"workspace-runtime-secrets-{Guid.NewGuid():N}");
        try
        {
            var store = new FileTokenStore(secretsDir);
            var humanToken = File.ReadAllText(Path.Combine(secretsDir, "human.token")).Trim();
            var agentToken = File.ReadAllText(Path.Combine(secretsDir, "agent.token")).Trim();

            Assert.Equal(RuntimePrincipals.Human, store.Authenticate(humanToken));
            Assert.Equal(RuntimePrincipals.Agent, store.Authenticate(agentToken));
            Assert.Null(store.Authenticate("not-a-token"));
            Assert.Null(store.Authenticate(""));
            Assert.NotEqual(humanToken, agentToken);
        }
        finally
        {
            Directory.Delete(secretsDir, recursive: true);
        }
    }

    [Fact]
    public void Empty_token_file_fails_fast_instead_of_authenticating_blank_bearers()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), $"workspace-runtime-secrets-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(secretsDir);
            File.WriteAllText(Path.Combine(secretsDir, "human.token"), "\n");
            Assert.Throws<InvalidOperationException>(() => new FileTokenStore(secretsDir));
        }
        finally
        {
            Directory.Delete(secretsDir, recursive: true);
        }
    }

    [Fact]
    public void Tokens_survive_a_restart_of_the_store()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), $"workspace-runtime-secrets-{Guid.NewGuid():N}");
        try
        {
            _ = new FileTokenStore(secretsDir);
            var humanToken = File.ReadAllText(Path.Combine(secretsDir, "human.token")).Trim();

            var reopened = new FileTokenStore(secretsDir);
            Assert.Equal(RuntimePrincipals.Human, reopened.Authenticate(humanToken));
        }
        finally
        {
            Directory.Delete(secretsDir, recursive: true);
        }
    }
}
