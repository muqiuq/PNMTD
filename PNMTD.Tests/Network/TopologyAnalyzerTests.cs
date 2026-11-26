using System.Text.Json;
using PNMTD.Lib.Network;

namespace PNMTD.Tests.Network;

/// <summary>
/// The analysis as scenarios (docs/network-map-plan.md §12): small worlds built in code, each with the suggestions, matches
/// and confirmations the plan's rules (§5.2, §5.3, §5.5, §6.2) give.
/// </summary>
[TestClass]
public sealed class TopologyAnalyzerTests
{
    private static readonly DateTime At = new(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc);

    /// <summary>A world: routers with ports, hosts, servers' adapters, what the routers saw, connections and assignments.</summary>
    private sealed class World
    {
        public readonly List<TopologyRouter> Routers = [];
        public readonly List<TopologyHost> Hosts = [];
        public readonly List<RouterPort> Ports = [];
        public readonly List<Observation> Seen = [];
        public readonly List<TopologyAdapter> Adapters = [];
        public readonly List<TopologyAssignment> Assignments = [];
        public readonly List<TopologyConnection> Connections = [];
        public readonly HashSet<string> Ignored = [];
        private int macs;

        public Guid Router(string name, params (string Port, string? Type, int? Rate)[] ports)
        {
            var id = Guid.NewGuid();
            var host = Host($"{name}-host");
            Routers.Add(new TopologyRouter(id, host, name, name, At));
            foreach (var (port, type, rate) in ports)
            {
                Ports.Add(new RouterPort(id, port)
                {
                    Type = type ?? "ether", Mac = NextMac(), RateMbps = rate,
                    DetectedType = ConnectionTypeDetector.Detect(new PortFacts { Type = type ?? "ether", Name = port, Rate = rate is { } r ? $"{r}Mbps" : null }),
                });
            }
            return id;
        }

        public string MacOf(Guid router, string port) => Ports.Single(p => p.RouterId == router && p.Name == port).Mac!;

        public Guid HostOf(Guid router) => Routers.Single(r => r.SensorId == router).HostId;

        public Guid Host(string name)
        {
            var id = Guid.NewGuid();
            Hosts.Add(new TopologyHost(id, name));
            return id;
        }

        /// <summary>A server: its host, and an adapter with its MAC and speed.</summary>
        public (Guid Host, string Mac) Server(string name, string adapter = "eno1", long? bps = 10_000_000_000, string? mac = null)
        {
            var host = Host(name);
            var m = mac ?? NextMac();
            Adapters.Add(new TopologyAdapter(host, Guid.NewGuid(), m, adapter, bps, false));
            return (host, m);
        }

        public string NextMac()
        {
            var n = ++macs;
            return $"00:16:{(n >> 24) & 0xFF:X2}:{(n >> 16) & 0xFF:X2}:{(n >> 8) & 0xFF:X2}:{n & 0xFF:X2}";
        }

        public void Learns(Guid router, string port, params string[] seen)
        {
            foreach (var mac in seen)
            {
                Seen.Add(new Observation(router, ObservationSource.BridgeHost) { Interface = port, Bridge = "bridge", Mac = mac, SeenAt = At });
            }
        }

        public void Neighbor(Guid router, string port, string identity, string? mac, string? theirPort, string by = "mndp", string? via = null) =>
            Seen.Add(new Observation(router, ObservationSource.Neighbor)
            {
                Interface = port, Bridge = via, Identity = identity, Mac = mac, RemoteInterface = theirPort, DiscoveredBy = by, SeenAt = At,
            });

        /// <summary>VLAN interfaces on a parent port or bridge, with the parent's MAC, as RouterOS gives them.</summary>
        public void Vlans(Guid router, string parent, params int[] ids)
        {
            var mac = MacOf(router, parent);
            foreach (var id in ids)
            {
                Ports.Add(new RouterPort(router, $"vlan{id}") { Type = "vlan", Mac = mac, VlanParent = parent, VlanId = id });
            }
        }

        /// <summary>A bridge with its first member's MAC (RouterOS' auto-mac).</summary>
        public void BridgeOn(Guid router, string name, string member) =>
            Ports.Add(new RouterPort(router, name) { Type = "bridge", Mac = MacOf(router, member) });

        public TopologyAdapter Adapter(Guid host, string mac, string adapter, bool isVirtual, string? kind = null, long? bps = 10_000_000_000)
        {
            var a = new TopologyAdapter(host, Guid.NewGuid(), mac, adapter, bps, isVirtual, kind);
            Adapters.Add(a);
            return a;
        }

        public readonly List<TopologyRule> Rules = [];

        public void Connect(ConnectionEnd a, ConnectionEnd b, string type = "baset-1g") => Connections.Add(new TopologyConnection(Guid.NewGuid(), a, b, type));

        public ConnectionEnd End(Guid router, string? port) => new(HostOf(router), router, port);

        public TopologyResult Analyze() => TopologyAnalyzer.Analyze(new TopologyInput
        {
            Routers = Routers, Hosts = Hosts, Ports = Ports, Observations = Seen, Adapters = Adapters, Assignments = Assignments,
            Connections = Connections, Ignored = Ignored, Rules = Rules,
        });
    }

    private static string Ends(SuggestionResult s) => $"{s.A.Interface}|{s.B.Interface}";

    // --- router ↔ router ---------------------------------------------------------------------------------------------------

