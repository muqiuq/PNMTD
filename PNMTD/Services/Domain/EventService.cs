using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models.Poco;

namespace PNMTD.Services.Domain;

public sealed class EventService(PnmtdDbContext db, MonitoringQueries queries)
{
    private static readonly string[] SortFields = ["created", "code"];

    private const int SuccessLimit = EventEntityPoco.END_OF_SUCCESS_CODES;

    /// <summary>Paged on the server. Search: the message, the sensor name or the host name.</summary>
    public async Task<Paged<EventItem>> ListAsync(EventQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "-created", SortFields);
        var status = query.Status?.Trim().ToLowerInvariant();
        new ApiValidator()
            .Check(status is null or "" or "ok" or "failed", "status", "ok or failed.")
            .Check(query.From is null || query.To is null || query.From <= query.To, "from", "from must not be after to.")
            .ThrowIfInvalid();

        var events = db.Events.AsNoTracking();
        if (query.SensorId is { } sensorId)
        {
            events = events.Where(e => e.SensorId == sensorId);
        }
        if (query.HostId is { } hostId)
        {
            events = events.Where(e => e.Sensor.ParentId == hostId);
        }
        if (status == "ok")
        {
            events = events.Where(e => e.Code <= SuccessLimit);
        }
        else if (status == "failed")
        {
            events = events.Where(e => e.Code > SuccessLimit);
        }
        if (query.From is { } from)
        {
            var fromUtc = Clock.AsUtc(from);
            events = events.Where(e => e.Created >= fromUtc);
        }
        if (query.To is { } to)
        {
            var toUtc = Clock.AsUtc(to);
            events = events.Where(e => e.Created <= toUtc);
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            events = events.Where(e => (e.Message != null && EF.Functions.ILike(e.Message, pattern, @"\"))
                || EF.Functions.ILike(e.Sensor.Name, pattern, @"\")
                || EF.Functions.ILike(e.Sensor.Parent.Name, pattern, @"\"));
        }
        events = (field, descending) switch
        {
            ("code", false) => events.OrderBy(e => e.Code).ThenByDescending(e => e.Created),
            ("code", true) => events.OrderByDescending(e => e.Code).ThenByDescending(e => e.Created),
            ("created", false) => events.OrderBy(e => e.Created).ThenBy(e => e.Id),
            _ => events.OrderByDescending(e => e.Created).ThenBy(e => e.Id),
        };
        return await Paging.PageAsync(events.Select(e => new EventItem(e.Id, e.Created, e.Code, e.Code <= SuccessLimit, e.Message,
            e.Source, e.SensorId, e.Sensor.Name, e.Sensor.ParentId, e.Sensor.Parent.Name)), query, ct);
    }

    public async Task<EventItem> GetAsync(Guid id, CancellationToken ct) =>
        await db.Events.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new EventItem(e.Id, e.Created, e.Code, e.Code <= SuccessLimit, e.Message, e.Source, e.SensorId,
                e.Sensor.Name, e.Sensor.ParentId, e.Sensor.Parent.Name))
            .SingleOrDefaultAsync(ct)
        ?? throw ApiException.NotFound("Event");

    /// <summary>
    /// Transitions between OK and failed, newest first: by default the last 24 hours of the sensors
    /// that are enabled, not ignored and not ENCAPSULADED parents (whose children report).
    /// </summary>
    public async Task<IReadOnlyList<StateChange>> StateChangesAsync(StateChangeQuery query, CancellationToken ct)
    {
        var to = query.To is { } t ? Clock.AsUtc(t) : Clock.UtcNow;
        var from = query.From is { } f ? Clock.AsUtc(f) : to.AddHours(-24);
        new ApiValidator()
            .Check(from <= to, "from", "from must not be after to.")
            .Check(query.Limit is >= 1 and <= 500, "limit", "Between 1 and 500.")
            .ThrowIfInvalid();

        var sensors = db.Sensors.AsNoTracking().Where(s => s.Type != SensorType.ENCAPSULADED);
        if (query.SensorId is { } sensorId)
        {
            sensors = sensors.Where(s => s.Id == sensorId);
        }
        else
        {
            sensors = sensors.Where(s => s.Enabled && !s.Ignore && s.Parent.Enabled);
        }
        if (query.HostId is { } hostId)
        {
            sensors = sensors.Where(s => s.ParentId == hostId);
        }
        var names = await sensors.Select(s => new { s.Id, s.Name, s.ParentId, HostName = s.Parent.Name }).ToDictionaryAsync(s => s.Id, ct);
        return await ResolveAsync(names.Keys.ToList(), from, to, query.Limit, id => (names[id].Name, names[id].ParentId, names[id].HostName), ct);
    }

    internal async Task<IReadOnlyList<StateChange>> ResolveAsync(IReadOnlyCollection<Guid> sensorIds, DateTime from, DateTime to, int limit,
        Func<Guid, (string Name, Guid HostId, string HostName)> names, CancellationToken ct)
    {
        var rows = await queries.StateChangesAsync(sensorIds, from, to, limit, ct);
        return rows.Select(r =>
        {
            var (name, hostId, hostName) = names(r.SensorId);
            var success = r.Code <= SuccessLimit;
            double? down = success && r.FailingSince is { } since ? Math.Round((r.At - since).TotalSeconds) : null;
            return new StateChange(r.At, r.SensorId, name, hostId, hostName, r.PreviousCode, r.Code, success, r.Message,
                success ? r.FailingSince : null, down);
        }).ToList();
    }
}
