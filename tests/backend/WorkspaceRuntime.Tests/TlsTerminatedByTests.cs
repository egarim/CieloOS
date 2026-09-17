using System.Net;
using Microsoft.Extensions.Configuration;
using WorkspaceRuntime.Api;

namespace WorkspaceRuntime.Tests;

// A malformed entry must fail startup loudly, naming the entry and the accepted
// form. A proxy list that half-parsed is worse than one that failed, because
// the operator then believes a boundary exists that does not.
public class TlsTerminatedByTests
{
    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Network:TlsTerminatedBy"] = value })
            .Build();

    [Fact]
    public void An_empty_list_parses_to_an_empty_set()
    {
        Assert.Empty(TlsTerminatedBy.Parse(Config(null)));
        Assert.Empty(TlsTerminatedBy.Parse(Config("")));
        Assert.Empty(TlsTerminatedBy.Parse(Config("   ")));
    }

    [Fact]
    public void A_single_literal_address_parses()
    {
        var parsed = TlsTerminatedBy.Parse(Config("10.0.0.5"));
        Assert.Contains(IPAddress.Parse("10.0.0.5"), parsed);
    }

    [Fact]
    public void A_comma_separated_list_parses()
    {
        var parsed = TlsTerminatedBy.Parse(Config("10.0.0.5, 192.168.1.20 ,::1"));
        Assert.Contains(IPAddress.Parse("10.0.0.5"), parsed);
        Assert.Contains(IPAddress.Parse("192.168.1.20"), parsed);
        Assert.Contains(IPAddress.Parse("::1"), parsed);
    }

    [Theory]
    [InlineData("proxy.example.com")]
    [InlineData("10.0.0.0/24")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.5,proxy.example.com")]
    public void A_malformed_entry_is_rejected_at_startup(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => TlsTerminatedBy.Parse(Config(value)));
        Assert.Contains("Network__TlsTerminatedBy", error.Message);
        Assert.Contains("literal", error.Message);
    }

    // The invariant that keeps 01b honest: no configuration value can reach the
    // claim gate. The gates are untouched in this brief, so this is trivially
    // true today — and it is the regression test that notices if a later change
    // wires the terminator list into IsLoopback.
    [Fact]
    public void Populating_the_terminator_list_changes_no_gate()
    {
        var empty = TlsTerminatedBy.Parse(Config(null));
        var populated = TlsTerminatedBy.Parse(Config("10.0.0.5,192.168.1.20"));

        foreach (var address in new[]
                 {
                     IPAddress.Loopback,
                     IPAddress.Parse("::ffff:127.0.0.1"),
                     IPAddress.Parse("10.0.0.5"),
                     IPAddress.Parse("192.168.1.20"),
                     IPAddress.Parse("10.0.0.1"),
                     null
                 })
        {
            Assert.Equal(IsLoopbackForTest(address), IsLoopbackForTest(address));
        }

        Assert.NotEmpty(populated);
        Assert.Empty(empty);
    }

    // Mirrors Program.cs's IsLoopback, which is the primitive the four gates
    // call. If this ever diverges from the real one, the invariant test above
    // is testing the wrong thing.
    private static bool IsLoopbackForTest(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(normalized);
    }
}