    [TestMethod]
    public void Two_routers_that_see_each_other_are_one_sure_suggestion()
    {
        var w = new World();
        var a = w.Router("office-gw", ("sfp-sfpplus1", null, 10_000), ("ether1", null, 1000));
        var b = w.Router("core-gw", ("sfp-sfpplus3", null, 10_000));
        w.Neighbor(a, "sfp-sfpplus1", "core-gw", w.MacOf(b, "sfp-sfpplus3"), "sfp-sfpplus3");
        w.Neighbor(b, "sfp-sfpplus3", "office-gw", w.MacOf(a, "sfp-sfpplus1"), "sfp-sfpplus1");
        var s = w.Analyze().Suggestions.Single();
        Assert.AreEqual(Confidence.High, s.Confidence);
        Assert.AreEqual(SuggestionKind.Link, s.Kind);
        CollectionAssert.AreEquivalent(new[] { "sfp-sfpplus1", "sfp-sfpplus3" }, new[] { s.A.Interface, s.B.Interface });
        Assert.IsTrue(s.Evidence.Any(e => e.Text.Contains("office-gw sees core-gw on sfp-sfpplus1 by mndp (its port sfp-sfpplus3)")), s.Evidence[0].Text);
        Assert.IsNull(s.Type, "the SFP cages say nothing about their modules here");
    }

    [TestMethod]
    public void Seen_from_one_side_only_is_medium()
    {
        var w = new World();
        var a = w.Router("a", ("ether2", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000));
        w.Neighbor(a, "ether2", "b", w.MacOf(b, "ether1"), "ether1");
        var s = w.Analyze().Suggestions.Single();
        Assert.AreEqual(Confidence.Medium, s.Confidence);
        Assert.AreEqual("baset-1g", s.Type);
        Assert.IsTrue(s.Evidence[0].Text.Contains("Both sides BASE-T 1G"), s.Evidence[0].Text);
    }

    [TestMethod]
    public void A_WireGuard_pair_is_a_virtual_connection()
    {
        var w = new World();
        var a = w.Router("a", ("wg-b", "wg", null));
        var b = w.Router("b", ("wg-a", "wg", null));
        w.Seen.Add(new Observation(a, ObservationSource.WireGuardInterface) { Interface = "wg-b", RemoteKey = "KEY-A", SeenAt = At });
        w.Seen.Add(new Observation(b, ObservationSource.WireGuardInterface) { Interface = "wg-a", RemoteKey = "KEY-B", SeenAt = At });
        w.Seen.Add(new Observation(a, ObservationSource.WireGuardPeer) { Interface = "wg-b", RemoteKey = "KEY-B", SeenAt = At });
        w.Seen.Add(new Observation(b, ObservationSource.WireGuardPeer) { Interface = "wg-a", RemoteKey = "KEY-A", SeenAt = At });
        var s = w.Analyze().Suggestions.Single();
        Assert.AreEqual((ConnectionTypes.WireGuard, Confidence.High), (s.Type, s.Confidence));
        CollectionAssert.AreEquivalent(new[] { "wg-a", "wg-b" }, new[] { s.A.Interface, s.B.Interface });
    }

    [TestMethod]
    public void A_MAC_learned_on_the_uplink_is_attached_at_the_far_routers_edge_port()
    {
        var w = new World();
        var a = w.Router("a", ("ether1", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000), ("ether5", null, 10_000));
        var (server, mac) = w.Server("srv-db");
        // a learns b's MAC and the server's on its uplink; b has the server alone on ether5.
        w.Learns(a, "ether1", w.MacOf(b, "ether1"), mac);
        w.Learns(b, "ether1", w.MacOf(a, "ether1"));
        w.Learns(b, "ether5", mac);
        var result = w.Analyze();
        var item = result.Interfaces.Single(i => i.Mac == mac);
        Assert.AreEqual((b, "ether5"), (item.AttachedRouterId!.Value, item.AttachedPort));
        Assert.AreEqual((MatchKind.ServerAdapter, server, "eno1"), (item.Match, item.MatchHostId!.Value, item.MatchInterface));
        Assert.AreEqual(PortRole.ToRouter, result.Roles.Single(r => r.RouterId == a && r.Port == "ether1").Role);

        var host = result.Suggestions.Single(s => s.A.SensorId is null || s.B.SensorId is null);
        Assert.AreEqual(Confidence.High, host.Confidence);
        Assert.AreEqual("baset-10g", host.Type);
        Assert.IsTrue(host.Evidence[0].Text.Contains("is the only device on b ether5. Both sides 10G"), host.Evidence[0].Text);
        var link = result.Suggestions.Single(s => s.A.SensorId is not null && s.B.SensorId is not null);
        Assert.AreEqual("ether1|ether1", Ends(link), "the MAC table gives the routers' link too");
    }

    // --- router ↔ host -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public void A_server_sharing_its_port_is_medium()
    {
        var w = new World();
        var r = w.Router("gw", ("ether7", null, 1000));
        var (_, mac) = w.Server("srv-app", bps: 1_000_000_000);
        w.Learns(r, "ether7", mac, w.NextMac(), w.NextMac(), w.NextMac(), w.NextMac(), w.NextMac());
        var s = w.Analyze().Suggestions.Single();
        Assert.AreEqual(Confidence.Medium, s.Confidence);
        Assert.IsTrue(s.Evidence[0].Text.Contains("with 5 other device(s)"), s.Evidence[0].Text);
    }

