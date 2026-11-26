using System.Text.Json;
using PNMTD.Lib.Network;

namespace PNMTD.Tests.Network;

/// <summary>
/// The network map's pure parts (docs/network-map-plan.md §12): MACs, the types and their detector, the scripts and the parser
/// against the output recorded on CHR 7.13 and 7.25.1 (N0), and the path search.
/// </summary>
[TestClass]
public sealed class NetworkPureTests
{
    private static readonly Guid R1 = new("10000000-0000-0000-0000-000000000001");

    private static string Fixture(string version, ScanTable table) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Network", version, $"{table}.json"));

    // --- VLANs, recorded on two linked CHRs (docs/network-suggestion-rules-plan.md §10, V0) -----------------------------------

    private static readonly ScanTable[] LabTables = [ScanTable.Interfaces, ScanTable.Bridges, ScanTable.MacTable, ScanTable.Neighbors, ScanTable.Arp, ScanTable.Addresses];

    /// <summary>A lab router's tables, parsed as the scan parses them.</summary>
    private static NetworkScanReading LabRouter(string shape, string router, Guid id)
    {
        var reading = new NetworkScanReading();
        foreach (var table in LabTables)
        {
            reading.Add(id, table, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Network", "vlans", shape, router, $"{table}.json")),
                new DateTime(2026, 11, 26, 22, 0, 0, DateTimeKind.Utc));
        }
        reading.Complete();
        return reading;
    }

