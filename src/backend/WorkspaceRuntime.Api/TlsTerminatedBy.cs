using System.Net;

namespace WorkspaceRuntime.Api;

// Network__TlsTerminatedBy: comma-separated literal IP addresses, empty by
// default. No CIDR, no hostnames. A range is a set of machines nobody named,
// and a hostname is a DNS answer somebody else controls — resolving one would
// make DNS a trust root for a security decision.
//
// A malformed entry fails startup loudly rather than being dropped. A proxy
// list that half-parsed is worse than one that failed, because the operator
// then believes a boundary exists that does not.
public static class TlsTerminatedBy
{
    public static IReadOnlySet<IPAddress> Parse(IConfiguration configuration)
    {
        var raw = configuration["Network:TlsTerminatedBy"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<IPAddress>();
        }

        var parsed = new HashSet<IPAddress>();
        foreach (var entry in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            if (entry.Length == 0)
            {
                continue;
            }

            if (!IPAddress.TryParse(entry, out var address))
            {
                throw new InvalidOperationException(
                    $"Network__TlsTerminatedBy contains '{entry}', which is not a literal IP address. " +
                    "Accepted form: comma-separated literal IPv4 or IPv6 addresses, for example " +
                    "Network__TlsTerminatedBy=10.0.0.5,192.168.1.20. " +
                    "CIDR ranges and hostnames are refused: a range is a set of machines you did not name, " +
                    "and a hostname is a DNS answer somebody else controls.");
            }

            // A wildcard parses as a valid IPAddress, so it has to be refused
            // by value. A wildcard socket is by construction also directly
            // reachable, so the assertion it encodes can never be true.
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                throw new InvalidOperationException(
                    $"Network__TlsTerminatedBy contains '{entry}', which is a wildcard address. " +
                    "Accepted form: comma-separated literal IPv4 or IPv6 addresses, for example " +
                    "Network__TlsTerminatedBy=10.0.0.5,192.168.1.20. " +
                    "A wildcard names every machine, which is the same as naming none.");
            }

            parsed.Add(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        }

        return parsed;
    }
}
