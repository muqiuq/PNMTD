using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Services.Events;

/// <summary>
/// Events that PNMTD writes itself (UDP nodes, MikroTik routers: docs/mikrotik-plan.md §5.3), with the
/// sensor API's rules (docs/webui-migration-plan.md §3.3): one writer per sensor at a time (a transaction-scoped advisory lock), the dedupe (when the last two events
/// and this one share code, message and source address, only the last one's time moves) and at
/// most 2000 events per sensor. The sensor API keeps its own copy of these rules, untouched, so its
/// contract cannot change through this code (docs/udp-iot-plan.md §10).
/// </summary>
public static class SensorEventWriter
{
    /// <summary>Must run inside a transaction (the lock is released at its end).</summary>
    public static async Task<EventEntity> WriteAsync(PnmtdDbContext db, Guid sensorId, int code, string? message, string source, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [BitConverter.ToInt64(sensorId.ToByteArray(), 0)], ct);
        var lastTwo = await db.Events.Where(e => e.SensorId == sensorId).OrderByDescending(e => e.Created).Take(2).ToListAsync(ct);
        var now = Clock.UtcNow;
        if (lastTwo is [var last, var preLast]
            && preLast.Code == last.Code && preLast.Message == last.Message
            && Address(preLast.Source) == Address(last.Source)
            && last.Code == code && last.Message == message && Address(last.Source) == Address(source))
        {
            last.Created = now;
            return last;
        }
        var entity = new EventEntity
        {
            Id = Guid.NewGuid(),
            SensorId = sensorId,
            Code = code,
            Message = message,
            Created = now,
            Source = source,
        };
        db.Events.Add(entity);
        var surplus = await db.Events.Where(e => e.SensorId == sensorId)
            .OrderByDescending(e => e.Created)
            .Skip(GlobalConfiguration.MAXIMUM_NUM_OF_EVENTS_PER_SENSOR - 1)
            .Select(e => e.Id)
            .ToListAsync(ct);
        if (surplus.Count > 0)
        {
            await db.Events.Where(e => surplus.Contains(e.Id)).ExecuteDeleteAsync(ct);
        }
        return entity;
    }

    private static string Address(string? source) => Lib.Helper.RemoteAddressHelper.StripPortFromRemoteAddressIfAny(source ?? "");
}

internal static class ObjectExtensions
{
    /// <summary>Sets a few properties of a new object in an expression.</summary>
    public static T Also<T>(this T value, Action<T> init)
    {
        init(value);
        return value;
    }
}
