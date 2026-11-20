using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Network;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;

namespace PNMTD.Network;

/// <summary>
/// The map's reads (docs/network-map-plan.md §8.2, §8.3, §10): the graph (no positions: the client lays it out), a device's
/// panel, the connection types, the summary and the path between two devices.
/// </summary>
public sealed class NetworkMapService(PnmtdDbContext db, LocationTree locations, MonitoringReader monitoring, NetworkScanService scans)
{
    public Task<NetworkDirectory> DirectoryAsync(CancellationToken ct) => NetworkDirectory.LoadAsync(db, locations, ct);

    public static IReadOnlyList<NetworkConnectionTypeItem> ConnectionTypeList() =>
        ConnectionTypes.All.Select(t => new NetworkConnectionTypeItem(t.Code, t.Label, t.Medium, t.SpeedMbps, t.Physical)).ToList();

    // --- the graph (§8.2) ------------------------------------------------------------------------------------------------

    public async Task<NetworkGraph> GraphAsync(NetworkGraphQuery query, CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        var connections = await db.NetworkConnections.AsNoTracking().ToListAsync(ct);
        var states = await StatesAsync(d, ct);

        // Routers: all, those of the chosen locations, or the failing ones.
        var mode = (query.Routers ?? "all").Trim().ToLowerInvariant();
        var routerLocations = Within(d, query.RouterLocationIds);
        var routers = d.Routers.Values.Where(r => mode switch
        {
            "locations" => r.LocationId is { } l && routerLocations.Contains(l),
            "problems" => states.GetValueOrDefault($"router:{r.SensorId}") is "Failing",
            _ => true,
        }).ToList();

        // Hosts: none by default; those of the chosen locations, only with a connection unless asked.
        var hostLocations = Within(d, query.HostLocationIds);
        var connected = connections.SelectMany(c => new[] { c.A, c.B }).Where(e => e.SensorId is null).Select(e => e.HostId).ToHashSet();
        var hosts = d.Hosts.Values.Where(h => h.LocationId is { } l && hostLocations.Contains(l) && (!h.HasRouter || connected.Contains(h.HostId))
            && (query.OnlyConnected == false || connected.Contains(h.HostId))).ToList();

        var nodes = new Dictionary<string, (NetworkDeviceKind Kind, Guid HostId, Guid? SensorId, string Name, Guid? Location, string? Os)>(StringComparer.Ordinal);
        foreach (var r in routers)
        {
            nodes[$"router:{r.SensorId}"] = (NetworkDeviceKind.Router, r.HostId, r.SensorId, r.Name, r.LocationId, null);
        }
        foreach (var h in hosts)
        {
            nodes[$"host:{h.HostId}"] = (NetworkDeviceKind.Host, h.HostId, null, h.Name, h.LocationId, h.Os);
        }

        var edges = new List<NetworkEdge>();
        var hidden = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in connections.OrderBy(c => c.Id))
        {
            var type = ConnectionTypes.Find(c.Type);
            var physical = ConnectionTypes.IsPhysical(c.Type);
            if (query.Physical is { } p && p != physical || query.Media is { Count: > 0 } media && (type is null || !media.Contains(type.Medium)))
            {
                continue;
            }
            var source = NetworkDirectory.NodeId(c.A);
            var target = NetworkDirectory.NodeId(c.B);
            if (!nodes.ContainsKey(source) || !nodes.ContainsKey(target))
            {
                foreach (var n in new[] { source, target }.Where(nodes.ContainsKey))
                {
                    hidden[n] = hidden.GetValueOrDefault(n) + 1;
                }
                continue;
            }
            edges.Add(new NetworkEdge(c.Id, source, target, c.AInterface, c.BInterface, c.Type, ConnectionTypes.Label(c.Type), physical, type?.SpeedMbps,
                NetworkViews.Evidence(c), false, null));
        }
        if (query.Suggestions == true)
        {
            var open = await db.NetworkSuggestions.AsNoTracking()
                .Where(s => s.State == NetworkSuggestionState.Open && s.Kind == SuggestionKind.Link).ToListAsync(ct);
            foreach (var s in open.OrderByDescending(s => s.Confidence).ThenBy(s => s.Id))
            {
                var source = NetworkDirectory.NodeId(s.A);
                var target = NetworkDirectory.NodeId(s.B);
                if (nodes.ContainsKey(source) && nodes.ContainsKey(target))
                {
                    edges.Add(new NetworkEdge(s.Id, source, target, s.AInterface, s.BInterface, s.Type, s.Type is null ? null : ConnectionTypes.Label(s.Type),
                        s.Type is null || ConnectionTypes.IsPhysical(s.Type), s.Type is null ? null : ConnectionTypes.Find(s.Type)?.SpeedMbps,
                        NetworkScanEvidence.Manual, true, s.Confidence));
                }
            }
        }

