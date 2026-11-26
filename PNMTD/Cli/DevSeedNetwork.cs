using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Network;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Network;

namespace PNMTD.Cli;

/// <summary>
/// The seed's network map (docs/network-map-plan.md §10.3): what the five office routers' scans would have stored, and what
/// people made of it. St. Gallen's CCR is the hub: WireGuard to the other four, and a fibre to router-core in the Zurich DC,
/// behind which switch-core-01 and backup-srv are connected by hand. Open: Basel ↔ Bern's fibre, which both see by MNDP, and
/// nas-01 behind router-core. Then the analysis, so the tabs have content without a router.
/// </summary>
internal static class DevSeedNetwork
{
    private sealed record Office(string Host, string Identity, int Index, bool Fibre);

    private static readonly Office[] Offices =
    [
        new("office-st-gallen-gw", "st-gallen-gw", 1, true),
        new("office-basel-gw", "basel-gw", 2, true),
        new("office-bern-gw", "bern-gw", 3, true),
        new("office-geneva-gw", "geneva-gw", 4, false),
        new("office-lugano-gw", "lugano-gw", 5, false),
    ];

    private const string Hub = "office-st-gallen-gw";

    public static int Seed(Func<PnmtdDbContext> database, IReadOnlyDictionary<string, HostEntity> hosts, DateTime now)
    {
        using var db = database();
        var routers = db.MikrotikRouters.Include(r => r.Sensor).ToList()
            .Join(hosts, r => r.Sensor.ParentId, h => h.Value.Id, (r, h) => (Host: h.Key, Router: r))
            .ToDictionary(x => x.Host, x => x.Router);
        if (Offices.Any(o => !routers.ContainsKey(o.Host)))
        {
            return 0;
        }
        var scanned = now.AddDays(-2).AddHours(-3);
        string Mac(int office, int port) => $"D4:01:C3:{office:X2}:00:{port:X2}";
        var rows = 0;

        // --- the routers' tables --------------------------------------------------------------------------------------------
        foreach (var o in Offices)
        {
            var sensor = routers[o.Host].SensorId;
            var ports = new List<NetworkRouterPortEntity>();
            void Port(string name, string type, int n, int? rate, string? detected, string? bridge = null, string? sfp = null) => ports.Add(new NetworkRouterPortEntity
            {
                SensorId = sensor, Name = name, DefaultName = type == "ether" ? name : null, Type = type, Mac = Mac(o.Index, n), Running = rate is not null || type != "ether",
                Bridge = bridge, RateMbps = rate, FullDuplex = rate is null ? null : true, DetectedType = detected, Sfp = sfp, SeenAt = scanned,
            });
            Port("ether1", "ether", 1, 1000, "baset-1g", sfp: null);
            foreach (var i in Enumerable.Range(2, 4))
            {
                Port($"ether{i}", "ether", i, i == 5 && o.Index == 5 ? 100 : 1000, i == 5 && o.Index == 5 ? "baset-100m" : "baset-1g", bridge: "bridge");
            }
            if (o.Fibre)
            {
                Port("sfp-sfpplus1", "ether", 6, 10_000, "smf-10g",
                    sfp: JsonSerializer.Serialize(new PortFacts { Name = "sfp-sfpplus1", Type = "ether", Rate = "10Gbps", LinkUp = true, SfpModulePresent = true, SfpConnector = "LC",
                        SfpWavelength = "1310nm", SfpVendor = "FS", SfpPartNumber = "SFP-10GLR-31" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            Port("bridge", "bridge", 2, null, null);
            // Basel ↔ Bern: the fibre carries two VLANs, each seen by MNDP; the analysis puts them on the fibre (docs/network-suggestion-rules-plan.md §3).
            if (o.Host is "office-basel-gw" or "office-bern-gw")
            {
                foreach (var id in new[] { 10, 20 })
                {
                    ports.Add(new NetworkRouterPortEntity
                    {
                        SensorId = sensor, Name = $"vlan{id}", Type = "vlan", Mac = Mac(o.Index, 6), Running = true, VlanParent = "sfp-sfpplus1", VlanId = id, SeenAt = scanned,
                    });
                }
            }
            Port(o.Host == Hub ? "wg-offices" : "wg-hub", "wg", 9, null, ConnectionTypes.WireGuard);
            db.NetworkRouterPorts.AddRange(ports);
            db.NetworkRouterSnapshots.Add(new NetworkRouterSnapshotEntity
            {
                SensorId = sensor, ScannedAt = scanned, ScanNumber = 3, RouterOsVersion = routers[o.Host].Version, Raw = [],
                DiscoverySettings = """{"discover-interface-list":"all","protocol":["lldp","mndp"]}""",
            });
            rows += ports.Count;

            void See(ObservationSource source, string? port, string? mac = null, string? ip = null, string? host = null, string? identity = null,
                string? remote = null, string? key = null, string? by = null, string? note = null) => db.NetworkObservations.Add(new NetworkObservationEntity
            {
                SensorId = sensor, Source = source, Interface = port, Bridge = source == ObservationSource.BridgeHost ? "bridge" : null, Mac = mac, Ip = ip,
                HostName = host, Identity = identity, RemoteInterface = remote, RemoteKey = key, DiscoveredBy = by, Note = note, SeenAt = scanned,
                Platform = identity is null ? null : identity.EndsWith("-gw", StringComparison.Ordinal) ? "MikroTik" : "Cisco IOS XE",
                Version = identity?.EndsWith("-gw", StringComparison.Ordinal) == true ? "7.25.1" : null,
            });
            See(ObservationSource.Address, "ether1", ip: $"203.0.113.{o.Index * 10}/29");
            See(ObservationSource.Address, "bridge", ip: $"10.{o.Index}.0.1/24");
            See(ObservationSource.WireGuardInterface, o.Host == Hub ? "wg-offices" : "wg-hub", key: $"SEED-KEY-{o.Identity}");
            if (o.Host == Hub)
            {
                foreach (var other in Offices.Where(x => x.Host != Hub))
                {
                    See(ObservationSource.WireGuardPeer, "wg-offices", ip: $"203.0.113.{other.Index * 10}", key: $"SEED-KEY-{other.Identity}");
                }
                // The fibre to the Zurich DC: router-core speaks LLDP; behind it, the DC's servers.
                See(ObservationSource.Neighbor, "sfp-sfpplus1", mac: "00:1E:BD:00:00:01", ip: "10.0.0.1", identity: "router-core", remote: "Te0/1/0", by: "lldp");
                See(ObservationSource.BridgeHost, "sfp-sfpplus1", mac: "00:1E:BD:00:00:01");
                See(ObservationSource.BridgeHost, "sfp-sfpplus1", mac: "00:11:32:00:0A:01");
            }
            else
            {
                See(ObservationSource.WireGuardPeer, "wg-hub", ip: "203.0.113.10", key: "SEED-KEY-st-gallen-gw");
            }
            if (o.Host == "office-basel-gw")
            {
                See(ObservationSource.Neighbor, "sfp-sfpplus1", mac: Mac(3, 6), identity: "bern-gw", remote: "sfp-sfpplus1", by: "mndp");
                See(ObservationSource.Neighbor, "vlan10", mac: Mac(3, 6), identity: "bern-gw", remote: "vlan10", by: "mndp");
                See(ObservationSource.Neighbor, "vlan20", mac: Mac(3, 6), identity: "bern-gw", remote: "vlan20", by: "mndp");
            }
            if (o.Host == "office-bern-gw")
            {
                See(ObservationSource.Neighbor, "sfp-sfpplus1", mac: Mac(2, 6), identity: "basel-gw", remote: "sfp-sfpplus1", by: "mndp");
                See(ObservationSource.Neighbor, "vlan10", mac: Mac(2, 6), identity: "basel-gw", remote: "vlan10", by: "mndp");
                See(ObservationSource.Neighbor, "vlan20", mac: Mac(2, 6), identity: "basel-gw", remote: "vlan20", by: "mndp");
            }
            // The office LAN: a printer, laptops and phones by DHCP; on Lugano, the camera that was unplugged.
            var lan = new (string Mac, string Port, string Host)[]
            {
                ($"00:1B:A9:{o.Index:X2}:00:10", "ether2", $"printer-{o.Identity[..^3]}"),
                ($"3C:22:FB:{o.Index:X2}:00:21", "ether3", $"laptop-{o.Index}1"),
                ($"3C:22:FB:{o.Index:X2}:00:22", "ether3", $"laptop-{o.Index}2"),
                ($"DA:A1:19:{o.Index:X2}:00:31", "ether4", "iPhone"),
            };
            foreach (var (mac, port, host) in lan)
            {
                See(ObservationSource.BridgeHost, port, mac: mac);
                See(ObservationSource.DhcpLease, "bridge", mac: mac, ip: $"10.{o.Index}.0.{20 + Array.FindIndex(lan, l => l.Mac == mac)}", host: host, note: "bound");
                See(ObservationSource.Arp, "bridge", mac: mac, ip: $"10.{o.Index}.0.{20 + Array.FindIndex(lan, l => l.Mac == mac)}");
            }
            if (o.Host == "office-lugano-gw")
            {
                See(ObservationSource.WifiClient, "wifi1", mac: "6E:3A:02:00:00:41", note: "-58");
            }
        }

        // --- the servers' MACs, from their agents' network sections ----------------------------------------------------------
        foreach (var section in db.AgentSections.AsNoTracking().Where(s => s.Section == SectionNames.Network).ToList())
        {
            db.AgentAdapterMacs.AddRange(AgentMacIndex.Rows(section.SensorId, AgentSections.Parse<NetworkSection>(section.Content)));
        }

        // --- what people decided ----------------------------------------------------------------------------------------------
        Guid H(string name) => hosts[name].Id;
        ConnectionEnd R(string host, string port) => new(H(host), routers[host].SensorId, port);
        db.NetworkInterfaces.Add(new NetworkInterfaceEntity
        {
            Mac = "00:1E:BD:00:00:01", AssignedHostId = H("router-core"), AssignedInterface = "Te0/1/0", AssignedAt = now.AddDays(-20), AssignedByName = "philipp",
            FirstSeenAt = now.AddDays(-30), LastSeenAt = scanned, Sources = InterfaceSources.Neighbor | InterfaceSources.MacTable,
        });
        db.NetworkInterfaces.Add(new NetworkInterfaceEntity
        {
            Mac = "00:11:32:00:0A:01", AssignedHostId = H("nas-01"), AssignedInterface = "eth0", AssignedAt = now.AddDays(-20), AssignedByName = "philipp",
            FirstSeenAt = now.AddDays(-30), LastSeenAt = scanned, Sources = InterfaceSources.MacTable,
        });
        void Connection(ConnectionEnd a, ConnectionEnd b, string type, NetworkConnectionSource source, DateTime? confirmed, string? notes = null,
            DateTime? unconfirmedSince = null)
        {
            var (first, second) = ConnectionEnd.Ordered(a, b);
            db.NetworkConnections.Add(new NetworkConnectionEntity
            {
                Id = Guid.NewGuid(), Key = ConnectionEnd.PairKey(first, second), AHostId = first.HostId, ASensorId = first.SensorId, AInterface = first.Interface,
                BHostId = second.HostId, BSensorId = second.SensorId, BInterface = second.Interface, Type = type, Notes = notes, Source = source,
                ConfirmedAt = confirmed, UnconfirmedSince = unconfirmedSince, Misses = unconfirmedSince is null ? 0 : 2, CheckedScans = 6,
                CreatedAt = now.AddDays(-25), CreatedByName = "philipp",
            });
            rows++;
        }
        foreach (var o in Offices.Where(x => x.Host != Hub))
        {
            Connection(R(Hub, "wg-offices"), R(o.Host, "wg-hub"), ConnectionTypes.WireGuard, NetworkConnectionSource.Suggestion, scanned);
        }
        Connection(R(Hub, "sfp-sfpplus1"), new ConnectionEnd(H("router-core"), null, "Te0/1/0"), "smf-10g", NetworkConnectionSource.Suggestion, scanned,
            "Leased dark fibre St. Gallen – Zurich, Swisscom circuit CH-4471");
        Connection(new ConnectionEnd(H("router-core"), null, "Te0/1/1"), new ConnectionEnd(H("switch-core-01"), null, "sfp1"), "dac-10g", NetworkConnectionSource.Manual, null);
        Connection(new ConnectionEnd(H("switch-core-01"), null, "ge-0/0/12"), new ConnectionEnd(H("backup-srv"), null, "Ethernet0"), "baset-10g",
            NetworkConnectionSource.Manual, null, "Rack B6, patch panel 3, port 12");
        Connection(R("office-lugano-gw", "ether5"), new ConnectionEnd(H("camera-nvr"), null, "eth0"), "baset-100m", NetworkConnectionSource.Manual,
            now.AddDays(-20), unconfirmedSince: now.AddDays(-6));

        // --- the scans' history -----------------------------------------------------------------------------------------------
        var full = new NetworkScanEntity
        {
            Id = Guid.NewGuid(), Scope = NetworkScanScope.All, ScopeName = NetworkScanQueue.AllRouters, Trigger = NetworkScanTrigger.Manual, RequestedByName = "philipp",
            State = NetworkScanState.Done, Total = Offices.Length, Succeeded = Offices.Length, CreatedAt = scanned.AddMinutes(-4), StartedAt = scanned.AddMinutes(-4),
            FinishedAt = scanned,
        };
        db.NetworkScans.Add(full);
        foreach (var (o, i) in Offices.Select((o, i) => (o, i)))
        {
            db.NetworkScanItems.Add(new NetworkScanItemEntity
            {
                Id = Guid.NewGuid(), ScanId = full.Id, SensorId = routers[o.Host].SensorId, RouterName = routers[o.Host].Sensor.Name, Position = i,
                State = NetworkScanItemState.Succeeded, StartedAt = full.StartedAt!.Value.AddSeconds(i * 40), FinishedAt = full.StartedAt.Value.AddSeconds(i * 40 + 31),
                DurationMs = 31_000 + i * 900, Counts = $$"""{"ports":{{(o.Fibre ? 8 : 7)}},"macs":{{(o.Host == Hub ? 6 : 4)}},"arp":4,"leases":4,"neighbors":{{(o.Fibre ? 1 : 0)}},"peers":{{(o.Host == Hub ? 4 : 1)}},"wifi":{{(o.Index == 5 ? 1 : 0)}}}""",
            });
        }
        db.NetworkMapSettings.Add(new NetworkMapSettingsEntity { ScanScheduleEnabled = true, ScanIntervalDays = 14, ScanHour = 2, ChangedAt = now.AddDays(-25), ChangedByName = "philipp" });
        db.SaveChanges();
        NetworkAnalysis.RunOnAsync(db, CancellationToken.None).GetAwaiter().GetResult();
        return rows;
    }
}
