using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Tasks.Helpers;

namespace PNMTD.Network;

/// <summary>
/// The network map's schedule and retention (docs/network-map-plan.md §7.3, §6.2), checked every minute under
/// BackgroundTasks:Enabled. A scheduled run of all routers starts when the schedule is on, the last full run (scheduled or by
/// hand) started at least the interval ago, and the hour has come; without a full run it waits. Once a day: interfaces no
/// router has seen for the retention (unless a person assigned them), dismissed suggestions as old, and the runs' history.
/// </summary>
public sealed class NetworkScanSchedule(
    IDbContextFactory<PnmtdDbContext> dbs,
    LocationTree locations,
    NetworkScanner scanner,
    IOptionsMonitor<NetworkMapOptions> options,
    IOptionsMonitor<MikrotikOptions> mikrotik,
    ILogger<NetworkScanSchedule> logger) : IHostedService, IDisposable
{
    private readonly RunGuard runGuard = new();
    private readonly CancellationTokenSource stopping = new();
    private Timer? timer;
    private DateTime? lastRetention;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        timer?.Change(Timeout.Infinite, 0);
        await stopping.CancelAsync();
    }

    public void Dispose()
    {
        timer?.Dispose();
        stopping.Dispose();
    }

    private async Task TickAsync()
    {
        if (!runGuard.TryEnter())
        {
            return;
        }
        try
        {
            await RunOnceAsync(stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The network map's schedule failed");
        }
        finally
        {
            runGuard.Exit();
        }
    }

    /// <summary>One check: the scheduled run if it is due, and the retention once a day. Returns the run it started. Public for the tests.</summary>
    public async Task<NetworkScanEntity?> RunOnceAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled || !mikrotik.CurrentValue.Enabled)
        {
            return null;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_NETWORK_SCAN_SCHEDULE_RUN);
        await db.SaveChangesAsync(ct);

        NetworkScanEntity? started = null;
        var settings = await db.NetworkMapSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == NetworkMapSettingsEntity.SingletonId, ct);
        if (settings is { ScanScheduleEnabled: true })
        {
            var lastFull = await LastFullAsync(db, ct);
            if (NetworkScanQueue.NextScheduled(lastFull, settings) is { } due && Clock.UtcNow >= due
                && await NetworkScanQueue.SameScopeAsync(db, NetworkScanScope.All, null, null, ct) is null)
            {
                started = await NetworkScanQueue.CreateAsync(db, await locations.GetAsync(ct), NetworkScanScope.All, null, null, NetworkScanTrigger.Schedule,
                    null, null, ct);
                scanner.Wake();
                logger.LogInformation("Scheduled network scan of {Count} routers started", started.Total);
            }
        }
        if (lastRetention is not { } last || Clock.UtcNow - last >= TimeSpan.FromHours(24))
        {
            await RetentionAsync(db, settings?.RetentionDays ?? 90, ct);
            lastRetention = Clock.UtcNow;
        }
        return started;
    }

    /// <summary>When the last run of all routers that wasn't cancelled started.</summary>
    public static Task<DateTime?> LastFullAsync(PnmtdDbContext db, CancellationToken ct) =>
        db.NetworkScans.Where(s => s.Scope == NetworkScanScope.All && s.State != NetworkScanState.Cancelled)
            .OrderByDescending(s => s.CreatedAt).Select(s => (DateTime?)s.CreatedAt).FirstOrDefaultAsync(ct);

    public async Task RetentionAsync(PnmtdDbContext db, int retentionDays, CancellationToken ct)
    {
        var cutoff = Clock.UtcNow.AddDays(-Math.Max(1, retentionDays));
        var interfaces = await db.NetworkInterfaces.Where(i => i.LastSeenAt < cutoff && i.AssignedHostId == null).ExecuteDeleteAsync(ct);
        var dismissed = await db.NetworkSuggestions.Where(s => s.State == NetworkSuggestionState.Dismissed && s.LastSeenAt < cutoff).ExecuteDeleteAsync(ct);
        var history = Clock.UtcNow.AddDays(-Math.Max(1, options.CurrentValue.Scan.HistoryDays));
        var scans = await db.NetworkScans.Where(s => s.CreatedAt < history && (s.State == NetworkScanState.Done || s.State == NetworkScanState.Cancelled))
            .ExecuteDeleteAsync(ct);
        if (interfaces + dismissed + scans > 0)
        {
            logger.LogInformation("Network map retention: {Interfaces} interfaces, {Dismissed} dismissed suggestions, {Scans} runs removed",
                interfaces, dismissed, scans);
        }
    }
}
