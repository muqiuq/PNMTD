using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;

namespace PNMTD.Network;

/// <summary>
/// The routers' saved places on the map (docs/network-map-positions-plan.md): one layout for everyone, saved by hand.
/// Saving adds or updates; a router deleted between the drag and the save is skipped, a sensor that isn't a router refused.
/// </summary>
public sealed class NetworkPositionService(PnmtdDbContext db, LocationTree locations, IHttpContextAccessor accessor)
{
    public const int MaxPositions = 5000;

    /// <summary>Far beyond any map; keeps a bad client from writing coordinates the layout can't handle.</summary>
    public const double MaxCoordinate = 100_000;

    public async Task<NetworkPositionsResult> SaveAsync(NetworkPositionsRequest request, CancellationToken ct)
    {
        var list = request.Positions ?? [];
        if (list.Count is 0 or > MaxPositions)
        {
            throw ApiException.Invalid("positions", $"Between 1 and {MaxPositions:N0} routers.");
        }
        if (list.Any(p => !Valid(p.X) || !Valid(p.Y)))
        {
            throw ApiException.Invalid("positions", $"X and Y are numbers between -{MaxCoordinate:N0} and {MaxCoordinate:N0}.");
        }
        if (list.Select(p => p.SensorId).Distinct().Count() != list.Count)
        {
            throw ApiException.Invalid("positions", "Each router once.");
        }

        var d = await NetworkDirectory.LoadAsync(db, locations, ct);
        var ids = list.Select(p => p.SensorId).ToList();
        var sensors = await db.Sensors.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
        if (sensors.Where(id => !d.Routers.ContainsKey(id)).ToList() is [var other, ..])
        {
            throw ApiException.Invalid("positions", $"Sensor {other} is not a MikroTik router.");
        }
        var known = list.Where(p => d.Routers.ContainsKey(p.SensorId)).ToList();
        var knownIds = known.Select(p => p.SensorId).ToList();
        var rows = await db.NetworkRouterPositions.Where(p => knownIds.Contains(p.SensorId)).ToDictionaryAsync(p => p.SensorId, ct);
        var who = AuditWho.From(accessor);
        foreach (var p in known)
        {
            if (!rows.TryGetValue(p.SensorId, out var row))
            {
                row = new NetworkRouterPositionEntity { SensorId = p.SensorId };
                db.NetworkRouterPositions.Add(row);
            }
            row.X = p.X;
            row.Y = p.Y;
            row.UpdatedAt = Clock.UtcNow;
            row.UpdatedBy = who.UserId;
            row.UpdatedByName = Name(who);
        }
        await db.SaveChangesAsync(ct);

        var names = known.Select(p => (p.SensorId, d.Routers[p.SensorId].Name)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        AuditLog.Target(AuditTarget.Many(AuditTargets.NetworkRouterPosition, names));
        AuditLog.Summary(names.Count == 1 ? $"Saved the position of {names[0].Name} on the network map"
            : $"Saved the positions of {names.Count} routers on the network map");
        return new NetworkPositionsResult(known.Count, list.Count - known.Count);
    }

    /// <summary>One router back to automatic. 404 when it has no saved position.</summary>
    public async Task ClearAsync(Guid sensorId, CancellationToken ct)
    {
        var row = await db.NetworkRouterPositions.SingleOrDefaultAsync(p => p.SensorId == sensorId, ct) ?? throw ApiException.NotFound("Saved position");
        var d = await NetworkDirectory.LoadAsync(db, locations, ct);
        var name = d.Routers.TryGetValue(sensorId, out var r) ? r.Name : sensorId.ToString();
        db.NetworkRouterPositions.Remove(row);
        await db.SaveChangesAsync(ct);
        AuditLog.Target(AuditTarget.Of(AuditTargets.NetworkRouterPosition, sensorId, name));
        AuditLog.Summary($"{name} placed automatically on the network map again");
    }

    /// <summary>Every router back to automatic. Answers how many had a saved position.</summary>
    public async Task<int> ResetAsync(CancellationToken ct)
    {
        var rows = await db.NetworkRouterPositions.ToListAsync(ct);
        var d = await NetworkDirectory.LoadAsync(db, locations, ct);
        db.NetworkRouterPositions.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        AuditLog.Target(AuditTarget.Many(AuditTargets.NetworkRouterPosition,
            rows.Select(p => (p.SensorId, d.Routers.TryGetValue(p.SensorId, out var r) ? r.Name : p.SensorId.ToString())).ToList()));
        AuditLog.Summary($"{rows.Count} saved router position{(rows.Count == 1 ? "" : "s")} cleared: the network map places every router automatically");
        return rows.Count;
    }

    /// <summary>The saved positions by sensor.</summary>
    public static async Task<Dictionary<Guid, NetworkRouterPositionEntity>> AllAsync(PnmtdDbContext db, CancellationToken ct) =>
        await db.NetworkRouterPositions.AsNoTracking().ToDictionaryAsync(p => p.SensorId, ct);

    private static bool Valid(double v) => double.IsFinite(v) && Math.Abs(v) <= MaxCoordinate;

    private static string? Name(AuditWho who) => string.IsNullOrEmpty(who.Username) ? null : who.Username;
}
