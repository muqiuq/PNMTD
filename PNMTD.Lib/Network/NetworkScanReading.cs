using System.Text.Json;
using PNMTD.Lib.Mikrotik;

namespace PNMTD.Lib.Network;

/// <summary>Where an observation comes from (docs/network-map-plan.md §4.1, §9.2).</summary>
public enum ObservationSource
{
    BridgeHost = 0,
    Arp = 1,
    DhcpLease = 2,
    Neighbor = 3,
    /// <summary>A WireGuard interface of the router itself: <see cref="Observation.RemoteKey"/> is its own public key.</summary>
    WireGuardInterface = 4,
    /// <summary>A WireGuard peer: <see cref="Observation.RemoteKey"/> is the peer's public key.</summary>
    WireGuardPeer = 5,
    /// <summary>An EoIP, GRE or IPIP tunnel: <see cref="Observation.RemoteKey"/> is its remote address.</summary>
    Tunnel = 6,
    /// <summary>An address of the router itself, on <see cref="Observation.Interface"/>.</summary>
    Address = 7,
    WifiClient = 8,
}

/// <summary>
/// One row a router showed: a MAC table entry, an ARP entry, a lease, a neighbor, a tunnel peer, a Wi-Fi client, or one of
/// the router's own addresses and WireGuard keys. The fields a source doesn't have are null.
/// </summary>
public sealed record Observation(Guid RouterId, ObservationSource Source)
{
    /// <summary>The router's port or L3 interface: the bridge host's <c>on-interface</c>, the ARP entry's interface, …</summary>
    public string? Interface { get; init; }

    public string? Bridge { get; init; }

    public int? VlanId { get; init; }

    public string? Mac { get; init; }

    /// <summary>An IP, or for <see cref="ObservationSource.Address"/> the address with its prefix.</summary>
    public string? Ip { get; init; }

    public string? HostName { get; init; }

    public string? Identity { get; init; }

    public string? Platform { get; init; }

    public string? Board { get; init; }

    public string? Version { get; init; }

    /// <summary>A neighbor's own port (<c>interface-name</c>).</summary>
    public string? RemoteInterface { get; init; }

    /// <summary>MNDP, CDP, LLDP.</summary>
    public string? DiscoveredBy { get; init; }

    /// <summary>A public key, or a tunnel's remote address.</summary>
    public string? RemoteKey { get; init; }

    /// <summary>A comment, a DHCP server's name, a Wi-Fi signal: shown, not matched.</summary>
    public string? Note { get; init; }

    public DateTime SeenAt { get; init; }
}

/// <summary>One of a router's interfaces, with what the scan read about its link and module (§9.2).</summary>
public sealed record RouterPort(Guid RouterId, string Name)
{
    public string? DefaultName { get; init; }

    public string? Type { get; init; }

    public string? Mac { get; init; }

    public int? Mtu { get; init; }

    public bool Running { get; init; }

    public bool Disabled { get; init; }

    public string? Comment { get; init; }

    /// <summary>The bridge the port is in.</summary>
    public string? Bridge { get; init; }

    /// <summary>For a VLAN interface, its parent interface; and its VLAN ID.</summary>
    public string? VlanParent { get; init; }

    public int? VlanId { get; init; }

    /// <summary>The bond the port is a member of.</summary>
    public string? Bond { get; init; }

    public int? RateMbps { get; init; }

    public bool? FullDuplex { get; init; }

    public PortFacts? Facts { get; init; }

    /// <summary>The proposed type (§3.3).</summary>
    public string? DetectedType { get; init; }
}

/// <summary>
/// What one scan of one router read (§4.2): its interfaces and the rows of each table, and the tables that failed. Parsed per
/// table, because each table is its own exec and may fail alone.
/// </summary>
public sealed class NetworkScanReading
{
    public List<RouterPort> Ports { get; } = [];

    public List<Observation> Observations { get; } = [];

    /// <summary>The tables that succeeded: only their rows replace the stored ones (§4.3).</summary>
    public HashSet<ScanTable> Read { get; } = [];