    [TestMethod]
    public void Behind_a_connected_switch_the_hosts_are_suggested_to_the_switch()
    {
        var w = new World();
        var r = w.Router("gw", ("ether7", null, 1000));
        var sw = w.Host("Switch 2nd floor");
        w.Connect(w.End(r, "ether7"), new ConnectionEnd(sw, null, null));
        var (server, mac) = w.Server("srv-app");
        w.Learns(r, "ether7", mac, w.NextMac());
        var result = w.Analyze();
        var s = result.Suggestions.Single();
        CollectionAssert.AreEquivalent(new[] { server, sw }, new[] { s.A.HostId, s.B.HostId });
        Assert.IsTrue(s.A.SensorId is null && s.B.SensorId is null, "host ↔ switch, no router end");
        Assert.AreEqual("behind-host", s.Evidence[0].Rule);
    }

    [TestMethod]
    public void Behind_a_neighbor_that_is_a_host_the_hosts_without_a_connection_are_suggested_to_it()
    {
        var w = new World();
        var r = w.Router("hub", ("sfp-sfpplus1", null, 10_000));
        var core = w.Host("router-core");
        var coreMac = "64:D1:54:00:00:01";
        w.Assignments.Add(new TopologyAssignment(coreMac, core, null));
        w.Neighbor(r, "sfp-sfpplus1", "router-core", coreMac, "Te0/1", "lldp");
        var (nas, nasMac) = w.Server("nas-01");
        var (wired, wiredMac) = w.Server("backup-srv");
        var sw = w.Host("switch-core-01");
        w.Connect(new ConnectionEnd(wired, null, "Ethernet0"), new ConnectionEnd(sw, null, "ge-0/0/12"));
        w.Learns(r, "sfp-sfpplus1", nasMac, wiredMac);
        var result = w.Analyze();
        var behind = result.Suggestions.Where(s => s.Evidence.Any(e => e.Rule == "behind-host")).ToList();
        Assert.AreEqual(1, behind.Count, "backup-srv has its connection already");
        CollectionAssert.AreEquivalent(new[] { nas, core }, new[] { behind[0].A.HostId, behind[0].B.HostId });
        Assert.IsTrue(result.Suggestions.Any(s => s.Evidence.Any(e => e.Rule == "neighbor-host")), "and the hub ↔ router-core link itself");
    }

    [TestMethod]
    public void An_unknown_neighbor_is_an_interface_until_a_person_assigns_it()
    {
        var w = new World();
        var r = w.Router("gw", ("sfp-sfpplus1", null, 10_000));
        var neighborMac = "64:D1:54:00:00:09";
        w.Neighbor(r, "sfp-sfpplus1", "core-sw", neighborMac, "sfp1", "lldp");
        var before = w.Analyze();
        Assert.AreEqual(0, before.Suggestions.Count, "no host, no suggestion (decision 6)");
        var item = before.Interfaces.Single(i => i.Mac == neighborMac);
        Assert.AreEqual(("core-sw", "sfp1", MatchKind.None), (item.NeighborIdentity, item.NeighborPort, item.Match));
        Assert.AreEqual(PortRole.ToNeighbor, before.Roles.Single(x => x.Port == "sfp-sfpplus1").Role);

        var sw = w.Host("core-sw");
        w.Assignments.Add(new TopologyAssignment(neighborMac, sw, null));
        var after = w.Analyze();
        var s = after.Suggestions.Single();
        Assert.AreEqual((Confidence.High, "neighbor-host"), (s.Confidence, s.Evidence[0].Rule));
        var hostEnd = s.A.SensorId is null ? s.A : s.B;
        Assert.AreEqual((sw, "sfp1"), (hostEnd.HostId, hostEnd.Interface), "the far port from the neighbor entry");
        Assert.AreEqual(MatchKind.Manual, after.Interfaces.Single(i => i.Mac == neighborMac).Match);
    }

    [TestMethod]
    public void A_Wi_Fi_client_is_a_Wi_Fi_connection()
    {
        var w = new World();
        var r = w.Router("ap", ("wifi1", "wifi", null));
        var (_, mac) = w.Server("laptop-01", "wlan0", bps: null);
        w.Seen.Add(new Observation(r, ObservationSource.WifiClient) { Interface = "wifi1", Mac = mac, Note = "-61", SeenAt = At });
        var s = w.Analyze().Suggestions.Single();
        Assert.AreEqual((ConnectionTypes.Wifi, Confidence.High), (s.Type, s.Confidence));
    }

    [TestMethod]
    public void Random_ignored_and_ambiguous_MACs_make_no_suggestions()
    {
        var w = new World();
        var r = w.Router("gw", ("ether2", null, 1000), ("ether3", null, 1000), ("ether4", null, 1000));
        var random = "DA:A1:19:00:00:01";
        w.Server("phone-ish", mac: random);
        w.Learns(r, "ether2", random);
        var (_, ignored) = w.Server("test-box");
        w.Ignored.Add(ignored);
        w.Learns(r, "ether3", ignored);
        var twin = "00:16:3E:AA:AA:AA";
        w.Server("vm-a", mac: twin);
        w.Server("vm-b", mac: twin);
        w.Learns(r, "ether4", twin);
        var result = w.Analyze();
        Assert.AreEqual(0, result.Suggestions.Count, string.Join("; ", result.Suggestions.Select(s => s.Evidence[0].Text)));
        Assert.AreEqual(MatchKind.None, result.Interfaces.Single(i => i.Mac == random).Match);
        Assert.IsTrue(result.Interfaces.Single(i => i.Mac == random).Random);
        Assert.AreEqual(MatchKind.Ambiguous, result.Interfaces.Single(i => i.Mac == twin).Match);
    }

