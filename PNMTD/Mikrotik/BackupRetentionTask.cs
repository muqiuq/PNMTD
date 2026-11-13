using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Mikrotik;

/// <summary>
/// Backup retention (docs/mikrotik-plan.md §6.3), hourly: every version younger than <c>KeepDays</c> stays,
/// and for each router at least the newest <c>KeepVersions</c>, whatever their age. Backups of deleted
/// routers keep only the first rule: nothing new arrives for them.
/// </summary>
public sealed class BackupRetentionTask(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<MikrotikOptions> options, ILogger<BackupRetentionTask> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await RunOnceAsync(stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Backup retention deleted {Count} old MikroTik backups", deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "MikroTik backup retention failed");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One run; how many backups it deleted. Public for the tests.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var o = options.CurrentValue.Backups;
        var cutoff = Clock.UtcNow - TimeSpan.FromDays(Math.Max(1, o.KeepDays));
        var keep = Math.Max(1, o.KeepVersions);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var old = await db.MikrotikBackups.Where(b => b.CreatedAt < cutoff).Select(b => new { b.Id, b.SensorId }).ToListAsync(ct);
        if (old.Count == 0)
        {
            return 0;
        }
        var doomed = new List<Guid>();
        foreach (var group in old.GroupBy(b => b.SensorId))
        {
            if (group.Key is not { } sensorId)
            {
                doomed.AddRange(group.Select(b => b.Id));
                continue;
            }
            var newest = (await db.MikrotikBackups.Where(b => b.SensorId == sensorId)
                .OrderByDescending(b => b.CreatedAt).Take(keep).Select(b => b.Id).ToListAsync(ct)).ToHashSet();
            doomed.AddRange(group.Select(b => b.Id).Where(id => !newest.Contains(id)));
        }
        return doomed.Count == 0 ? 0 : await db.MikrotikBackups.Where(b => doomed.Contains(b.Id)).ExecuteDeleteAsync(ct);
    }
}
