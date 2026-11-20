using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Network;
using PNMTD.Models.Db;

namespace PNMTD.Network;

/// <summary>
/// Stores what one scan of one router read (docs/network-map-plan.md §4.3): the snapshot with each table's raw output, the
/// ports, and the observations. A table replaces the router's rows of its sources only when it succeeded; a failed table
/// keeps the previous scan's rows, with their older time.
/// </summary>
public static class NetworkScanStore
{
    /// <summary>The observations each table writes.</summary>
    public static IReadOnlyList<ObservationSource> SourcesOf(ScanTable table) => table switch
    {
        ScanTable.MacTable => [ObservationSource.BridgeHost],
        ScanTable.Neighbors => [ObservationSource.Neighbor],
        ScanTable.Arp => [ObservationSource.Arp],
        ScanTable.Dhcp => [ObservationSource.DhcpLease],
        ScanTable.Addresses => [ObservationSource.Address],
        ScanTable.Tunnels => [ObservationSource.WireGuardInterface, ObservationSource.WireGuardPeer, ObservationSource.Tunnel],
        ScanTable.Wifi => [ObservationSource.WifiClient],
        _ => [],
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task SaveAsync(PnmtdDbContext db, Guid sensorId, string? routerOsVersion, NetworkScanReading reading, DateTime scannedAt,
        CancellationToken ct)
    {
        var snapshot = await db.NetworkRouterSnapshots.SingleOrDefaultAsync(s => s.SensorId == sensorId, ct);
        var raw = snapshot is null ? new Dictionary<string, string>() : ReadRaw(snapshot.Raw);
        foreach (var (table, output) in reading.Raw.Where(r => reading.Read.Contains(r.Key)))
        {
            raw[table.ToString()] = output;
        }
        if (snapshot is null)
        {
            snapshot = new NetworkRouterSnapshotEntity { SensorId = sensorId };
            db.NetworkRouterSnapshots.Add(snapshot);
        }
        snapshot.ScannedAt = scannedAt;
        snapshot.ScanNumber++;
        snapshot.RouterOsVersion = routerOsVersion;
        snapshot.Raw = WriteRaw(raw);
        if (reading.Read.Contains(ScanTable.Neighbors))
        {
            snapshot.DiscoverySettings = reading.DiscoverySettings;
        }
        snapshot.TableErrors = reading.Errors.Count == 0 ? null : JsonSerializer.Serialize(reading.Errors.ToDictionary(e => e.Key.ToString(), e => e.Value), Json);

        if (reading.Read.Contains(ScanTable.Interfaces))
        {
            var old = await db.NetworkRouterPorts.Where(p => p.SensorId == sensorId).ToListAsync(ct);
            var before = old.ToDictionary(p => p.Name, StringComparer.Ordinal);
            db.NetworkRouterPorts.RemoveRange(old);
            await db.SaveChangesAsync(ct);
            foreach (var port in reading.Ports)
            {
                before.TryGetValue(port.Name, out var was);
                // Without this scan's Ethernet table, the link and module stay as the last scan read them.
                var keepLink = !reading.Read.Contains(ScanTable.Ethernet) && was is not null;
                db.NetworkRouterPorts.Add(new NetworkRouterPortEntity
                {
                    SensorId = sensorId,
                    Name = Cut(port.Name, 200)!,
                    DefaultName = port.DefaultName,
                    Type = port.Type,
                    Mac = port.Mac,
                    Mtu = port.Mtu,
                    Running = port.Running,
                    Disabled = port.Disabled,
                    Comment = Cut(port.Comment, 500),
                    Bridge = port.Bridge,
                    VlanParent = port.VlanParent,
                    VlanId = port.VlanId,
                    Bond = port.Bond,
                    RateMbps = keepLink ? was!.RateMbps : port.RateMbps,
                    FullDuplex = keepLink ? was!.FullDuplex : port.FullDuplex,
                    Sfp = keepLink ? was!.Sfp : port.Facts is { } f ? JsonSerializer.Serialize(f, Json) : null,
                    DetectedType = keepLink ? was!.DetectedType : port.DetectedType,
                    Role = was?.Role ?? PortRole.Edge,
                    MacCount = was?.MacCount ?? 0,
                    SeenAt = scannedAt,
                });
            }
        }

        foreach (var table in reading.Read)
        {
            var sources = SourcesOf(table);
            if (sources.Count == 0)
            {
                continue;
            }
            await db.NetworkObservations.Where(o => o.SensorId == sensorId && sources.Contains(o.Source)).ExecuteDeleteAsync(ct);
            foreach (var o in reading.Observations.Where(o => sources.Contains(o.Source)))
            {
                db.NetworkObservations.Add(new NetworkObservationEntity
                {
                    SensorId = sensorId,
                    Source = o.Source,
                    Interface = Cut(o.Interface, 200),
                    Bridge = Cut(o.Bridge, 200),
                    VlanId = o.VlanId,
                    Mac = o.Mac,
                    Ip = Cut(o.Ip, 100),
                    HostName = Cut(o.HostName, 255),
                    Identity = Cut(o.Identity, 255),
                    Platform = Cut(o.Platform, 255),
                    Board = Cut(o.Board, 255),
                    Version = Cut(o.Version, 255),
                    RemoteInterface = Cut(o.RemoteInterface, 200),
                    DiscoveredBy = Cut(o.DiscoveredBy, 50),
                    RemoteKey = Cut(o.RemoteKey, 200),
                    Note = Cut(o.Note, 500),
                    SeenAt = o.SeenAt,
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The counts the Scans tab shows for a router.</summary>
    public static Dictionary<string, int> Counts(NetworkScanReading reading) => new()
    {
        ["ports"] = reading.Ports.Count,
        ["macs"] = reading.Observations.Count(o => o.Source == ObservationSource.BridgeHost),
        ["arp"] = reading.Observations.Count(o => o.Source == ObservationSource.Arp),
        ["leases"] = reading.Observations.Count(o => o.Source == ObservationSource.DhcpLease),
        ["neighbors"] = reading.Observations.Count(o => o.Source == ObservationSource.Neighbor),
        ["peers"] = reading.Observations.Count(o => o.Source is ObservationSource.WireGuardPeer or ObservationSource.Tunnel),
        ["wifi"] = reading.Observations.Count(o => o.Source == ObservationSource.WifiClient),
    };

    public static Dictionary<string, string> ReadRaw(byte[] gzip)
    {
        if (gzip.Length == 0)
        {
            return [];
        }
        using var input = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd(), Json) ?? [];
    }

    private static byte[] WriteRaw(Dictionary<string, string> raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(raw, Json)));
        }
        return output.ToArray();
    }

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
