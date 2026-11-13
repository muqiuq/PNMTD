using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Events;
using PNMTD.UplinkCheck;

namespace PNMTD.Services.Domain;

/// <summary>The uplink page (docs/ai-operations-plan.md §7.3), from <c>uplink_checks</c> and the monitor's state.</summary>
public sealed class UplinkService(PnmtdDbContext db, UplinkMonitor monitor, SystemService system)
{
    /// <summary>The users of <c>TaskRunDecisionMaker</c>: they pause while the uplink is down, and 5 minutes after.</summary>
    public static readonly string[] PausedTasks = ["Heartbeat check", "Ping check", "MikroTik poller"];

    public async Task<UplinkOverview> OverviewAsync(bool admin, CancellationToken ct)
    {
        var values = await system.KeyValuesAsync(ct);
        var lastCheck = SystemService.Timestamp(values, KeyValueKeyEnums.LAST_UPLINK_CHECK);
        var lastSuccess = SystemService.Timestamp(values, KeyValueKeyEnums.LAST_UPLINK_CHECK_SUCCESSFULL);
        var status = await system.UplinkAsync(ct);
        var now = Clock.UtcNow;
        var weekStart = now.AddDays(-7);
        var checks = await db.UplinkChecks.AsNoTracking().Where(c => c.At >= weekStart).OrderBy(c => c.At).ToListAsync(ct);
        var dayStart = now.AddHours(-24);
        var endpoints = monitor.Endpoints.Select(e =>
        {
            var mine = checks.Where(c => c.Endpoint == e.Number).ToList();
            var day = mine.Where(c => c.At >= dayStart).ToList();
            var last = mine.LastOrDefault();
            var latencies = day.Where(c => c.Outcome == UplinkOutcome.Ok && c.LatencyMs is not null).Select(c => c.LatencyMs!.Value).Order().ToList();
            return new UplinkEndpointStats(e.Number, admin ? e.Url : e.Host, last?.Outcome, last is null ? null : Instants.Utc(last.At), last?.LatencyMs,
                Rate(day), Rate(mine), Percentile(latencies, 0.5), Percentile(latencies, 0.95), day.Count);
        }).ToList();
        var (failures, downSince) = monitor.State;
        var sensorId = await db.Sensors.Where(s => s.Type == SensorType.SYSTEM && s.Parameters == SystemSensorKeys.Uplink)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        return new UplinkOverview(monitor.Configured, status.IsUp, failures, UplinkMonitor.FailuresForDown, downSince, lastCheck, lastSuccess,
            lastCheck?.AddSeconds(UplinkMonitor.IntervalSeconds), UplinkMonitor.IntervalSeconds, (int)monitor.Timeout.TotalSeconds, monitor.SharedKeySet,
            endpoints, PausedTasks, 5, sensorId,
            Buckets(checks, now, TimeSpan.FromMinutes(30), 48), Buckets(checks, now, TimeSpan.FromHours(1), 7 * 24));
    }

    public async Task<Paged<UplinkCheckItem>> ChecksAsync(UplinkCheckQuery query, CancellationToken ct)
    {
        var checks = db.UplinkChecks.AsNoTracking();
        if (query.FailuresOnly == true)
        {
            checks = checks.Where(c => c.Outcome != UplinkOutcome.Ok);
        }
        if (query.Endpoint is { } endpoint)
        {
            checks = checks.Where(c => c.Endpoint == endpoint);
        }
        var page = await Paging.PageAsync(checks.OrderByDescending(c => c.At).ThenByDescending(c => c.Id), query, ct);
        return new Paged<UplinkCheckItem>(page.Items.Select(Item).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    /// <summary>The outages of the last <paramref name="days"/> days (at most 30), newest first.</summary>
    public async Task<IReadOnlyList<UplinkOutage>> OutagesAsync(int days, CancellationToken ct)
    {
        var since = Clock.UtcNow.AddDays(-Math.Clamp(days, 1, UplinkMonitor.KeepDays));
        var checks = await db.UplinkChecks.AsNoTracking().Where(c => c.At >= since).OrderBy(c => c.At).ToListAsync(ct);
        return Outages(checks, Clock.UtcNow);
    }

    internal static IReadOnlyList<UplinkOutage> Outages(IReadOnlyList<UplinkCheckEntity> checks, DateTime now)
    {
        var outages = new List<UplinkOutage>();
        var run = new List<UplinkCheckEntity>();
        void Close(DateTime? end)
        {
            if (run.Count >= UplinkMonitor.FailuresForDown)
            {
                var start = Instants.Utc(run[0].At);
                outages.Add(new UplinkOutage(start, end, (int)((end ?? now) - start).TotalSeconds, run[0].Error,
                    run.Select(c => c.Endpoint).Distinct().Order().ToList()));
            }
            run.Clear();
        }
        foreach (var check in checks)
        {
            if (check.Outcome == UplinkOutcome.Ok)
            {
                Close(Instants.Utc(check.At));
            }
            else
            {
                run.Add(check);
            }
        }
        Close(null);
        outages.Reverse();
        return outages;
    }

    public async Task<UplinkCheckItem> CheckNowAsync(CancellationToken ct) =>
        await monitor.CheckAsync(ct) is { } check
            ? Item(check)
            : throw ApiException.Conflict("uplink_not_configured", "The uplink check is not configured: UplinkCheck__SharedKey and two of UplinkCheck__Url1..3.");

    private static UplinkCheckItem Item(UplinkCheckEntity c) =>
        new(c.Id, Instants.Utc(c.At), c.Endpoint, c.EndpointHost, c.Outcome, c.HttpStatus, c.LatencyMs, c.Error);

    private static double? Rate(IReadOnlyCollection<UplinkCheckEntity> checks) =>
        checks.Count == 0 ? null : Math.Round((double)checks.Count(c => c.Outcome == UplinkOutcome.Ok) / checks.Count, 4);

    private static int? Percentile(IReadOnlyList<int> sorted, double p) =>
        sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    internal static IReadOnlyList<UplinkBucket> Buckets(IReadOnlyList<UplinkCheckEntity> checks, DateTime now, TimeSpan size, int count)
    {
        var end = new DateTime(now.Ticks - now.Ticks % size.Ticks + size.Ticks, DateTimeKind.Utc);
        var start = end - size * count;
        return Enumerable.Range(0, count).Select(i =>
        {
            var from = start + size * i;
            var to = from + size;
            var inside = checks.Where(c => c.At >= from && c.At < to).ToList();
            var latencies = inside.Where(c => c.Outcome == UplinkOutcome.Ok && c.LatencyMs is not null).Select(c => c.LatencyMs!.Value).Order().ToList();
            return new UplinkBucket(from, inside.Count(c => c.Outcome == UplinkOutcome.Ok), inside.Count(c => c.Outcome != UplinkOutcome.Ok),
                Percentile(latencies, 0.5));
        }).ToList();
    }
}
