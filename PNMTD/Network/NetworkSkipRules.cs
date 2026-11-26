using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Network;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Network;

/// <summary>
/// The skip rules as the analysis applies them (docs/network-suggestion-rules-plan.md §4.4): a location's rule covers its
/// routers and those of its sub-locations (a router's location is its host's); the analyser never sees locations.
/// </summary>
public static class NetworkSkipRules
{
    /// <summary>The enabled ones, oldest first, so the same rules give the same tags.</summary>
    public static IReadOnlyList<TopologyRule> ForAnalysis(IEnumerable<NetworkSkipRuleEntity> rules, IReadOnlyDictionary<Guid, Guid?> routerLocations,
        LocationTreeSnapshot tree) =>
        rules.Where(r => r.Enabled).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Select(r => ToTopology(r, routerLocations, tree)).ToList();

    public static TopologyRule ToTopology(NetworkSkipRuleEntity r, IReadOnlyDictionary<Guid, Guid?> routerLocations, LocationTreeSnapshot tree)
    {
        IReadOnlySet<Guid>? routers = r.Scope switch
        {
            NetworkSkipRuleScope.Router => r.SensorId is { } s ? new HashSet<Guid> { s } : new HashSet<Guid>(),
            NetworkSkipRuleScope.Location => r.LocationId is { } l ? Within(l, routerLocations, tree) : new HashSet<Guid>(),
            _ => null,
        };
        return new TopologyRule
        {
            Id = r.Id,
            Routers = routers,
            InterfacePattern = string.IsNullOrWhiteSpace(r.InterfacePattern) ? null : r.InterfacePattern,
            Types = r.InterfaceTypes is { Count: > 0 } types ? types : null,
            Vlans = SkipRuleSyntax.ParseVlans(r.Vlans) ?? [],
            Evidence = r.Evidence,
            MacPrefix = string.IsNullOrWhiteSpace(r.MacPrefix) ? null : r.MacPrefix,
        };
    }

    private static HashSet<Guid> Within(Guid location, IReadOnlyDictionary<Guid, Guid?> routerLocations, LocationTreeSnapshot tree)
    {
        var within = tree.SelfAndDescendants(location).ToHashSet();
        return routerLocations.Where(r => r.Value is { } l && within.Contains(l)).Select(r => r.Key).ToHashSet();
    }

    public static SkipEvidence Evidence(IEnumerable<NetworkSkipEvidence>? evidence) =>
        (evidence ?? []).Aggregate(SkipEvidence.None, (all, e) => all | Flag(e));

    public static IReadOnlyList<NetworkSkipEvidence> Evidence(SkipEvidence flags) =>
        Enum.GetValues<NetworkSkipEvidence>().Where(e => flags.HasFlag(Flag(e))).ToList();

    private static SkipEvidence Flag(NetworkSkipEvidence e) => e switch
    {
        NetworkSkipEvidence.MacTable => SkipEvidence.MacTable,
        NetworkSkipEvidence.Arp => SkipEvidence.Arp,
        NetworkSkipEvidence.Mndp => SkipEvidence.Mndp,
        NetworkSkipEvidence.Lldp => SkipEvidence.Lldp,
        NetworkSkipEvidence.Cdp => SkipEvidence.Cdp,
        NetworkSkipEvidence.Wifi => SkipEvidence.Wifi,
        NetworkSkipEvidence.WireGuard => SkipEvidence.WireGuard,
        NetworkSkipEvidence.Tunnel => SkipEvidence.Tunnel,
        _ => SkipEvidence.None,
    };
}
