namespace WorkspaceRuntime.Application;

public static class RuntimePrincipals
{
    public const string Human = "human";
    public const string Agent = "agent";
}

public enum AccessLevel
{
    Public,
    AnyPrincipal,
    HumanOnly
}

// The single map from route to required principal. Reads are policed too:
// observation is a privileged operation, not a free channel.
public static class AccessPolicy
{
    public static AccessLevel Required(string path, string method)
    {
        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.Public;
        }

        // /api/inference/status doubles as the readiness probe that the VM's
        // agent-runtime.service and `workspace-agent status` call before any
        // token can exist on a fresh installation.
        if (path == "/" || path == "/api/branding" || path == "/api/inference/status")
        {
            return AccessLevel.Public;
        }

        if (path.StartsWith("/api/approvals/", StringComparison.OrdinalIgnoreCase)
            && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.HumanOnly;
        }

        return AccessLevel.AnyPrincipal;
    }
}

public interface ITokenAuthenticator
{
    // Returns the principal name for a presented bearer token, or null.
    string? Authenticate(string bearerToken);
}