    [TestMethod]
    [DataRow("on-port", DisplayName = "VLANs on the port")]
    [DataRow("bridge", DisplayName = "VLANs on a vlan-filtering bridge")]
    [DataRow("bridge-per-vlan", DisplayName = "one bridge per VLAN")]
    public void Two_routers_with_VLANs_on_one_cable_are_one_suggestion(string shape)
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var (ra, rb) = (LabRouter(shape, "vla", a), LabRouter(shape, "vlb", b));
        Assert.IsTrue(ra.Observations.Count(o => o.Source == ObservationSource.Neighbor) >= 3, "the neighbor on the port and on each VLAN");
        Assert.IsTrue(ra.Observations.Any(o => o.Source == ObservationSource.Neighbor && o.RemoteInterface is not null && o.DiscoveredBy is not null),
            "the proplist brings interface-name and discovered-by");
        var (ha, hb) = (Guid.NewGuid(), Guid.NewGuid());
        var result = TopologyAnalyzer.Analyze(new TopologyInput
        {
            Routers = [new TopologyRouter(a, ha, "vla", "vla", null), new TopologyRouter(b, hb, "vlb", "vlb", null)],
            Hosts = [new TopologyHost(ha, "vla"), new TopologyHost(hb, "vlb")],
            Ports = [.. ra.Ports, .. rb.Ports],
            Observations = [.. ra.Observations, .. rb.Observations],
        });
        var s = result.Suggestions.Single();
        Assert.AreEqual(Confidence.High, s.Confidence, string.Join("\n", s.Evidence.Select(e => e.Text)));
        CollectionAssert.AreEquivalent(new[] { "ether2", "ether2" }, new[] { s.A.Interface, s.B.Interface });
        Assert.IsTrue(s.Evidence.Any(e => e.Vlans is { Count: 2 }), string.Join("\n", s.Evidence.Select(e => e.Text)));
    }

    // --- MACs and types ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public void Macs_are_normalised_and_random_ones_marked()
    {
        Assert.AreEqual("AA:BB:CC:DD:EE:FF", Mac.Normalize("aa-bb-cc-dd-ee-ff"));
        Assert.AreEqual("AA:BB:CC:DD:EE:FF", Mac.Normalize("aabb.ccdd.eeff"));
        Assert.AreEqual("52:54:00:12:34:56", Mac.Normalize(" 52:54:00:12:34:56 "));
        Assert.IsNull(Mac.Normalize("00:00:00:00:00:00"), "the loopback's MAC is no device");
        Assert.IsNull(Mac.Normalize("not a mac"));
        Assert.IsNull(Mac.Normalize("AA:BB:CC:DD:EE"));
        Assert.IsTrue(Mac.IsRandom("DA:A1:19:00:00:01"), "a phone's private address");
        Assert.IsFalse(Mac.IsRandom("D8:A1:19:00:00:01"));
        Assert.IsTrue(Mac.IsMulticast("01:00:5E:00:00:01"));
    }

    [TestMethod]
    public void The_types_hold_the_medium_and_the_speed()
    {
        Assert.AreEqual("DAC 10G", ConnectionTypes.Label("dac-10g"));
        Assert.AreEqual("Fiber MMF 1G", ConnectionTypes.Label("mmf-1g"));
        Assert.AreEqual("BASE-T 100M", ConnectionTypes.Label("baset-100m"));
        Assert.AreEqual("old-code", ConnectionTypes.Label("old-code"), "a code no longer in the list shows as itself");
        Assert.IsFalse(ConnectionTypes.IsPhysical(ConnectionTypes.WireGuard));
        Assert.IsTrue(ConnectionTypes.IsPhysical(ConnectionTypes.Wifi));
        Assert.AreEqual("smf-10g", ConnectionTypes.Of(ConnectionMedium.FiberSmf, 10_000));
        Assert.IsNull(ConnectionTypes.Of(ConnectionMedium.Dac, 2_500), "no DAC at 2.5G");
        Assert.AreEqual(ConnectionTypes.All.Count, ConnectionTypes.All.Select(t => t.Code).Distinct().Count());
    }

    [TestMethod]
    public void The_detector_tells_the_media_apart()
    {
        static string? Sfp(string rate, string connector, string? wavelength = null, string? lengthKey = null, string lengthValue = "1m") =>
            ConnectionTypeDetector.Detect(new PortFacts
            {
                Type = "ether", Name = "sfp-sfpplus1", Rate = rate, SfpModulePresent = true, SfpConnector = connector, SfpWavelength = wavelength,
                SfpLinkLengths = lengthKey is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [lengthKey] = lengthValue },
            });
        Assert.AreEqual("dac-10g", Sfp("10Gbps", "copper-pigtail", lengthKey: "copper"));
        Assert.AreEqual("mmf-10g", Sfp("10Gbps", "LC", "850nm", "om3", "300m"));
        Assert.AreEqual("smf-10g", Sfp("10Gbps", "LC", "1310nm", "sm", "10km"));
        Assert.AreEqual("smf-1g", Sfp("1Gbps", "SC", "1550nm"));
        Assert.AreEqual("baset-1g", Sfp("1Gbps", "RJ45"));
        Assert.AreEqual("aoc-40g", Sfp("40Gbps", "no-separable-connector", "850nm"));
        Assert.IsNull(Sfp("10Gbps", "unknown"), "unclear gives no type");
        Assert.AreEqual("baset-1g", ConnectionTypeDetector.Detect(new PortFacts { Type = "ether", Name = "ether2", Rate = "1Gbps" }));
        Assert.AreEqual("baset-2.5g", ConnectionTypeDetector.Detect(new PortFacts { Type = "ether", Name = "ether1", Rate = "2.5Gbps" }));
        Assert.IsNull(ConnectionTypeDetector.Detect(new PortFacts { Type = "ether", Name = "ether1" }), "no link, no rate");
        Assert.IsNull(ConnectionTypeDetector.Detect(new PortFacts { Type = "ether", Name = "sfp1", Rate = "1Gbps" }), "an SFP cage that says nothing");
        Assert.AreEqual(ConnectionTypes.WireGuard, ConnectionTypeDetector.Detect(new PortFacts { Type = "wg" }));
        Assert.AreEqual(ConnectionTypes.Tunnel, ConnectionTypeDetector.Detect(new PortFacts { Type = "eoip" }));
        Assert.AreEqual(ConnectionTypes.Wifi, ConnectionTypeDetector.Detect(new PortFacts { Type = "wifi" }));
        Assert.IsNull(ConnectionTypeDetector.Detect(new PortFacts { Type = "vlan" }), "a VLAN is no connection of its own");
        Assert.IsNull(ConnectionTypeDetector.Detect(new PortFacts { Type = "bridge" }));
        Assert.AreEqual(100, ConnectionTypeDetector.RateMbps("100Mbps"));
        Assert.AreEqual(2500, ConnectionTypeDetector.RateMbps("2.5Gbps"));
    }

    // --- the scripts and the parser ----------------------------------------------------------------------------------------

    [TestMethod]
    public void The_scripts_are_one_line_and_read_no_secrets()
    {
        foreach (var table in NetworkScanScript.Order)
        {
            var script = NetworkScanScript.Script(table, "pnmt:7f3a");
            Assert.IsFalse(script.Contains('\n'), table.ToString());
            foreach (var secret in new[] { "private-key", "preshared-key", "ipsec-secret", "password", "show-sensitive", "passphrase" })
            {
                Assert.IsFalse(script.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{table} reads {secret}");
            }
        }
        StringAssert.Contains(NetworkScanScript.Script(ScanTable.Tunnels, "pnmt:7\"f"), "comment!=\"pnmt:7\\\"f\"", "the tag is quoted, PNMT's own tunnel left out");
    }

    [TestMethod]
    [DataRow("7.13")]
    [DataRow("7.25.1")]
    public void The_recorded_output_parses(string version)
    {
        var reading = new NetworkScanReading();
        var at = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc);
        foreach (var table in NetworkScanScript.Order)
        {
            reading.Add(R1, table, Fixture(version, table), at);
        }
        reading.Complete();
        CollectionAssert.AreEquivalent(NetworkScanScript.Order.ToList(), reading.Read.ToList());
        var ether1 = reading.Ports.Single(p => p.Name == "ether1");
        Assert.AreEqual("ether", ether1.Type);
        Assert.AreEqual("ether1", ether1.DefaultName);
        Assert.AreEqual("52:54:00:12:34:56", ether1.Mac);
        Assert.IsTrue(ether1.Running);
        Assert.IsFalse(ether1.Disabled);
        Assert.IsTrue(ether1.Facts!.LinkUp);
        Assert.IsNull(ether1.DetectedType, "a CHR's virtio port reports no rate");
        var arp = reading.Observations.Single(o => o.Source == ObservationSource.Arp && o.Ip == "10.0.2.2");
        Assert.AreEqual("52:55:0A:00:02:02", arp.Mac);
        Assert.AreEqual("10.0.2.2", arp.Ip);
        Assert.AreEqual("ether1", arp.Interface);
        Assert.AreEqual("10.0.2.15/24", reading.Observations.Single(o => o.Source == ObservationSource.Address).Ip);
        Assert.IsNotNull(reading.DiscoverySettings);
        Assert.AreEqual(0, reading.Observations.Count(o => o.Source is ObservationSource.BridgeHost or ObservationSource.WifiClient or ObservationSource.DhcpLease));
        if (version == "7.25.1")
        {
            Assert.AreEqual("wg", reading.Ports.Single(p => p.Name == "wgtest").Type);
            Assert.IsNull(reading.Ports.Single(p => p.Name == "lo").Mac, "the loopback's zeros are no MAC");
            var wg = reading.Observations.Single(o => o.Source == ObservationSource.WireGuardInterface);
            Assert.AreEqual("wgtest", wg.Interface);
            Assert.AreEqual("EUxIa0PxM3uZ1E4i1IpNyFk/8/I9jtkuDP1tEGiIVnY=", wg.RemoteKey);
        }
    }

    [TestMethod]
    public void A_router_with_bridges_vlans_and_modules_parses()
    {
        var reading = new NetworkScanReading();
        var at = DateTime.UtcNow;
        reading.Add(R1, ScanTable.Interfaces, """
            [{"name":"ether1","default-name":"ether1","type":"ether","mac-address":"D4:01:C3:00:00:01","running":true,"disabled":false},
             {"name":"sfp-sfpplus1","default-name":"sfp-sfpplus1","type":"ether","mac-address":"D4:01:C3:00:00:02","running":true,"disabled":false},
             {"name":"bridge","type":"bridge","mac-address":"D4:01:C3:00:00:01","running":true,"disabled":false},
             {"name":"vlan20","type":"vlan","mac-address":"D4:01:C3:00:00:01","running":true,"disabled":false}]
            """, at);
        reading.Add(R1, ScanTable.Ethernet, """
            {"ports":[{"name":"ether1"},{"name":"sfp-sfpplus1"}],
             "monitor":{"ether1":{"name":"ether1","status":"link-ok","rate":"1Gbps","full-duplex":true},
                        "sfp-sfpplus1":{"name":"sfp-sfpplus1","status":"link-ok","rate":"10Gbps","full-duplex":true,"sfp-module-present":true,
                                        "sfp-connector-type":"LC","sfp-wavelength":"1310nm","sfp-link-length-sm":"10km","sfp-vendor-name":"FS"}}}
            """, at);
        reading.Add(R1, ScanTable.Bridges, """
            {"bridges":[{"name":"bridge"}],"ports":[{"interface":"ether1","bridge":"bridge"}],"vlans":[{"name":"vlan20","interface":"sfp-sfpplus1","vlan-id":20}],"bonds":[]}
            """, at);
        reading.Add(R1, ScanTable.MacTable, """
            [{"mac-address":"D4:01:C3:00:00:01","on-interface":"bridge","bridge":"bridge","local":true},
             {"mac-address":"00:11:22:33:44:55","on-interface":"ether1","bridge":"bridge","vid":1,"local":false},
             {"mac-address":"01:00:5E:00:00:01","on-interface":"ether1","bridge":"bridge","local":false}]
            """, at);
        reading.Add(R1, ScanTable.Neighbors, """
            {"neighbors":[{"interface":"ether1,bridge","mac-address":"64:D1:54:00:00:09","address4":"192.168.88.2","identity":"core-sw","platform":"MikroTik",
                           "board":"CRS326-24G-2S+","version":"7.16.1","interface-name":"ether24","discovered-by":"lldp,mndp"}],
             "settings":{"discover-interface-list":"LAN","protocol":["lldp","mndp"]}}
            """, at);
        reading.Add(R1, ScanTable.Arp, """[{"address":"10.20.0.5","mac-address":"00:11:22:33:44:66","interface":"vlan20"}]""", at);
        reading.Add(R1, ScanTable.Dhcp, """
            {"servers":[{"name":"lan","interface":"bridge"}],"leases":[{"address":"192.168.88.10","mac-address":"00:11:22:33:44:55","host-name":"nas01","server":"lan","status":"bound"}]}
            """, at);
        reading.Complete();

        var sfp = reading.Ports.Single(p => p.Name == "sfp-sfpplus1");
        Assert.AreEqual("smf-10g", sfp.DetectedType);
        Assert.AreEqual(10_000, sfp.RateMbps);
        Assert.AreEqual("FS", sfp.Facts!.SfpVendor);
        Assert.AreEqual("baset-1g", reading.Ports.Single(p => p.Name == "ether1").DetectedType);
        Assert.AreEqual("bridge", reading.Ports.Single(p => p.Name == "ether1").Bridge);
        var vlan = reading.Ports.Single(p => p.Name == "vlan20");
        Assert.AreEqual(("sfp-sfpplus1", 20), (vlan.VlanParent, vlan.VlanId));
        Assert.IsNull(vlan.DetectedType);

        var hosts = reading.Observations.Where(o => o.Source == ObservationSource.BridgeHost).ToList();
        Assert.AreEqual(1, hosts.Count, "local and multicast entries are left out");
        Assert.AreEqual(("ether1", 1), (hosts[0].Interface, hosts[0].VlanId));
        var neighbor = reading.Observations.Single(o => o.Source == ObservationSource.Neighbor);
        Assert.AreEqual(("ether1", "bridge", "ether24", "core-sw", "192.168.88.2"), (neighbor.Interface, neighbor.Bridge, neighbor.RemoteInterface, neighbor.Identity, neighbor.Ip));
        Assert.AreEqual("lldp,mndp", neighbor.DiscoveredBy);
        var arp = reading.Observations.Single(o => o.Source == ObservationSource.Arp);
        Assert.AreEqual(("sfp-sfpplus1", 20, "vlan20"), (arp.Interface, arp.VlanId, arp.Bridge), "ARP on a VLAN goes to its parent port");
        var lease = reading.Observations.Single(o => o.Source == ObservationSource.DhcpLease);
        Assert.AreEqual(("nas01", "bridge", "192.168.88.10"), (lease.HostName, lease.Interface, lease.Ip));
    }

    [TestMethod]
    public void Output_that_is_no_JSON_fails_the_table()
    {
        var reading = new NetworkScanReading();
        Assert.ThrowsExactly<FormatException>(() => reading.Add(R1, ScanTable.Arp, "bad command name print (line 1 column 10)", DateTime.UtcNow));
        Assert.IsFalse(reading.Read.Contains(ScanTable.Arp));
    }

    // --- paths -------------------------------------------------------------------------------------------------------------

    private static PathEdge E(int id, string a, string b, string type = "baset-1g") => new(new Guid(id, 0, 0, new byte[8]), a, $"{a}-p", b, $"{b}-p", type);

    [TestMethod]
    public void A_chain_is_all_single_points_and_a_ring_has_none()
    {
        var chain = new[] { E(1, "A", "B"), E(2, "B", "C") };
        var path = TopologyPaths.Find(chain, "A", "C")!;
        CollectionAssert.AreEqual(new[] { "B", "C" }, path.Hops.Select(h => h.To).ToArray());
        Assert.AreEqual(2, path.SinglePoints.Count);
        Assert.AreEqual(("A-p", "B-p"), (path.Hops[0].FromPort, path.Hops[0].ToPort));

        var ring = new[] { E(1, "A", "B"), E(2, "B", "C"), E(3, "C", "A") };
        var direct = TopologyPaths.Find(ring, "A", "C")!;
        Assert.AreEqual(1, direct.Hops.Count);
        Assert.AreEqual(0, direct.SinglePoints.Count);
    }

    [TestMethod]
    public void Ties_go_to_the_faster_path_and_virtual_links_can_be_left_out()
    {
        var edges = new[] { E(1, "A", "B", "baset-100m"), E(2, "B", "D", "smf-10g"), E(3, "A", "C", "smf-10g"), E(4, "C", "D", "dac-10g"), E(5, "A", "D", "wireguard") };
        var any = TopologyPaths.Find(edges, "A", "D")!;
        Assert.AreEqual(1, any.Hops.Count);
        Assert.AreEqual(1, any.VirtualHops);
        var physical = TopologyPaths.Find(edges, "A", "D", physicalOnly: true)!;
        CollectionAssert.AreEqual(new[] { "C", "D" }, physical.Hops.Select(h => h.To).ToArray(), "10G over 100M");
        Assert.AreEqual(10_000, physical.SlowestMbps);
        Assert.IsNull(TopologyPaths.Find(edges, "A", "Z"));
        Assert.IsNull(TopologyPaths.Find(edges, "A", "A"));
    }

    [TestMethod]
    public void More_than_twelve_hops_is_no_path()
    {
        var edges = Enumerable.Range(0, 13).Select(i => E(i + 1, $"N{i}", $"N{i + 1}")).ToList();
        Assert.IsNull(TopologyPaths.Find(edges, "N0", "N13"));
        Assert.IsNotNull(TopologyPaths.Find(edges, "N0", "N12"));
    }

    [TestMethod]
    public void Ends_have_one_key_whichever_comes_first()
    {
        var a = new ConnectionEnd(R1, R1, "Ether1");
        var b = new ConnectionEnd(Guid.NewGuid(), null, null);
        Assert.AreEqual(ConnectionEnd.PairKey(a, b), ConnectionEnd.PairKey(b, a));
        Assert.AreEqual(ConnectionEnd.PairKey(a, b), ConnectionEnd.PairKey(a with { Interface = "ether1" }, b), "interfaces ignore case");
        Assert.AreEqual(JsonSerializer.Serialize(ConnectionEnd.Ordered(a, b)), JsonSerializer.Serialize(ConnectionEnd.Ordered(b, a)));
    }
}
