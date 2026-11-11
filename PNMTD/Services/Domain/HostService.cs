using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

public sealed class HostService(PnmtdDbContext db, MonitoringReader reader)
{
    private static readonly string[] SortFields = ["name", "state", "uptime", "created", "lastevent"];

    /// <summary>Sort: name (default), state (failing first), uptime (lowest first), created, lastEvent.</summary>
    public async Task<Paged<HostListItem>> ListAsync(HostQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "name", SortFields);
        var pattern = Text.Pattern(query.Search);
        var hostsQuery = db.Hosts.AsNoTracking();
        if (pattern is not null)
        {
            hostsQuery = hostsQuery.Where(h => EF.Functions.ILike(h.Name, pattern, @"\")
                || (h.Location != null && EF.Functions.ILike(h.Location, pattern, @"\")));
        }
        var hosts = await hostsQuery.ToListAsync(ct);
        var hostIds = hosts.Select(h => h.Id).ToList();
        var snapshot = await reader.SnapshotAsync(s => hostIds.Contains(s.ParentId), ct);
        var days = await reader.DaysAsync(ActiveIds(snapshot.Sensors.Values), 30, ct);

        var items = hosts.Select(h =>
        {
            var sensors = snapshot.OfHost(h.Id).ToList();
            var active = ActiveIds(sensors);
            return new HostListItem(h.Id, h.Name, h.Location, h.Enabled, Instants.Utc(h.Created),
                MonitoringSnapshot.HostStateOf(h.Enabled, sensors), MonitoringSnapshot.Count(sensors),
                days.AverageOverall(active, 30), sensors.Max(s => s.LastEventAt), days.Series(days.Average(active)));
        });
        if (query.State is { } state)
        {
            items = items.Where(i => i.State == state);
        }
        IOrderedEnumerable<HostListItem> sorted = field switch
        {
            "state" => items.OrderBy(i => StateOrder(i.State)),
            "uptime" => items.OrderBy(i => i.Uptime30Days ?? double.MaxValue),
            "created" => items.OrderBy(i => i.Created),
            "lastevent" => items.OrderBy(i => i.LastEventAt ?? DateTime.MinValue),
            _ => items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = (descending ? sorted.Reverse() : sorted.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return Paging.Page(list, query);
    }

    /// <summary>Failing first, then no data, OK, disabled.</summary>
    internal static int StateOrder(HostState state) => state switch
    {
        HostState.Failing => 0,
        HostState.NoData => 1,
        HostState.Ok => 2,
        _ => 3,
    };

    internal static List<Guid> ActiveIds(IEnumerable<SensorStatus> sensors) =>
        sensors.Where(s => s.IsActive).Select(s => s.Info.Id).ToList();

    public async Task<HostDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var host = await db.Hosts.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, ct) ?? throw ApiException.NotFound("Host");
        var snapshot = await reader.SnapshotAsync(s => s.ParentId == id, ct);
        var sensors = snapshot.Sensors.Values.ToList();
        var active = ActiveIds(sensors);
        var days = await reader.DaysAsync(active, 90, ct);
        var hours = await reader.HoursAsync(active, 24, ct);
        var eventCount = await db.Events.CountAsync(e => e.Sensor.ParentId == id, ct);

        return new HostDetail(host.Id, host.Name, host.Location, host.Notes, host.Enabled, Instants.Utc(host.Created),
            MonitoringSnapshot.HostStateOf(host.Enabled, sensors), MonitoringSnapshot.Count(sensors),
            hours.AverageOverall(active, 24), days.AverageOverall(active, 30), sensors.Max(s => s.LastEventAt),
            days.Series(days.Average(active)), eventCount,
            sensors.Where(s => s.Info.ParentSensorId is null)
                .OrderBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => s.ToSummary())
                .ToList());
    }

    public async Task<HostDetail> CreateAsync(HostRequest request, CancellationToken ct)
    {
        Validate(request);
        var host = new HostEntity
        {
            Id = Guid.NewGuid(),
            Created = Clock.UtcNow,
        };
        Apply(host, request);
        db.Hosts.Add(host);
        await db.SaveChangesAsync(ct);
        return await GetAsync(host.Id, ct);
    }

    public async Task<HostDetail> UpdateAsync(Guid id, HostRequest request, CancellationToken ct)
    {
        var host = await db.Hosts.SingleOrDefaultAsync(h => h.Id == id, ct) ?? throw ApiException.NotFound("Host");
        Validate(request);
        Apply(host, request);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// The host with its sensors and their events, rule links and notification records. DNS zones
    /// stay, without a host; mail input rules that wrote to these sensors stay, without a target.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Hosts.AnyAsync(h => h.Id == id, ct))
        {
            throw ApiException.NotFound("Host");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var sensorIds = await db.Sensors
            .Where(s => s.ParentId == id || (s.OlderSibling != null && s.OlderSibling.ParentId == id))
            .Select(s => s.Id)
            .ToListAsync(ct);
        await db.DnsZones.Where(z => z.HostId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(z => z.HostId, (Guid?)null), ct);
        await db.MailInputs.Where(m => m.SensorOutputId != null && sensorIds.Contains(m.SensorOutputId.Value))
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.SensorOutputId, (Guid?)null), ct);
        // ENCAPSULADED children first (their parent may sit on another host), then the host, which
        // takes its sensors, their events and everything hanging off those along (ON DELETE CASCADE).
        await db.Sensors.Where(s => s.OlderSiblingId != null && sensorIds.Contains(s.Id))
            .ExecuteDeleteAsync(ct);
        await db.Hosts.Where(h => h.Id == id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static void Validate(HostRequest request)
    {
        var name = request.Name?.Trim();
        new ApiValidator()
            .Check(name is { Length: >= 2 and <= 64 }, "name", "The name must have 2 to 64 characters.")
            .Check(request.Location is null || request.Location.Trim().Length <= 128, "location", "At most 128 characters.")
            .Check(request.Notes is null || request.Notes.Length <= 4000, "notes", "At most 4000 characters.")
            .ThrowIfInvalid();
    }

    private static void Apply(HostEntity host, HostRequest request)
    {
        host.Name = request.Name!.Trim();
        host.Location = Text.Trimmed(request.Location);
        host.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes;
        host.Enabled = request.Enabled;
    }
}
