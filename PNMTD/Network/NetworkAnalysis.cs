using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Network;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Network;

/// <summary>What one analysis did, for the CLI and the tests.</summary>
public sealed record NetworkAnalysisOutcome(int Interfaces, int OpenSuggestions, int Confirmed, int Unconfirmed, TimeSpan Duration);

/// <summary>
/// Runs <see cref="TopologyAnalyzer"/> on everything stored (docs/network-map-plan.md §5.1) and writes what it found: the ports'
/// roles, the network interfaces with their matches, the suggestions, and the connections' confirmations (§5.5). One at a
/// time; at the end of each run, and at most every <c>Analysis:IntervalMinutes</c> during a long one.
/// </summary>
public sealed class NetworkAnalysis(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<NetworkMapOptions> options, ILogger<NetworkAnalysis> logger)
    : IDisposable
{
    private bool disposed;

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            soon?.Dispose();
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim gate = new(1, 1);

    public DateTime? LastRunAt { get; private set; }

    /// <summary>Whether a run during a scan is due: the last one is older than the interval.</summary>
    public bool Due => LastRunAt is not { } last || Clock.UtcNow - last >= TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.Analysis.IntervalMinutes));

    private Timer? soon;

    /// <summary>
    /// After a person's change (an assignment, an ignore, a connection): an analysis in a few seconds, so the suggestions follow
    /// without a scan. Several changes in a row make one run.
    /// </summary>
    public void RequestSoon(TimeSpan? delay = null)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            soon?.Dispose();
            soon = new Timer(_ => _ = RunQuietlyAsync(), null, delay ?? TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RunQuietlyAsync()
    {
        try
        {
            await RunAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // PNMTD stops: the next scan analyses again.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The network analysis failed");
        }
    }

    public async Task<NetworkAnalysisOutcome> RunAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            var outcome = await RunOnAsync(db, ct);
            LastRunAt = Clock.UtcNow;
            logger.LogInformation("Network analysis: {Interfaces} interfaces, {Open} open suggestions, {Confirmed} connections confirmed in {Ms} ms",
                outcome.Interfaces, outcome.OpenSuggestions, outcome.Confirmed, (int)outcome.Duration.TotalMilliseconds);
            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>One analysis on a context of the caller's (the seed): load, analyse, write, save.</summary>
    public static async Task<NetworkAnalysisOutcome> RunOnAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var started = Clock.UtcNow;
        var input = await LoadAsync(db, ct);
        var result = TopologyAnalyzer.Analyze(input);
        var now = Clock.UtcNow;
        await WriteRolesAsync(db, result, ct);
        var interfaces = await WriteInterfacesAsync(db, result, now, ct);
        var open = await WriteSuggestionsAsync(db, result, now, ct);
        var (confirmed, unconfirmed) = await WriteChecksAsync(db, result, now, ct);
        await WriteHitsAsync(db, result, now, ct);
        await db.SaveChangesAsync(ct);
        return new NetworkAnalysisOutcome(interfaces, open, confirmed, unconfirmed, Clock.UtcNow - started);
    }

    /// <summary>The analysis' input, as stored.</summary>
    public static async Task<TopologyInput> LoadAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var routerRows = await db.MikrotikRouters.AsNoTracking()
            .Select(r => new { r.SensorId, HostId = r.Sensor.ParentId, Sensor = r.Sensor.Name, Host = r.Sensor.Parent.Name, r.Identity, r.Sensor.Parent.LocationId })
            .ToListAsync(ct);
        var perHost = routerRows.GroupBy(r => r.HostId).ToDictionary(g => g.Key, g => g.Count());
        var routers = routerRows.Select(r => new { r.SensorId, r.HostId, Name = NetworkDirectory.RouterName(r.Host, r.Sensor, perHost[r.HostId]), r.Identity }).ToList();
        var scanned = await db.NetworkRouterSnapshots.AsNoTracking().ToDictionaryAsync(s => s.SensorId, s => s.ScannedAt, ct);
        var hosts = await db.Hosts.AsNoTracking().Select(h => new TopologyHost(h.Id, h.Name)).ToListAsync(ct);
        var ports = await db.NetworkRouterPorts.AsNoTracking().ToListAsync(ct);
        var observations = await db.NetworkObservations.AsNoTracking().ToListAsync(ct);
        var adapters = await db.AgentAdapterMacs.AsNoTracking()
            .Join(db.Sensors, m => m.SensorId, s => s.Id, (m, s) => new { m.Mac, m.Adapter, m.SpeedBps, m.Virtual, m.Kind, m.SensorId, HostId = s.ParentId })
            .ToListAsync(ct);
        var interfaces = await db.NetworkInterfaces.AsNoTracking()
            .Where(i => i.AssignedHostId != null || i.Ignored)
            .Select(i => new { i.Mac, i.AssignedHostId, i.AssignedInterface, i.Ignored })
            .ToListAsync(ct);
        var connections = await db.NetworkConnections.AsNoTracking().ToListAsync(ct);
        var rules = await db.NetworkSkipRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
        IReadOnlyList<TopologyRule> topologyRules = rules.Count == 0 ? [] : NetworkSkipRules.ForAnalysis(rules, routerRows.ToDictionary(r => r.SensorId, r => r.LocationId),
            await LocationTree.LoadAsync(db, ct));
        return new TopologyInput
        {
            Routers = routers.Select(r => new TopologyRouter(r.SensorId, r.HostId, r.Name, r.Identity, scanned.TryGetValue(r.SensorId, out var at) ? at : null)).ToList(),
            Hosts = hosts,
            Ports = ports.Select(p => new RouterPort(p.SensorId, p.Name)
            {
                DefaultName = p.DefaultName,
                Type = p.Type,
                Mac = p.Mac,
                Mtu = p.Mtu,
                Running = p.Running,
                Disabled = p.Disabled,
                Comment = p.Comment,
                Bridge = p.Bridge,
                VlanParent = p.VlanParent,
                VlanId = p.VlanId,
                Bond = p.Bond,
                RateMbps = p.RateMbps,
                FullDuplex = p.FullDuplex,
                DetectedType = p.DetectedType,
            }).ToList(),
            Observations = observations.Select(o => new Observation(o.SensorId, o.Source)
            {
                Interface = o.Interface,
                Bridge = o.Bridge,
                VlanId = o.VlanId,
                Mac = o.Mac,
                Ip = o.Ip,
                HostName = o.HostName,
                Identity = o.Identity,
                Platform = o.Platform,
                Board = o.Board,
                Version = o.Version,
                RemoteInterface = o.RemoteInterface,
                DiscoveredBy = o.DiscoveredBy,
                RemoteKey = o.RemoteKey,
                Note = o.Note,
                SeenAt = o.SeenAt,
            }).ToList(),
            Adapters = adapters.Select(a => new TopologyAdapter(a.HostId, a.SensorId, a.Mac, a.Adapter, a.SpeedBps, a.Virtual, a.Kind)).ToList(),
            Assignments = interfaces.Where(i => i.AssignedHostId is not null).Select(i => new TopologyAssignment(i.Mac, i.AssignedHostId!.Value, i.AssignedInterface)).ToList(),
            Ignored = interfaces.Where(i => i.Ignored).Select(i => i.Mac).ToHashSet(StringComparer.Ordinal),
            Connections = connections.Select(c => new TopologyConnection(c.Id, c.A, c.B, c.Type)).ToList(),
            Rules = topologyRules,
        };
    }

    private static async Task WriteRolesAsync(PnmtdDbContext db, TopologyResult result, CancellationToken ct)
    {
        var roles = result.Roles.ToDictionary(r => (r.RouterId, r.Port));
        foreach (var port in await db.NetworkRouterPorts.ToListAsync(ct))
        {
            var (role, count) = roles.TryGetValue((port.SensorId, port.Name), out var r) ? (r.Role, r.MacCount) : (PortRole.Edge, 0);
            if (port.Role != role || port.MacCount != count)
            {
                port.Role = role;
                port.MacCount = count;
            }
        }
    }

    private static async Task<int> WriteInterfacesAsync(PnmtdDbContext db, TopologyResult result, DateTime now, CancellationToken ct)
    {
        var existing = await db.NetworkInterfaces.ToDictionaryAsync(i => i.Mac, StringComparer.Ordinal, ct);
        foreach (var r in result.Interfaces)
        {
            if (!existing.TryGetValue(r.Mac, out var row))
            {
                row = new NetworkInterfaceEntity { Mac = r.Mac, FirstSeenAt = r.LastSeenAt };
                db.NetworkInterfaces.Add(row);
                existing[r.Mac] = row;
            }
            row.Ips = r.Ips.ToList();
            row.HostName = Cut(r.HostName, 255);
            row.Random = r.Random;
            row.Sources = r.Sources;
            row.AttachedSensorId = r.AttachedRouterId;
            row.AttachedPort = r.AttachedPort;
            row.AttachedVlan = r.AttachedVlan;
            row.AttachedNote = Cut(r.AttachedNote, 500);
            row.NeighborIdentity = Cut(r.NeighborIdentity, 255);
            row.NeighborPlatform = Cut(r.NeighborPlatform, 255);
            row.NeighborBoard = Cut(r.NeighborBoard, 255);
            row.NeighborPort = Cut(r.NeighborPort, 200);
            row.MatchKind = r.Match;
            row.MatchHostId = r.MatchHostId;
            row.MatchSensorId = r.MatchSensorId;
            row.MatchInterface = r.MatchInterface;
            row.HintHostId = r.HintHostId;
            row.SkippedRuleId = r.SkippedBy;
            if (r.LastSeenAt > row.LastSeenAt)
            {
                row.LastSeenAt = r.LastSeenAt;
            }
            if (row.FirstSeenAt == default || row.FirstSeenAt > row.LastSeenAt)
            {
                row.FirstSeenAt = row.LastSeenAt;
            }
        }
        return result.Interfaces.Count;
    }

    private static async Task<int> WriteSuggestionsAsync(PnmtdDbContext db, TopologyResult result, DateTime now, CancellationToken ct)
    {
        var existing = await db.NetworkSuggestions.ToDictionaryAsync(s => s.Key, StringComparer.Ordinal, ct);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var open = 0;
        foreach (var s in result.Suggestions)
        {
            found.Add(s.Key);
            if (!existing.TryGetValue(s.Key, out var row))
            {
                row = new NetworkSuggestionEntity { Id = Guid.NewGuid(), Key = s.Key, FirstSeenAt = now, State = NetworkSuggestionState.Open };
                db.NetworkSuggestions.Add(row);
            }
            else if (row.State == NetworkSuggestionState.Accepted && row.ConnectionId is null)
            {
                // Its connection was deleted, and the routers still show it: a suggestion again.
                row.State = NetworkSuggestionState.Open;
                row.DecidedAt = null;
                row.DecidedBy = null;
                row.DecidedByName = null;
            }
            row.Kind = s.Kind;
            row.AHostId = s.A.HostId;
            row.ASensorId = s.A.SensorId;
            row.AInterface = s.A.Interface;
            row.BHostId = s.B.HostId;
            row.BSensorId = s.B.SensorId;
            row.BInterface = s.B.Interface;
            row.Type = s.Type;
            row.Confidence = s.Confidence;
            row.Evidence = JsonSerializer.Serialize(s.Evidence, Json);
            row.DiffersFrom = s.DiffersFrom;
            row.LastSeenAt = now;
            if (row.State == NetworkSuggestionState.Open)
            {
                open++;
            }
        }
        // An open suggestion whose evidence is gone goes; accepted and dismissed ones stay (§5.4).
        db.NetworkSuggestions.RemoveRange(existing.Values.Where(s => s.State == NetworkSuggestionState.Open && !found.Contains(s.Key)));
        return open;
    }

    private static async Task<(int Confirmed, int Unconfirmed)> WriteChecksAsync(PnmtdDbContext db, TopologyResult result, DateTime now, CancellationToken ct)
    {
        var numbers = await db.NetworkRouterSnapshots.AsNoTracking().ToDictionaryAsync(s => s.SensorId, s => s.ScanNumber, ct);
        var checks = result.Checks.ToDictionary(c => c.ConnectionId);
        int confirmed = 0, unconfirmed = 0;
        foreach (var c in await db.NetworkConnections.ToListAsync(ct))
        {
            if (!checks.TryGetValue(c.Id, out var check))
            {
                continue;
            }
            // The scans of its routers so far: a new scan of either counts as one look; a full run scans both and is analysed once.
            var scans = check.Routers.Sum(r => numbers.GetValueOrDefault(r));
            if (check.Evidenced)
            {
                c.ConfirmedAt = now;
                c.Misses = 0;
                c.UnconfirmedSince = null;
            }
            else if (check.Routers.Count > 0 && scans != c.CheckedScans && c.ConfirmedAt is not null)
            {
                // MAC tables age out: a device that was off once is no warning, two scans without it are (§5.5).
                c.Misses++;
                if (c.Misses >= 2 && c.UnconfirmedSince is null)
                {
                    c.UnconfirmedSince = now;
                }
            }
            c.CheckedScans = scans;
            if (c.ConfirmedAt is not null && c.UnconfirmedSince is null)
            {
                confirmed++;
            }
            else if (c.UnconfirmedSince is not null)
            {
                unconfirmed++;
            }
        }
        return (confirmed, unconfirmed);
    }

    /// <summary>Each rule's hits at this analysis; a disabled rule has none (docs/network-suggestion-rules-plan.md §4.4).</summary>
    private static async Task WriteHitsAsync(PnmtdDbContext db, TopologyResult result, DateTime now, CancellationToken ct)
    {
        var hits = result.Hits.ToDictionary(h => h.RuleId);
        foreach (var rule in await db.NetworkSkipRules.ToListAsync(ct))
        {
            (rule.Hits, rule.HitRouters) = hits.TryGetValue(rule.Id, out var hit) ? (hit.Rows, hit.Routers) : (0, 0);
            rule.HitsAt = now;
        }
    }

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
