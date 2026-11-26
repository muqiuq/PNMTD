using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PNMTD.Lib.Network;

/// <summary>The tables a skip rule can name (docs/network-suggestion-rules-plan.md §4.1), stored as flags.</summary>
[Flags]
public enum SkipEvidence : short
{
    None = 0,
    MacTable = 1,
    Arp = 2,
    Mndp = 4,
    Lldp = 8,
    Cdp = 16,
    Wifi = 32,
    WireGuard = 64,
    Tunnel = 128,
}

/// <summary>A range of VLAN IDs, both ends included.</summary>
public readonly record struct VlanRange(int From, int To)
{
    public bool Contains(int vlan) => vlan >= From && vlan <= To;

    public override string ToString() => From == To ? From.ToString(CultureInfo.InvariantCulture) : $"{From}-{To}";
}

/// <summary>
/// A skip rule as the analysis applies it (docs/network-suggestion-rules-plan.md §4): the routers it is on (null: all), and
/// its conditions, all of which must match. A row it matches is no evidence; a port its interface or type matches is no end.
/// A rule without conditions only means something on chosen routers: everything they show.
/// </summary>
public sealed class TopologyRule
{
    private Regex? pattern;
    private HashSet<string>? types;

    public Guid Id { get; init; }

    /// <summary>The routers (their sensors), or null for all.</summary>
    public IReadOnlySet<Guid>? Routers { get; init; }

    /// <summary>With <c>*</c> and <c>?</c>, case-insensitive.</summary>
    public string? InterfacePattern { get; init; }

    /// <summary>RouterOS interface types: <c>vlan</c>, <c>bridge</c>, <c>wg</c>, …</summary>
    public IReadOnlyCollection<string>? Types { get; init; }

    public IReadOnlyList<VlanRange>? Vlans { get; init; }

    public SkipEvidence Evidence { get; init; }

    /// <summary>Normalised: upper case with colons, 1 to 6 octets.</summary>
    public string? MacPrefix { get; init; }

    public bool HasConditions =>
        !string.IsNullOrWhiteSpace(InterfacePattern) || Types is { Count: > 0 } || Vlans is { Count: > 0 } || Evidence != SkipEvidence.None
        || !string.IsNullOrWhiteSpace(MacPrefix);

    private bool OnlyPorts => Vlans is not { Count: > 0 } && Evidence == SkipEvidence.None && string.IsNullOrWhiteSpace(MacPrefix);

    public bool Covers(Guid router) => Routers is null || Routers.Contains(router);

