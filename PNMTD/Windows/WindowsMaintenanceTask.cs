using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Windows;

/// <summary>
/// Hourly (docs/windows-sensor-plan.md §8.4): join tokens expired a month ago go, and the retention of the agent log,
/// logins, changes and diagnostics runs.
/// </summary>
public sealed class WindowsMaintenanceTask(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<WindowsOptions> options, ILogger<WindowsMaintenanceTask> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Windows maintenance failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var tokens = await db.WindowsJoinTokens.Where(t => t.ExpiresAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
        var log = await db.WindowsAgentLog.Where(l => l.At < now.AddDays(-Math.Max(30, settings.KeepAgentLogDays))).ExecuteDeleteAsync(ct);
        var more = await RetentionAsync(db, settings, now, ct);
        if (tokens + log + more > 0)
        {
            logger.LogInformation("Windows maintenance: {Tokens} old join tokens, {Log} agent log entries and {More} logins, changes and runs deleted", tokens, log, more);
        }
    }

    /// <summary>The retention of the later phases' tables.</summary>
    private static async Task<int> RetentionAsync(PnmtdDbContext db, WindowsOptions settings, DateTime now, CancellationToken ct)
    {
        var logins = await db.WindowsLogins.Where(l => l.At < now.AddDays(-Math.Max(7, settings.KeepLoginsDays))).ExecuteDeleteAsync(ct);
        var changes = await db.WindowsChanges.Where(c => c.At < now.AddDays(-Math.Max(7, settings.KeepChangesDays))).ExecuteDeleteAsync(ct);
        return logins + changes;
    }
}
