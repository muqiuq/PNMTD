using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace PNMTD.Hosting;

/// <summary>
/// The <c>Proxy</c> setting (docs/connectors-plan.md §6.4): the reverse proxies whose <c>X-Forwarded-For</c> and
/// <c>X-Forwarded-Proto</c> are trusted, as a comma-separated list of <c>docker</c> (Docker's bridge range,
/// 172.16.0.0/12), addresses and networks. An entry that doesn't parse stops the start: a typo would otherwise trust
/// nothing, or the wrong thing. IPv4 entries also cover their IPv4-mapped IPv6 form, because the middleware maps those.
/// </summary>
public static class ProxySetting
{
    /// <summary>Docker's default bridge networks.</summary>
    public static readonly IPNetwork Docker = new(IPAddress.Parse("172.16.0.0"), 12);

    /// <summary>What the setting trusts, or an error naming the entry.</summary>
    public sealed record Parsed(IReadOnlyList<IPAddress> Proxies, IReadOnlyList<IPNetwork> Networks);

    /// <summary>Parses the setting; throws <see cref="InvalidOperationException"/> with a clear message for a bad entry.</summary>
    public static Parsed Parse(string value)
    {
        var proxies = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        foreach (var raw in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Equals("docker", StringComparison.OrdinalIgnoreCase))
            {
                networks.Add(Docker);
            }
            else if (raw.Contains('/'))
            {
                if (!IPNetwork.TryParse(raw, out var network) || !StartsAtFirstAddress(raw, network))
                {
                    throw new InvalidOperationException(
                        $"Proxy: \"{raw}\" is not a network such as 10.0.0.0/8 or 2001:db8::/32 (a network starts at its first address).");
                }
                networks.Add(network);
            }
            else if (IPAddress.TryParse(raw, out var address))
            {
                proxies.Add(address);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Proxy: \"{raw}\" is neither docker, an address such as 192.168.1.80, nor a network such as 10.0.0.0/8.");
            }
        }
        if (proxies.Count == 0 && networks.Count == 0)
        {
            throw new InvalidOperationException("Proxy: no entry. Use docker, an address, a network, or a comma-separated list of them.");
        }
        return new Parsed(proxies, networks);
    }

    /// <summary>Trusts what <paramref name="value"/> names, on top of loopback.</summary>
    public static void Configure(ForwardedHeadersOptions options, string value)
    {
        var parsed = Parse(value);
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.RequireHeaderSymmetry = false;
        options.ForwardLimit = null;
        foreach (var proxy in parsed.Proxies)
        {
            options.KnownProxies.Add(proxy);
            if (proxy.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                options.KnownProxies.Add(proxy.MapToIPv6());
            }
            else if (proxy.IsIPv4MappedToIPv6)
            {
                options.KnownProxies.Add(proxy.MapToIPv4());
            }
        }
        foreach (var network in parsed.Networks)
        {
            options.KnownIPNetworks.Add(network);
        }
    }

    /// <summary><c>IPNetwork</c> masks host bits silently (10.0.0.5/24 is 10.0.0.0/24); an entry with them is a mistake.</summary>
    private static bool StartsAtFirstAddress(string raw, IPNetwork network) =>
        IPAddress.TryParse(raw[..raw.IndexOf('/')], out var given) && given.Equals(network.BaseAddress);
}
