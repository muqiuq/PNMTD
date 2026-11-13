using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Ai;

/// <summary>
/// The AI budget (docs/ai-operations-plan.md §3.7): <c>Ai:Budget:Usd</c> per calendar month (or day) in PNMT's time
/// zone. Spending is counted per day, purpose and model in <c>ai_spending</c>. At 80 % and 90 % the AI budget sensor
/// gets 425 and 426, at 100 % 427, each once per period; then new attempts wait for the next period, which writes 220.
/// </summary>
public sealed class AiBudget(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<AiOptions> options, SystemSensors sensors,
    ILogger<AiBudget> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public decimal Limit => Math.Max(0m, options.CurrentValue.Budget.Usd);

    public bool Daily => string.Equals(options.CurrentValue.Budget.Period, "day", StringComparison.OrdinalIgnoreCase);

    /// <summary>The current period: its key, its first and its next day (local), and when it ends (UTC).</summary>
    public (string Key, DateOnly First, DateOnly Next, DateTime EndsAt) Period()
    {
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var first = Daily ? today : new DateOnly(today.Year, today.Month, 1);
        var next = Daily ? first.AddDays(1) : first.AddMonths(1);
        return (Daily ? first.ToString("yyyy-MM-dd") : first.ToString("yyyy-MM"), first, next, Clock.ToUtc(next.ToDateTime(TimeOnly.MinValue)));
    }

    public async Task<decimal> SpentAsync(CancellationToken ct)
    {
        var (_, first, next, _) = Period();
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.AiSpending.Where(s => s.Day >= first && s.Day < next).SumAsync(s => (decimal?)s.Cost, ct) ?? 0m;
    }

    /// <summary>Throws <see cref="AiBudgetExceededException"/> when the period's budget is spent.</summary>
    public async Task EnsureAvailableAsync(CancellationToken ct)
    {
        if (Limit <= 0)
        {
            return;
        }
        if (await SpentAsync(ct) >= Limit)
        {
            throw new AiBudgetExceededException(Period().EndsAt);
        }
    }

    /// <summary>Adds a request's cost to today's row, and writes a threshold's event when it is crossed.</summary>
    public async Task RecordAsync(string purpose, string model, decimal cost, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(Clock.LocalNow);
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ai_spending ("Day", "Purpose", "Model", "Requests", "Cost") VALUES ({0}, {1}, {2}, 1, {3})
                ON CONFLICT ("Day", "Purpose", "Model") DO UPDATE SET "Requests" = ai_spending."Requests" + 1, "Cost" = ai_spending."Cost" + EXCLUDED."Cost"
                """, [day, purpose, model, cost], ct);
        }
        await CheckAsync(ct);
    }

    /// <summary>The thresholds of the current period, and 220 when a new period begins below 80 % (§3.7).</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        if (Limit <= 0 || !options.CurrentValue.Enabled)
        {
            return;
        }
        await gate.WaitAsync(ct);
        try
        {
            var (key, _, _, endsAt) = Period();
            var spent = await SpentAsync(ct);
            var share = spent / Limit;
            await using var db = await dbs.CreateDbContextAsync(ct);
            var state = await db.AiBudgetState.SingleOrDefaultAsync(s => s.Period == key, ct);
            if (state is null)
            {
                state = new AiBudgetStateEntity { Period = key };
                db.AiBudgetState.Add(state);
                await db.SaveChangesAsync(ct);
                // A new period: the sensor says so if the last one ended above 80 %.
                var last = await sensors.LastAsync(SystemSensorKeys.AiBudget, ct);
                if (share < 0.8m && last is { Code: PNMTStatusCodes.AI_BUDGET_80 or PNMTStatusCodes.AI_BUDGET_90 or PNMTStatusCodes.AI_BUDGET_REACHED })
                {
                    await sensors.WriteAsync(SystemSensorKeys.AiBudget, PNMTStatusCodes.SYSTEM_OK, "Below 80 % of the budget.", ct);
                }
            }
            var unit = Daily ? "today" : "this month";
            if (share >= 1m && !state.Notified100)
            {
                state.Notified100 = state.Notified90 = state.Notified80 = true;
                await db.SaveChangesAsync(ct);
                logger.LogWarning("The AI budget of {Limit} USD is spent {Unit}: AI work waits", Limit, unit);
                await sensors.WriteAsync(SystemSensorKeys.AiBudget, PNMTStatusCodes.AI_BUDGET_REACHED,
                    $"AI budget reached: {spent:0.00} of {Limit:0.00} USD {unit}. AI work waits until {Clock.ToLocal(endsAt):yyyy-MM-dd HH:mm}.", ct);
            }
            else if (share >= 0.9m && !state.Notified90)
            {
                state.Notified90 = state.Notified80 = true;
                await db.SaveChangesAsync(ct);
                await sensors.WriteAsync(SystemSensorKeys.AiBudget, PNMTStatusCodes.AI_BUDGET_90,
                    $"AI spending at 90 % of the budget: {spent:0.00} of {Limit:0.00} USD {unit}.", ct);
            }
            else if (share >= 0.8m && !state.Notified80)
            {
                state.Notified80 = true;
                await db.SaveChangesAsync(ct);
                await sensors.WriteAsync(SystemSensorKeys.AiBudget, PNMTStatusCodes.AI_BUDGET_80,
                    $"AI spending at 80 % of the budget: {spent:0.00} of {Limit:0.00} USD {unit}.", ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
