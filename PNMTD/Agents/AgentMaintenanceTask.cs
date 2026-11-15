using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Agents;

/// <summary>
/// Hourly (docs/windows-sensor-plan.md §8.4), for the agents of every platform: join tokens expired a month ago go, and the
/// retention of the agent log, logins, changes and diagnostics runs; key removals waiting too long for their server expire; the
/// download folder keeps the versions of docs/agent-update-plan.md §3.4.
/// </summary>
public sealed class AgentMaintenanceTask(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<AgentOptions> options, AgentDownloads downloads,
    ILogger<AgentMaintenanceTask> logger)
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
                logger.LogWarning(e, "Agent maintenance failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var tokens = await db.AgentJoinTokens.Where(t => t.ExpiresAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
        var log = await db.AgentLog.Where(l => l.At < now.AddDays(-Math.Max(30, settings.KeepAgentLogDays))).ExecuteDeleteAsync(ct);
        var more = await RetentionAsync(db, settings, now, ct);
        if (tokens + log + more > 0)
        {
            logger.LogInformation("Agent maintenance: {Tokens} old join tokens, {Log} agent log entries and {More} logins, changes and runs deleted", tokens, log, more);
        }
        var agentSettings = await db.AgentSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == Models.Db.AgentSettingsEntity.SingletonId, ct);
        var chosen = AgentVersionService.Resolve(agentSettings?.WindowsAgentVersion, agentSettings?.LinuxAgentVersion);
        downloads.Remember(chosen);
        var deleted = downloads.Retain(chosen);
        if (deleted.Count > 0)
        {
            logger.LogInformation("Agent maintenance: the agent files of {Versions} deleted from the download folder", string.Join(", ", deleted));
        }
    }

    /// <summary>The retention of the later phases' tables.</summary>
    private static async Task<int> RetentionAsync(PnmtdDbContext db, AgentOptions settings, DateTime now, CancellationToken ct)
    {
        var logins = await db.AgentLogins.Where(l => l.At < now.AddDays(-Math.Max(7, settings.KeepLoginsDays))).ExecuteDeleteAsync(ct);
        var changes = await db.AgentChanges.Where(c => c.At < now.AddDays(-Math.Max(7, settings.KeepChangesDays))).ExecuteDeleteAsync(ct);
        // Runs: 30 days, and at least the last 100 per assignment (§5.4).
        var runs = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM diagnostic_runs WHERE \"StartedAt\" < {0} AND \"Id\" NOT IN (SELECT \"Id\" FROM (SELECT \"Id\", row_number() OVER " +
            "(PARTITION BY \"AssignmentId\" ORDER BY \"StartedAt\" DESC) AS n FROM diagnostic_runs) recent WHERE recent.n <= 100)",
            [now.AddDays(-30)], ct);
        // Key removals (docs/linux-sensor-plan.md §6.5): pending ones expire after their days; the history is kept a year, beyond its servers.
        var expired = await SshKeyManager.SshKeyRemovals.ExpireAsync(db, settings.KeyRemovalPendingDays, now, null, ct);
        var removals = await db.SshKeyRemovals.Where(r => r.RequestedAt < now.AddDays(-Math.Max(30, settings.KeepKeyRemovalsDays))).ExecuteDeleteAsync(ct);
        return logins + changes + runs + expired + removals;
    }
}
