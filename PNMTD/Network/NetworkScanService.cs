using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;

namespace PNMTD.Network;

/// <summary>
/// Scans (docs/network-map-plan.md §7, §8.7): the status for the Scans tab and its strip, the history, starting and cancelling
/// runs, and the settings. A run of one router needs Commands: Run (as a poll does) or Manage; every other run Manage.
/// </summary>
public sealed class NetworkScanService(
    PnmtdDbContext db,
    LocationTree locations,
    NetworkScanner scanner,
    IOptionsMonitor<NetworkMapOptions> options,
    IOptionsMonitor<MikrotikOptions> mikrotik,
    IHttpContextAccessor accessor)
{
    public async Task<NetworkScanStatus> StatusAsync(CancellationToken ct)
    {
        var active = await db.NetworkScans.AsNoTracking()
            .Where(s => s.State == NetworkScanState.Queued || s.State == NetworkScanState.Running)
            .OrderBy(s => s.Scope).ThenBy(s => s.CreatedAt).ToListAsync(ct);
        var running = active.FirstOrDefault(s => s.CurrentRouterName is not null) ?? active.FirstOrDefault(s => s.State == NetworkScanState.Running);
        var lastFull = await db.NetworkScans.AsNoTracking().Where(s => s.Scope == NetworkScanScope.All && s.State == NetworkScanState.Done)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        var settings = await SettingsEntityAsync(ct);
        var lastFullStart = await NetworkScanSchedule.LastFullAsync(db, ct);
        return new NetworkScanStatus(
            running is null ? null : NetworkViews.Scan(running, await EstimateAsync(running, ct)),
            active.Where(s => s != running).Select(s => NetworkViews.Scan(s)).ToList(),
            lastFull is null ? null : NetworkViews.Scan(lastFull),
            NetworkScanQueue.NextScheduled(lastFullStart, settings),
            settings.ScanScheduleEnabled,
            options.CurrentValue.Enabled && mikrotik.CurrentValue.Enabled);
    }

    /// <summary>The seconds left: the average router so far, times the routers left.</summary>
    private async Task<int?> EstimateAsync(NetworkScanEntity scan, CancellationToken ct)
    {
        var done = await db.NetworkScanItems.AsNoTracking().Where(i => i.ScanId == scan.Id && i.DurationMs != null)
            .Select(i => i.DurationMs!.Value).ToListAsync(ct);
        var left = await db.NetworkScanItems.CountAsync(i => i.ScanId == scan.Id && (i.State == NetworkScanItemState.Queued || i.State == NetworkScanItemState.Running), ct);
        return done.Count == 0 || left == 0 ? null : (int)Math.Ceiling(done.Average() * left / 1000d);
    }

    public async Task<Paged<NetworkScanRow>> ListAsync(NetworkScanQuery query, CancellationToken ct)
    {
        var page = await Paging.PageAsync(db.NetworkScans.AsNoTracking().OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id), query, ct);
        return new Paged<NetworkScanRow>(page.Items.Select(s => NetworkViews.Scan(s)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<NetworkScanDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var scan = await db.NetworkScans.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Scan");
        var items = await db.NetworkScanItems.AsNoTracking().Where(i => i.ScanId == id).OrderBy(i => i.Position).ToListAsync(ct);
        return new NetworkScanDetail(NetworkViews.Scan(scan, scan.State is NetworkScanState.Running or NetworkScanState.Queued ? await EstimateAsync(scan, ct) : null),
            items.Select(NetworkViews.ScanItem).ToList());
    }

    public async Task<NetworkScanRow> StartAsync(NetworkScanRequest request, CancellationToken ct)
    {
        var user = accessor.HttpContext?.User;
        if (!options.CurrentValue.Enabled || !mikrotik.CurrentValue.Enabled)
        {
            throw ApiException.Conflict("network_map_disabled", "The network map is turned off (NetworkMap__Enabled or Mikrotik__Enabled).");
        }
        if (!Enum.IsDefined(request.Scope))
        {
            throw ApiException.Invalid("scope", "Router, Location or All.");
        }
        var manage = user?.Has(Area.NetworkMap, Level.Manage) == true;
        if (request.Scope == NetworkScanScope.Router)
        {
            if (!manage && user?.Has(Area.Commands, Level.Run) != true)
            {
                throw ApiException.MissingRight(Area.Commands, Level.Run);
            }
            if (request.SensorId is not { } sensorId || !await db.MikrotikRouters.AnyAsync(r => r.SensorId == sensorId, ct))
            {
                throw ApiException.Invalid("sensorId", "A MikroTik router.");
            }
        }
        else if (!manage)
        {
            throw ApiException.MissingRight(Area.NetworkMap, Level.Manage);
        }
        var tree = await locations.GetAsync(ct);
        if (request.Scope == NetworkScanScope.Location && (request.LocationId is not { } locationId || !tree.Contains(locationId)))
        {
            throw ApiException.Invalid("locationId", "A location.");
        }
        var locationIdOrNull = request.Scope == NetworkScanScope.Location ? request.LocationId : null;
        var sensorIdOrNull = request.Scope == NetworkScanScope.Router ? request.SensorId : null;
        if (await NetworkScanQueue.SameScopeAsync(db, request.Scope, locationIdOrNull, sensorIdOrNull, ct) is { } same)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "scan_queued",
                same.State == NetworkScanState.Running ? "This scan is running already." : "This scan is queued already.",
                extensions: new Dictionary<string, object?> { ["scanId"] = same.Id });
        }
        var who = AuditWho.From(accessor);
        var scan = await NetworkScanQueue.CreateAsync(db, tree, request.Scope, locationIdOrNull, sensorIdOrNull, NetworkScanTrigger.Manual, who.UserId,
            string.IsNullOrEmpty(who.Username) ? null : who.Username, ct);
        AuditLog.Target(AuditTarget.Of(AuditTargets.NetworkScan, scan.Id, scan.ScopeName, sensorIdOrNull, locationIdOrNull));
        AuditLog.Summary($"Network scan of {scan.ScopeName} ({scan.Total} router{(scan.Total == 1 ? "" : "s")})");
        scanner.Wake();
        return NetworkViews.Scan(scan);
    }

    public async Task<NetworkScanRow> CancelAsync(Guid id, CancellationToken ct)
    {
        var scan = await db.NetworkScans.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Scan");
        if (scan.State is not (NetworkScanState.Queued or NetworkScanState.Running))
        {
            throw ApiException.Conflict("scan_finished", "The scan has finished already.");
        }
        scan.State = NetworkScanState.Cancelled;
        scan.FinishedAt = Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await db.NetworkScanItems.Where(i => i.ScanId == id && i.State == NetworkScanItemState.Queued)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.State, NetworkScanItemState.Cancelled).SetProperty(i => i.FinishedAt, Clock.UtcNow), ct);
        scanner.Cancel(id);
        AuditLog.Target(AuditTarget.Of(AuditTargets.NetworkScan, scan.Id, scan.ScopeName, scan.SensorId, scan.LocationId));
        AuditLog.Summary($"Network scan of {scan.ScopeName} cancelled");
        return NetworkViews.Scan(scan);
    }

    // --- settings (§7.3) -------------------------------------------------------------------------------------------------

    private async Task<NetworkMapSettingsEntity> SettingsEntityAsync(CancellationToken ct) =>
        await db.NetworkMapSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == NetworkMapSettingsEntity.SingletonId, ct) ?? new NetworkMapSettingsEntity();

    public async Task<NetworkMapSettings> SettingsAsync(CancellationToken ct) => View(await SettingsEntityAsync(ct));

    public async Task<NetworkMapSettings> SaveSettingsAsync(NetworkMapSettingsRequest request, CancellationToken ct)
    {
        new ApiValidator()
            .Check(request.ScanIntervalDays is >= 1 and <= 365, "scanIntervalDays", "Between 1 and 365 days.")
            .Check(request.ScanHour is >= 0 and <= 23, "scanHour", "An hour from 0 to 23.")
            .Check(request.RetentionDays is >= 7 and <= 3650, "retentionDays", "Between 7 and 3650 days.")
            .ThrowIfInvalid();
        var row = await db.NetworkMapSettings.SingleOrDefaultAsync(s => s.Id == NetworkMapSettingsEntity.SingletonId, ct);
        if (row is null)
        {
            row = new NetworkMapSettingsEntity();
            db.NetworkMapSettings.Add(row);
        }
        var who = AuditWho.From(accessor);
        row.ScanScheduleEnabled = request.ScanScheduleEnabled;
        row.ScanIntervalDays = request.ScanIntervalDays;
        row.ScanHour = request.ScanHour;
        row.RetentionDays = request.RetentionDays;
        row.ChangedAt = Clock.UtcNow;
        row.ChangedBy = who.UserId;
        row.ChangedByName = string.IsNullOrEmpty(who.Username) ? null : who.Username;
        AuditLog.Target(AuditTarget.Of(AuditTargets.NetworkScan, "settings", "Network map settings"));
        await db.SaveChangesAsync(ct);
        return View(row);
    }

    private static NetworkMapSettings View(NetworkMapSettingsEntity s) =>
        new(s.ScanScheduleEnabled, s.ScanIntervalDays, s.ScanHour, s.RetentionDays, Clock.TimeZone.Id, s.ChangedAt, s.ChangedByName);
}
