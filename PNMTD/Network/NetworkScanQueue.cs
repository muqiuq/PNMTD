using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Network;

/// <summary>
/// Creating a scan run (docs/network-map-plan.md §7.1), for the API and the schedule alike: its routers are fixed when it
/// starts, the enabled MIKROTIK sensors in scope (one router: that router, whatever its state, so its skip shows), ordered by
/// location, then name.
/// </summary>
public static class NetworkScanQueue
{
    public const string AllRouters = "All routers";

    /// <summary>A run of the same scope that is queued or running, if any.</summary>
    public static Task<NetworkScanEntity?> SameScopeAsync(PnmtdDbContext db, NetworkScanScope scope, Guid? locationId, Guid? sensorId, CancellationToken ct) =>
        db.NetworkScans.AsNoTracking()
            .Where(s => (s.State == NetworkScanState.Queued || s.State == NetworkScanState.Running) && s.Scope == scope
                && s.LocationId == locationId && s.SensorId == sensorId)
            .OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);

    public static async Task<NetworkScanEntity> CreateAsync(PnmtdDbContext db, LocationTreeSnapshot tree, NetworkScanScope scope, Guid? locationId,
        Guid? sensorId, NetworkScanTrigger trigger, Guid? userId, string? userName, CancellationToken ct)
    {
        var query = db.MikrotikRouters.AsNoTracking();
        string scopeName;
        switch (scope)
        {
            case NetworkScanScope.Router:
                query = query.Where(r => r.SensorId == sensorId);
                scopeName = await db.Sensors.Where(s => s.Id == sensorId).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "?";
                break;
            case NetworkScanScope.Location:
                var within = tree.SelfAndDescendants(locationId!.Value).ToList();
                query = query.Where(r => r.Sensor.Enabled && r.Sensor.Parent.LocationId != null && within.Contains(r.Sensor.Parent.LocationId.Value));
                scopeName = tree.Path(locationId.Value);
                break;
            default:
                query = query.Where(r => r.Sensor.Enabled);
                scopeName = AllRouters;
                break;
        }
        var routers = await query.Select(r => new { r.SensorId, r.Sensor.Name, r.Sensor.Parent.LocationId }).ToListAsync(ct);
        var ordered = routers
            .OrderBy(r => r.LocationId is { } l && tree.Contains(l) ? 0 : 1)
            .ThenBy(r => r.LocationId is { } l && tree.Contains(l) ? tree.Path(l) : "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var now = Clock.UtcNow;
        var scan = new NetworkScanEntity
        {
            Id = Guid.NewGuid(),
            Scope = scope,
            LocationId = scope == NetworkScanScope.Location ? locationId : null,
            SensorId = scope == NetworkScanScope.Router ? sensorId : null,
            ScopeName = scopeName,
            Trigger = trigger,
            RequestedBy = userId,
            RequestedByName = userName,
            State = ordered.Count == 0 ? NetworkScanState.Done : NetworkScanState.Queued,
            Total = ordered.Count,
            CreatedAt = now,
            FinishedAt = ordered.Count == 0 ? now : null,
        };
        db.NetworkScans.Add(scan);
        for (var i = 0; i < ordered.Count; i++)
        {
            db.NetworkScanItems.Add(new NetworkScanItemEntity
            {
                Id = Guid.NewGuid(),
                ScanId = scan.Id,
                SensorId = ordered[i].SensorId,
                RouterName = ordered[i].Name,
                Position = i,
                State = NetworkScanItemState.Queued,
            });
        }
        await db.SaveChangesAsync(ct);
        return scan;
    }

    /// <summary>
    /// When the schedule starts its next run (§7.3): the interval after the last full run's day, at the hour, in the server's
    /// time zone. Null without a full run: the first one is started by hand.
    /// </summary>
    public static DateTime? NextScheduled(DateTime? lastFullStart, NetworkMapSettingsEntity settings)
    {
        if (!settings.ScanScheduleEnabled || lastFullStart is not { } last)
        {
            return null;
        }
        var day = Clock.ToLocal(last).Date.AddDays(Math.Max(1, settings.ScanIntervalDays));
        return Clock.ToUtc(day.AddHours(Math.Clamp(settings.ScanHour, 0, 23)));
    }
}