    public Dictionary<ScanTable, string> Errors { get; } = [];

    /// <summary>The neighbor discovery settings, as the router printed them (for "neighbor discovery is off on …").</summary>
    public string? DiscoverySettings { get; set; }

    /// <summary>Each table's output, as it came (gzip-stored with the snapshot, §4.3).</summary>
    public Dictionary<ScanTable, string> Raw { get; } = [];

    private readonly Dictionary<string, PortFacts> facts = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> bridgeOf = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (string Parent, int VlanId)> vlanOf = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> bondOf = new(StringComparer.Ordinal);

    /// <summary>Records a table's output; throws <see cref="FormatException"/> when it is not the expected JSON.</summary>
    public void Add(Guid routerId, ScanTable table, string output, DateTime seenAt)
    {
        Raw[table] = output;
        using var document = Parse(output);
        var root = document.RootElement;
        switch (table)
        {
            case ScanTable.Interfaces:
                foreach (var i in Array(root))
                {
                    if (i.Text("name") is not { Length: > 0 } name)
                    {
                        continue;
                    }
                    Ports.Add(new RouterPort(routerId, name)
                    {
                        DefaultName = i.Text("default-name"),
                        Type = i.Text("type"),
                        Mac = Mac.Normalize(i.Text("mac-address")),
                        Mtu = i.Int("actual-mtu") ?? i.Int("mtu"),
                        Running = i.Bool("running") ?? false,
                        Disabled = i.Bool("disabled") ?? false,
                        Comment = i.Text("comment"),
                    });
                }
                break;
            case ScanTable.Ethernet:
                var monitor = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("monitor", out var m) ? m : default;
                foreach (var e in root.Items("ports"))
                {
                    if (e.Text("name") is not { Length: > 0 } name)
                    {
                        continue;
                    }
                    var mon = monitor.ValueKind == JsonValueKind.Object && monitor.TryGetProperty(name, out var one) ? one : default;
                    facts[name] = Facts(e, mon, name);
                }
                break;
            case ScanTable.Bridges:
                foreach (var p in root.Items("ports"))
                {
                    if (p.Text("interface") is { Length: > 0 } port && p.Text("bridge") is { Length: > 0 } bridge)
                    {
                        bridgeOf[port] = bridge;
                    }
                }
                foreach (var v in root.Items("vlans"))
                {
                    if (v.Text("name") is { Length: > 0 } name && v.Text("interface") is { Length: > 0 } parent && v.Int("vlan-id") is { } id)
                    {
                        vlanOf[name] = (parent, id);
                    }
                }
                foreach (var b in root.Items("bonds"))
                {
                    if (b.Text("name") is { Length: > 0 } name)
                    {
                        foreach (var member in b.List("slaves"))
                        {
                            bondOf[member] = name;
                        }
                    }
                }
                break;
            case ScanTable.MacTable:
                foreach (var h in Array(root))
                {
                    if (h.Bool("local") == true || Mac.Normalize(h.Text("mac-address")) is not { } mac || Mac.IsMulticast(mac))
                    {
                        continue;
                    }
                    Observations.Add(new Observation(routerId, ObservationSource.BridgeHost)
                    {
                        Interface = h.Text("on-interface") ?? h.Text("interface"),
                        Bridge = h.Text("bridge"),
                        VlanId = h.Int("vid"),
                        Mac = mac,
                        SeenAt = seenAt,
                    });
                }
                break;
            case ScanTable.Neighbors:
                foreach (var n in root.Items("neighbors"))
                {
                    var interfaces = n.List("interface");
                    Observations.Add(new Observation(routerId, ObservationSource.Neighbor)
                    {
                        // On a bridge port RouterOS names both, "ether3,bridge": the port first.
                        Interface = interfaces.FirstOrDefault(),
                        Bridge = interfaces.Count > 1 ? interfaces[^1] : null,
                        Mac = Mac.Normalize(n.Text("mac-address")),
                        Ip = FirstAddress(n.Text("address4") ?? n.Text("address")),
                        Identity = n.Text("identity"),
                        Platform = n.Text("platform"),
                        Board = n.Text("board"),
                        Version = n.Text("version"),
                        RemoteInterface = n.Text("interface-name"),
                        DiscoveredBy = n.List("discovered-by") is { Count: > 0 } by ? string.Join(",", by) : null,
                        SeenAt = seenAt,
                    });
                }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("settings", out var settings))
                {
                    DiscoverySettings = settings.GetRawText();
                }
                break;
            case ScanTable.Arp:
                foreach (var a in Array(root))
                {
                    if (Mac.Normalize(a.Text("mac-address")) is not { } mac || Mac.IsMulticast(mac))
                    {
                        continue;
                    }
                    Observations.Add(new Observation(routerId, ObservationSource.Arp)
                    {
                        Interface = a.Text("interface"),
                        Mac = mac,
                        Ip = a.Text("address"),
                        SeenAt = seenAt,
                    });
                }
                break;
            case ScanTable.Dhcp:
                var serverInterface = root.Items("servers").Where(s => s.Text("name") is not null)
                    .GroupBy(s => s.Text("name")!).ToDictionary(g => g.Key, g => g.First().Text("interface"));
                foreach (var l in root.Items("leases"))
                {
                    var mac = Mac.Normalize(l.Text("active-mac-address")) ?? Mac.Normalize(l.Text("mac-address"));
                    if (mac is null)
                    {
                        continue;
                    }
                    var server = l.Text("server");
                    Observations.Add(new Observation(routerId, ObservationSource.DhcpLease)
                    {
                        Interface = server is not null && serverInterface.TryGetValue(server, out var iface) ? iface : null,
                        Mac = mac,
                        Ip = l.Text("active-address") ?? l.Text("address"),
                        HostName = l.Text("host-name"),
                        Note = l.Text("status"),
                        SeenAt = seenAt,
                    });
                }
                break;
            case ScanTable.Addresses:
                foreach (var a in Array(root))
                {
                    if (a.Text("address") is { Length: > 0 } address)
                    {
                        Observations.Add(new Observation(routerId, ObservationSource.Address)
                        {
                            Interface = a.Text("interface"),
                            Ip = address,
                            Note = a.Text("network"),
                            SeenAt = seenAt,
                        });
                    }
                }
                break;
            case ScanTable.Tunnels:
                foreach (var w in root.Items("wireguard"))
                {
                    Observations.Add(new Observation(routerId, ObservationSource.WireGuardInterface)
                    {
                        Interface = w.Text("name"),
                        RemoteKey = w.Text("public-key"),
                        SeenAt = seenAt,
                    });
                }
                foreach (var p in root.Items("peers"))
                {
                    Observations.Add(new Observation(routerId, ObservationSource.WireGuardPeer)
                    {
                        Interface = p.Text("interface"),
                        RemoteKey = p.Text("public-key"),
                        Ip = p.Text("endpoint-address"),
                        Note = p.List("allowed-address") is { Count: > 0 } allowed ? string.Join(",", allowed) : null,
                        SeenAt = seenAt,
                    });
                }
                foreach (var kind in new[] { "eoip", "gre", "ipip" })
                {
                    foreach (var tunnel in root.Items(kind))
                    {
                        Observations.Add(new Observation(routerId, ObservationSource.Tunnel)
                        {
                            Interface = tunnel.Text("name"),
                            Ip = tunnel.Text("local-address"),
                            RemoteKey = tunnel.Text("remote-address"),
                            Note = kind,
                            SeenAt = seenAt,
                        });
                    }
                }
                break;
            case ScanTable.Wifi:
                foreach (var c in root.Items("wifi").Concat(root.Items("wireless")))
                {
                    if (Mac.Normalize(c.Text("mac-address")) is not { } mac)
                    {
                        continue;
                    }
                    Observations.Add(new Observation(routerId, ObservationSource.WifiClient)
                    {
                        Interface = c.Text("interface"),
                        Mac = mac,
                        Note = c.Text("signal") ?? c.Text("signal-strength"),
                        SeenAt = seenAt,
                    });
                }
                break;
        }
        Read.Add(table);
    }

    /// <summary>
    /// After the tables: each port gets its bridge, VLAN, bond and the Ethernet facts with the proposed type; ARP entries on a
    /// VLAN get its parent port. Call once, when every table that could be read is in.
    /// </summary>
    public void Complete()
    {
        for (var i = 0; i < Ports.Count; i++)
        {
            var p = Ports[i];
            facts.TryGetValue(p.Name, out var f);
            f = (f ?? new PortFacts { Name = p.Name, DefaultName = p.DefaultName }) with { Type = p.Type };
            var vlan = vlanOf.TryGetValue(p.Name, out var v) ? v : default;
            Ports[i] = p with
            {
                Bridge = bridgeOf.GetValueOrDefault(p.Name),
                VlanParent = vlan.Parent,
                VlanId = vlan.Parent is null ? null : vlan.VlanId,
                Bond = bondOf.GetValueOrDefault(p.Name),
                RateMbps = ConnectionTypeDetector.RateMbps(f.Rate),
                FullDuplex = f.FullDuplex,
                Facts = facts.ContainsKey(p.Name) ? f : null,
                DetectedType = ConnectionTypeDetector.Detect(f),
            };
        }
        for (var i = 0; i < Observations.Count; i++)
        {
            var o = Observations[i];
            if (o.Source is ObservationSource.Arp or ObservationSource.DhcpLease && o.Interface is { } l3 && vlanOf.TryGetValue(l3, out var vlan))
            {
                Observations[i] = o with { Interface = vlan.Parent, VlanId = vlan.VlanId, Bridge = l3 };
            }
        }
    }

    private static PortFacts Facts(JsonElement port, JsonElement monitor, string name)
    {
        var lengths = new Dictionary<string, string>(StringComparer.Ordinal);
        string? compliance = null;
        bool? present = null, linkUp = null, duplex = null;
        string? rate = null, sfpType = null, connector = null, wavelength = null, vendor = null, part = null;
        if (monitor.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in monitor.EnumerateObject())
            {
                if (property.Name.StartsWith("sfp-link-length-", StringComparison.Ordinal) && RouterOsValues.AsText(property.Value) is { } length)
                {
                    lengths[property.Name["sfp-link-length-".Length..]] = length;
                }
            }
            present = monitor.Bool("sfp-module-present");
            linkUp = monitor.Text("status") is { } status ? status == "link-ok" : null;
            duplex = monitor.Bool("full-duplex");
            rate = monitor.Text("rate");
            sfpType = monitor.Text("sfp-type");
            connector = monitor.Text("sfp-connector-type");
            wavelength = monitor.Text("sfp-wavelength");
            vendor = monitor.Text("sfp-vendor-name");
            part = monitor.Text("sfp-vendor-part-number");
            compliance = monitor.Text("sfp-ethernet-compliance-code") ?? monitor.Text("sfp-ethernet-compliance-codes")
                ?? (monitor.List("sfp-ethernet-compliance") is { Count: > 0 } codes ? string.Join(",", codes) : null);
        }
        return new PortFacts
        {
            Name = name,
            DefaultName = port.Text("default-name"),
            Rate = rate,
            LinkUp = linkUp,
            FullDuplex = duplex,
            SfpModulePresent = present,
            SfpType = sfpType,
            SfpConnector = connector,
            SfpWavelength = wavelength,
            SfpVendor = vendor,
            SfpPartNumber = part,
            SfpLinkLengths = lengths,
            SfpCompliance = compliance,
        };
    }

    private static string? FirstAddress(string? text) =>
        text?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static IEnumerable<JsonElement> Array(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Array => root.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList(),
        JsonValueKind.Object => [root],
        _ => [],
    };

    private static JsonDocument Parse(string output)
    {
        var json = output.Trim();
        try
        {
            return JsonDocument.Parse(json.Length == 0 ? "[]" : json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Unexpected output: {(json.Length <= 200 ? json : json[..200] + "…")}", ex);
        }
    }
}
