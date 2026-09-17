using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Connections.Features;

namespace WorkspaceRuntime.Api;

// The three facts from docs/tls-and-proxies.md §4, each a property of the
// connection rather than of its content. A caller cannot forge any of them:
// the handshake feature is set by the layer that performed the handshake, a
// loopback local endpoint is routing rather than a claim, and the named
// terminator list is a root-owned file the caller contributes nothing to.
//
// This is deliberately NOT Request.IsHttps. IsHttps is Scheme == "https", and
// Scheme is a mutable string any middleware can assign — including
// ForwardedHeadersMiddleware, which is the middleware this design exists to
// stay immune to. Reading the local half of the socket instead of the remote
// half is what makes that immunity structural rather than a promise.
public static class TransportFacts
{
    // Once per process per address. A hostile caller who forges the header can
    // only get themselves logged, which is the whole point of the rule below.
    private static readonly ConcurrentDictionary<string, byte> WarnedAddresses = new();

    // A header may be read only to reduce authority or to say something out
    // loud, never to grant it. This reads X-Forwarded-Proto to log a warning
    // and for nothing else: the return value is discarded, and no caller may
    // use this to decide anything.
    public static void WarnOnUnnamedForwardedProto(HttpContext context, IReadOnlySet<IPAddress> namedTerminators, ILogger logger)
    {
        if (!context.Request.Headers.ContainsKey("X-Forwarded-Proto"))
        {
            return;
        }

        var peer = Normalize(context.Connection.RemoteIpAddress);
        if (peer is null || namedTerminators.Contains(peer))
        {
            return;
        }

        var key = peer.ToString();
        if (!WarnedAddresses.TryAdd(key, 0))
        {
            return;
        }

        logger.LogWarning(
            "A request from {Address} carried X-Forwarded-Proto, but {Address} is not named in Network__TlsTerminatedBy. " +
            "The header was ignored. If {Address} is your TLS terminator, add it to /etc/cielo/network.env as: " +
            "Network__TlsTerminatedBy={Address}",
            key, key, key, key);
    }

    // True when any of the three facts holds. The order is cheapest-first and
    // has no security meaning; all three are equally sufficient.
    public static bool Confidential(HttpContext context, IReadOnlySet<IPAddress> namedTerminators)
    {
        // Fact 1: Kestrel performed the TLS handshake itself. The feature is
        // present only when the connection layer actually did the handshake,
        // which is why this is not Request.IsHttps.
        if (context.Features.Get<ITlsHandshakeFeature>() is not null)
        {
            return true;
        }

        // Fact 2: accepted on a loopback local endpoint. The kernel will not
        // deliver an off-box packet to a loopback local endpoint, so this is
        // routing rather than a claim. LocalIpAddress, not RemoteIpAddress:
        // ForwardedHeadersMiddleware rewrites the remote half and not the
        // local half, so building on the local half is immune by construction.
        if (IPAddress.IsLoopback(Normalize(context.Connection.LocalIpAddress) ?? IPAddress.None))
        {
            return true;
        }

        // Fact 3: the peer is an address the operator named as their own TLS
        // terminator. The operator describes their own network in a root-owned
        // file; the caller contributes nothing to the comparison.
        var peer = Normalize(context.Connection.RemoteIpAddress);
        return peer is not null && namedTerminators.Contains(peer);
    }

    // True when the caller is standing on this machine. Narrower than
    // Confidential on purpose: Confidential asks "is this channel private",
    // which a co-located TLS terminator can answer yes to on behalf of an
    // off-box caller. OnThisMachine asks "is this caller the box", which a
    // terminator cannot answer yes to, because a proxy is not the box.
    //
    // The four gates in Program.cs still call IsLoopback. This predicate is
    // built here and switched in 01b-2, once it has been proven on a real VM.
    // Do not wire it in from this commit.
    public static bool OnThisMachine(HttpContext context, IReadOnlySet<IPAddress> namedTerminators)
    {
        var remote = Normalize(context.Connection.RemoteIpAddress);
        var local = Normalize(context.Connection.LocalIpAddress);

        // The admin unix socket. Kestrel's unix transport leaves both halves
        // null because there is no IP endpoint to report, and the TCP
        // transport always populates at least the local half once a connection
        // is accepted. So "both null" means unix socket — but only because
        // this process listens on TCP and unix sockets and nothing else. A
        // future transport that also leaves both null (a named pipe, an
        // in-process test transport) would be misclassified as on-machine
        // here, and this branch is where that would have to be revisited.
        if (remote is null && local is null)
        {
            return true;
        }

        // A named terminator is never on this machine, even from loopback. The
        // operator has told us that address is a proxy, and a proxy is not the
        // box. This is the one place OnThisMachine is narrower than
        // Confidential on a loopback peer, and it is deliberate.
        if (remote is null || namedTerminators.Contains(remote))
        {
            return false;
        }

        // Both halves loopback. The remote half alone is not enough: a
        // co-located proxy forwarding the internet looks exactly like an
        // on-box caller from the remote half alone, and the local half is what
        // distinguishes them. The local half alone is not enough either: a
        // loopback local endpoint with a non-loopback peer is a forwarded
        // connection, which is the case Confidential's Fact 2 accepts and this
        // predicate must not.
        return IPAddress.IsLoopback(remote) && IPAddress.IsLoopback(local ?? IPAddress.None);
    }

    // The four gates in Program.cs call this today. It is exposed here so the
    // test that pins its behaviour for part one of 01b can reach it without
    // duplicating the implementation; the gates themselves are unchanged.
    public static bool IsLoopback(IPAddress? address) =>
        IPAddress.IsLoopback(Normalize(address) ?? IPAddress.None);

    // IPv4-mapped IPv6 (::ffff:127.0.0.1) is unwrapped so a mapped loopback
    // still counts, matching the existing IsLoopback helper in Program.cs.
    private static IPAddress? Normalize(IPAddress? address) =>
        address is null ? null : address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