    [TestMethod]
    public void A_DHCP_host_name_that_is_a_hosts_name_is_a_hint()
    {
        var w = new World();
        var r = w.Router("gw", ("ether2", null, 1000));
        var printer = w.Host("printer-2og");
        var mac = w.NextMac();
        w.Learns(r, "ether2", mac);
        w.Seen.Add(new Observation(r, ObservationSource.DhcpLease) { Interface = "bridge", Mac = mac, Ip = "192.168.88.40", HostName = "PRINTER-2OG.local", SeenAt = At });
        var item = w.Analyze().Interfaces.Single();
        Assert.AreEqual((MatchKind.None, printer), (item.Match, item.HintHostId!.Value));
        CollectionAssert.AreEqual(new[] { "192.168.88.40" }, item.Ips.ToArray());
        Assert.AreEqual(InterfaceSources.MacTable | InterfaceSources.Dhcp, item.Sources);
    }

    // --- connections ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void A_connection_that_the_scan_shows_is_confirmed_and_no_suggestion()
    {
        var w = new World();
        var a = w.Router("a", ("ether2", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000));
        w.Neighbor(a, "ether2", "b", w.MacOf(b, "ether1"), "ether1");
        w.Neighbor(b, "ether1", "a", w.MacOf(a, "ether2"), "ether2");
        w.Connect(w.End(b, "ether1"), w.End(a, null));
        var result = w.Analyze();
        Assert.AreEqual(0, result.Suggestions.Count, "a connection without a port on one side covers the suggestion");
        var check = result.Checks.Single();
        Assert.IsTrue(check.Evidenced);
        CollectionAssert.AreEquivalent(new[] { a, b }, check.Routers.ToArray());
    }

    [TestMethod]
    public void A_connection_the_scan_contradicts_gives_a_difference()
    {
        var w = new World();
        var a = w.Router("a", ("ether2", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000));
        var c = w.Router("c", ("ether9", null, 1000));
        w.Connect(w.End(a, "ether2"), w.End(c, "ether9"));
        w.Neighbor(a, "ether2", "b", w.MacOf(b, "ether1"), "ether1");
        w.Neighbor(b, "ether1", "a", w.MacOf(a, "ether2"), "ether2");
        var result = w.Analyze();
        var s = result.Suggestions.Single();
        Assert.AreEqual(SuggestionKind.Differs, s.Kind);
        Assert.AreEqual(w.Connections[0].Id, s.DiffersFrom);
        Assert.IsFalse(result.Checks.Single().Evidenced);
    }

    [TestMethod]
    public void The_same_input_gives_the_same_output()
    {
        string Run()
        {
            var w = new World();
            var a = w.Router("a", ("ether1", null, 1000), ("ether2", null, 1000));
            var b = w.Router("b", ("ether1", null, 1000));
            var (_, mac) = w.Server("srv");
            w.Learns(a, "ether1", w.MacOf(b, "ether1"));
            w.Learns(a, "ether2", mac, "00:AA:00:00:00:01");
            w.Neighbor(a, "ether1", "b", w.MacOf(b, "ether1"), "ether1");
            var r = w.Analyze();
            // Ids are random per world: compare what doesn't depend on them.
            return JsonSerializer.Serialize(new
            {
                S = r.Suggestions.Select(s => new { s.Confidence, s.Type, A = s.A.Interface, B = s.B.Interface, E = s.Evidence.Select(e => e.Text) })
                    .OrderBy(s => s.A).ThenBy(s => s.B),
                I = r.Interfaces.Select(i => new { i.Mac, i.AttachedPort, i.Match }),
                R = r.Roles.Select(x => new { x.Port, x.Role, x.MacCount }).OrderBy(x => x.Port).ThenBy(x => x.Role).ThenBy(x => x.MacCount),
            });
        }
        Assert.AreEqual(Run(), Run());
    }

    // --- ends on the wire (docs/network-suggestion-rules-plan.md §3) -------------------------------------------------------

    private static readonly int[] NineVlans = [10, 20, 30, 40, 50, 60, 70, 80, 90];

    [TestMethod]
    public void Nine_VLANs_on_one_trunk_are_one_suggestion_on_the_physical_ports()
    {
        var w = new World();
        var a = w.Router("office-gw", ("ether1", null, 1000));
        var b = w.Router("core-gw", ("sfp-sfpplus1", null, 1000));
        w.Vlans(a, "ether1", NineVlans);
        w.Vlans(b, "sfp-sfpplus1", NineVlans);
        var (macA, macB) = (w.MacOf(a, "ether1"), w.MacOf(b, "sfp-sfpplus1"));
        w.Neighbor(a, "ether1", "core-gw", macB, "sfp-sfpplus1");
        w.Neighbor(b, "sfp-sfpplus1", "office-gw", macA, "ether1");
        foreach (var id in NineVlans)
        {
            w.Neighbor(a, $"vlan{id}", "core-gw", macB, $"vlan{id}");
            w.Neighbor(b, $"vlan{id}", "office-gw", macA, $"vlan{id}");
        }
        var s = w.Analyze().Suggestions.Single();
        Assert.AreEqual((Confidence.High, "baset-1g"), (s.Confidence, s.Type), "the type comes from the physical ports");
        CollectionAssert.AreEquivalent(new[] { "ether1", "sfp-sfpplus1" }, new[] { s.A.Interface, s.B.Interface });
        Assert.AreEqual(2, s.Evidence.Count, string.Join("\n", s.Evidence.Select(e => e.Text)));
        var joined = s.Evidence.Single(e => e.Vlans is not null);
        StringAssert.Contains(joined.Text, "and on 9 VLANs on that port: vlan10, vlan20, vlan30 and 6 more");
        Assert.AreEqual(9, joined.Vlans!.Count);
        Assert.AreEqual("mndp", joined.By);
    }

