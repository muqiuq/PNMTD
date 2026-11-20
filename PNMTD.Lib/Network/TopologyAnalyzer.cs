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

/// <summary>A server's adapter, from its agent (agent_adapter_macs).</summary>
public sealed record TopologyAdapter(Guid HostId, Guid SensorId, string Mac, string Adapter, long? SpeedBps, bool Virtual);

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
}

public sealed record PortRoleResult(Guid RouterId, string Port, PortRole Role, int MacCount);

/// <summary>One sentence of a suggestion's evidence, the rule that made it, and how sure it is.</summary>
public sealed record Evidence(string Rule, Confidence Confidence, string Text);

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

    public DateTime LastSeenAt { get; init; }
}

/// <summary>A suggestion (§5.3): two ends, the proposed type, the confidence and the evidence.</summary>
public sealed record SuggestionResult(string Key, SuggestionKind Kind, ConnectionEnd A, ConnectionEnd B, string? Type, Confidence Confidence,
    IReadOnlyList<Evidence> Evidence, Guid? DiffersFrom);

/// <summary>Whether the latest scans show a connection (§5.5), and which of its ends' routers were looked at.</summary>
public sealed record ConnectionCheck(Guid ConnectionId, bool Evidenced, IReadOnlyList<Guid> Routers);

public sealed class TopologyResult
{
    public List<PortRoleResult> Roles { get; } = [];

    public List<InterfaceResult> Interfaces { get; } = [];

    public List<SuggestionResult> Suggestions { get; } = [];

    public List<ConnectionCheck> Checks { get; } = [];
}

