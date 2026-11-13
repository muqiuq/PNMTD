using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Decisions;

/// <summary>
/// Every hour (docs/ai-operations-plan.md §5.1): open decisions past their expiry become Expired, and those whose
/// subject was handled elsewhere (a new rule took the mail) become Superseded.
/// </summary>
public sealed class DecisionMaintenanceTask(IDbContextFactory<PnmtdDbContext> dbs, IEnumerable<IDecisionKind> kinds, ILogger<DecisionMaintenanceTask> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "DecisionMaintenanceTask");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var now = Clock.UtcNow;
        await db.Decisions.Where(d => d.State == DecisionState.Open && d.ExpiresAt < now)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.State, DecisionState.Expired).SetProperty(d => d.DecidedAt, now), ct);
        var open = await db.Decisions.Where(d => d.State == DecisionState.Open).ToListAsync(ct);
        foreach (var decision in open)
        {
            if (kinds.FirstOrDefault(k => k.Kind == decision.Kind) is { } kind && !await kind.StillRelevantAsync(decision, db, ct))
            {
                decision.State = DecisionState.Superseded;
                decision.DecidedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