    /// <summary>
    /// Whether a row is skipped: <paramref name="names"/> are the interfaces it came through as printed and the physical port
    /// under them; <paramref name="vlan"/> its VLAN.
    /// </summary>
    public bool Matches(Observation row, IReadOnlyList<string> names, Func<string, string?> typeOf, int? vlan)
    {
        if (!Covers(row.RouterId) || (Routers is null && !HasConditions))
        {
            return false;
        }
        return NamesMatch(names, typeOf)
            && (Vlans is not { Count: > 0 } || (vlan is { } v && Vlans.Any(r => r.Contains(v))))
            && (Evidence == SkipEvidence.None || EvidenceMatches(row))
            && (string.IsNullOrWhiteSpace(MacPrefix) || (row.Mac is { } mac && mac.StartsWith(MacPrefix, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Whether a router's port is no suggestion's end: a rule of interfaces and types alone, or one without conditions on
    /// chosen routers. A rule with a VLAN, a table or a MAC prefix skips rows, never a whole port.
    /// </summary>
    public bool MatchesEnd(Guid router, IReadOnlyList<string> names, Func<string, string?> typeOf)
    {
        if (!Covers(router) || !OnlyPorts)
        {
            return false;
        }
        if (!HasConditions)
        {
            return Routers is not null;
        }
        return NamesMatch(names, typeOf);
    }

    private bool NamesMatch(IReadOnlyList<string> names, Func<string, string?> typeOf)
    {
        if (!string.IsNullOrWhiteSpace(InterfacePattern))
        {
            pattern ??= SkipRuleSyntax.Glob(InterfacePattern);
            if (!names.Any(pattern.IsMatch))
            {
                return false;
            }
        }
        if (Types is { Count: > 0 })
        {
            types ??= new HashSet<string>(Types.Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
            if (!names.Any(n => typeOf(n) is { } t && types.Contains(t)))
            {
                return false;
            }
        }
        return true;
    }

    private bool EvidenceMatches(Observation row) => row.Source switch
    {
        ObservationSource.BridgeHost => Evidence.HasFlag(SkipEvidence.MacTable),
        ObservationSource.Arp => Evidence.HasFlag(SkipEvidence.Arp),
        ObservationSource.WifiClient => Evidence.HasFlag(SkipEvidence.Wifi),
        ObservationSource.WireGuardPeer => Evidence.HasFlag(SkipEvidence.WireGuard),
        ObservationSource.Tunnel => Evidence.HasFlag(SkipEvidence.Tunnel),
        // Seen by several protocols: skipped only when every one of them is.
        ObservationSource.Neighbor => SkipRuleSyntax.Protocols(row.DiscoveredBy) is { Count: > 0 } by
            ? by.All(p => (Evidence & p) != 0)
            : (Evidence & (SkipEvidence.Mndp | SkipEvidence.Lldp | SkipEvidence.Cdp)) != 0,
        _ => false,
    };
}

/// <summary>The rules' text: interface patterns, VLAN lists and MAC prefixes, as people type them.</summary>
public static class SkipRuleSyntax
{
    public const int MaxPattern = 100;
    public const int MaxVlans = 200;

    /// <summary>A pattern with <c>*</c> and <c>?</c> as a case-insensitive regular expression for the whole name.</summary>
    public static Regex Glob(string pattern)
    {
        var text = new StringBuilder("^");
        foreach (var c in pattern.Trim())
        {
            text.Append(c switch { '*' => ".*", '?' => ".", _ => Regex.Escape(c.ToString()) });
        }
        return new Regex(text.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>"10,20-29, 99": the ranges, merged nowhere; null when a part is no VLAN ID (1–4094) or range.</summary>
    public static IReadOnlyList<VlanRange>? ParseVlans(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var ranges = new List<VlanRange>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length is < 1 or > 2 || !Id(ends[0], out var from))
            {
                return null;
            }
            var to = from;
            if (ends.Length == 2 && !Id(ends[1], out to))
            {
                return null;
            }
            if (to < from)
            {
                return null;
            }
            ranges.Add(new VlanRange(from, to));
        }
        return ranges.Count == 0 ? null : ranges;

        static bool Id(string s, out int id) =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id is >= 1 and <= 4094;
    }

    public static string FormatVlans(IEnumerable<VlanRange> ranges) => string.Join(",", ranges);

    /// <summary>"00:50:56", "00-50-56", "005056": 1 to 6 octets, upper case with colons; null when it is none.</summary>
    public static string? MacPrefix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var hex = new StringBuilder();
        foreach (var c in text.Trim())
        {
            if (c is ':' or '-' or '.' or ' ')
            {
                continue;
            }
            if (!char.IsAsciiHexDigit(c))
            {
                return null;
            }
            hex.Append(char.ToUpperInvariant(c));
        }
        if (hex.Length is 0 or > 12 || hex.Length % 2 != 0)
        {
            return null;
        }
        var octets = Enumerable.Range(0, hex.Length / 2).Select(i => hex.ToString(i * 2, 2));
        return string.Join(":", octets);
    }

    /// <summary>A neighbor's protocols ("mndp,lldp") as flags, each one alone.</summary>
    public static IReadOnlyList<SkipEvidence> Protocols(string? discoveredBy) =>
        (discoveredBy ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToLowerInvariant() switch
            {
                "mndp" => SkipEvidence.Mndp,
                "lldp" => SkipEvidence.Lldp,
                "cdp" => SkipEvidence.Cdp,
                _ => SkipEvidence.None,
            })
            .ToList();
}