/// <summary>
/// The analysis (docs/network-map-plan.md §5): port roles, the network interfaces with their matches, the suggestions, and the
/// connections' confirmations, from everything stored. Pure: the same input gives the same output. Every end of a suggestion
/// is a router or a host (decision 6); a MAC that matches no host is an interface, never a node.
/// </summary>
public static class TopologyAnalyzer
{
    private static readonly HashSet<string> VirtualTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "wg", "vlan", "bridge", "eoip", "eoipv6", "gre-tunnel", "gre6-tunnel", "ipip-tunnel", "ipipv6-tunnel", "vxlan", "loopback", "vpls",
        "6to4-tunnel", "l2tp-out", "l2tp-in", "sstp-out", "sstp-in", "pptp-out", "pptp-in", "ovpn-out", "ovpn-in", "pppoe-out", "pppoe-in",
    };

    private static readonly HashSet<string> NoPortTypes = new(StringComparer.OrdinalIgnoreCase) { "bridge", "vlan" };

    public static TopologyResult Analyze(TopologyInput input) => new Run(input).Execute();

    private sealed class Run(TopologyInput input)
    {
        private readonly TopologyResult result = new();
        private readonly Dictionary<Guid, TopologyRouter> routers = input.Routers.ToDictionary(r => r.SensorId);
        private readonly Dictionary<Guid, TopologyHost> hosts = input.Hosts.GroupBy(h => h.HostId).ToDictionary(g => g.Key, g => g.First());
        private readonly Dictionary<Guid, Dictionary<string, RouterPort>> ports = new();
        private readonly Dictionary<string, List<(Guid Router, string Port)>> routerMacs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Guid>> identities = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<HostMac>> hostMacs = new(StringComparer.Ordinal);
        private readonly Dictionary<(Guid, string), PortRole> roles = new();
        private readonly Dictionary<(Guid, string), HashSet<string>> macsOnPort = new();
        private readonly Dictionary<string, InterfaceResult> interfaces = new(StringComparer.Ordinal);
        private readonly List<Candidate> candidates = [];
        private readonly List<Observation> observations = [];
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

        private sealed record HostMac(Guid HostId, string? Interface, long? SpeedBps, bool Manual, Guid? SensorId);

        private sealed record Candidate(ConnectionEnd A, ConnectionEnd B, string? Type, Confidence Confidence, Evidence Evidence,
            SuggestionKind Kind = SuggestionKind.Link, Guid? DiffersFrom = null);

        public TopologyResult Execute()
        {
            Index();
            Roles();
            Interfaces();
            RouterLinks();
            HostLinks();
            Finish();
            return result;
        }

        // --- indexes ---------------------------------------------------------------------------------------------------

        private void Index()
        {
            observations.AddRange(input.Observations.Where(o => routers.ContainsKey(o.RouterId)));
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
                    if (end.SensorId is { } s && !string.IsNullOrWhiteSpace(end.Interface))
                    {
                        Add(connectionsAtPort, (s, end.Interface.Trim().ToLowerInvariant()), c);
                    }
                }
            }
            foreach (var port in input.Ports.Where(p => routers.ContainsKey(p.RouterId)).OrderBy(p => p.RouterId).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!ports.TryGetValue(port.RouterId, out var byName))
                {
                    ports[port.RouterId] = byName = new Dictionary<string, RouterPort>(StringComparer.Ordinal);
                }
                byName[port.Name] = port;
                if (port.Mac is { } mac)
                {
                    Add(routerMacs, mac, (port.RouterId, port.Name));
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
                    Add(hostMacs, mac, new HostMac(adapter.HostId, adapter.Adapter, adapter.SpeedBps, false, adapter.SensorId));
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
            foreach (var o in observations.Where(o => o.Source == ObservationSource.BridgeHost && o.Interface is not null && o.Mac is not null))
            {
                if (!macsOnPort.TryGetValue((o.RouterId, o.Interface!), out var set))
                {
                    macsOnPort[(o.RouterId, o.Interface!)] = set = new HashSet<string>(StringComparer.Ordinal);
                    if (!macsByRouter.TryGetValue(o.RouterId, out var byPort))
                    {
                        macsByRouter[o.RouterId] = byPort = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                    }
                    byPort[o.Interface!] = set;
                }
                set.Add(o.Mac!);
            }
            foreach (var o in observations.Where(o => o.Source == ObservationSource.Neighbor))
            {
                if (o.Interface is not null)
                {
                    Add(neighborsOnPort, (o.RouterId, o.Interface), o);
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

        /// <summary>The one other known router a MAC belongs to, or null.</summary>
        private Guid? RouterOfMac(string? mac, Guid except)
        {
            if (mac is null || !routerMacs.TryGetValue(mac, out var owners))
            {
                return null;
            }
            var others = owners.Select(o => o.Router).Where(r => r != except).Distinct().ToList();
            return others.Count == 1 && owners.All(o => o.Router == others[0] || o.Router == except) ? others[0] : null;
        }

        private string? PortOfMac(Guid router, string? mac) =>
            mac is not null && routerMacs.TryGetValue(mac, out var owners)
                ? owners.Where(o => o.Router == router).Select(o => o.Port).OrderBy(p => ports[router][p].Type == "bridge" ? 1 : 0).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault()
                : null;

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

        private RouterPort? Port(Guid router, string? name) =>
            name is not null && ports.TryGetValue(router, out var byName) && byName.TryGetValue(name, out var port) ? port : null;

        private string? TypeOf(Guid router, string? port) => Port(router, port)?.DetectedType;

        private string RouterName(Guid id) => routers.TryGetValue(id, out var r) ? r.Name : "?";

        private string HostName(Guid id) => hosts.TryGetValue(id, out var h) ? h.Name : "?";

        private ConnectionEnd RouterEnd(Guid router, string? port) => new(routers[router].HostId, router, port);

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
            foreach (var o in observations.Where(o => o.Interface is not null && o.Source is ObservationSource.BridgeHost or ObservationSource.Neighbor))
            {
                names.Add((o.RouterId, o.Interface!));
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

        private sealed record Attachment(Guid Router, string Port, int? Vlan, InterfaceSources Via, bool Conflict, string? Note);

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
                    LastSeenAt = list.Max(o => o.SeenAt),
                };
                interfaces[mac] = item;
                result.Interfaces.Add(item);
            }
        }

        /// <summary>
        /// Where a MAC is attached (§5.2, §6.1): the edge port of a MAC table, newest scan first; else a Wi-Fi interface; else an
        /// ARP entry on a wire port; else a note on what it is behind.
        /// </summary>
        private Attachment? Attach(string mac, List<Observation> list)
        {
            var bridge = list.Where(o => o.Source == ObservationSource.BridgeHost && o.Interface is not null).ToList();
            var edge = bridge.Where(o => RoleOf(o.RouterId, o.Interface) == PortRole.Edge)
                .OrderByDescending(o => o.SeenAt).ThenBy(o => RouterName(o.RouterId), StringComparer.Ordinal).ThenBy(o => o.Interface, StringComparer.Ordinal).ToList();
            if (edge.Count > 0)
            {
                var chosen = edge[0];
                var others = edge.Select(o => (o.RouterId, o.Interface)).Distinct().Where(p => p != (chosen.RouterId, chosen.Interface)).ToList();
                var note = others.Count > 0 ? "also seen on " + string.Join(", ", others.Select(p => $"{RouterName(p.RouterId)} {p.Interface}")) : null;
                return new Attachment(chosen.RouterId, chosen.Interface!, chosen.VlanId, InterfaceSources.MacTable, others.Count > 0, note);
            }
            if (list.Where(o => o.Source == ObservationSource.WifiClient && o.Interface is not null).OrderByDescending(o => o.SeenAt).FirstOrDefault() is { } wifi)
            {
                return new Attachment(wifi.RouterId, wifi.Interface!, null, InterfaceSources.Wifi, false, null);
            }
            if (bridge.Count == 0 && list.Where(o => o.Source == ObservationSource.Arp && IsWire(o.RouterId, o.Interface) && RoleOf(o.RouterId, o.Interface) == PortRole.Edge)
                    .OrderByDescending(o => o.SeenAt).FirstOrDefault() is { } arp)
            {
                return new Attachment(arp.RouterId, arp.Interface!, arp.VlanId, InterfaceSources.Arp, false, null);
            }
            if (bridge.Where(o => RoleOf(o.RouterId, o.Interface) == PortRole.ToNeighbor).OrderByDescending(o => o.SeenAt).FirstOrDefault() is { } behind)
            {
                var who = NeighborsOn(behind.RouterId, behind.Interface).Select(o => o.Identity).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)) ?? "a neighbor";
                return new Attachment(behind.RouterId, behind.Interface!, behind.VlanId, InterfaceSources.MacTable, false, $"behind {who}");
            }
            if (bridge.Where(o => RoleOf(o.RouterId, o.Interface) == PortRole.ToRouter).OrderByDescending(o => o.SeenAt).FirstOrDefault() is { } uplink)
            {
                var far = macsOnPort.TryGetValue((uplink.RouterId, uplink.Interface!), out var macs)
                    ? macs.Select(m => RouterOfMac(m, uplink.RouterId)).OfType<Guid>().Distinct().ToList() : [];
                return new Attachment(uplink.RouterId, uplink.Interface!, uplink.VlanId, InterfaceSources.MacTable, false,
                    far.Count == 1 ? $"behind {RouterName(far[0])}" : "behind a known router");
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
                    devices.Add((MatchKind.RouterPort, routers[router].HostId, router, PortOfMac(router, mac)));
                }
            }
            if (hostMacs.TryGetValue(mac, out var adapters))
            {
                foreach (var a in adapters.Where(a => !a.Manual).GroupBy(a => a.HostId))
                {
                    devices.Add((MatchKind.ServerAdapter, a.Key, a.First().SensorId, a.First().Interface));
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
                var reverse = (byRouter.GetValueOrDefault(b) ?? []).Where(o => NeighborRouter(o) == a).OrderBy(o => o.Interface, StringComparer.Ordinal).ToList();
                // The far router's own view of its port wins: interface-name may name its bridge.
                var reverseHere = reverse.FirstOrDefault(r => r.RemoteInterface is null || r.RemoteInterface == n.Interface || Port(a, r.RemoteInterface)?.Type == "bridge");
                var bPort = reverseHere?.Interface ?? (Port(b, n.RemoteInterface) is { Type: not "bridge" } ? n.RemoteInterface : null) ?? NonBridge(b, PortOfMac(b, n.Mac));
                var confidence = reverseHere is not null ? Confidence.High : Confidence.Medium;
                var text = $"{RouterName(a)} sees {RouterName(b)} on {n.Interface} by {n.DiscoveredBy ?? "neighbor discovery"}"
                    + (n.RemoteInterface is { } remote ? $" (its port {remote})" : "")
                    + (reverseHere is not null ? $", and {RouterName(b)} sees {RouterName(a)} on {reverseHere.Interface}" : "");
                var (type, note) = CombineTypes(a, n.Interface, b, bPort);
                Propose(RouterEnd(a, n.Interface), RouterEnd(b, bPort), type, confidence, "neighbor", text + note);
            }

            // WireGuard: a peer's public key is another router's interface key.
            var ownKeys = observations.Where(o => o.Source == ObservationSource.WireGuardInterface && !string.IsNullOrWhiteSpace(o.RemoteKey))
                .GroupBy(o => o.RemoteKey!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var p in observations.Where(o => o.Source == ObservationSource.WireGuardPeer && !string.IsNullOrWhiteSpace(o.RemoteKey)))
            {
                if (ownKeys.TryGetValue(p.RemoteKey!, out var own) && own.RouterId != p.RouterId)
                {
                    Propose(RouterEnd(p.RouterId, p.Interface), RouterEnd(own.RouterId, own.Interface), ConnectionTypes.WireGuard, Confidence.High, "wireguard",
                        $"{RouterName(p.RouterId)}'s WireGuard peer on {p.Interface} is {RouterName(own.RouterId)}'s interface {own.Interface}");
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
                    $"{RouterName(t.RouterId)}'s {(t.Note ?? "tunnel").ToUpperInvariant()} {t.Interface} goes to {t.RemoteKey}, an address of {RouterName(b)}");
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
                var aMacs = ports.TryGetValue(a, out var aPorts) ? aPorts.Values.Select(p => p.Mac).OfType<string>().ToHashSet(StringComparer.Ordinal) : [];
                var backPorts = (macsByRouter.GetValueOrDefault(b) ?? []).Where(p => p.Value.Overlaps(aMacs)).Select(p => p.Key).Distinct().ToList();
                var bPort = backPorts.Count == 1 ? backPorts[0] : null;
                var (type, note) = CombineTypes(a, port, b, bPort);
                Propose(RouterEnd(a, port), RouterEnd(b, bPort), type, Confidence.Medium, "mac-table",
                    $"{RouterName(a)} learns {RouterName(b)}'s MAC on {port}" + (bPort is not null ? $", and {RouterName(b)} learns {RouterName(a)}'s on {bPort}" : "") + note);
            }

            // ARP on a wire port (a routed link): the other router's MAC.
            foreach (var arp in observations.Where(o => o.Source == ObservationSource.Arp && o.Mac is not null && IsWire(o.RouterId, o.Interface)))
            {
                if (RouterOfMac(arp.Mac, arp.RouterId) is not { } b)
                {
                    continue;
                }
                var bPort = NonBridge(b, PortOfMac(b, arp.Mac));
                var (type, note) = CombineTypes(arp.RouterId, arp.Interface, b, bPort);
                Propose(RouterEnd(arp.RouterId, arp.Interface), RouterEnd(b, bPort), type, Confidence.Medium, "arp",
                    $"{RouterName(arp.RouterId)} has {RouterName(b)}'s address {arp.Ip} in its ARP table on {arp.Interface}" + note);
            }
        }

        private string? NonBridge(Guid router, string? port) => Port(router, port) is { } p && NoPortTypes.Contains(p.Type ?? "") ? null : port;

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

                // A neighbor that is the host: its own port is in the neighbor entry.
                foreach (var n in (neighborsByMac.GetValueOrDefault(mac) ?? []).Where(o => o.Interface is not null))
                {
                    Propose(RouterEnd(n.RouterId, n.Interface), hostEnd with { Interface = n.RemoteInterface ?? owner.Interface }, TypeOf(n.RouterId, n.Interface),
                        Confidence.High, "neighbor-host",
                        $"{RouterName(n.RouterId)} sees {HostName(host)} on {n.Interface} by {n.DiscoveredBy ?? "neighbor discovery"}"
                        + (n.RemoteInterface is { } remote ? $" (its port {remote})" : ""));
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
                if (at.Via == InterfaceSources.Wifi)
                {
                    Propose(RouterEnd(at.Router, at.Port), hostEnd, ConnectionTypes.Wifi, Confidence.High, "wifi",
                        $"{HostName(host)} ({mac}) is a Wi-Fi client of {routerName} on {at.Port}");
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
                            $"{HostName(host)}'s MAC {mac} is learned on {routerName} {at.Port}, which leads to {HostName(leadsTo[0])}");
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
                Propose(RouterEnd(at.Router, at.Port), hostEnd, type, confidence, at.Via == InterfaceSources.Arp ? "arp-host" : "mac-table-host", text);
            }
        }

        /// <summary>The far end of a connection that has an end at a router's port, or null.</summary>
        private static ConnectionEnd? OtherEnd(TopologyConnection c, Guid router, string port)
        {
            static bool At(ConnectionEnd e, Guid router, string port) =>
                e.SensorId == router && string.Equals(e.Interface, port, StringComparison.OrdinalIgnoreCase);
            return At(c.A, router, port) ? c.B : At(c.B, router, port) ? c.A : null;
        }

        private void Propose(ConnectionEnd a, ConnectionEnd b, string? type, Confidence confidence, string rule, string text)
        {
            if (a.Device == b.Device)
            {
                return;
            }
            candidates.Add(new Candidate(a, b, type, confidence, new Evidence(rule, confidence, text)));
        }

        // --- merging, connections, differences ------------------------------------------------------------------------

        private void Finish()
        {
            // One suggestion per pair of ends; then the unspecific ones (an end without its port) go into a specific one.
            var merged = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
            foreach (var c in candidates)
            {
                Add(merged, ConnectionEnd.PairKey(c.A, c.B), c);
            }
            var keys = merged.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            foreach (var key in keys)
            {
                var first = merged[key][0];
                var into = keys.Where(k => k != key && merged.ContainsKey(k)).Select(k => merged[k][0])
                    .FirstOrDefault(other => Covers(first.A, first.B, other.A, other.B) && !Covers(other.A, other.B, first.A, first.B));
                if (into is not null)
                {
                    merged[ConnectionEnd.PairKey(into.A, into.B)].AddRange(merged[key]);
                    merged.Remove(key);
                }
            }

            var evidenced = new HashSet<Guid>();
            foreach (var (key, group) in merged.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                var best = group.OrderByDescending(c => c.Confidence).ThenBy(c => c.Type is null ? 1 : 0).First();
                var confidence = group.Max(c => c.Confidence);
                var type = group.Where(c => c.Type is not null).OrderByDescending(c => c.Confidence).Select(c => c.Type).FirstOrDefault();
                var evidence = group.Select(c => c.Evidence).Distinct().OrderByDescending(e => e.Confidence).ThenBy(e => e.Text, StringComparer.Ordinal).ToList();

                var covering = (connectionsByDevices.GetValueOrDefault(DevicePair(best.A, best.B)) ?? [])
                    .Where(c => Covers(c.A, c.B, best.A, best.B) || Covers(best.A, best.B, c.A, c.B)).ToList();
                if (covering.Count > 0)
                {
                    foreach (var c in covering)
                    {
                        evidenced.Add(c.Id);
                    }
                    continue;
                }
                result.Suggestions.Add(new SuggestionResult(key, SuggestionKind.Link, best.A, best.B, type, confidence, evidence, null));

                // A connection that says this router port goes elsewhere: a sure neighbor link says otherwise.
                if (confidence == Confidence.High && best.A.SensorId is not null && best.B.SensorId is not null)
                {
                    foreach (var end in new[] { best.A, best.B })
                    {
                        if (end.Interface is null)
                        {
                            continue;
                        }
                        var other = end == best.A ? best.B : best.A;
                        foreach (var c in connectionsAtPort.GetValueOrDefault((end.SensorId!.Value, end.Interface.Trim().ToLowerInvariant())) ?? [])
                        {
                            if (OtherEnd(c, end.SensorId!.Value, end.Interface) is { SensorId: not null } far && far.Device != other.Device)
                            {
                                result.Suggestions.Add(new SuggestionResult($"{key}|differs:{c.Id:N}", SuggestionKind.Differs, best.A, best.B, type, confidence,
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

        /// <summary>Whether the pair (a, b) covers (c, d): the same devices, and each end's interface the same or unsaid on (a, b).</summary>
        private static bool Covers(ConnectionEnd a, ConnectionEnd b, ConnectionEnd c, ConnectionEnd d)
        {
            static bool End(ConnectionEnd general, ConnectionEnd specific) =>
                general.Device == specific.Device && (string.IsNullOrWhiteSpace(general.Interface)
                    || string.Equals(general.Interface.Trim(), specific.Interface?.Trim(), StringComparison.OrdinalIgnoreCase));
            return (End(a, c) && End(b, d)) || (End(a, d) && End(b, c));
        }
    }
}