    [TestMethod]
    [DataRow("vlan alone", DisplayName = "the neighbor names only the VLAN")]
    [DataRow("port then vlan", DisplayName = "the neighbor names the port, then the VLAN")]
    public void VLANs_on_a_vlan_filtering_bridge_are_one_suggestion(string shape)
    {
        var w = new World();
        var a = w.Router("office-gw", ("ether1", null, 1000));
        var b = w.Router("core-gw", ("sfp-sfpplus1", null, 1000));
        w.BridgeOn(a, "bridge", "ether1");
        w.BridgeOn(b, "bridge", "sfp-sfpplus1");
        w.Vlans(a, "bridge", NineVlans);
        w.Vlans(b, "bridge", NineVlans);
        var (macA, macB) = (w.MacOf(a, "ether1"), w.MacOf(b, "sfp-sfpplus1"));
        // A port in a bridge: its far side names it as bridge/port.
        w.Neighbor(a, "ether1", "core-gw", macB, "bridge/sfp-sfpplus1", via: "bridge");
        w.Neighbor(b, "sfp-sfpplus1", "office-gw", macA, "bridge/ether1", via: "bridge");
        foreach (var id in NineVlans)
        {
            if (shape == "vlan alone")
            {
                w.Neighbor(a, $"vlan{id}", "core-gw", macB, $"vlan{id}");
                w.Neighbor(b, $"vlan{id}", "office-gw", macA, $"vlan{id}");
            }
            else
            {
                w.Neighbor(a, "ether1", "core-gw", macB, $"vlan{id}", via: $"vlan{id}");
                w.Neighbor(b, "sfp-sfpplus1", "office-gw", macA, $"vlan{id}", via: $"vlan{id}");
            }
        }
        var s = w.Analyze().Suggestions.Single();
        Assert.AreEqual(Confidence.High, s.Confidence);
        CollectionAssert.AreEquivalent(new[] { "ether1", "sfp-sfpplus1" }, new[] { s.A.Interface, s.B.Interface });
        Assert.IsTrue(s.Evidence.Any(e => e.Text.Contains("9 VLANs")), string.Join("\n", s.Evidence.Select(e => e.Text)));
    }

    [TestMethod]
    public void One_bridge_per_VLAN_counts_for_the_trunk_and_attaches_the_hosts_behind_it()
    {
        var w = new World();
        var a = w.Router("office-gw", ("ether1", null, 1000), ("ether3", null, 1000));
        var b = w.Router("core-gw", ("sfp-sfpplus1", null, 1000));
        w.Vlans(a, "ether1", 10, 20);
        w.Vlans(a, "ether3", 30);
        w.Vlans(b, "sfp-sfpplus1", 10, 20);
        // The VLAN interfaces are the bridges' ports, so the MAC table names them.
        w.Learns(a, "vlan10", w.MacOf(b, "sfp-sfpplus1"));
        w.Learns(a, "vlan20", w.MacOf(b, "sfp-sfpplus1"));
        w.Learns(b, "sfp-sfpplus1", w.MacOf(a, "ether1"));
        var (server, mac) = w.Server("srv-db");
        w.Learns(a, "vlan30", mac);
        var result = w.Analyze();
        var link = result.Suggestions.Single(s => s.A.SensorId is not null && s.B.SensorId is not null);
        CollectionAssert.AreEquivalent(new[] { "ether1", "sfp-sfpplus1" }, new[] { link.A.Interface, link.B.Interface });
        var host = result.Suggestions.Single(s => s.A.SensorId is null || s.B.SensorId is null);
        Assert.AreEqual(Confidence.High, host.Confidence);
        Assert.IsTrue(new[] { host.A, host.B }.Any(e => e.SensorId == a && e.Interface == "ether3") && new[] { host.A, host.B }.Any(e => e.HostId == server));
        var item = result.Interfaces.Single(i => i.Mac == mac);
        Assert.AreEqual(("ether3", 30), (item.AttachedPort, item.AttachedVlan!.Value));
    }

