using PNMTD.Lib.Models.Enum;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

public sealed class HostService(PnmtdDbContext db, MonitoringReader reader, LocationTree locations, DashboardCache dashboardCache,
    IHttpContextAccessor accessor)
{
    private static readonly string[] SortFields = ["name", "state", "uptime", "created", "lastevent"];

    /// <summary>The location a host is assigned to, with its path (docs/locations-knowledge-plan.md §3.2).</summary>
    public static LocationRef? LocationOf(LocationTreeSnapshot tree, Guid? locationId) =>
        locationId is { } id && tree.Find(id) is { } node ? new LocationRef(id, node.Name, tree.Path(id)) : null;

    /// <summary>
    /// Sort: name (default), state (failing first), uptime (lowest first), created, lastEvent. A location includes its
    /// sub-locations' hosts (§3.2).
    /// </summary>
    public async Task<Paged<HostListItem>> ListAsync(HostQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "name", SortFields);
        var pattern = Text.Pattern(query.Search);
        var tree = await locations.GetAsync(ct);
        var hostsQuery = db.Hosts.AsNoTracking();
        if (pattern is not null)
        {
            var search = query.Search!.Trim();
            var named = tree.All.Where(n => tree.Path(n.Id).Contains(search, StringComparison.OrdinalIgnoreCase)).Select(n => n.Id).ToList();
            hostsQuery = hostsQuery.Where(h => EF.Functions.ILike(h.Name, pattern, @"\")
                || (h.Location != null && EF.Functions.ILike(h.Location, pattern, @"\"))
                || (h.LocationId != null && named.Contains(h.LocationId.Value)));
        }
        if (query.LocationId is { } locationId)
        {
            var subtree = tree.SelfAndDescendants(locationId).ToList();
            hostsQuery = hostsQuery.Where(h => h.LocationId != null && subtree.Contains(h.LocationId.Value));
        }
        if (query.NoLocation == true)
        {
            hostsQuery = hostsQuery.Where(h => h.LocationId == null);
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
                days.AverageOverall(active, 30), sensors.Max(s => s.LastEventAt), days.Series(days.Average(active)), h.Importance,
                LocationOf(tree, h.LocationId));
        });
        if (query.State is { } state)
        {
            items = items.Where(i => i.State == state);
        }
        if (query.Importance is { } importance)
        {
            items = items.Where(i => i.Importance == importance);
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
        var tree = await locations.GetAsync(ct);

        return new HostDetail(host.Id, host.Name, host.Location, host.Notes, host.Enabled, Instants.Utc(host.Created),
            MonitoringSnapshot.HostStateOf(host.Enabled, sensors), MonitoringSnapshot.Count(sensors),
            hours.AverageOverall(active, 24), days.AverageOverall(active, 30), sensors.Max(s => s.LastEventAt),
            days.Series(days.Average(active)), eventCount,
            sensors.Where(s => s.Info.ParentSensorId is null)
                .OrderBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => s.ToSummary())
                .ToList(), host.Importance, LocationOf(tree, host.LocationId));
    }

    /// <summary>Many hosts' importance at once (docs/ai-operations-plan.md §11.1).</summary>
    public async Task<int> SetImportanceAsync(ImportanceRequest request, CancellationToken ct)
    {
        new ApiValidator()
            .Check(request.Ids.Count is >= 1 and <= 5000, "ids", "Between 1 and 5000 hosts.")
            .Check(request.Importance is { } i && Enum.IsDefined(i), "importance", "Low, Normal or High.")
            .ThrowIfInvalid();
        var ids = request.Ids.Distinct().ToList();
        return await db.Hosts.Where(h => ids.Contains(h.Id)).ExecuteUpdateAsync(u => u.SetProperty(h => h.Importance, request.Importance!.Value), ct);
    }

    public async Task<HostDetail> CreateAsync(HostRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var host = new HostEntity
        {
            Id = Guid.NewGuid(),
            Created = Clock.UtcNow,
        };
        Apply(host, request);
        db.Hosts.Add(host);
        await db.SaveChangesAsync(ct);
        if (host.LocationId is not null)
        {
            dashboardCache.Invalidate();
        }
        return await GetAsync(host.Id, ct);
    }

    public async Task<HostDetail> UpdateAsync(Guid id, HostRequest request, CancellationToken ct)
    {
        var host = await db.Hosts.SingleOrDefaultAsync(h => h.Id == id, ct) ?? throw ApiException.NotFound("Host");
        await ValidateAsync(request, ct);
        var before = host.LocationId;
        Apply(host, request);
        await db.SaveChangesAsync(ct);
        if (before != host.LocationId)
        {
            dashboardCache.Invalidate();
        }
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
        if (await db.Sensors.AnyAsync(s => s.ParentId == id && s.Type == SensorType.SYSTEM, ct))
        {
            throw ApiException.Conflict("system_sensors", "This host holds PNMT's system sensors: move them to another host first.");
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
        // Host fields that named it name none now (docs/locations-knowledge-plan.md §4.1); its location's history says it left.
        var named = LocationValues.StringValue(id.ToString());
        await db.LocationValues.Where(v => v.Field!.Type == LocationFieldType.Host && v.Value == named)
            .ExecuteUpdateAsync(u => u.SetProperty(v => v.Value, (string?)null).SetProperty(v => v.Revision, v => v.Revision + 1), ct);
        var host = await db.Hosts.AsNoTracking().Where(h => h.Id == id).Select(h => new { h.Name, h.LocationId }).SingleAsync(ct);
        if (host.LocationId is not null)
        {
            LocationService.HostsMoved(db, LocationActor.Of(accessor), [(host.Name, host.LocationId, null)]);
            await db.SaveChangesAsync(ct);
            dashboardCache.Invalidate();
        }
        await db.Hosts.Where(h => h.Id == id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ValidateAsync(HostRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        new ApiValidator()
            .Check(name is { Length: >= 2 and <= 64 }, "name", "The name must have 2 to 64 characters.")
            .Check(request.Location is null || request.Location.Trim().Length <= 128, "location", "At most 128 characters.")
            .Check(request.Notes is null || request.Notes.Length <= 4000, "notes", "At most 4000 characters.")
            .Check(Enum.IsDefined(request.Importance), "importance", "Low, Normal or High.")
            .ThrowIfInvalid();
        if (request.LocationId is { } locationId && !await db.Locations.AnyAsync(l => l.Id == locationId, ct))
        {
            throw ApiException.Invalid("locationId", "No such location.");
        }
    }

    private void Apply(HostEntity host, HostRequest request)
    {
        if (host.LocationId != request.LocationId)
        {
            LocationService.HostsMoved(db, LocationActor.Of(accessor), [(request.Name!.Trim(), host.LocationId, request.LocationId)]);
        }
        host.Name = request.Name!.Trim();
        host.Location = Text.Trimmed(request.Location);
        host.LocationId = request.LocationId;
        host.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes;
        host.Enabled = request.Enabled;
        host.Importance = request.Importance;
    }
}
