using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

/// <summary>What the state rules need of a sensor, read in one projection (no lazy loading).</summary>
public sealed record SensorInfo(
    Guid Id,
    Guid HostId,
    string HostName,
    bool HostEnabled,
    Guid? ParentSensorId,
    string Name,
    SensorType Type,
    bool Enabled,
    bool Ignore);

/// <summary>A sensor with its resolved state and its newest relevant event.</summary>
public sealed class SensorStatus(SensorInfo info)
{
    public SensorInfo Info { get; } = info;
    public SensorState State { get; set; }
    public int? LastCode { get; set; }
    public string? LastMessage { get; set; }
    public DateTime? LastEventAt { get; set; }
    public DateTime? FailingSince { get; set; }
    public List<SensorStatus> Children { get; } = [];

    /// <summary>ENCAPSULADED parents report through their children and are not counted themselves.</summary>
    public bool IsContainer => Info.Type == SensorType.ENCAPSULADED;

    /// <summary>Counted in the summary, the problems and uptime: not a container, not disabled, not ignored.</summary>
    public bool IsActive => !IsContainer && State is SensorState.Ok or SensorState.Failing or SensorState.NoData;

    public SensorSummary ToSummary() => new(Info.Id, Info.Name, Info.Type, Info.Enabled, Info.Ignore, State,
        LastCode, LastMessage, LastEventAt, FailingSince, Children.Count);
}

/// <summary>
/// The states of a set of sensors (see <see cref="SensorState"/> for the rules). Children of an
/// ENCAPSULADED sensor inherit its Disabled and Ignore; the parent takes the worst state of its
/// active children.
/// </summary>
public sealed class MonitoringSnapshot
{
    public IReadOnlyDictionary<Guid, SensorStatus> Sensors { get; }

    public MonitoringSnapshot(IReadOnlyList<SensorInfo> sensors, IReadOnlyDictionary<Guid, MonitoringQueries.LatestRow> latest)
    {
        var byId = sensors.ToDictionary(s => s.Id, s => new SensorStatus(s));
        foreach (var status in byId.Values)
        {
            if (status.Info.ParentSensorId is { } parentId && byId.TryGetValue(parentId, out var parent))
            {
                parent.Children.Add(status);
            }
            if (latest.TryGetValue(status.Info.Id, out var row))
            {
                status.LastCode = row.LastCode;
                status.LastMessage = row.LastMessage;
                status.LastEventAt = row.LastAt;
                status.FailingSince = row.FailingSince;
            }
        }
        foreach (var status in byId.Values)
        {
            var parent = status.Info.ParentSensorId is { } pid && byId.TryGetValue(pid, out var p) ? p.Info : null;
            status.State = OwnState(status, parent);
        }
        foreach (var container in byId.Values.Where(s => s.IsContainer && s.State == SensorState.NoData))
        {
            ResolveContainer(container);
        }
        Sensors = byId;
    }

    private static SensorState OwnState(SensorStatus status, SensorInfo? parent)
    {
        var info = status.Info;
        if (!info.Enabled || !info.HostEnabled || parent is { Enabled: false })
        {
            return SensorState.Disabled;
        }
        if (info.Ignore || parent is { Ignore: true })
        {
            return SensorState.Ignored;
        }
        if (status.LastCode is not { } code)
        {
            return SensorState.NoData;
        }
        return code <= Lib.Models.Poco.EventEntityPoco.END_OF_SUCCESS_CODES ? SensorState.Ok : SensorState.Failing;
    }

    private static void ResolveContainer(SensorStatus container)
    {
        var active = container.Children.Where(c => c.State is SensorState.Ok or SensorState.Failing).ToList();
        var failing = active.Where(c => c.State == SensorState.Failing).ToList();
        container.LastEventAt = container.Children.Max(c => c.LastEventAt);
        if (failing.Count > 0)
        {
            var newest = failing.MaxBy(c => c.LastEventAt)!;
            container.State = SensorState.Failing;
            container.LastCode = newest.LastCode;
            container.LastMessage = newest.LastMessage;
            container.FailingSince = failing.Min(c => c.FailingSince);
        }
        else if (active.Count > 0)
        {
            container.State = SensorState.Ok;
        }
    }

    public IEnumerable<SensorStatus> OfHost(Guid hostId) => Sensors.Values.Where(s => s.Info.HostId == hostId);

    public static SensorCounts Count(IEnumerable<SensorStatus> sensors)
    {
        int ok = 0, failing = 0, noData = 0, ignored = 0, disabled = 0;
        foreach (var s in sensors.Where(s => !s.IsContainer))
        {
            switch (s.State)
            {
                case SensorState.Ok: ok++; break;
                case SensorState.Failing: failing++; break;
                case SensorState.NoData: noData++; break;
                case SensorState.Ignored: ignored++; break;
                default: disabled++; break;
            }
        }
        return new SensorCounts(ok, failing, noData, ignored, disabled);
    }

