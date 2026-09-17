using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using WorkspaceRuntime.Api;

namespace WorkspaceRuntime.Tests;

// The three facts from docs/tls-and-proxies.md §4, and the one that matters:
// an unnamed non-loopback peer is not confidential even when it says it is.
public class ConfidentialTests
{
    private static readonly IReadOnlySet<IPAddress> NoTerminators = new HashSet<IPAddress>();

    private static HttpContext Connection(IPAddress? local, IPAddress? remote, bool tls = false)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = local;
        context.Connection.RemoteIpAddress = remote;
        if (tls)
        {
            context.Features.Set<ITlsHandshakeFeature>(new StubTlsHandshakeFeature());
        }

        return context;
    }

    [Fact]
    public void A_kestrel_terminated_tls_connection_is_confidential()
    {
        var context = Connection(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.5"), tls: true);
        Assert.True(TransportFacts.Confidential(context, NoTerminators));
    }

    [Fact]
    public void A_loopback_local_endpoint_is_confidential()
    {
        var context = Connection(IPAddress.Loopback, IPAddress.Parse("10.0.0.5"));
        Assert.True(TransportFacts.Confidential(context, NoTerminators));
    }

    [Fact]
    public void A_named_terminator_peer_is_confidential()
    {
        var terminators = new HashSet<IPAddress> { IPAddress.Parse("10.0.0.5") };
        var context = Connection(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.5"));
        Assert.True(TransportFacts.Confidential(context, terminators));
    }

    // THE regression. A caller who forges X-Forwarded-Proto must not be able to
    // grant themselves a Secure cookie, because the header is read to log and
    // for nothing else.
    [Fact]
    public void An_unnamed_non_loopback_peer_is_not_confidential_even_when_it_says_it_is_https()
    {
        var context = Connection(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.5"));
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        Assert.False(TransportFacts.Confidential(context, NoTerminators));
    }

    [Fact]
    public void An_ipv4_mapped_loopback_local_endpoint_is_confidential()
    {
        var context = Connection(IPAddress.Parse("::ffff:127.0.0.1"), IPAddress.Parse("10.0.0.5"));
        Assert.True(TransportFacts.Confidential(context, NoTerminators));
    }

    [Fact]
    public void An_ipv4_mapped_named_terminator_is_confidential()
    {
        var terminators = new HashSet<IPAddress> { IPAddress.Parse("10.0.0.5") };
        var context = Connection(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("::ffff:10.0.0.5"));
        Assert.True(TransportFacts.Confidential(context, terminators));
    }

    [Fact]
    public void A_null_peer_on_a_non_loopback_local_endpoint_is_not_confidential()
    {
        var context = Connection(IPAddress.Parse("10.0.0.1"), remote: null);
        Assert.False(TransportFacts.Confidential(context, NoTerminators));
    }

    // The warning fires once per process per address, and it names the exact
    // string to paste. A second request from the same address is silent.
    [Fact]
    public void The_forwarded_proto_warning_fires_once_per_address()
    {
        var logger = new CountingLogger();
        var context = Connection(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.5"));
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        TransportFacts.WarnOnUnnamedForwardedProto(context, NoTerminators, logger);
        TransportFacts.WarnOnUnnamedForwardedProto(context, NoTerminators, logger);

        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public void The_forwarded_proto_warning_does_not_fire_for_a_named_terminator()
    {
        var logger = new CountingLogger();
        var terminators = new HashSet<IPAddress> { IPAddress.Parse("10.0.0.5") };
        var context = Connection(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.5"));
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        TransportFacts.WarnOnUnnamedForwardedProto(context, terminators, logger);

        Assert.Equal(0, logger.Warnings);
    }

    // Only Protocol is read by anything under test; the rest exist because the
    // interface demands them. The values are the weakest legal ones on purpose —
    // if a future predicate starts caring about cipher strength, a stub that
    // claimed AES-256 would hide the fact that this test never negotiated anything.
    private sealed class StubTlsHandshakeFeature : ITlsHandshakeFeature
    {
        public SslProtocols Protocol => SslProtocols.Tls12;
        public TlsCipherSuite? NegotiatedCipherSuite => null;
        public string HostName => "test";
        public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.None;
        public int CipherStrength => 0;
        public HashAlgorithmType HashAlgorithm => HashAlgorithmType.None;
        public int HashStrength => 0;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.None;
        public int KeyExchangeStrength => 0;
    }

    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
