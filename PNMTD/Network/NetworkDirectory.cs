using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Network;
using PNMTD.Locations;
using PNMTD.Models.Db;

namespace PNMTD.Network;

/// <summary>
/// The names the network map shows, read once per request (docs/network-map-plan.md §8): the routers (their sensors), the
/// hosts, and the location tree with each location's site (its top-level location, decision 1).
/// </summary>
public sealed class NetworkDirectory
{
    public sealed record RouterRow(Guid SensorId, Guid HostId, string Name, bool Enabled, Guid? LocationId, string? Identity, string? Model, string? Version);

    public sealed record HostRow(Guid HostId, string Name, bool Enabled, Guid? LocationId, string? Os, bool HasRouter);

    public required IReadOnlyDictionary<Guid, RouterRow> Routers { get; init; }

    public required IReadOnlyDictionary<Guid, HostRow> Hosts { get; init; }

    public required LocationTreeSnapshot Tree { get; init; }

    /// <summary>The routers' VLANs and bridges: (router, port, lower case) → "VLAN 10 on ether1", "bridge".</summary>
    public IReadOnlyDictionary<(Guid, string), string> VirtualPorts { get; init; } = new Dictionary<(Guid, string), string>();

    /// <summary>The skip rules' names (docs/network-suggestion-rules-plan.md §4).</summary>
    public IReadOnlyDictionary<Guid, string> SkipRules { get; init; } = new Dictionary<Guid, string>();

