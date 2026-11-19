using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using IPNetwork = System.Net.IPNetwork;

namespace PNMTD.Connectors;

/// <summary>A caller's network as stored (§6.3): CIDR, normalised, with an optional label.</summary>
public sealed record ConnectorNetwork(string Cidr, string? Label);

/// <summary>
/// The networks a caller calls from (docs/connectors-plan.md §6.3): a single address becomes /32 or /128, host bits are refused
/// because <see cref="IPNetwork"/> would mask them silently, and a call's address (IPv4-in-IPv6 mapped to IPv4) must be in one.
/// </summary>
public static class ConnectorNetworks
{
    public const int MaxEntries = 50;

    public const int MaxLabel = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The entry in its stored form ("10.20.0.17" → "10.20.0.17/32"), or why it isn't one.</summary>
    public static (string? Cidr, string? Error) Normalize(string? entry)
    {
        var text = entry?.Trim() ?? "";
        if (text.Length == 0)
        {
            return (null, "An address or a network, such as 10.20.0.0/24 or 2001:db8::17.");
        }
        if (!text.Contains('/'))
        {
            if (!IPAddress.TryParse(text, out var single) || text.Contains('%'))
            {
                return (null, $"\"{text}\" is not an address or a network such as 10.20.0.0/24 or 2001:db8::/48.");
            }
            single = Plain(single);
            return ($"{single}/{(single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)}", null);
        }
        var slash = text.IndexOf('/');
        if (!IPAddress.TryParse(text[..slash], out var address) || !int.TryParse(text[(slash + 1)..], out var prefix) || text.Contains('%'))
        {
            return (null, $"\"{text}\" is not a network such as 10.20.0.0/24 or 2001:db8::/48.");
        }
        address = Plain(address);
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > max)
        {
            return (null, $"\"{text}\": the prefix is 0 to {max}.");
        }
        if (!IPNetwork.TryParse($"{address}/{prefix}", out var network))
        {
            return (null, $"\"{text}\" is not a network such as 10.20.0.0/24 or 2001:db8::/48.");
        }
        if (!network.BaseAddress.Equals(address))
        {
            return (null, $"A network starts at its first address: {network}, or {address}/{max} for one address.");
        }
        return ($"{network}", null);
    }

    /// <summary>Whether <paramref name="address"/> (mapped to IPv4 where it is IPv4-in-IPv6) is in one of the networks.</summary>
    public static bool Contains(IEnumerable<ConnectorNetwork> networks, IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }
        address = Plain(address);
        foreach (var network in networks)
        {
            if (IPNetwork.TryParse(network.Cidr, out var parsed) && parsed.Contains(address))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The address as PNMT shows it: IPv4 without its ::ffff: prefix.</summary>
    public static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public static string? Text(IPAddress? address) => address is null ? null : Plain(address).ToString();

    /// <summary>Whether the networks let any address of IPv4 or IPv6 in: <c>0.0.0.0/0</c> or <c>::/0</c>.</summary>
    public static bool AnyAddress(IEnumerable<ConnectorNetwork> networks) => networks.Any(n => n.Cidr is "0.0.0.0/0" or "::/0");

    /// <summary>For each entry, the other entry it lies inside (or duplicates); null where none.</summary>
    public static IReadOnlyList<string?> Inside(IReadOnlyList<ConnectorNetwork> networks)
    {
        var parsed = networks.Select(n => IPNetwork.TryParse(n.Cidr, out var p) ? p : (IPNetwork?)null).ToList();
        return parsed.Select((p, i) =>
        {
            if (p is not { } own)
            {
                return null;
            }
            for (var j = 0; j < parsed.Count; j++)
            {
                if (j == i || parsed[j] is not { } other || other.BaseAddress.AddressFamily != own.BaseAddress.AddressFamily)
                {
                    continue;
                }
                var contains = other.PrefixLength <= own.PrefixLength && other.Contains(own.BaseAddress);
                // Two equal entries: the second is flagged as the first's duplicate.
                if (contains && (other.PrefixLength < own.PrefixLength || j < i))
                {
                    return networks[j].Cidr;
                }
            }
            return null;
        }).ToList();
    }

    public static IReadOnlyList<ConnectorNetwork> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<ConnectorNetwork>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Write(IEnumerable<ConnectorNetwork> networks) => JsonSerializer.Serialize(networks.ToList(), Json);
}