    public static HostState HostStateOf(bool hostEnabled, IEnumerable<SensorStatus> sensors)
    {
        if (!hostEnabled)
        {
            return HostState.Disabled;
        }
        var active = sensors.Where(s => s.IsActive).ToList();
        if (active.Any(s => s.State == SensorState.Failing))
        {
            return HostState.Failing;
        }
        return active.Any(s => s.State == SensorState.Ok) ? HostState.Ok : HostState.NoData;
    }
}

/// <summary>Uptime per sensor and bucket, as fractions; null where there is no data.</summary>
public sealed class UptimeTable
{
    private readonly Dictionary<Guid, (double Up, double Known)[]> rows;

    public int Count { get; }

    /// <summary>The first day, for day buckets.</summary>
    public DateOnly Start { get; }

    public UptimeTable(int count, DateOnly start, IEnumerable<MonitoringQueries.BucketRow> buckets)
    {
        Count = count;
        Start = start;
        rows = [];
        foreach (var b in buckets)
        {
            if (!rows.TryGetValue(b.SensorId, out var row))
            {
                rows[b.SensorId] = row = new (double, double)[count];
            }
            if (b.Bucket >= 1 && b.Bucket <= count)
            {
                row[b.Bucket - 1] = (b.UpSeconds, b.KnownSeconds);
            }
        }
    }

    /// <summary>Overall values (30 days, 24 hours) keep four decimals; the per-bucket values of a bar three.</summary>
    public static double? Ratio(double up, double known, int decimals = 4) =>
        known > 0 ? Math.Round(Math.Clamp(up / known, 0, 1), decimals) : null;

    private const int BarDecimals = 3;

    public double?[] Values(Guid sensorId) =>
        rows.TryGetValue(sensorId, out var row) ? row.Select(r => Ratio(r.Up, r.Known, BarDecimals)).ToArray() : new double?[Count];

    /// <summary>Over the last <paramref name="lastBuckets"/> buckets, weighted by time.</summary>
    public double? Overall(Guid sensorId, int lastBuckets)
    {
        if (!rows.TryGetValue(sensorId, out var row))
        {
            return null;
        }
        var tail = row.Skip(Math.Max(0, Count - lastBuckets)).ToList();
        return Ratio(tail.Sum(r => r.Up), tail.Sum(r => r.Known));
    }

    /// <summary>
    /// A host's value per bucket: the average of its sensors that have data in that bucket, so a
    /// sensor added last week does not paint the weeks before red (issue 15).
    /// </summary>
    public double?[] Average(IReadOnlyCollection<Guid> sensorIds)
    {
        var result = new double?[Count];
        for (var i = 0; i < Count; i++)
        {
            var values = sensorIds
                .Select(id => rows.TryGetValue(id, out var row) ? Ratio(row[i].Up, row[i].Known) : null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();
            result[i] = values.Count > 0 ? Math.Round(values.Average(), BarDecimals) : null;
        }
        return result;
    }

    public double? AverageOverall(IReadOnlyCollection<Guid> sensorIds, int lastBuckets)
    {
        var values = sensorIds.Select(id => Overall(id, lastBuckets)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return values.Count > 0 ? Math.Round(values.Average(), 4) : null;
    }

    public UptimeSeries Series(double?[] values) => new(Start, values);
}

/// <summary>Reads sensors with their state, and uptime in local days or hours.</summary>
public sealed class MonitoringReader(PnmtdDbContext db, MonitoringQueries queries)
{
    public async Task<MonitoringSnapshot> SnapshotAsync(Expression<Func<SensorEntity, bool>>? filter, CancellationToken ct)
    {
        IQueryable<SensorEntity> query = db.Sensors;
        if (filter is not null)
        {
            query = query.Where(filter);
        }
        var sensors = await query
            .Select(s => new SensorInfo(s.Id, s.ParentId, s.Parent.Name, s.Parent.Enabled, s.OlderSiblingId, s.Name, s.Type,
                s.Enabled, s.Ignore))
            .ToListAsync(ct);
        var latest = await queries.LatestAsync(sensors.Select(s => s.Id).ToList(), ct);
        return new MonitoringSnapshot(sensors, latest);
    }

    /// <summary>The last <paramref name="days"/> local days (TZ), ending with today so far.</summary>
    public async Task<UptimeTable> DaysAsync(IReadOnlyCollection<Guid> sensorIds, int days, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var today = Clock.ToLocal(now).Date;
        var starts = Enumerable.Range(0, days).Select(i => Clock.ToUtc(today.AddDays(i - (days - 1)))).ToList();
        var rows = await queries.UptimeAsync(sensorIds, starts, now, ct);
        return new UptimeTable(days, DateOnly.FromDateTime(today.AddDays(-(days - 1))), rows);
    }

    /// <summary>The last <paramref name="hours"/> hours (UTC hours), ending with the current one so far.</summary>
    public async Task<UptimeTable> HoursAsync(IReadOnlyCollection<Guid> sensorIds, int hours, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var starts = Enumerable.Range(0, hours).Select(i => hour.AddHours(i - (hours - 1))).ToList();
        var rows = await queries.UptimeAsync(sensorIds, starts, now, ct);
        return new UptimeTable(hours, DateOnly.FromDateTime(hour.AddHours(-(hours - 1))), rows);
    }
}
