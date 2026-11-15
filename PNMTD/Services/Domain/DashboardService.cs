using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Services.Domain;

/// <summary>
/// <c>GET /api/v1/dashboard</c> (docs/webui-migration-plan.md §5.5): one response with everything the
/// dashboard shows. Built by <see cref="DashboardCache"/>; it holds no secrets and is the same for
/// every role.
/// </summary>
public sealed class DashboardService(
    PnmtdDbContext db,
    MonitoringReader reader,
    EventService events,
    SystemService system,
    IConfiguration configuration,
    LocationTree locations)
{
    public const int Days = 90;

    public const int RecentChangesShown = 15;

    public Task<Dashboard> BuildAsync(CancellationToken ct) => BuildCoreAsync(ct);

    private sealed record HostRow(Guid Id, string Name, string? Location, Guid? LocationId, bool Enabled);

    /// <summary>
    /// Some hosts as the dashboard's panel shows them, built now rather than from the cache: a location's page
    /// (docs/locations-knowledge-plan.md §3.4).
    /// </summary>
    public async Task<IReadOnlyList<DashboardHost>> HostsAsync(Expression<Func<HostEntity, bool>> filter, CancellationToken ct)
    {
        var hosts = await db.Hosts.AsNoTracking().Where(filter)
            .Select(h => new HostRow(h.Id, h.Name, h.Location, h.LocationId, h.Enabled)).ToListAsync(ct);
        if (hosts.Count == 0)
        {
            return [];
        }
        var ids = hosts.Select(h => h.Id).ToList();
        var snapshot = await reader.SnapshotAsync(s => ids.Contains(s.ParentId), ct);
        var days = await reader.DaysAsync(HostService.ActiveIds(snapshot.Sensors.Values), Days, ct);
        return HostRows(hosts, snapshot, days, await locations.GetAsync(ct));
    }

    /// <summary>Hosts: failing first, then by name; disabled ones last. Each with its location's path (§3.2).</summary>
    private static List<DashboardHost> HostRows(IEnumerable<HostRow> hosts, MonitoringSnapshot snapshot, UptimeTable days, LocationTreeSnapshot tree) =>
        hosts.Select(h =>
            {
                var sensors = snapshot.OfHost(h.Id).ToList();
                var activeIds = HostService.ActiveIds(sensors);
                var state = MonitoringSnapshot.HostStateOf(h.Enabled, sensors);
                return new DashboardHost(h.Id, h.Name, h.Location, h.Enabled, state,
                    sensors.Count(s => s.IsActive && s.State == SensorState.Failing),
                    days.AverageOverall(activeIds, 30), days.Series(days.Average(activeIds)), HostService.LocationOf(tree, h.LocationId));
            })
            .OrderBy(h => HostService.StateOrder(h.State) == 0 ? 0 : h.Enabled ? 1 : 2)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task<Dashboard> BuildCoreAsync(CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var hosts = await db.Hosts.AsNoTracking().Select(h => new HostRow(h.Id, h.Name, h.Location, h.LocationId, h.Enabled)).ToListAsync(ct);
        var snapshot = await reader.SnapshotAsync(null, ct);
        var all = snapshot.Sensors.Values.ToList();
        var active = all.Where(s => s.IsActive).ToList();
        var days = await reader.DaysAsync(active.Select(s => s.Info.Id).ToList(), Days, ct);

        var hostRows = HostRows(hosts, snapshot, days, await locations.GetAsync(ct));

        // Problems: failing active sensors by host, the most important and then the longest-failing first
        // (docs/ai-operations-plan.md §11.1).
        var failing = active.Where(s => s.State == SensorState.Failing).ToList();
        var hours = await reader.HoursAsync(failing.Select(s => s.Info.Id).ToList(), 24, ct);
        var problems = failing
            .GroupBy(s => (s.Info.HostId, s.Info.HostName))
            .Select(g => new ProblemHost(g.Key.HostId, g.Key.HostName, g
                .OrderByDescending(s => s.Info.EffectiveImportance)
                .ThenBy(s => s.FailingSince ?? s.LastEventAt ?? now)
                .ThenBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new ProblemSensor(s.Info.Id, s.Info.Name, s.Info.Type, s.LastCode, s.LastMessage, s.FailingSince,
                    s.LastEventAt, hours.Values(s.Info.Id)))
                .ToList()))
            .OrderByDescending(p => failing.Where(s => s.Info.HostId == p.Id).Max(s => s.Info.EffectiveImportance))
            .ThenBy(p => p.Sensors.Min(s => s.FailingSince ?? s.LastEventAt ?? now))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var recent = await events.ResolveAsync(active.Select(s => s.Info.Id).ToList(), now.AddHours(-24), now, RecentChangesShown,
            id => (snapshot.Sensors[id].Info.Name, snapshot.Sensors[id].Info.HostId, snapshot.Sensors[id].Info.HostName), ct);

        var hostCounts = new HostCounts(hostRows.Count,
            hostRows.Count(h => h.State == HostState.Ok),
            hostRows.Count(h => h.State == HostState.Failing),
            hostRows.Count(h => h.State == HostState.NoData),
            hostRows.Count(h => h.State == HostState.Disabled));

        var tasks = await system.TasksAsync(ct);
        var running = tasks.Where(t => t.State != "off").ToList();
        var taskSummary = new TaskSummary(running.Count(t => t.State == "ok"), running.Count(t => t.State == "late"),
            running.Count(t => t.State == "notRun"), running.Max(t => t.LastRun));

        var zones = await db.DnsZones.AsNoTracking().Select(z => new { z.Enabled, z.RecordsMatch }).ToListAsync(ct);
        var dns = new DnsSummary(zones.Count, zones.Count(z => z.Enabled && z.RecordsMatch), zones.Count(z => z.Enabled && !z.RecordsMatch),
            zones.Count(z => !z.Enabled));

        var keyValues = await system.KeyValuesAsync(ct);
        var mail = new MailSummary(SystemService.MailboxConfigured(configuration),
            SystemService.Timestamp(keyValues, KeyValueKeyEnums.LAST_MAILBOX_CHECK),
            await db.MailLogs.CountAsync(m => !m.Processed, ct));

        var summary = new DashboardSummary(hostCounts, MonitoringSnapshot.Count(all), await system.UplinkAsync(ct), taskSummary, dns, mail);
        return new Dashboard(now, Clock.TimeZone.Id, summary, problems, recent, hostRows);
    }
}