    [TestMethod]
    public void A_server_whose_adapters_share_a_MAC_is_suggested_on_its_physical_adapter()
    {
        var w = new World();
        var r = w.Router("gw", ("ether5", null, 10_000), ("ether6", null, 10_000));
        var hv = w.Host("hv01");
        var mac = w.NextMac();
        w.Adapter(hv, mac, "eno1", false, "Ethernet");
        w.Adapter(hv, mac, "eno1.10", true, "VLAN");
        w.Adapter(hv, mac, "vmbr0", true, "Bridge", null);
        var nas = w.Host("nas02");
        var bondMac = w.NextMac();
        w.Adapter(nas, bondMac, "bond0", true, "Bond");
        w.Adapter(nas, bondMac, "eno1", false, "Ethernet");
        w.Adapter(nas, bondMac, "eno2", false, "Ethernet");
        w.Learns(r, "ether5", mac);
        w.Learns(r, "ether6", bondMac);
        var result = w.Analyze();
        Assert.AreEqual(2, result.Suggestions.Count, "today a MAC with several adapters gave none");
        string HostSide(Guid host) => result.Suggestions.Select(s => s.A.HostId == host ? s.A : s.B.HostId == host ? s.B : null).OfType<ConnectionEnd>().Single().Interface!;
        Assert.AreEqual("eno1", HostSide(hv));
        Assert.AreEqual("bond0", HostSide(nas), "a bond's members carry its MAC");
        Assert.AreEqual(("eno1", MatchKind.ServerAdapter), (result.Interfaces.Single(i => i.Mac == mac).MatchInterface, result.Interfaces.Single(i => i.Mac == mac).Match));
    }

    [TestMethod]
    public void Two_vNICs_of_one_host_on_one_port_are_one_cable()
    {
        var w = new World();
        var r = w.Router("gw", ("ether5", null, 10_000));
        var hv = w.Host("hv02");
        var (mgmt, backup) = ("00:15:5D:00:00:01", "00:15:5D:00:00:02");
        w.Adapter(hv, mgmt, "vEthernet (Mgmt)", true);
        w.Adapter(hv, backup, "vEthernet (Backup)", true);
        w.Learns(r, "ether5", mgmt, backup);
        var s = w.Analyze().Suggestions.Single();
        var hostEnd = s.A.SensorId is null ? s.A : s.B;
        Assert.AreEqual((hv, (string?)null), (hostEnd.HostId, hostEnd.Interface), "no physical adapter seen: the person picks");
        Assert.AreEqual(2, s.Evidence.Count, "both vNICs are named");
    }

    [TestMethod]
    public void Two_real_cables_stay_two_and_two_tunnels_stay_two()
    {
        var w = new World();
        var a = w.Router("a", ("ether1", null, 1000), ("ether2", null, 1000), ("wg-b1", "wg", null), ("wg-b2", "wg", null));
        var b = w.Router("b", ("ether1", null, 1000), ("ether2", null, 1000), ("wg-a1", "wg", null), ("wg-a2", "wg", null));
        foreach (var port in new[] { "ether1", "ether2" })
        {
            w.Neighbor(a, port, "b", w.MacOf(b, port), port);
            w.Neighbor(b, port, "a", w.MacOf(a, port), port);
        }
        foreach (var n in new[] { "1", "2" })
        {
            w.Seen.Add(new Observation(a, ObservationSource.WireGuardInterface) { Interface = $"wg-b{n}", RemoteKey = $"KEY-A{n}", SeenAt = At });
            w.Seen.Add(new Observation(b, ObservationSource.WireGuardInterface) { Interface = $"wg-a{n}", RemoteKey = $"KEY-B{n}", SeenAt = At });
            w.Seen.Add(new Observation(a, ObservationSource.WireGuardPeer) { Interface = $"wg-b{n}", RemoteKey = $"KEY-B{n}", SeenAt = At });
            w.Seen.Add(new Observation(b, ObservationSource.WireGuardPeer) { Interface = $"wg-a{n}", RemoteKey = $"KEY-A{n}", SeenAt = At });
        }
        var result = w.Analyze();
        Assert.AreEqual(2, result.Suggestions.Count(s => s.Type != ConnectionTypes.WireGuard));
        Assert.AreEqual(2, result.Suggestions.Count(s => s.Type == ConnectionTypes.WireGuard));
    }

    [TestMethod]
    public void A_VLAN_seen_across_two_cables_adds_no_third_suggestion()
    {
        var w = new World();
        var a = w.Router("a", ("ether1", null, 1000), ("ether2", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000), ("ether2", null, 1000));
        w.Vlans(a, "ether1", 20);
        w.Vlans(b, "ether2", 20);
        foreach (var port in new[] { "ether1", "ether2" })
        {
            w.Neighbor(a, port, "b", w.MacOf(b, port), port);
            w.Neighbor(b, port, "a", w.MacOf(a, port), port);
        }
        w.Neighbor(a, "vlan20", "b", w.MacOf(b, "ether2"), "vlan20");
        w.Neighbor(b, "vlan20", "a", w.MacOf(a, "ether1"), "vlan20");
        var result = w.Analyze();
        Assert.AreEqual(2, result.Suggestions.Count, string.Join("\n", result.Suggestions.Select(Ends)));
        Assert.IsTrue(result.Suggestions.All(s => s.Evidence.Any(e => e.Text.Contains("vlan20"))), "each side names the VLAN on its cable");
    }

    [TestMethod]
    public void A_second_path_that_only_the_MAC_table_shows_is_low()
    {
        var w = new World();
        var a = w.Router("a", ("ether1", null, 1000), ("ether2", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000));
        w.Neighbor(a, "ether1", "b", w.MacOf(b, "ether1"), "ether1");
        w.Neighbor(b, "ether1", "a", w.MacOf(a, "ether1"), "ether1");
        // A switch on ether2 carries a VLAN to b by another way.
        w.Learns(a, "ether2", w.MacOf(b, "ether1"));
        var result = w.Analyze();
        Assert.AreEqual(2, result.Suggestions.Count);
        var second = result.Suggestions.Single(s => s.A.Interface == "ether2" || s.B.Interface == "ether2");
        Assert.AreEqual(Confidence.Low, second.Confidence);
        Assert.IsTrue(second.Evidence.Any(e => e.Rule == "second-path" && e.Text.Contains("by neighbor discovery")), second.Evidence[^1].Text);
        Assert.AreEqual(Confidence.High, result.Suggestions.Single(s => s != second).Confidence);
    }