        var positions = await NetworkPositionService.AllAsync(db, ct);
        var nodeList = nodes.OrderBy(n => n.Value.Kind).ThenBy(n => n.Value.Name, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Key, StringComparer.Ordinal)
            .Select(n =>
            {
                var saved = n.Value.SensorId is { } s ? positions.GetValueOrDefault(s) : null;
                return new NetworkNode(n.Key, n.Value.Kind, n.Value.HostId, n.Value.SensorId, n.Value.Name, n.Value.Location, d.SiteOf(n.Value.Location),
                    states.GetValueOrDefault(n.Key) ?? "NoData", n.Value.Os, hidden.GetValueOrDefault(n.Key), saved?.X, saved?.Y);
            })
            .ToList();
        var groups = nodeList.Select(n => n.SiteId).OfType<Guid>().Distinct()
            .Select(s => new NetworkGroup(s, d.SiteName(s) ?? "?")).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new NetworkGraph(nodeList, groups, edges, await NetworkScanSchedule.LastFullAsync(db, ct), positions.Count);
    }

    private static HashSet<Guid> Within(NetworkDirectory d, List<Guid>? ids) =>
        (ids ?? []).SelectMany(d.Tree.SelfAndDescendants).ToHashSet();

    /// <summary>Each node's state: a router's sensor, a host's worst sensor (§8.2).</summary>
    private async Task<Dictionary<string, string>> StatesAsync(NetworkDirectory d, CancellationToken ct)
    {
        var snapshot = await monitoring.SnapshotAsync(null, ct);
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in d.Routers.Values)
        {
            states[$"router:{r.SensorId}"] = snapshot.Sensors.TryGetValue(r.SensorId, out var s) ? s.State.ToString() : "NoData";
        }
        foreach (var group in snapshot.Sensors.Values.GroupBy(s => s.Info.HostId))
        {
            if (d.Hosts.TryGetValue(group.Key, out var h))
            {
                states[$"host:{h.HostId}"] = MonitoringSnapshot.HostStateOf(h.Enabled, group).ToString();
            }
        }
        return states;
    }

    // --- a device (§8.3) -------------------------------------------------------------------------------------------------

    public async Task<NetworkDeviceDetail> DeviceAsync(NetworkDeviceKind kind, Guid id, bool withInterfaces, CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        var states = await StatesAsync(d, ct);
        var connections = await db.NetworkConnections.AsNoTracking()
            .Where(c => kind == NetworkDeviceKind.Router ? c.ASensorId == id || c.BSensorId == id
                : (c.AHostId == id && c.ASensorId == null) || (c.BHostId == id && c.BSensorId == null))
            .ToListAsync(ct);
        var items = connections.Select(c => NetworkViews.Connection(c, d)).OrderBy(c => c.A.Interface ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        if (kind == NetworkDeviceKind.Router)
        {
            if (!d.Routers.TryGetValue(id, out var r))
            {
                throw ApiException.NotFound("Router");
            }
            var router = await db.MikrotikRouters.AsNoTracking().Where(x => x.SensorId == id)
                .Select(x => new { x.LastError, x.Mode }).SingleAsync(ct);
            var ports = await db.NetworkRouterPorts.AsNoTracking().Where(p => p.SensorId == id).ToListAsync(ct);
            var suggestions = await db.NetworkSuggestions.AsNoTracking()
                .Where(s => s.State == NetworkSuggestionState.Open && (s.ASensorId == id || s.BSensorId == id)).ToListAsync(ct);
            var portItems = ports.OrderBy(p => PortOrder(p)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p =>
            {
                var connection = connections.FirstOrDefault(c => At(c.A, id, p.Name) || At(c.B, id, p.Name));
                var suggestion = suggestions.FirstOrDefault(s => At(s.A, id, p.Name) || At(s.B, id, p.Name));
                return new NetworkPortItem(p.Name, p.DefaultName, p.Type, p.Mac, p.Running, p.Disabled, p.Comment, p.Bridge, p.VlanParent, p.VlanId, p.Bond,
                    p.RateMbps, p.FullDuplex, p.DetectedType, p.DetectedType is null ? null : ConnectionTypes.Label(p.DetectedType), p.Role, p.MacCount,
                    NetworkViews.Sfp(p.Sfp), connection?.Id, connection is null ? null : d.Label(At(connection.A, id, p.Name) ? connection.B : connection.A),
                    suggestion?.Id);
            }).ToList();
            var seen = db.NetworkInterfaces.AsNoTracking().Where(i => i.AttachedSensorId == id);
            var position = await db.NetworkRouterPositions.AsNoTracking().Where(p => p.SensorId == id)
                .Select(p => new NetworkRouterPosition(p.X, p.Y, p.UpdatedAt, p.UpdatedByName)).SingleOrDefaultAsync(ct);
            return new NetworkDeviceDetail(
                $"router:{id}", NetworkDeviceKind.Router, r.HostId, id, r.Name, d.Hosts.TryGetValue(r.HostId, out var host) ? host.Name : "?",
                r.LocationId, r.LocationId is { } l && d.Tree.Contains(l) ? d.Tree.Path(l) : null,
                states.GetValueOrDefault($"router:{id}") ?? "NoData", router.LastError, r.Model, r.Version, r.Identity, null,
                await ScanOfAsync(id, ct), items, portItems, [],
                withInterfaces ? (await seen.OrderBy(i => i.AttachedPort).ThenBy(i => i.Mac).Take(500).ToListAsync(ct)).Select(i => NetworkViews.Interface(i, d)).ToList() : null,
                await seen.CountAsync(ct), await seen.CountAsync(i => i.MatchKind == MatchKind.None || i.MatchKind == MatchKind.Ambiguous, ct),
                suggestions.Count, position);
        }
        if (!d.Hosts.TryGetValue(id, out var h))
        {
            throw ApiException.NotFound("Host");
        }
        var adapters = await db.AgentAdapterMacs.AsNoTracking()
            .Where(m => db.Sensors.Any(s => s.Id == m.SensorId && s.ParentId == id))
            .OrderBy(m => m.Adapter).ToListAsync(ct);
        var mine = db.NetworkInterfaces.AsNoTracking().Where(i => i.MatchHostId == id || i.AssignedHostId == id);
        var openSuggestions = await db.NetworkSuggestions.AsNoTracking()
            .CountAsync(s => s.State == NetworkSuggestionState.Open && ((s.AHostId == id && s.ASensorId == null) || (s.BHostId == id && s.BSensorId == null)), ct);
        return new NetworkDeviceDetail(
            $"host:{id}", NetworkDeviceKind.Host, id, null, h.Name, h.Name, h.LocationId, h.LocationId is { } hl && d.Tree.Contains(hl) ? d.Tree.Path(hl) : null,
            states.GetValueOrDefault($"host:{id}") ?? "NoData", null, null, null, null, h.Os, null, items, [],
            adapters.Select(a => new NetworkAdapterItem(a.Adapter, a.Mac, a.Kind, a.SpeedBps, a.Virtual)).ToList(),
            withInterfaces ? (await mine.OrderBy(i => i.Mac).Take(500).ToListAsync(ct)).Select(i => NetworkViews.Interface(i, d)).ToList() : null,
            await mine.CountAsync(ct), 0, openSuggestions);
    }

    private static bool At(ConnectionEnd end, Guid router, string port) =>
        end.SensorId == router && string.Equals(end.Interface, port, StringComparison.OrdinalIgnoreCase);

    /// <summary>Physical ports first, then bonds and Wi-Fi, then the virtual ones.</summary>
    private static int PortOrder(NetworkRouterPortEntity p) => p.Type switch
    {
        "ether" => 0,
        "bond" or "wifi" or "wlan" => 1,
        "wg" or "eoip" or "gre-tunnel" or "ipip-tunnel" => 2,
        _ => 3,
    };

    private async Task<NetworkDeviceScan?> ScanOfAsync(Guid sensorId, CancellationToken ct)
    {
        var snapshot = await db.NetworkRouterSnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.SensorId == sensorId, ct);
        var last = await db.NetworkScanItems.AsNoTracking().Where(i => i.SensorId == sensorId && i.FinishedAt != null)
            .OrderByDescending(i => i.FinishedAt).FirstOrDefaultAsync(ct);
        if (snapshot is null && last is null)
        {
            return null;
        }
        return new NetworkDeviceScan(snapshot?.ScannedAt, last?.State, last?.Error, NetworkViews.Map<string>(snapshot?.TableErrors ?? last?.TableErrors),
            snapshot?.DiscoverySettings);
    }

    // --- the summary and paths (§10.2) -----------------------------------------------------------------------------------

    public async Task<NetworkMapSummary> SummaryAsync(CancellationToken ct)
    {
        var connections = await db.NetworkConnections.AsNoTracking().Select(c => new { c.Type, c.ASensorId, c.BSensorId, c.AHostId, c.BHostId, c.UnconfirmedSince })
            .ToListAsync(ct);
        var suggestions = await db.NetworkSuggestions.AsNoTracking().Where(s => s.State == NetworkSuggestionState.Open)
            .GroupBy(s => s.Confidence).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var hosts = connections.SelectMany(c => new[] { c.ASensorId is null ? c.AHostId : (Guid?)null, c.BSensorId is null ? c.BHostId : null })
            .OfType<Guid>().Distinct().Count();
        return new NetworkMapSummary(
            await db.MikrotikRouters.CountAsync(ct),
            hosts,
            connections.Count,
            connections.Count(c => ConnectionTypes.IsPhysical(c.Type)),
            connections.Count(c => !ConnectionTypes.IsPhysical(c.Type)),
            connections.Count(c => c.UnconfirmedSince is not null),
            connections.GroupBy(c => ConnectionTypes.Find(c.Type)?.Medium.ToString() ?? "Other").OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            suggestions.OrderByDescending(s => s.Key).ToDictionary(s => s.Key.ToString(), s => s.Count),
            await db.NetworkInterfaces.CountAsync(i => !i.Ignored && (i.MatchKind == MatchKind.None || i.MatchKind == MatchKind.Ambiguous), ct),
            await scans.StatusAsync(ct));
    }

    public async Task<NetworkPath?> PathAsync(string from, string to, bool physicalOnly, CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        var a = d.ParseDevice(from) ?? throw ApiException.Invalid("from", "A router (router:{id}) or a host (host:{id}).");
        var b = d.ParseDevice(to) ?? throw ApiException.Invalid("to", "A router (router:{id}) or a host (host:{id}).");
        var connections = await db.NetworkConnections.AsNoTracking().ToListAsync(ct);
        var byId = connections.ToDictionary(c => c.Id);
        var edges = connections.Select(c => new PathEdge(c.Id, NetworkDirectory.NodeId(c.A), c.AInterface, NetworkDirectory.NodeId(c.B), c.BInterface, c.Type)).ToList();
        var path = TopologyPaths.Find(edges, NetworkDirectory.NodeId(a), NetworkDirectory.NodeId(b), physicalOnly);
        if (path is null)
        {
            return null;
        }
        var hops = path.Hops.Select(h =>
        {
            var c = byId[h.Edge.ConnectionId];
            var (fromEnd, toEnd) = NetworkDirectory.NodeId(c.A) == h.From ? (c.A, c.B) : (c.B, c.A);
            return new NetworkPathHop(c.Id, d.End(fromEnd), d.End(toEnd), c.Type, ConnectionTypes.Label(c.Type), ConnectionTypes.IsPhysical(c.Type),
                NetworkViews.Evidence(c), c.ConfirmedAt);
        }).ToList();
        return new NetworkPath(d.End(a), d.End(b), hops, path.SlowestMbps is { } s ? ConnectionTypes.SpeedLabel(s) : null, path.VirtualHops, path.SinglePoints,
            await NetworkScanSchedule.LastFullAsync(db, ct));
    }
}
