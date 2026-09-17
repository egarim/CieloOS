using System.Net;
using Microsoft.AspNetCore.Http;
using WorkspaceRuntime.Api;

namespace WorkspaceRuntime.Tests;

// OnThisMachine is narrower than Confidential: it asks whether the caller is
// the box, not whether the channel is private. The case it exists for is a
// co-located proxy forwarding the internet, which looks like an on-box caller
// from the remote half alone.
public class OnThisMachineTests
{
    private static readonly IReadOnlySet<IPAddress> NoTerminators = new HashSet<IPAddress>();

    private static HttpContext Connection(IPAddress? local, IPAddress? remote)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = local;
        context.Connection.RemoteIpAddress = remote;
        return context;
    }

    [Fact]
    public void A_loopback_peer_on_a_loopback_local_endpoint_is_on_this_machine()
    {
        var context = Connection(IPAddress.Loopback, IPAddress.Loopback);
        Assert.True(TransportFacts.OnThisMachine(context, NoTerminators));
    }

    // The ambiguous case the narrowing exists for. A co-located proxy
    // forwarding the internet presents a loopback remote half and a
    // non-loopback local half; from the remote half alone it is
    // indistinguishable from an on-box caller.
    [Fact]
    public void A_loopback_peer_on_a_non_loopback_local_endpoint_is_not_on_this_machine()
    {
        var context = Connection(IPAddress.Parse("10.0.0.1"), IPAddress.Loopback);
        Assert.False(TransportFacts.OnThisMachine(context, NoTerminators));
    }

    [Fact]
    public void A_named_terminator_is_never_on_this_machine_even_from_loopback()
    {
        var terminators = new HashSet<IPAddress> { IPAddress.Loopback };
        var context = Connection(IPAddress.Loopback, IPAddress.Loopback);
        Assert.False(TransportFacts.OnThisMachine(context, terminators));
    }

    // The admin unix socket. Kestrel leaves both halves null because there is
    // no IP endpoint to report. This is sound only because the process listens
    // on TCP and unix sockets and nothing else.
    [Fact]
    public void A_connection_with_both_addresses_null_is_on_this_machine()
    {
        var context = Connection(local: null, remote: null);
        Assert.True(TransportFacts.OnThisMachine(context, NoTerminators));
    }

    // The invariant ConfidentialTests already guards, from this angle: naming
    // a terminator can only ever remove authority, never grant it. A peer that
    // was off-machine before the terminator was named must still be
    // off-machine after.
    [Fact]
    public void Naming_a_terminator_does_not_make_an_off_machine_peer_on_machine()
    {
        var peer = IPAddress.Parse("10.0.0.5");
        var context = Connection(IPAddress.Parse("10.0.0.1"), peer);

        Assert.False(TransportFacts.OnThisMachine(context, NoTerminators));
        Assert.False(TransportFacts.OnThisMachine(context, new HashSet<IPAddress> { peer }));
    }

    [Fact]
    public void An_ipv4_mapped_loopback_peer_on_a_loopback_local_endpoint_is_on_this_machine()
    {
        var context = Connection(IPAddress.Parse("::ffff:127.0.0.1"), IPAddress.Parse("::ffff:127.0.0.1"));
        Assert.True(TransportFacts.OnThisMachine(context, NoTerminators));
    }

    [Fact]
    public void A_null_peer_on_a_loopback_local_endpoint_is_not_on_this_machine()
    {
        var context = Connection(IPAddress.Loopback, remote: null);
        Assert.False(TransportFacts.OnThisMachine(context, NoTerminators));
    }

    [Fact]
    public void A_loopback_peer_on_a_null_local_endpoint_is_not_on_this_machine()
    {
        var context = Connection(local: null, IPAddress.Loopback);
        Assert.False(TransportFacts.OnThisMachine(context, NoTerminators));
    }
}
