namespace WorkspaceRuntime.Application;

// The egress decision for the agent's browser, per desk (#17 phase 2). A
// per-desk allowlist is a set of domains the browser may reach. A proxy in the
// session container sees CONNECT host:port for TLS, so the granularity is the
// host, not the URL: this is the decision function that proxy applies, live and
// at all times, rather than only during a single command.
//
// A bare name allows that host and its subdomains (example.com + foo.example.com).
// A leading "*." entry is spelled out for clarity but behaves identically, so an
// allowlist reads as a list of domains. Missing or empty means deny-by-default.
public static class EgressAllowlist
{
    public static bool IsAllowed(string? host, IReadOnlySet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(host) || allowed.Count == 0)
        {
            return false;
        }

        host = host.ToLowerInvariant().Trim();
        foreach (var entry in allowed)
        {
            var domain = entry.Trim().ToLowerInvariant();
            if (domain.Length == 0)
            {
                continue;
            }

            // Explicit "*." prefix is equivalent to a bare domain: both allow the
            // domain and everything under it. A bare "example.com" is a domain
            // allowlist, not a strict single-host one.
            if (domain.StartsWith("*."))
            {
                domain = domain[2..];
            }

            if (string.Equals(host, domain, StringComparison.Ordinal)
                || host.EndsWith("." + domain, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