    public static async Task<NetworkDirectory> LoadAsync(PnmtdDbContext db, LocationTree tree, CancellationToken ct)
    {
        var rows = await db.MikrotikRouters.AsNoTracking()
            .Select(r => new { r.SensorId, HostId = r.Sensor.ParentId, Sensor = r.Sensor.Name, Host = r.Sensor.Parent.Name, r.Sensor.Enabled, r.Sensor.Parent.LocationId,
                r.Identity, Model = r.Model ?? r.BoardName, r.Version })
            .ToListAsync(ct);
        var perHost = rows.GroupBy(r => r.HostId).ToDictionary(g => g.Key, g => g.Count());
        var routers = rows.Select(r => new RouterRow(r.SensorId, r.HostId, RouterName(r.Host, r.Sensor, perHost[r.HostId]), r.Enabled, r.LocationId, r.Identity,
            r.Model, r.Version)).ToList();
        var routerHosts = routers.Select(r => r.HostId).ToHashSet();
        var servers = await db.Sensors.AsNoTracking()
            .Where(s => s.Type == SensorType.WINDOWS || s.Type == SensorType.LINUX)
            .Select(s => new { s.ParentId, s.Type })
            .ToListAsync(ct);
        var os = servers.GroupBy(s => s.ParentId).ToDictionary(g => g.Key, g => g.First().Type == SensorType.WINDOWS ? "Windows" : "Linux");
        var hosts = await db.Hosts.AsNoTracking().Select(h => new { h.Id, h.Name, h.Enabled, h.LocationId }).ToListAsync(ct);
        var virtualPorts = await db.NetworkRouterPorts.AsNoTracking()
            .Where(p => p.Type == "vlan" || p.Type == "bridge" || p.VlanParent != null)
            .Select(p => new { p.SensorId, p.Name, p.Type, p.VlanParent, p.VlanId })
            .ToListAsync(ct);
        return new NetworkDirectory
        {
            Routers = routers.ToDictionary(r => r.SensorId),
            Hosts = hosts.ToDictionary(h => h.Id, h => new HostRow(h.Id, h.Name, h.Enabled, h.LocationId, os.GetValueOrDefault(h.Id), routerHosts.Contains(h.Id))),
            Tree = await tree.GetAsync(ct),
            VirtualPorts = virtualPorts.GroupBy(p => (p.SensorId, p.Name.ToLowerInvariant())).ToDictionary(g => g.Key, g => VirtualPort(g.First().Type, g.First().VlanParent, g.First().VlanId)),
            SkipRules = await db.NetworkSkipRules.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct),
        };
    }

    private static string VirtualPort(string? type, string? parent, int? vlanId) =>
        parent is not null || type == "vlan"
            ? $"VLAN{(vlanId is { } id ? $" {id}" : "")}{(parent is not null ? $" on {parent}" : "")}"
            : "a bridge";

    /// <summary>A router's port that is a VLAN or a bridge, as "VLAN 10 on ether1"; null for a port on the wire or a host's.</summary>
    public string? VirtualPortOf(ConnectionEnd end) =>
        end.SensorId is { } s && end.Interface is { Length: > 0 } i && VirtualPorts.TryGetValue((s, i.Trim().ToLowerInvariant()), out var text) ? text : null;

    /// <summary>
    /// A router as the map names it: its host's name when the host has only this router (a sensor is often just "router"),
    /// else "host / sensor".
    /// </summary>
    public static string RouterName(string host, string sensor, int routersOnHost) =>
        routersOnHost <= 1 ? host : $"{host} / {sensor}";

    /// <summary>The top-level location of a location: the circle it is drawn in.</summary>
    public Guid? SiteOf(Guid? locationId) =>
        locationId is { } l && Tree.Contains(l) ? Tree.Lineage(l)[0].Id : null;

    public string? SiteName(Guid? siteId) => siteId is { } s ? Tree.Find(s)?.Name : null;

    public Guid? LocationOf(ConnectionEnd end) => end.SensorId is { } s && Routers.TryGetValue(s, out var r) ? r.LocationId
        : Hosts.TryGetValue(end.HostId, out var h) ? h.LocationId : null;

    /// <summary>"router:{id}" or "host:{id}": a node of the map.</summary>
    public static string NodeId(ConnectionEnd end) => end.SensorId is { } s ? $"router:{s}" : $"host:{end.HostId}";

    public NetworkEndItem End(ConnectionEnd end)
    {
        var location = LocationOf(end);
        var site = SiteOf(location);
        if (end.SensorId is { } s)
        {
            var name = Routers.TryGetValue(s, out var r) ? r.Name : "(deleted router)";
            return new NetworkEndItem(NetworkDeviceKind.Router, end.HostId, s, name, end.Interface, site, SiteName(site), VirtualPortOf(end));
        }
        var host = Hosts.TryGetValue(end.HostId, out var h) ? h.Name : "(deleted host)";
        return new NetworkEndItem(NetworkDeviceKind.Host, end.HostId, null, host, end.Interface, site, SiteName(site));
    }

    /// <summary>"office-gw ether1", "srv-db".</summary>
    public string Label(ConnectionEnd end)
    {
        var item = End(end);
        return item.Interface is { Length: > 0 } i ? $"{item.Name} {i}" : item.Name;
    }

    /// <summary>Whether an end is in a location or below it.</summary>
    public bool Within(ConnectionEnd end, Guid locationId) =>
        LocationOf(end) is { } l && Tree.IsWithin(l, locationId);

    /// <summary>A device id from the API: a router's sensor or a host.</summary>
    public bool Is(ConnectionEnd end, Guid deviceId) => end.SensorId == deviceId || (end.SensorId is null && end.HostId == deviceId);

    /// <summary>Parses "router:{guid}" or "host:{guid}" (a bare guid: a router if it is one, else a host).</summary>
    public ConnectionEnd? ParseDevice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var parts = text.Trim().Split(':', 2);
        var kind = parts.Length == 2 ? parts[0].ToLowerInvariant() : null;
        if (!Guid.TryParse(parts[^1], out var id))
        {
            return null;
        }
        if (kind is null or "router" && Routers.TryGetValue(id, out var r))
        {
            return new ConnectionEnd(r.HostId, r.SensorId, null);
        }
        if (kind is null or "host" && Hosts.ContainsKey(id))
        {
            return new ConnectionEnd(id, null, null);
        }
        return null;
    }
}
