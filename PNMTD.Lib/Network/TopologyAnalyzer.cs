using System.Text.Json.Serialization;

namespace PNMTD.Lib.Network;

[JsonConverter(typeof(JsonStringEnumConverter<PortRole>))]
public enum PortRole { Edge = 0, ToRouter = 1, ToNeighbor = 2, Virtual = 3 }

[JsonConverter(typeof(JsonStringEnumConverter<MatchKind>))]
public enum MatchKind { None = 0, RouterPort = 1, ServerAdapter = 2, Manual = 3, Ambiguous = 4 }

[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence { Low = 0, Medium = 1, High = 2 }

[JsonConverter(typeof(JsonStringEnumConverter<SuggestionKind>))]
public enum SuggestionKind { Link = 0, Differs = 1 }

/// <summary>The tables a network interface was seen in.</summary>
[Flags]
public enum InterfaceSources { None = 0, MacTable = 1, Arp = 2, Dhcp = 4, Neighbor = 8, Wifi = 16 }

/// <summary>
/// One end of a connection (docs/network-map-plan.md §3.1): a host, and for a router the router's sensor, and optionally the
/// interface (a router's port, a server's adapter).
/// </summary>
public sealed record ConnectionEnd(Guid HostId, Guid? SensorId, string? Interface)
{
    public string Key => $"{HostId:N}/{(SensorId is { } s ? s.ToString("N") : "-")}/{(string.IsNullOrWhiteSpace(Interface) ? "-" : Interface.Trim().ToLowerInvariant())}";

    /// <summary>The device without the interface: "router:{id}" or "host:{id}".</summary>
    public string Device => SensorId is { } s ? $"router:{s:N}" : $"host:{HostId:N}";

    /// <summary>The key of a pair of ends, the same whichever comes first.</summary>
    public static string PairKey(ConnectionEnd a, ConnectionEnd b) =>
        string.CompareOrdinal(a.Key, b.Key) <= 0 ? $"{a.Key}|{b.Key}" : $"{b.Key}|{a.Key}";

    /// <summary>The pair in its stored order: the smaller key first.</summary>
    public static (ConnectionEnd First, ConnectionEnd Second) Ordered(ConnectionEnd a, ConnectionEnd b) =>
        string.CompareOrdinal(a.Key, b.Key) <= 0 ? (a, b) : (b, a);
}

public sealed record TopologyRouter(Guid SensorId, Guid HostId, string Name, string? Identity, DateTime? ScannedAt);

public sealed record TopologyHost(Guid HostId, string Name);

/// <summary>A server's adapter, from its agent (agent_adapter_macs); <paramref name="Kind"/> as the agent names it ("Bond", "VLAN").</summary>
public sealed record TopologyAdapter(Guid HostId, Guid SensorId, string Mac, string Adapter, long? SpeedBps, bool Virtual, string? Kind = null);

/// <summary>A MAC a person assigned to a host (§6.2), with the interface's name if they gave one.</summary>
public sealed record TopologyAssignment(string Mac, Guid HostId, string? Interface);

public sealed record TopologyConnection(Guid Id, ConnectionEnd A, ConnectionEnd B, string Type);

/// <summary>Everything the analysis reads (§5.1).</summary>
public sealed class TopologyInput
{
    public IReadOnlyList<TopologyRouter> Routers { get; init; } = [];

    public IReadOnlyList<TopologyHost> Hosts { get; init; } = [];

    public IReadOnlyList<RouterPort> Ports { get; init; } = [];

    public IReadOnlyList<Observation> Observations { get; init; } = [];

    public IReadOnlyList<TopologyAdapter> Adapters { get; init; } = [];

    public IReadOnlyList<TopologyAssignment> Assignments { get; init; } = [];

    public IReadOnlySet<string> Ignored { get; init; } = new HashSet<string>();

    public IReadOnlyList<TopologyConnection> Connections { get; init; } = [];

    /// <summary>The enabled skip rules (docs/network-suggestion-rules-plan.md §4).</summary>
    public IReadOnlyList<TopologyRule> Rules { get; init; } = [];
}

public sealed record PortRoleResult(Guid RouterId, string Port, PortRole Role, int MacCount);

/// <summary>
/// One sentence of a suggestion's evidence, the rule that made it, and how sure it is. As data too (§3.4 of
/// docs/network-suggestion-rules-plan.md): the router whose row it is, the interface as that router printed it, the VLAN,
/// the VLANs joined into the sentence, and a neighbor's protocols.
/// </summary>
public sealed record Evidence(string Rule, Confidence Confidence, string Text)
{
    public Guid? RouterId { get; init; }

    public string? Interface { get; init; }

    public int? Vlan { get; init; }

    public IReadOnlyList<string>? Vlans { get; init; }

    public string? By { get; init; }
}

/// <summary>A network interface as the analysis sees it (§6.1).</summary>
public sealed record InterfaceResult(string Mac)
{
    public IReadOnlyList<string> Ips { get; init; } = [];

    public string? HostName { get; init; }

    public InterfaceSources Sources { get; init; }

    public bool Random { get; init; }

    public Guid? AttachedRouterId { get; init; }

    public string? AttachedPort { get; init; }

    public int? AttachedVlan { get; init; }

    /// <summary>"behind core-sw", "behind a known router", "also seen on branch-gw ether3".</summary>
    public string? AttachedNote { get; init; }

    public string? NeighborIdentity { get; init; }

    public string? NeighborPlatform { get; init; }

    public string? NeighborBoard { get; init; }

    public string? NeighborPort { get; init; }

    public MatchKind Match { get; init; }

    public Guid? MatchHostId { get; init; }

    public Guid? MatchSensorId { get; init; }

    public string? MatchInterface { get; init; }

    public Guid? HintHostId { get; init; }

    /// <summary>The skip rule that leaves out the row it is attached by, if any.</summary>
    public Guid? SkippedBy { get; init; }

    public DateTime LastSeenAt { get; init; }
}

/// <summary>A suggestion (§5.3): two ends, the proposed type, the confidence and the evidence.</summary>
public sealed record SuggestionResult(string Key, SuggestionKind Kind, ConnectionEnd A, ConnectionEnd B, string? Type, Confidence Confidence,
    IReadOnlyList<Evidence> Evidence, Guid? DiffersFrom);

/// <summary>Whether the latest scans show a connection (§5.5), and which of its ends' routers were looked at.</summary>
public sealed record ConnectionCheck(Guid ConnectionId, bool Evidenced, IReadOnlyList<Guid> Routers);

/// <summary>What a skip rule left out: rows, and the routers they came from.</summary>
public sealed record RuleHit(Guid RuleId, int Rows, int Routers);

public sealed class TopologyResult
{
    public List<PortRoleResult> Roles { get; } = [];

    public List<InterfaceResult> Interfaces { get; } = [];

    public List<SuggestionResult> Suggestions { get; } = [];

    public List<ConnectionCheck> Checks { get; } = [];

    public List<RuleHit> Hits { get; } = [];
}

/// <summary>
/// The analysis (docs/network-map-plan.md §5): port roles, the network interfaces with their matches, the suggestions, and the
/// connections' confirmations, from everything stored. Pure: the same input gives the same output. Every end of a suggestion
/// is a router or a host (decision 6); a MAC that matches no host is an interface, never a node.
/// <para>
/// Every end sits on the wire (docs/network-suggestion-rules-plan.md §3): a VLAN's rows count for its parent port, a bridge's
/// for the member where the device was seen, and a device pair gets one suggestion per cable. With skip rules, it runs twice:
/// as the routers show it (the interfaces, the confirmations) and without what the rules skip (the roles, the suggestions).
/// </para>
/// </summary>
public static class TopologyAnalyzer
{
    private static readonly HashSet<string> VirtualTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "wg", "vlan", "bridge", "eoip", "eoipv6", "gre-tunnel", "gre6-tunnel", "ipip-tunnel", "ipipv6-tunnel", "vxlan", "loopback", "vpls",
        "6to4-tunnel", "l2tp-out", "l2tp-in", "sstp-out", "sstp-in", "pptp-out", "pptp-in", "ovpn-out", "ovpn-in", "pppoe-out", "pppoe-in", "vrrp",
    };

    private static readonly HashSet<string> NoPortTypes = new(StringComparer.OrdinalIgnoreCase) { "bridge", "vlan", "vrrp" };

    public static TopologyResult Analyze(TopologyInput input)
    {
        if (input.Rules.Count == 0)
        {
            return new Run(input, false).Execute();
        }
        var seen = new Run(input, false).Execute();
        var ruled = new Run(input, true).Execute();
        var result = new TopologyResult();
        result.Roles.AddRange(ruled.Roles);
        result.Suggestions.AddRange(ruled.Suggestions);
        result.Hits.AddRange(ruled.Hits);
        result.Interfaces.AddRange(seen.Interfaces);
        result.Checks.AddRange(seen.Checks);
        return result;
    }

    /// <summary>The physical port under an interface, and the VLAN it came through (plan §3.1).</summary>
    private readonly record struct Wired(string? Port, string? Vlan, int? VlanId);

    /// <summary>Where a candidate's row comes from: for the evidence's data and for joining its VLANs (§3.4).</summary>
    private sealed record Origin(Guid? Router = null, string? Interface = null, int? VlanId = null, string? Vlan = null, string? By = null)
    {
        public bool Tagged => Vlan is not null || VlanId is not null;

        public string VlanLabel => Vlan is { } name
            ? VlanId is { } id && !name.Contains(id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) ? $"{name} (VLAN {id})" : name
            : $"VLAN {VlanId}";
    }

    private sealed class Run(TopologyInput input, bool filter)
    {
        private readonly TopologyResult result = new();
        private readonly IReadOnlyList<TopologyRule> rules = input.Rules;
        private readonly Dictionary<Guid, TopologyRouter> routers = input.Routers.ToDictionary(r => r.SensorId);
        private readonly Dictionary<Guid, TopologyHost> hosts = input.Hosts.GroupBy(h => h.HostId).ToDictionary(g => g.Key, g => g.First());
        private readonly Dictionary<Guid, Dictionary<string, RouterPort>> ports = new();
        private readonly Dictionary<Guid, Dictionary<string, RouterPort>> portsAnyCase = new();
        private readonly Dictionary<string, List<(Guid Router, string Port)>> routerMacs = new(StringComparer.Ordinal);
        private readonly HashSet<string> sharedMacs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Guid>> identities = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<HostMac>> hostMacs = new(StringComparer.Ordinal);
        private readonly HashSet<(Guid, string)> physicalAdapters = [];
        private readonly HashSet<(Guid, string)> virtualAdapters = [];
        private readonly Dictionary<(Guid, string), PortRole> roles = new();
        private readonly Dictionary<(Guid, string), HashSet<string>> macsOnPort = new();
        private readonly Dictionary<string, InterfaceResult> interfaces = new(StringComparer.Ordinal);
        private readonly List<Candidate> candidates = [];
        private readonly List<Observation> observations = [];
        private readonly Dictionary<Observation, Wired> wired = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Observation, Guid> skippedBy = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Guid, (int Rows, HashSet<Guid> Routers)> hits = new();
        private readonly Dictionary<(Guid, string), List<Observation>> neighborsOnPort = new();
        private readonly Dictionary<string, List<Observation>> neighborsByMac = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, Dictionary<string, HashSet<string>>> macsByRouter = new();
        private readonly Dictionary<Guid, HashSet<string>> macsOfHost = new();
        private readonly Dictionary<string, List<Guid>> hostsByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(Guid, string), List<TopologyConnection>> connectionsAtPort = new();
        private readonly Dictionary<Observation, Guid?> neighborRouter = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, List<TopologyConnection>> connectionsByDevices = new(StringComparer.Ordinal);
        private readonly HashSet<Guid> connectedHosts = [];

        private static string DevicePair(ConnectionEnd a, ConnectionEnd b) =>
            string.CompareOrdinal(a.Device, b.Device) <= 0 ? $"{a.Device}|{b.Device}" : $"{b.Device}|{a.Device}";

        /// <summary>A host's MAC: the adapter it is the end of (null when the host's adapters leave it open), and for the match the first name.</summary>
        private sealed record HostMac(Guid HostId, string? Interface, long? SpeedBps, bool Manual, Guid? SensorId, bool Virtual = false, string? Kind = null,
            string? Shown = null);

        private sealed record Candidate(ConnectionEnd A, ConnectionEnd B, string? Type, Confidence Confidence, string Rule, string Text, string Note, Origin Origin);

        public TopologyResult Execute()
        {
            Index();
            Roles();
            Interfaces();
            RouterLinks();
            HostLinks();
            Finish();
            foreach (var rule in rules)
            {
                if (filter)
                {
                    var (rows, on) = hits.GetValueOrDefault(rule.Id, (0, []));
                    result.Hits.Add(new RuleHit(rule.Id, rows, on.Count));
                }
            }
            return result;
        }

        // --- indexes ---------------------------------------------------------------------------------------------------

        private void Index()
        {
            foreach (var port in input.Ports.Where(p => routers.ContainsKey(p.RouterId)).OrderBy(p => p.RouterId).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!ports.TryGetValue(port.RouterId, out var byName))
                {
                    ports[port.RouterId] = byName = new Dictionary<string, RouterPort>(StringComparer.Ordinal);
                    portsAnyCase[port.RouterId] = new Dictionary<string, RouterPort>(StringComparer.OrdinalIgnoreCase);
                }
                byName[port.Name] = port;
                portsAnyCase[port.RouterId].TryAdd(port.Name, port);
                if (port.Mac is { } mac)
                {
                    Add(routerMacs, mac, (port.RouterId, port.Name));
                    if (string.Equals(port.Type, "vrrp", StringComparison.OrdinalIgnoreCase) || Mac.IsVrrp(mac))
                    {
                        sharedMacs.Add(mac);
                    }
                }
            }
            foreach (var o in input.Observations.Where(o => routers.ContainsKey(o.RouterId)))
            {
                var w = WireOf(o);
                if (rules.Count > 0 && o.Source is not (ObservationSource.Address or ObservationSource.WireGuardInterface))
                {
                    var names = Names(o.Interface, o.Bridge, w.Port, w.Vlan);
                    var router = o.RouterId;
                    var matched = rules.Where(r => r.Matches(o, names, n => Port(router, n)?.Type, w.VlanId)).ToList();
                    if (matched.Count > 0)
                    {
                        if (!filter)
                        {
                            skippedBy[o] = matched[0].Id;
                        }
                        else
                        {
                            foreach (var rule in matched)
                            {
                                var (rows, on) = hits.GetValueOrDefault(rule.Id, (0, []));
                                on.Add(router);
                                hits[rule.Id] = (rows + 1, on);
                            }
                            continue;
                        }
                    }
                }
                observations.Add(o);
                wired[o] = w;
            }
            foreach (var host in hosts.Values)
            {
                Add(hostsByName, host.Name.Trim(), host.HostId);
            }
            foreach (var c in input.Connections)
            {
                Add(connectionsByDevices, DevicePair(c.A, c.B), c);
                foreach (var end in new[] { c.A, c.B }.Where(e => e.SensorId is null))
                {
                    connectedHosts.Add(end.HostId);
                }
                foreach (var end in new[] { c.A, c.B })
                {
                    if (end.SensorId is { } s && Wire(s, end.Interface).Port is { } port)
                    {
                        Add(connectionsAtPort, (s, port.ToLowerInvariant()), c);
                    }
                }
            }
            foreach (var router in input.Routers.Where(r => !string.IsNullOrWhiteSpace(r.Identity)))
            {
                Add(identities, router.Identity!.Trim(), router.SensorId);
            }
            foreach (var adapter in input.Adapters.OrderBy(a => a.HostId).ThenBy(a => a.Adapter, StringComparer.Ordinal))
            {
                if (Mac.Normalize(adapter.Mac) is { } mac && hosts.ContainsKey(adapter.HostId))
                {
                    Add(hostMacs, mac, new HostMac(adapter.HostId, adapter.Adapter, adapter.SpeedBps, false, adapter.SensorId, adapter.Virtual, adapter.Kind));
                    (adapter.Virtual ? virtualAdapters : physicalAdapters).Add((adapter.HostId, adapter.Adapter));
                }
            }
            foreach (var assignment in input.Assignments)
            {
                if (Mac.Normalize(assignment.Mac) is { } mac && hosts.ContainsKey(assignment.HostId))
                {
                    // A person's assignment replaces whatever the agents say about the MAC.
                    hostMacs[mac] = [new HostMac(assignment.HostId, assignment.Interface, null, true, null)];
                }
            }
            // One owner per host (§3.5): a VLAN sub-interface, a bridge and a bond's members carry their parent's MAC.
            foreach (var mac in hostMacs.Keys.ToList())
            {
                if (hostMacs[mac].Count > 1)
                {
                    hostMacs[mac] = hostMacs[mac].GroupBy(h => h.HostId).Select(g => OneAdapter(g.ToList())).ToList();
                }
            }
            foreach (var o in observations.Where(o => o.Source == ObservationSource.BridgeHost && o.Mac is not null))
            {
                if (PortOf(o) is not { } port)
                {
                    continue;
                }
                if (!macsOnPort.TryGetValue((o.RouterId, port), out var set))
                {
                    macsOnPort[(o.RouterId, port)] = set = new HashSet<string>(StringComparer.Ordinal);
                    if (!macsByRouter.TryGetValue(o.RouterId, out var byPort))
                    {
                        macsByRouter[o.RouterId] = byPort = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                    }
                    byPort[port] = set;
                }
                set.Add(o.Mac!);
            }
            foreach (var o in observations.Where(o => o.Source == ObservationSource.Neighbor))
            {
                if (PortOf(o) is { } port)
                {
                    Add(neighborsOnPort, (o.RouterId, port), o);
                }
                if (o.Mac is not null)
                {
                    Add(neighborsByMac, o.Mac, o);
                }
            }
            foreach (var (mac, owners) in hostMacs)
            {
                if (owners.Count == 1)
                {
                    if (!macsOfHost.TryGetValue(owners[0].HostId, out var set))
                    {
                        macsOfHost[owners[0].HostId] = set = new HashSet<string>(StringComparer.Ordinal);
                    }
                    set.Add(mac);
                }
            }
        }

        /// <summary>A host's adapters with one MAC: the one physical adapter, else the bond, else none (§3.5).</summary>
        private static HostMac OneAdapter(List<HostMac> adapters)
        {
            if (adapters.Count == 1)
            {
                return adapters[0];
            }
            var physical = adapters.Where(a => !a.Virtual).ToList();
            var bonds = adapters.Where(a => string.Equals(a.Kind, "Bond", StringComparison.OrdinalIgnoreCase)).ToList();
            var chosen = physical.Count == 1 ? physical[0] : bonds.Count == 1 ? bonds[0] : null;
            return chosen ?? adapters[0] with { Interface = null, SpeedBps = null, Shown = adapters[0].Interface };
        }

        private static IReadOnlyList<string> Names(params string?[] names) =>
            names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.Ordinal).ToList();

        private IReadOnlyList<Observation> NeighborsOn(Guid router, string? port) =>
            port is not null && neighborsOnPort.TryGetValue((router, port), out var list) ? list : [];

        private static void Add<TK, TV>(Dictionary<TK, List<TV>> map, TK key, TV value) where TK : notnull
        {
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }
            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }

        /// <summary>The one other known router a MAC belongs to, or null. VRRP's MACs are shared by design and belong to none.</summary>
        private Guid? RouterOfMac(string? mac, Guid except)
        {
            if (mac is null || sharedMacs.Contains(mac) || !routerMacs.TryGetValue(mac, out var owners))
            {
                return null;
            }
            var others = owners.Select(o => o.Router).Where(r => r != except).Distinct().ToList();
            return others.Count == 1 && owners.All(o => o.Router == others[0] || o.Router == except) ? others[0] : null;
        }

        /// <summary>The physical port of a router that has the MAC: a VLAN shares its parent's MAC; a bridge's gives none.</summary>
        private string? MacPort(Guid router, string? mac)
        {
            if (mac is null || sharedMacs.Contains(mac) || !routerMacs.TryGetValue(mac, out var owners))
            {
                return null;
            }
            var name = owners.Where(o => o.Router == router).Select(o => o.Port)
                .OrderBy(p => NoPortTypes.Contains(Port(router, p)?.Type ?? "") ? 1 : 0).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            return Wire(router, name).Port;
        }

        /// <summary>A neighbor resolved to another known router: by its MAC, else by a unique identity.</summary>
        private Guid? NeighborRouter(Observation neighbor)
        {
            if (neighborRouter.TryGetValue(neighbor, out var cached))
            {
                return cached;
            }
            Guid? found = null;
            if (RouterOfMac(neighbor.Mac, neighbor.RouterId) is { } byMac)
            {
                found = byMac;
            }
            else if (neighbor.Identity is { Length: > 0 } identity && identities.TryGetValue(identity.Trim(), out var named) && named.Count == 1 && named[0] != neighbor.RouterId)
            {
                found = named[0];
            }
            neighborRouter[neighbor] = found;
            return found;
        }

        private RouterPort? Port(Guid router, string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || !ports.TryGetValue(router, out var byName))
            {
                return null;
            }
            return byName.TryGetValue(name, out var port) || byName.TryGetValue(name.Trim(), out port) || portsAnyCase[router].TryGetValue(name.Trim(), out port)
                ? port : null;
        }

        /// <summary>
        /// The physical port under an interface (plan §3.1): a VLAN's parent, recursively; a bridge's member that the row names,
        /// else none; a tunnel or any other port itself; a name the router has no port for, itself. The VLAN as printed stays.
        /// </summary>
        private Wired Wire(Guid router, string? name, string? member = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return default;
            }
            var current = name.Trim();
            string? vlan = null;
            int? vlanId = null;
            for (var step = 0; step < 6; step++)
            {
                var port = Port(router, current);
                if (port is null)
                {
                    // "bridge/ether2": a bridge's port, as interface-name may say it.
                    var slash = current.LastIndexOf('/');
                    if (slash > 0 && slash < current.Length - 1 && Port(router, current[(slash + 1)..]) is { } inner)
                    {
                        current = inner.Name;
                        continue;
                    }
                    return new Wired(current, vlan, vlanId);
                }
                var type = port.Type ?? "";
                if (string.Equals(type, "vlan", StringComparison.OrdinalIgnoreCase) || port.VlanParent is not null)
                {
                    vlan ??= port.Name;
                    vlanId ??= port.VlanId;
                    if (port.VlanParent is not { Length: > 0 } parent)
                    {
                        return new Wired(null, vlan, vlanId);
                    }
                    current = parent;
                    continue;
                }
                if (string.Equals(type, "bridge", StringComparison.OrdinalIgnoreCase))
                {
                    var m = member is null ? null : Port(router, member);
                    return new Wired(m is not null && !NoPortTypes.Contains(m.Type ?? "") ? m.Name : null, vlan, vlanId);
                }
                if (string.Equals(type, "vrrp", StringComparison.OrdinalIgnoreCase))
                {
                    return new Wired(null, vlan, vlanId);
                }
                return new Wired(port.Name, vlan, vlanId);
            }
            return new Wired(null, vlan, vlanId);
        }

        /// <summary>A row's place on the wire: each table names its interface its own way.</summary>
        private Wired WireOf(Observation o)
        {
            switch (o.Source)
            {
                case ObservationSource.BridgeHost:
                {
                    var w = Wire(o.RouterId, o.Interface);
                    return w with { VlanId = w.VlanId ?? o.VlanId };
                }
                case ObservationSource.Neighbor:
                {
                    // "ether2,vlan10": the port first, the VLAN last.
                    var w = Wire(o.RouterId, o.Interface);
                    if (w.Vlan is null && Port(o.RouterId, o.Bridge) is { } via && (via.VlanParent is not null || string.Equals(via.Type, "vlan", StringComparison.OrdinalIgnoreCase)))
                    {
                        w = w with { Vlan = via.Name, VlanId = via.VlanId };
                    }
                    return w;
                }
                case ObservationSource.Arp or ObservationSource.DhcpLease:
                {
                    // The parser moved a VLAN's entries to its parent, the VLAN's name into Bridge.
                    var w = Wire(o.RouterId, o.Interface);
                    return o.VlanId is { } id ? w with { Vlan = w.Vlan ?? o.Bridge, VlanId = w.VlanId ?? id } : w;
                }
                default:
                    return new Wired(o.Interface, null, null);
            }
        }

        private string? PortOf(Observation o) => wired.TryGetValue(o, out var w) ? w.Port : o.Interface;

        private Wired WiredOf(Observation o) => wired.TryGetValue(o, out var w) ? w : new Wired(o.Interface, null, null);

        private string? TypeOf(Guid router, string? port) => Port(router, port)?.DetectedType;

        private string RouterName(Guid id) => routers.TryGetValue(id, out var r) ? r.Name : "?";

        private string HostName(Guid id) => hosts.TryGetValue(id, out var h) ? h.Name : "?";

        private ConnectionEnd RouterEnd(Guid router, string? port) => new(routers[router].HostId, router, port);

        private static string On(string? port) => port is null ? "" : $" on {port}";

        // --- port roles (§5.2) -----------------------------------------------------------------------------------------

        private void Roles()
        {
            var names = new SortedSet<(Guid, string)>();
            foreach (var (router, byName) in ports)
            {
                foreach (var name in byName.Keys)
                {
                    names.Add((router, name));
                }
            }
            foreach (var o in observations.Where(o => o.Source is ObservationSource.BridgeHost or ObservationSource.Neighbor))
            {
                if (PortOf(o) is { } port)
                {
                    names.Add((o.RouterId, port));
                }
            }
            foreach (var (router, name) in names)
            {
                var role = PortRole.Edge;
                if (Port(router, name) is { Type: { } type } && VirtualTypes.Contains(type))
                {
                    role = PortRole.Virtual;
                }
                else if (NeighborsOn(router, name).Any(o => NeighborRouter(o) is not null)
                    || (macsOnPort.TryGetValue((router, name), out var macs) && macs.Any(m => RouterOfMac(m, router) is not null)))
                {
                    role = PortRole.ToRouter;
                }
                else if (NeighborsOn(router, name).Count > 0)
                {
                    role = PortRole.ToNeighbor;
                }
                roles[(router, name)] = role;
                result.Roles.Add(new PortRoleResult(router, name, role, macsOnPort.TryGetValue((router, name), out var count) ? count.Count : 0));
            }
        }

        private PortRole RoleOf(Guid router, string? port) => port is not null && roles.TryGetValue((router, port), out var role) ? role : PortRole.Edge;

        /// <summary>A port that can be a connection's end: not a bridge or VLAN, so the ARP entry there is on the wire.</summary>
        private bool IsWire(Guid router, string? port) => Port(router, port) is { } p && !NoPortTypes.Contains(p.Type ?? "");

        // --- interfaces (§6) -------------------------------------------------------------------------------------------

        private sealed record Attachment(Guid Router, string Port, int? Vlan, InterfaceSources Via, bool Conflict, string? Note, Observation Row);

        private readonly Dictionary<string, Attachment?> attachments = new(StringComparer.Ordinal);

        private void Interfaces()
        {
            var seen = observations
                .Where(o => o.Mac is not null && o.Source is ObservationSource.BridgeHost or ObservationSource.Arp or ObservationSource.DhcpLease
                    or ObservationSource.Neighbor or ObservationSource.WifiClient)
                .GroupBy(o => o.Mac!, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var group in seen)
            {
                var mac = group.Key;
                var list = group.ToList();
                var random = Mac.IsRandom(mac);
                var sources = InterfaceSources.None;
                foreach (var o in list)
                {
                    sources |= o.Source switch
                    {
                        ObservationSource.BridgeHost => InterfaceSources.MacTable,
                        ObservationSource.Arp => InterfaceSources.Arp,
                        ObservationSource.DhcpLease => InterfaceSources.Dhcp,
                        ObservationSource.Neighbor => InterfaceSources.Neighbor,
                        ObservationSource.WifiClient => InterfaceSources.Wifi,
                        _ => InterfaceSources.None,
                    };
                }
                var neighbor = list.Where(o => o.Source == ObservationSource.Neighbor).OrderByDescending(o => o.SeenAt).FirstOrDefault();
                var lease = list.Where(o => o.Source == ObservationSource.DhcpLease).OrderByDescending(o => o.SeenAt).FirstOrDefault();
                var attachment = Attach(mac, list);
                attachments[mac] = attachment;

                var (kind, hostId, sensorId, iface) = Match(mac, random);
                Guid? hint = null;
                if (kind == MatchKind.None && lease?.HostName is { Length: > 0 } dhcpName)
                {
                    var named = (hostsByName.GetValueOrDefault(dhcpName.Trim()) ?? []).Concat(hostsByName.GetValueOrDefault(dhcpName.Split('.')[0].Trim()) ?? [])
                        .Distinct().ToList();
                    hint = named.Count == 1 ? named[0] : null;
                }
                var item = new InterfaceResult(mac)
                {
                    Ips = list.Where(o => o.Source is ObservationSource.Arp or ObservationSource.DhcpLease && !string.IsNullOrWhiteSpace(o.Ip))
                        .OrderByDescending(o => o.SeenAt).Select(o => o.Ip!.Trim()).Distinct().Take(16).ToList(),
                    HostName = lease?.HostName,
                    Sources = sources,
                    Random = random,
                    AttachedRouterId = attachment?.Router,
                    AttachedPort = attachment?.Port,
                    AttachedVlan = attachment?.Vlan,
                    AttachedNote = attachment?.Note,
                    NeighborIdentity = neighbor?.Identity,
                    NeighborPlatform = neighbor?.Platform,
                    NeighborBoard = neighbor?.Board,
                    NeighborPort = neighbor?.RemoteInterface,
                    Match = kind,
                    MatchHostId = hostId,
                    MatchSensorId = sensorId,
                    MatchInterface = iface,
                    HintHostId = hint,
                    SkippedBy = attachment is not null && skippedBy.TryGetValue(attachment.Row, out var rule) ? rule : null,
                    LastSeenAt = list.Max(o => o.SeenAt),
                };
                interfaces[mac] = item;
                result.Interfaces.Add(item);
            }
        }

        /// <summary>
        /// Where a MAC is attached (§5.2, §6.1): the edge port of a MAC table, newest scan first; else a Wi-Fi interface; else an
        /// ARP entry on a wire port; else a note on what it is behind. Always on the physical port (plan §3.2).
        /// </summary>
        private Attachment? Attach(string mac, List<Observation> list)
        {
            var bridge = list.Where(o => o.Source == ObservationSource.BridgeHost && PortOf(o) is not null).ToList();
            var edge = bridge.Where(o => RoleOf(o.RouterId, PortOf(o)) == PortRole.Edge)
                .OrderByDescending(o => o.SeenAt).ThenBy(o => RouterName(o.RouterId), StringComparer.Ordinal).ThenBy(o => PortOf(o), StringComparer.Ordinal).ToList();
            if (edge.Count > 0)
            {
                var chosen = edge[0];
                var others = edge.Select(o => (o.RouterId, Port: PortOf(o))).Distinct().Where(p => p != (chosen.RouterId, PortOf(chosen))).ToList();
                var note = others.Count > 0 ? "also seen on " + string.Join(", ", others.Select(p => $"{RouterName(p.RouterId)} {p.Port}")) : null;
                return new Attachment(chosen.RouterId, PortOf(chosen)!, WiredOf(chosen).VlanId, InterfaceSources.MacTable, others.Count > 0, note, chosen);
            }
            if (list.Where(o => o.Source == ObservationSource.WifiClient && o.Interface is not null).OrderByDescending(o => o.SeenAt).FirstOrDefault() is { } wifi)
            {
                return new Attachment(wifi.RouterId, wifi.Interface!, null, InterfaceSources.Wifi, false, null, wifi);
            }
            if (bridge.Count == 0 && list.Where(o => o.Source == ObservationSource.Arp && IsWire(o.RouterId, PortOf(o)) && RoleOf(o.RouterId, PortOf(o)) == PortRole.Edge)
                    .OrderByDescending(o => o.SeenAt).FirstOrDefault() is { } arp)
            {
                return new Attachment(arp.RouterId, PortOf(arp)!, WiredOf(arp).VlanId, InterfaceSources.Arp, false, null, arp);
            }
            if (bridge.Where(o => RoleOf(o.RouterId, PortOf(o)) == PortRole.ToNeighbor).OrderByDescending(o => o.SeenAt).FirstOrDefault() is { } behind)
            {
                var who = NeighborsOn(behind.RouterId, PortOf(behind)).Select(o => o.Identity).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)) ?? "a neighbor";
                return new Attachment(behind.RouterId, PortOf(behind)!, WiredOf(behind).VlanId, InterfaceSources.MacTable, false, $"behind {who}", behind);
            }
            if (bridge.Where(o => RoleOf(o.RouterId, PortOf(o)) == PortRole.ToRouter).OrderByDescending(o => o.SeenAt).FirstOrDefault() is { } uplink)
            {
                var far = macsOnPort.TryGetValue((uplink.RouterId, PortOf(uplink)!), out var macs)
                    ? macs.Select(m => RouterOfMac(m, uplink.RouterId)).OfType<Guid>().Distinct().ToList() : [];
                return new Attachment(uplink.RouterId, PortOf(uplink)!, WiredOf(uplink).VlanId, InterfaceSources.MacTable, false,
                    far.Count == 1 ? $"behind {RouterName(far[0])}" : "behind a known router", uplink);
            }
            return null;
        }

        private (MatchKind, Guid?, Guid?, string?) Match(string mac, bool random)
        {
            if (hostMacs.TryGetValue(mac, out var assigned) && assigned.Count == 1 && assigned[0].Manual)
            {
                return (MatchKind.Manual, assigned[0].HostId, null, assigned[0].Interface);
            }
            if (random)
            {
                return (MatchKind.None, null, null, null);
            }
            var devices = new List<(MatchKind Kind, Guid HostId, Guid? SensorId, string? Interface)>();
            if (routerMacs.TryGetValue(mac, out var owners))
            {
                foreach (var router in owners.Select(o => o.Router).Distinct())
                {
                    var name = owners.Where(o => o.Router == router).Select(o => o.Port)
                        .OrderBy(p => Port(router, p)?.Type == "bridge" ? 1 : 0).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault();
                    devices.Add((MatchKind.RouterPort, routers[router].HostId, router, name));
                }
            }
            if (hostMacs.TryGetValue(mac, out var adapters))
            {
                foreach (var a in adapters.Where(a => !a.Manual).GroupBy(a => a.HostId))
                {
                    devices.Add((MatchKind.ServerAdapter, a.Key, a.First().SensorId, a.First().Interface ?? a.First().Shown));
                }
            }
            return devices.Count switch
            {
                0 => (MatchKind.None, null, null, null),
                1 => (devices[0].Kind, devices[0].HostId, devices[0].SensorId, devices[0].Interface),
                _ => (MatchKind.Ambiguous, null, null, null),
            };
        }

        // --- router ↔ router (§5.3) ------------------------------------------------------------------------------------

        private void RouterLinks()
        {
            var neighbors = observations.Where(o => o.Source == ObservationSource.Neighbor && o.Interface is not null).ToList();
            var byRouter = neighbors.GroupBy(o => o.RouterId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var n in neighbors.OrderBy(o => o.RouterId).ThenBy(o => o.Interface, StringComparer.Ordinal))
            {
                if (NeighborRouter(n) is not { } b)
                {
                    continue;
                }
                var a = n.RouterId;
                var near = WiredOf(n);
                var reverse = (byRouter.GetValueOrDefault(b) ?? []).Where(o => NeighborRouter(o) == a).OrderBy(o => o.Interface, StringComparer.Ordinal).ToList();
                // The far router's own view of its port wins. Both sides compare on the wire: its VLAN entry names this port too.
                var reverseHere = reverse.FirstOrDefault(r => r.RemoteInterface is null || Wire(a, r.RemoteInterface).Port is not { } back
                    || (near.Port is not null && string.Equals(back, near.Port, StringComparison.OrdinalIgnoreCase)));
                var remote = Wire(b, n.RemoteInterface).Port;
                var bPort = (reverseHere is not null ? PortOf(reverseHere) : null) ?? remote ?? MacPort(b, n.Mac);
                var confidence = reverseHere is not null ? Confidence.High : Confidence.Medium;
                var text = $"{RouterName(a)} sees {RouterName(b)}{On(near.Port)} by {n.DiscoveredBy ?? "neighbor discovery"}"
                    + (remote is not null ? $" (its port {remote})" : "")
                    + (reverseHere is not null ? $", and {RouterName(b)} sees {RouterName(a)}{On(PortOf(reverseHere))}" : "");
                var (type, note) = CombineTypes(a, near.Port, b, bPort);
                Propose(RouterEnd(a, near.Port), RouterEnd(b, bPort), type, confidence, "neighbor", text,
                    new Origin(a, n.Interface, near.VlanId, near.Vlan, n.DiscoveredBy), note, n.Interface, n.RemoteInterface);
            }

            // WireGuard: a peer's public key is another router's interface key.
            var ownKeys = observations.Where(o => o.Source == ObservationSource.WireGuardInterface && !string.IsNullOrWhiteSpace(o.RemoteKey))
                .GroupBy(o => o.RemoteKey!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var p in observations.Where(o => o.Source == ObservationSource.WireGuardPeer && !string.IsNullOrWhiteSpace(o.RemoteKey)))
            {
                if (ownKeys.TryGetValue(p.RemoteKey!, out var own) && own.RouterId != p.RouterId)
                {
                    Propose(RouterEnd(p.RouterId, p.Interface), RouterEnd(own.RouterId, own.Interface), ConnectionTypes.WireGuard, Confidence.High, "wireguard",
                        $"{RouterName(p.RouterId)}'s WireGuard peer on {p.Interface} is {RouterName(own.RouterId)}'s interface {own.Interface}",
                        new Origin(p.RouterId, p.Interface));
                }
            }

            // Tunnels: a remote address is another router's address.
            var addresses = observations.Where(o => o.Source == ObservationSource.Address && o.Ip is not null)
                .Select(o => (o.RouterId, Ip: o.Ip!.Split('/')[0].Trim())).ToList();
            foreach (var t in observations.Where(o => o.Source == ObservationSource.Tunnel && !string.IsNullOrWhiteSpace(o.RemoteKey)))
            {
                var owners = addresses.Where(a => a.Ip == t.RemoteKey!.Trim() && a.RouterId != t.RouterId).Select(a => a.RouterId).Distinct().ToList();
                if (owners.Count != 1)
                {
                    continue;
                }
                var b = owners[0];
                var mine = addresses.Where(a => a.RouterId == t.RouterId).Select(a => a.Ip).ToHashSet(StringComparer.Ordinal);
                var back = observations.Where(o => o.RouterId == b && o.Source == ObservationSource.Tunnel && o.RemoteKey is not null && mine.Contains(o.RemoteKey.Trim()))
                    .OrderBy(o => o.Note == t.Note ? 0 : 1).ThenBy(o => o.Interface, StringComparer.Ordinal).FirstOrDefault();
                Propose(RouterEnd(t.RouterId, t.Interface), RouterEnd(b, back?.Interface), ConnectionTypes.Tunnel, Confidence.Medium, "tunnel",
                    $"{RouterName(t.RouterId)}'s {(t.Note ?? "tunnel").ToUpperInvariant()} {t.Interface} goes to {t.RemoteKey}, an address of {RouterName(b)}",
                    new Origin(t.RouterId, t.Interface));
            }

            // The MAC table: a port that learns the MACs of exactly one other router.
            foreach (var ((a, port), macs) in macsOnPort.OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2, StringComparer.Ordinal))
            {
                var others = macs.Select(m => RouterOfMac(m, a)).OfType<Guid>().Distinct().ToList();
                if (others.Count != 1 || !routers.ContainsKey(a))
                {
                    continue;
                }
                var b = others[0];
                var aMacs = ports.TryGetValue(a, out var aPorts)
                    ? aPorts.Values.Select(p => p.Mac).OfType<string>().Where(m => !sharedMacs.Contains(m)).ToHashSet(StringComparer.Ordinal) : [];
                var backPorts = (macsByRouter.GetValueOrDefault(b) ?? []).Where(p => p.Value.Overlaps(aMacs)).Select(p => p.Key).Distinct().ToList();
                var bPort = backPorts.Count == 1 ? backPorts[0] : null;
                var (type, note) = CombineTypes(a, port, b, bPort);
                Propose(RouterEnd(a, port), RouterEnd(b, bPort), type, Confidence.Medium, "mac-table",
                    $"{RouterName(a)} learns {RouterName(b)}'s MAC on {port}" + (bPort is not null ? $", and {RouterName(b)} learns {RouterName(a)}'s on {bPort}" : ""),
                    new Origin(a, port), note, port, bPort);
            }

            // ARP on a wire port (a routed link): the other router's MAC.
            foreach (var arp in observations.Where(o => o.Source == ObservationSource.Arp && o.Mac is not null && IsWire(o.RouterId, PortOf(o))))
            {
                if (RouterOfMac(arp.Mac, arp.RouterId) is not { } b)
                {
                    continue;
                }
                var at = WiredOf(arp);
                var bPort = MacPort(b, arp.Mac);
                var (type, note) = CombineTypes(arp.RouterId, at.Port, b, bPort);
                Propose(RouterEnd(arp.RouterId, at.Port), RouterEnd(b, bPort), type, Confidence.Medium, "arp",
                    $"{RouterName(arp.RouterId)} has {RouterName(b)}'s address {arp.Ip} in its ARP table on {at.Port}",
                    new Origin(arp.RouterId, at.Vlan ?? arp.Interface, at.VlanId, at.Vlan), note, at.Vlan ?? arp.Interface, bPort);
            }
        }

        /// <summary>The proposed type of a router-to-router link: each side's, and the lower speed when they differ.</summary>
        private (string? Type, string Note) CombineTypes(Guid a, string? aPort, Guid b, string? bPort)
        {
            var ta = TypeOf(a, aPort);
            var tb = TypeOf(b, bPort);
            if (ta is null || tb is null || ta == tb)
            {
                return (ta ?? tb, ta is not null && ta == tb ? $". Both sides {ConnectionTypes.Label(ta)}" : "");
            }
            var sa = ConnectionTypes.Find(ta)?.SpeedMbps ?? int.MaxValue;
            var sb = ConnectionTypes.Find(tb)?.SpeedMbps ?? int.MaxValue;
            return (sa <= sb ? ta : tb, $". {RouterName(a)} says {ConnectionTypes.Label(ta)}, {RouterName(b)} {ConnectionTypes.Label(tb)}");
        }

        // --- router ↔ host (§5.3) --------------------------------------------------------------------------------------

        private void HostLinks()
        {
            foreach (var (mac, owners) in hostMacs.OrderBy(h => h.Key, StringComparer.Ordinal))
            {
                if (owners.Count != 1 || Mac.IsRandom(mac) || input.Ignored.Contains(mac) || routerMacs.ContainsKey(mac))
                {
                    continue;
                }
                var owner = owners[0];
                var host = owner.HostId;
                var hostEnd = new ConnectionEnd(host, null, owner.Interface);

                // A neighbor that is the host: its own port is in the neighbor entry. Through a VLAN, that port is the VLAN's.
                foreach (var n in (neighborsByMac.GetValueOrDefault(mac) ?? []).Where(o => o.Interface is not null))
                {
                    var near = WiredOf(n);
                    var theirs = near.Vlan is not null || near.VlanId is not null || (n.RemoteInterface is { } r && virtualAdapters.Contains((host, r)))
                        ? owner.Interface : n.RemoteInterface ?? owner.Interface;
                    Propose(RouterEnd(n.RouterId, near.Port), hostEnd with { Interface = theirs }, TypeOf(n.RouterId, near.Port),
                        Confidence.High, "neighbor-host",
                        $"{RouterName(n.RouterId)} sees {HostName(host)}{On(near.Port)} by {n.DiscoveredBy ?? "neighbor discovery"}"
                        + (n.RemoteInterface is { } remote && near.Vlan is null && near.VlanId is null ? $" (its port {remote})" : ""),
                        new Origin(n.RouterId, n.Interface, near.VlanId, near.Vlan, n.DiscoveredBy), "", n.Interface);
                }

                if (!attachments.TryGetValue(mac, out var at) || at is null)
                {
                    continue;
                }
                var behindNeighbor = RoleOf(at.Router, at.Port) == PortRole.ToNeighbor;
                if (at.Note?.StartsWith("behind", StringComparison.Ordinal) == true && !behindNeighbor)
                {
                    continue;
                }
                var routerName = RouterName(at.Router);
                var origin = new Origin(at.Router, at.Row.Interface);
                if (at.Via == InterfaceSources.Wifi)
                {
                    Propose(RouterEnd(at.Router, at.Port), hostEnd, ConnectionTypes.Wifi, Confidence.High, "wifi",
                        $"{HostName(host)} ({mac}) is a Wi-Fi client of {routerName} on {at.Port}", origin, "", at.Row.Interface);
                    continue;
                }
                // Behind a known host: the port leads to a switch or hypervisor that a person added, by a connection or as the
                // neighbor seen there. Only for a host without a connection yet: one that has its own isn't guessed elsewhere.
                var leadsTo = (connectionsAtPort.GetValueOrDefault((at.Router, at.Port.Trim().ToLowerInvariant())) ?? [])
                    .Select(c => OtherEnd(c, at.Router, at.Port)).OfType<ConnectionEnd>()
                    .Where(e => e.SensorId is null).Select(e => e.HostId)
                    .Concat(NeighborsOn(at.Router, at.Port).Select(n => n.Mac is { } m && hostMacs.TryGetValue(m, out var o) && o.Count == 1 ? o[0].HostId : (Guid?)null).OfType<Guid>())
                    .Where(h => h != host).Distinct().ToList();
                if (leadsTo.Count == 1)
                {
                    if (!connectedHosts.Contains(host))
                    {
                        Propose(hostEnd, new ConnectionEnd(leadsTo[0], null, null), null, Confidence.Medium, "behind-host",
                            $"{HostName(host)}'s MAC {mac} is learned on {routerName} {at.Port}, which leads to {HostName(leadsTo[0])}", origin);
                    }
                    continue;
                }
                if (behindNeighbor)
                {
                    continue;
                }
                var portMacs = macsOnPort.TryGetValue((at.Router, at.Port), out var set) ? set : new HashSet<string>(StringComparer.Ordinal);
                var mine = macsOfHost.GetValueOrDefault(host) ?? [];
                var alone = portMacs.All(mine.Contains);
                var confidence = at.Conflict ? Confidence.Low : at.Via == InterfaceSources.Arp ? Confidence.Medium : alone ? Confidence.High : Confidence.Medium;
                var type = TypeOf(at.Router, at.Port);
                var text = at.Via == InterfaceSources.Arp
                    ? $"{HostName(host)}'s MAC {mac} is in {routerName}'s ARP table on {at.Port}"
                    : alone
                        ? $"{HostName(host)}'s {(owner.Interface is { } i ? $"adapter {i} " : "")}({mac}) is the only device on {routerName} {at.Port}"
                        : $"{HostName(host)}'s MAC {mac} is learned on {routerName} {at.Port}, with {portMacs.Count - 1} other device(s)";
                if (owner.SpeedBps is > 0 and var bps)
                {
                    var speed = (int)(bps / 1_000_000);
                    var portSpeed = Port(at.Router, at.Port)?.RateMbps;
                    text += portSpeed == speed ? $". Both sides {ConnectionTypes.SpeedLabel(speed)}" : $". The adapter reports {ConnectionTypes.SpeedLabel(speed)}";
                }
                if (at.Note is { } also)
                {
                    text += $" ({also})";
                }
                Propose(RouterEnd(at.Router, at.Port), hostEnd, type, confidence, at.Via == InterfaceSources.Arp ? "arp-host" : "mac-table-host", text, origin,
                    "", at.Row.Interface);
            }
        }

        /// <summary>The far end of a connection that has an end at a router's port (on the wire), or null.</summary>
        private ConnectionEnd? OtherEnd(TopologyConnection c, Guid router, string port)
        {
            bool At(ConnectionEnd e) => e.SensorId == router && Wire(router, e.Interface).Port is { } p && string.Equals(p, port, StringComparison.OrdinalIgnoreCase);
            return At(c.A) ? c.B : At(c.B) ? c.A : null;
        }

        private void Propose(ConnectionEnd a, ConnectionEnd b, string? type, Confidence confidence, string rule, string text, Origin origin, string note = "",
            string? aPrinted = null, string? bPrinted = null)
        {
            if (a.Device == b.Device)
            {
                return;
            }
            // A port a rule skips is no end, also when the evidence is the other router's (plan §4.2).
            if (filter && rules.Count > 0 && (SkippedEnd(a, aPrinted) || SkippedEnd(b, bPrinted)))
            {
                return;
            }
            candidates.Add(new Candidate(a, b, type, confidence, rule, text, note, origin));
        }

        private bool SkippedEnd(ConnectionEnd end, string? printed)
        {
            if (end.SensorId is not { } router)
            {
                return false;
            }
            var names = Names(end.Interface, printed, Wire(router, printed).Vlan);
            return rules.Any(r => r.MatchesEnd(router, names, n => Port(router, n)?.Type));
        }

        // --- merging, connections, differences ------------------------------------------------------------------------

        /// <summary>The candidates of one pair of ends; <see cref="A"/> and <see cref="B"/> are the suggestion's ends.</summary>
        private sealed class Group(string key, ConnectionEnd a, ConnectionEnd b)
        {
            public string Key { get; set; } = key;

            public ConnectionEnd A { get; set; } = a;

            public ConnectionEnd B { get; set; } = b;

            public List<Candidate> Items { get; } = [];

            public bool Folded { get; set; }
        }

        private static int Rank(string rule) => rule switch
        {
            "neighbor" or "neighbor-host" or "wifi" => 0,
            "wireguard" or "tunnel" => 1,
            "mac-table" or "mac-table-host" => 2,
            "arp" or "arp-host" => 3,
            _ => 4,
        };

        private void Finish()
        {
            // One group per pair of ends.
            var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
            foreach (var c in candidates)
            {
                var key = ConnectionEnd.PairKey(c.A, c.B);
                if (!groups.TryGetValue(key, out var group))
                {
                    groups[key] = group = new Group(key, c.A, c.B);
                }
                group.Items.Add(c);
            }

            // One cable per port (plan §3.3): within a device pair, the strongest first; one that shares a router port with it is the same cable.
            foreach (var pair in groups.Values.GroupBy(g => DevicePair(g.A, g.B)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList())
            {
                var taken = new List<Group>();
                foreach (var g in pair.OrderByDescending(g => g.Items.Max(c => c.Confidence)).ThenBy(g => g.Items.Min(c => Rank(c.Rule)))
                             .ThenBy(g => g.Items.Any(c => !c.Origin.Tagged) ? 0 : 1).ThenBy(g => HostEndPhysical(g) ? 0 : 1).ThenBy(g => g.Key, StringComparer.Ordinal))
                {
                    if (taken.FirstOrDefault(t => SharesPort(t, g)) is { } into)
                    {
                        into.Items.AddRange(g.Items);
                        into.Folded = true;
                        groups.Remove(g.Key);
                    }
                    else
                    {
                        taken.Add(g);
                    }
                }
            }
            // The host's end, when the cable's candidates name several adapters: a physical one, else none (§3.5).
            foreach (var g in groups.Values.Where(g => g.Folded).ToList())
            {
                var (hostEnd, other) = g.A.SensorId is null && g.B.SensorId is not null ? (g.A, g.B) : g.B.SensorId is null && g.A.SensorId is not null ? (g.B, g.A) : (null, null);
                // Only an agent's virtual adapter gives way: a port that a switch's LLDP names stays.
                if (hostEnd is null || other is null || hostEnd.Interface is null || !virtualAdapters.Contains((hostEnd.HostId, hostEnd.Interface)))
                {
                    continue;
                }
                var named = g.Items.Select(c => c.A.SensorId is null ? c.A.Interface : c.B.Interface).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (named.Count < 2)
                {
                    continue;
                }
                var chosen = hostEnd with { Interface = named.FirstOrDefault(i => physicalAdapters.Contains((hostEnd.HostId, i))) };
                Rekey(groups, g, g.A == hostEnd ? chosen : g.A, g.B == hostEnd ? chosen : g.B);
            }

            // Then the unspecific ones (an end without its port) go into a specific one.
            var keys = groups.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            foreach (var key in keys)
            {
                if (!groups.TryGetValue(key, out var first))
                {
                    continue;
                }
                var into = keys.Where(k => k != key && groups.ContainsKey(k)).Select(k => groups[k])
                    .FirstOrDefault(other => Covers(first.A, first.B, other.A, other.B) && !Covers(other.A, other.B, first.A, first.B));
                if (into is not null)
                {
                    into.Items.AddRange(first.Items);
                    groups.Remove(key);
                }
            }

            // Device pairs linked already: by neighbor discovery, or by a connection (§3.7).
            var linked = groups.Values.Where(g => g.A.SensorId is not null && g.B.SensorId is not null && g.Items.Any(c => c.Rule == "neighbor"))
                .GroupBy(g => DevicePair(g.A, g.B)).ToDictionary(p => p.Key, p => p.ToList(), StringComparer.Ordinal);

            var evidenced = new HashSet<Guid>();
            foreach (var (key, group) in groups.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                var items = group.Items;
                var confidence = items.Max(c => c.Confidence);
                var own = items.Where(c => ConnectionEnd.PairKey(c.A, c.B) == key).ToList();
                var type = own.Concat(items).Where(c => c.Type is not null).OrderByDescending(c => c.Confidence).Select(c => c.Type).FirstOrDefault();
                var evidence = Sentences(items);

                var covering = (connectionsByDevices.GetValueOrDefault(DevicePair(group.A, group.B)) ?? [])
                    .Where(c => Covers(c.A, c.B, group.A, group.B) || Covers(group.A, group.B, c.A, c.B)).ToList();
                if (covering.Count > 0)
                {
                    foreach (var c in covering)
                    {
                        evidenced.Add(c.Id);
                    }
                    continue;
                }

                // A second path (§3.7): only the MAC table or ARP shows it, and the pair is linked on other ports already.
                if (group.A.SensorId is not null && group.B.SensorId is not null && items.All(c => c.Rule is "mac-table" or "arp"))
                {
                    var pair = DevicePair(group.A, group.B);
                    var byNeighbor = (linked.GetValueOrDefault(pair) ?? []).Any(g => g != group);
                    var byConnection = (connectionsByDevices.GetValueOrDefault(pair) ?? []).Count > 0;
                    if (byNeighbor || byConnection)
                    {
                        confidence = Confidence.Low;
                        evidence.Add(new Evidence("second-path", Confidence.Low,
                            $"{RouterName(group.A.SensorId.Value)} and {RouterName(group.B.SensorId.Value)} are linked on other ports already, by "
                            + (byNeighbor ? "neighbor discovery" : "a connection") + ": this is probably the same network through another device"));
                    }
                }
                result.Suggestions.Add(new SuggestionResult(key, SuggestionKind.Link, group.A, group.B, type, confidence, evidence, null));

                // A connection that says this router port goes elsewhere: a sure neighbor link says otherwise.
                if (confidence == Confidence.High && group.A.SensorId is not null && group.B.SensorId is not null)
                {
                    foreach (var end in new[] { group.A, group.B })
                    {
                        if (end.Interface is null)
                        {
                            continue;
                        }
                        var other = end == group.A ? group.B : group.A;
                        foreach (var c in connectionsAtPort.GetValueOrDefault((end.SensorId!.Value, end.Interface.Trim().ToLowerInvariant())) ?? [])
                        {
                            if (OtherEnd(c, end.SensorId!.Value, end.Interface) is { SensorId: not null } far && far.Device != other.Device)
                            {
                                result.Suggestions.Add(new SuggestionResult($"{key}|differs:{c.Id:N}", SuggestionKind.Differs, group.A, group.B, type, confidence,
                                    evidence, c.Id));
                            }
                        }
                    }
                }
            }
            // A Differs is shown instead of its plain link.
            var differs = result.Suggestions.Where(s => s.Kind == SuggestionKind.Differs).Select(s => s.Key.Split("|differs:")[0]).ToHashSet(StringComparer.Ordinal);
            result.Suggestions.RemoveAll(s => s.Kind == SuggestionKind.Link && differs.Contains(s.Key));

            foreach (var c in input.Connections.OrderBy(c => c.Id))
            {
                var involved = new[] { c.A.SensorId, c.B.SensorId }.OfType<Guid>().Where(routers.ContainsKey).Distinct().ToList();
                result.Checks.Add(new ConnectionCheck(c.Id, evidenced.Contains(c.Id), involved));
            }
        }

        private bool HostEndPhysical(Group g)
        {
            var hostEnd = g.A.SensorId is null ? g.A : g.B.SensorId is null ? g.B : null;
            return hostEnd?.Interface is null || physicalAdapters.Contains((hostEnd.HostId, hostEnd.Interface));
        }

        /// <summary>Whether two groups of one device pair share a router port: the same cable.</summary>
        private static bool SharesPort(Group taken, Group g) =>
            new[] { taken.A, taken.B }.Any(t => t.SensorId is not null && !string.IsNullOrWhiteSpace(t.Interface)
                && new[] { g.A, g.B }.Any(e => e.Device == t.Device && string.Equals(e.Interface?.Trim(), t.Interface.Trim(), StringComparison.OrdinalIgnoreCase)));

        private static void Rekey(Dictionary<string, Group> groups, Group g, ConnectionEnd a, ConnectionEnd b)
        {
            var key = ConnectionEnd.PairKey(a, b);
            if (key == g.Key)
            {
                return;
            }
            groups.Remove(g.Key);
            if (groups.TryGetValue(key, out var existing))
            {
                existing.Items.AddRange(g.Items);
                return;
            }
            (g.Key, g.A, g.B) = (key, a, b);
            groups[key] = g;
        }

        /// <summary>
        /// A suggestion's sentences (plan §3.4): one per rule and text, strongest first. The rows of one rule that came through
        /// VLANs join the sentence of the same rule on the same ports: "…, and on 9 VLANs on that port: vlan10, vlan20, vlan30 and 6 more".
        /// </summary>
        private static List<Evidence> Sentences(List<Candidate> items)
        {
            var lines = new List<Evidence>();
            foreach (var bySpot in items.GroupBy(c => (c.Rule, Spot(c))))
            {
                var untagged = bySpot.Where(c => !c.Origin.Tagged).ToList();
                var tagged = bySpot.Where(c => c.Origin.Tagged && c.Rule is "neighbor" or "neighbor-host" or "arp")
                    .OrderBy(c => c.Origin.VlanId ?? int.MaxValue).ThenBy(c => c.Origin.Vlan, StringComparer.Ordinal).ToList();
                var plain = bySpot.Where(c => c.Origin.Tagged && c.Rule is not ("neighbor" or "neighbor-host" or "arp")).ToList();
                var vlans = tagged.Select(c => c.Origin.VlanLabel).Distinct(StringComparer.Ordinal).ToList();
                var texts = untagged.Concat(plain).GroupBy(c => c.Text + c.Note, StringComparer.Ordinal).Select(g => g.ToList()).ToList();
                if (vlans.Count > 0)
                {
                    var list = string.Join(", ", vlans.Take(3)) + (vlans.Count > 3 ? $" and {vlans.Count - 3} more" : "");
                    var count = vlans.Count == 1 ? "1 VLAN" : $"{vlans.Count} VLANs";
                    if (texts.Count > 0)
                    {
                        var head = texts[0][0];
                        texts.RemoveAt(0);
                        var all = untagged.Where(c => c.Text + c.Note == head.Text + head.Note).Concat(tagged).ToList();
                        lines.Add(Line(head, all, $"{head.Text}, and on {count} on that port: {list}{head.Note}", vlans));
                    }
                    else
                    {
                        var head = tagged[0];
                        lines.Add(Line(head, tagged, $"{head.Text}, on {count}: {list}{head.Note}", vlans));
                    }
                }
                foreach (var same in texts)
                {
                    lines.Add(Line(same[0], same, same[0].Text + same[0].Note, null));
                }
            }
            return lines.OrderByDescending(e => e.Confidence).ThenBy(e => e.Text, StringComparer.Ordinal).ToList();

            static string Spot(Candidate c) =>
                string.Join("|", new[] { c.A, c.B }.Where(e => e.SensorId is not null).Select(e => $"{e.SensorId:N}/{e.Interface?.Trim().ToLowerInvariant()}").Order(StringComparer.Ordinal));

            static Evidence Line(Candidate head, List<Candidate> members, string text, IReadOnlyList<string>? vlans) =>
                new(head.Rule, members.Max(c => c.Confidence), text)
                {
                    RouterId = head.Origin.Router,
                    Interface = head.Origin.Interface,
                    Vlan = head.Origin.VlanId,
                    Vlans = vlans,
                    By = head.Origin.By,
                };
        }

        /// <summary>
        /// Whether the pair (a, b) covers (c, d): the same devices, and each end's interface the same on the wire or unsaid on (a, b).
        /// A connection made on vlan10 covers what the scan shows on the VLAN's port.
        /// </summary>
        private bool Covers(ConnectionEnd a, ConnectionEnd b, ConnectionEnd c, ConnectionEnd d)
        {
            bool End(ConnectionEnd general, ConnectionEnd specific) =>
                general.Device == specific.Device && (Norm(general) is not { } g || string.Equals(g, Norm(specific), StringComparison.OrdinalIgnoreCase));
            return (End(a, c) && End(b, d)) || (End(a, d) && End(b, c));
        }

        private string? Norm(ConnectionEnd e) =>
            string.IsNullOrWhiteSpace(e.Interface) ? null : e.SensorId is { } s ? Wire(s, e.Interface).Port : e.Interface.Trim();
    }
}