/// <summary>
/// Every open browser polls the dashboard, and building it reads up to 90 days of events (about a
/// second at 300 sensors with 2000 events each). So requests get the last build at once, and a build
/// older than <see cref="MaxAge"/> is replaced in the background: stale while revalidating. Only the
/// first request after a start, or after <see cref="Invalidate"/>, waits for a build.
/// </summary>
public sealed class DashboardCache(IServiceScopeFactory scopes, ILogger<DashboardCache> logger)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    /// <summary>How old a build may be for <i>Refresh</i>: anything older waits for a new one.</summary>
    public static readonly TimeSpan FreshAge = TimeSpan.FromSeconds(2);

    private sealed record Entry(Dashboard Dashboard, long BuiltAt);

    private readonly object gate = new();
    private Entry? current;
    private Task<Entry>? building;
    private int generation;

    /// <summary>
    /// The last build at once (the polls), or with <paramref name="fresh"/> one at most <see cref="FreshAge"/> old, waiting
    /// for it if need be (the dashboard's <i>Refresh</i>). A build already running is shared either way.
    /// </summary>
    public async Task<Dashboard> GetAsync(bool fresh = false)
    {
        var entry = Volatile.Read(ref current);
        if (entry is null || (fresh && System.Diagnostics.Stopwatch.GetElapsedTime(entry.BuiltAt) > FreshAge))
        {
            return (await StartBuild()).Dashboard;
        }
        if (System.Diagnostics.Stopwatch.GetElapsedTime(entry.BuiltAt) > MaxAge)
        {
            _ = StartBuild();
        }
        return entry.Dashboard;
    }

    /// <summary>
    /// The next request waits for a build that starts after this call (tests, and changes that
    /// should show at once). A build already running finishes but is not kept.
    /// </summary>
    public void Invalidate()
    {
        lock (gate)
        {
            current = null;
            building = null;
            generation++;
        }
    }

    /// <summary>One build at a time; callers during a build share it.</summary>
    private Task<Entry> StartBuild()
    {
        lock (gate)
        {
            var started = generation;
            return building ??= Task.Run(() => BuildAsync(started));
        }
    }

    private async Task<Entry> BuildAsync(int started)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var dashboard = await scope.ServiceProvider.GetRequiredService<DashboardService>().BuildAsync(CancellationToken.None);
            var entry = new Entry(dashboard, System.Diagnostics.Stopwatch.GetTimestamp());
            lock (gate)
            {
                if (generation == started)
                {
                    current = entry;
                    building = null;
                }
            }
            return entry;
        }
        catch (Exception ex)
        {
            // The last build stays; the next request tries again.
            logger.LogError(ex, "Building the dashboard failed");
            lock (gate)
            {
                if (generation == started)
                {
                    building = null;
                }
            }
            throw;
        }
    }
}
