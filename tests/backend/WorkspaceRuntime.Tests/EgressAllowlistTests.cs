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

    [Fact]
    public void Browser_url_navigation_applies_the_egress_allowlist()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };

        Assert.True(BrowserUrl.IsAllowed("https://example.com/page", allowed, out _));
        Assert.True(BrowserUrl.IsAllowed("https://www.example.com/page", allowed, out _));
        Assert.False(BrowserUrl.IsAllowed("https://evil.com/page", allowed, out var refusal));
        Assert.Contains("egress allowlist", refusal);

        // An empty allowlist is "no restriction": the scheme floor still holds,
        // but nothing egress-level is enforced until a desk actually has a list.
        Assert.True(BrowserUrl.IsAllowed("https://evil.com/page", new HashSet<string>(), out _));
    }

    [Fact]
    public void Egress_policy_resolves_a_desk_allowlist_from_config()
    {
        var config = new Dictionary<string, string> { ["Egress:Allowlists:office"] = "example.com, *.example.org" };
        var hosts = EgressPolicy.AllowedHosts(null, key => config.TryGetValue(key, out var value) ? value : null);

        Assert.Contains("example.com", hosts);
        Assert.Contains("*.example.org", hosts);
    }

    [Fact]
    public void Egress_policy_defaults_to_no_restriction_when_unset()
    {
        var hosts = EgressPolicy.AllowedHosts(null, _ => null);
        Assert.Empty(hosts);
    }
}