    [TestMethod]
    public void A_VRRP_MAC_makes_no_link()
    {
        var w = new World();
        var a = w.Router("a", ("ether2", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000));
        const string vrrp = "00:00:5E:00:01:0A";
        w.Ports.Add(new RouterPort(a, "vrrp10") { Type = "vrrp", Mac = vrrp });
        w.Ports.Add(new RouterPort(b, "vrrp10") { Type = "vrrp", Mac = vrrp });
        // a is the backup: its bridge learns the group's MAC from b, the master.
        w.Learns(a, "ether2", vrrp);
        var result = w.Analyze();
        Assert.AreEqual(0, result.Suggestions.Count, string.Join("; ", result.Suggestions.Select(s => s.Evidence[0].Text)));
        Assert.AreEqual(PortRole.Edge, result.Roles.Single(r => r.RouterId == a && r.Port == "ether2").Role);
    }

    [TestMethod]
    public void A_connection_made_on_a_VLAN_is_confirmed_by_its_port()
    {
        var w = new World();
        var a = w.Router("a", ("ether1", null, 1000));
        var b = w.Router("b", ("sfp-sfpplus1", null, 1000));
        w.Vlans(a, "ether1", 10);
        w.Vlans(b, "sfp-sfpplus1", 10);
        w.Neighbor(a, "ether1", "b", w.MacOf(b, "sfp-sfpplus1"), "sfp-sfpplus1");
        w.Neighbor(b, "sfp-sfpplus1", "a", w.MacOf(a, "ether1"), "ether1");
        w.Connect(w.End(a, "vlan10"), w.End(b, "vlan10"));
        var result = w.Analyze();
        Assert.AreEqual(0, result.Suggestions.Count, "the VLAN connection covers its port's cable");
        Assert.IsTrue(result.Checks.Single().Evidenced);
    }

    [TestMethod]
    public void A_switch_seen_on_its_VLANs_is_one_suggestion_with_its_own_port()
    {
        var w = new World();
        var r = w.Router("gw", ("sfp-sfpplus1", null, 10_000));
        w.Vlans(r, "sfp-sfpplus1", 10, 20, 30);
        var sw = w.Host("core-sw");
        const string swMac = "64:D1:54:00:00:09";
        w.Assignments.Add(new TopologyAssignment(swMac, sw, null));
        w.Neighbor(r, "sfp-sfpplus1", "core-sw", swMac, "sfp1");
        foreach (var id in new[] { 10, 20, 30 })
        {
            w.Neighbor(r, $"vlan{id}", "core-sw", swMac, $"vlan{id}");
        }
        var s = w.Analyze().Suggestions.Single();
        var hostEnd = s.A.SensorId is null ? s.A : s.B;
        var routerEnd = s.A.SensorId is null ? s.B : s.A;
        Assert.AreEqual(("sfp1", "sfp-sfpplus1"), (hostEnd.Interface, routerEnd.Interface));
        StringAssert.Contains(s.Evidence.Single().Text, "and on 3 VLANs on that port: vlan10, vlan20, vlan30");
    }

    // --- skip rules (docs/network-suggestion-rules-plan.md §4) ---------------------------------------------------------------

    [TestMethod]
    public void A_VLAN_rule_removes_the_second_path_and_keeps_the_interface()
    {
        var w = new World();
        var a = w.Router("a", ("ether1", null, 1000), ("ether2", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000));
        w.Neighbor(a, "ether1", "b", w.MacOf(b, "ether1"), "ether1");
        w.Neighbor(b, "ether1", "a", w.MacOf(a, "ether1"), "ether1");
        w.Seen.Add(new Observation(a, ObservationSource.BridgeHost) { Interface = "ether2", Bridge = "bridge", VlanId = 20, Mac = w.MacOf(b, "ether1"), SeenAt = At });
        var (_, guest) = w.Server("guest-laptop");
        w.Seen.Add(new Observation(a, ObservationSource.BridgeHost) { Interface = "ether2", Bridge = "bridge", VlanId = 20, Mac = guest, SeenAt = At });
        var rule = new TopologyRule { Id = Guid.NewGuid(), Routers = new HashSet<Guid> { a }, Vlans = [new VlanRange(20, 20)] };
        w.Rules.Add(rule);
        var result = w.Analyze();
        var s = result.Suggestions.Single();
        CollectionAssert.AreEquivalent(new[] { "ether1", "ether1" }, new[] { s.A.Interface, s.B.Interface });
        var item = result.Interfaces.Single(i => i.Mac == guest);
        Assert.AreEqual(("ether2", rule.Id), (item.AttachedPort, item.SkippedBy!.Value), "the interface stays, tagged");
        Assert.AreEqual(new RuleHit(rule.Id, 2, 1), result.Hits.Single());
        Assert.AreEqual(PortRole.Edge, result.Roles.Single(r => r.RouterId == a && r.Port == "ether2").Role, "the roles follow the rules");
    }

