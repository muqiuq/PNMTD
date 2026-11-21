using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;

namespace PNMTD.OsUpdates;

/// <summary>
/// OS updates on Linux servers (docs/linux-os-updates-plan.md): the settings, what agents report (runs, logs, history, changelogs, the
/// section), 467's conditions, the high-risk check (sources, fetcher with its HTTP client, evaluator, AI handler), and the hourly upkeep.
/// </summary>
public static class OsUpdatesSetup
{
    public static IServiceCollection AddOsUpdates(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OsUpdatesOptions>(configuration.GetSection(OsUpdatesOptions.Section));
        services.AddSingleton<OsAdvisoryNudge>();
        services.AddSingleton<OsUpdateConditions>();
        services.AddSingleton<OsUpdatePackages>();
        services.AddSingleton<OsUpdateRuns>();
        services.AddSingleton<OsAdvisoryEvaluator>();
        services.AddSingleton<OsAdvisoryFetcher>();
        services.AddSingleton<OsAdvisoryRating>();
        services.AddSingleton<Ai.IAiWorkHandler, OsAdvisoryRateHandler>();
        services.AddSingleton<OsUpdatesMaintenance>();
        services.AddHttpClient(OsAdvisoryFetcher.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(10))
            .ConfigurePrimaryHttpMessageHandler(OsAdvisoryFetcher.Handler);
        return services;
    }
}

/// <summary>
/// Once an hour (§5.3, §5.4): lost runs and every server's retention, 467's conditions (the lists' age moves with time), and the AI's
/// waiting advisory texts considered again (a server may need them now).
/// </summary>
public sealed class OsUpdatesMaintenance(IDbContextFactory<PnmtdDbContext> dbs, OsUpdateRuns runs, OsUpdateConditions conditions, OsAdvisoryRating rating,
    ILogger<OsUpdatesMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "OS updates maintenance failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await runs.SweepAsync(ct);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var agents = await db.Agents.Where(a => a.Platform == AgentPlatform.Linux).ToListAsync(ct);
        foreach (var agent in agents)
        {
            await conditions.RefreshAsync(db, agent, ct);
        }
        await db.SaveChangesAsync(ct);
        await rating.ConsiderAsync(null, ct);
    }
}
