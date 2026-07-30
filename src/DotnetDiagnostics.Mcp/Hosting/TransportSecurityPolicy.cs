using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using IPNetwork = System.Net.IPNetwork;

namespace DotnetDiagnostics.Mcp.Hosting;

/// <summary>Startup policy for network HTTP transport. A cleartext non-loopback
/// listener is permitted only behind an explicitly trusted TLS-terminating proxy or
/// with the intentionally unsafe development override.</summary>
internal sealed class TransportSecurityPolicy
{
    public const string AllowInsecureHttpKey = "MCP_ALLOW_INSECURE_HTTP";
    public const string TrustedProxyCidrsKey = "MCP_TRUSTED_PROXY_CIDRS";

    private static readonly char[] ProxyListSeparators = { ',', ';' };
    private readonly IReadOnlyList<IPAddress> _trustedProxies;
    private readonly IReadOnlyList<IPNetwork> _trustedNetworks;

    private TransportSecurityPolicy(
        bool allowInsecureHttp,
        IReadOnlyList<IPAddress> trustedProxies,
        IReadOnlyList<IPNetwork> trustedNetworks)
    {
        AllowInsecureHttp = allowInsecureHttp;
        _trustedProxies = trustedProxies;
        _trustedNetworks = trustedNetworks;
    }

    public bool AllowInsecureHttp { get; }
    public bool HasTrustedProxies => _trustedProxies.Count > 0 || _trustedNetworks.Count > 0;

    public static TransportSecurityPolicy Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var allowInsecureHttp = string.Equals(
            configuration[AllowInsecureHttpKey],
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);

        var trustedProxies = new List<IPAddress>();
        var trustedNetworks = new List<IPNetwork>();
        var raw = configuration[TrustedProxyCidrsKey];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var entry in raw.Split(
                ProxyListSeparators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (entry.Contains('/'))
                {
                    if (!IPNetwork.TryParse(entry, out var network))
                    {
                        throw new InvalidOperationException(
                            $"{TrustedProxyCidrsKey} contains invalid CIDR '{entry}'.");
                    }

                    trustedNetworks.Add(network);
                    continue;
                }

                if (!IPAddress.TryParse(entry, out var proxy))
                {
                    throw new InvalidOperationException(
                        $"{TrustedProxyCidrsKey} contains invalid IP address '{entry}'.");
                }

                trustedProxies.Add(proxy);
            }
        }

        return new TransportSecurityPolicy(
            allowInsecureHttp,
            trustedProxies,
            trustedNetworks);
    }

    public ForwardedHeadersOptions CreateForwardedHeadersOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in _trustedProxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var network in _trustedNetworks)
        {
            options.KnownIPNetworks.Add(network);
        }

        return options;
    }

    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }
}
