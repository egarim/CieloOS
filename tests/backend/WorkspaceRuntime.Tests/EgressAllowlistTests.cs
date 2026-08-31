using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Tests;

public class EgressAllowlistTests
{
    [Fact]
    public void Allowlist_allows_exact_host_and_subdomains_denies_others()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com", "*.example.org" };

        Assert.True(EgressAllowlist.IsAllowed("example.com", allowed));
        Assert.True(EgressAllowlist.IsAllowed("www.example.com", allowed));
        Assert.True(EgressAllowlist.IsAllowed("sub.example.org", allowed));

        // Not in the list, or only a suffix-looking match: both denied.
        Assert.False(EgressAllowlist.IsAllowed("evil.com", allowed));
        Assert.False(EgressAllowlist.IsAllowed("notexample.com", allowed));
        Assert.False(EgressAllowlist.IsAllowed("example.com.evil.org", allowed));
    }

    [Fact]
    public void Allowlist_denies_by_default_when_empty_or_null()
    {
        Assert.False(EgressAllowlist.IsAllowed("example.com", new HashSet<string>()));
        Assert.False(EgressAllowlist.IsAllowed(null, new HashSet<string> { "example.com" }));
    }
}