    [TestMethod]
    public void An_interface_rule_skips_the_port_also_from_the_other_side()
    {
        var w = new World();
        var a = w.Router("a", ("ether10", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000));
        // Only b's neighbor entry names a's ether10.
        w.Neighbor(b, "ether1", "a", w.MacOf(a, "ether10"), "ether10");
        w.Rules.Add(new TopologyRule { Id = Guid.NewGuid(), Routers = new HashSet<Guid> { a }, InterfacePattern = "ETHER1?" });
        Assert.AreEqual(0, w.Analyze().Suggestions.Count);
    }

    [TestMethod]
    public void An_evidence_rule_skips_a_neighbor_only_when_all_its_protocols_are_skipped()
    {
        var w = new World();
        var a = w.Router("a", ("ether1", null, 1000), ("ether2", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000));
        var c = w.Router("c", ("ether1", null, 1000));
        w.Neighbor(a, "ether1", "b", w.MacOf(b, "ether1"), "ether1", by: "cdp");
        w.Neighbor(a, "ether2", "c", w.MacOf(c, "ether1"), "ether1", by: "mndp,lldp");
        w.Rules.Add(new TopologyRule { Id = Guid.NewGuid(), Evidence = SkipEvidence.Cdp | SkipEvidence.Mndp });
        var s = w.Analyze().Suggestions.Single();
        Assert.IsTrue(s.A.SensorId == c || s.B.SensorId == c, "c is seen by LLDP too");
    }

    [TestMethod]
    public void A_router_rule_without_conditions_skips_the_router_and_never_unconfirms_its_connections()
    {
        var w = new World();
        var a = w.Router("lab", ("ether1", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000), ("ether2", null, 1000));
        var c = w.Router("c", ("ether1", null, 1000));
        w.Neighbor(a, "ether1", "b", w.MacOf(b, "ether1"), "ether1");
        w.Neighbor(b, "ether1", "lab", w.MacOf(a, "ether1"), "ether1");
        w.Neighbor(b, "ether2", "c", w.MacOf(c, "ether1"), "ether1");
        w.Connect(w.End(a, "ether1"), w.End(b, "ether1"));
        w.Rules.Add(new TopologyRule { Id = Guid.NewGuid(), Routers = new HashSet<Guid> { a } });
        var result = w.Analyze();
        var s = result.Suggestions.Single();
        Assert.IsTrue(s.A.SensorId != a && s.B.SensorId != a, "b's view of the lab router makes no suggestion either");
        Assert.IsTrue(result.Checks.Single().Evidenced, "the rules change suggestions, never a connection's state");
    }

    [TestMethod]
    public void A_rule_on_all_routers_without_conditions_does_nothing()
    {
        var w = new World();
        var a = w.Router("a", ("ether2", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000));
        w.Neighbor(a, "ether2", "b", w.MacOf(b, "ether1"), "ether1");
        w.Rules.Add(new TopologyRule { Id = Guid.NewGuid() });
        Assert.AreEqual(1, w.Analyze().Suggestions.Count);
    }

    [TestMethod]
    public void Type_and_MAC_prefix_rules_skip_what_they_name()
    {
        var w = new World();
        var a = w.Router("a", ("ether1", null, 1000), ("ether2", null, 1000));
        var b = w.Router("b", ("ether1", null, 1000));
        w.Vlans(a, "ether1", 10);
        w.Vlans(b, "ether1", 10);
        w.Neighbor(a, "vlan10", "b", w.MacOf(b, "ether1"), "vlan10");
        var vm = w.Host("test-vm");
        w.Adapter(vm, "00:50:56:00:00:01", "ens192", false);
        w.Learns(a, "ether2", "00:50:56:00:00:01");
        w.Rules.Add(new TopologyRule { Id = Guid.NewGuid(), Types = ["VLAN"] });
        w.Rules.Add(new TopologyRule { Id = Guid.NewGuid(), MacPrefix = "00:50:56" });
        var result = w.Analyze();
        Assert.AreEqual(0, result.Suggestions.Count, string.Join("; ", result.Suggestions.Select(s => s.Evidence[0].Text)));
        Assert.AreEqual(2, result.Hits.Count(h => h.Rows == 1));
    }

    [TestMethod]
    [DataRow(false, DisplayName = "without rules")]
    [DataRow(true, DisplayName = "with three rules: two runs")]
    public void Five_hundred_routers_are_analysed_in_seconds(bool withRules)
    {
        var w = new World();
        if (withRules)
        {
            w.Rules.Add(new TopologyRule { Id = Guid.NewGuid(), Vlans = [new VlanRange(99, 99)] });
            w.Rules.Add(new TopologyRule { Id = Guid.NewGuid(), InterfacePattern = "*-guest" });
            w.Rules.Add(new TopologyRule { Id = Guid.NewGuid(), MacPrefix = "00:50:56" });
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var routers = Enumerable.Range(0, 500).Select(i => w.Router($"r{i:000}", ("ether1", null, 1000), ("ether2", null, 1000), ("ether3", null, 1000))).ToList();
        for (var i = 1; i < routers.Count; i++)
        {
            w.Neighbor(routers[i], "ether1", $"r{i - 1:000}", w.MacOf(routers[i - 1], "ether2"), "ether2");
            var macs = Enumerable.Range(0, 200).Select(_ => w.NextMac()).ToArray();
            w.Learns(routers[i], "ether3", macs);
        }
        var result = w.Analyze();
        watch.Stop();
        Assert.AreEqual(499, result.Suggestions.Count(s => s.A.SensorId is not null && s.B.SensorId is not null));
        Assert.IsTrue(result.Interfaces.Count >= 499 * 200);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }
}
