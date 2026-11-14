using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>
/// Cookie's loop (docs/cookie-plan.md §4.1): stopped until an admin starts it, and resuming after a restart if it was
/// running; asleep until the timer or a trigger (grouped for 30 seconds, a message for 2); one round at a time; paused,
/// with the reason shown, while the AI is off, its budget is spent, it has no group, or rounds keep failing. The case
/// book and the code's duties don't depend on any of this.
/// </summary>
public sealed class CookieLoop(IDbContextFactory<PnmtdDbContext> dbs, CookieRound round, CookieSignals signals, CookieSettings settings,
    IOptionsMonitor<CookieOptions> options, IOptionsMonitor<AiOptions> ai, CookieModel model, ILogger<CookieLoop> logger) : BackgroundService
{
    public static readonly TimeSpan GroupFor = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan GroupMessagesFor = TimeSpan.FromSeconds(2);
    public const int MaxFailures = 3;
    public const int MaxExtraRounds = 3;
    private DateTime retentionAt = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        var nextAt = DateTime.MinValue;
        var extra = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                (nextAt, extra) = await StepAsync(nextAt, extra, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Cookie's loop failed");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    /// <summary>One turn of the loop: wait, then a round. Public for the tests, which call it with a wait of none.</summary>
    public async Task<(DateTime NextAt, int Extra)> StepAsync(DateTime nextAt, int extra, CancellationToken ct, bool wait = true)
    {
        await RetentionAsync(ct);
        var runtime = await RuntimeAsync(ct);
        if (!options.CurrentValue.Enabled || !runtime.Running)
        {
            await SetAsync(CookieState.Stopped, options.CurrentValue.Enabled ? "An admin starts Cookie on Admin → Cookie." : "Cookie__Enabled is not set.", null, ct);
            if (wait)
            {
                await signals.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return (DateTime.MinValue, 0);
        }
        if (await BlockedAsync(runtime, ct) is { } why)
        {
            await SetAsync(CookieState.Paused, why, null, ct);
            if (wait)
            {
                await signals.WaitAsync(TimeSpan.FromMinutes(1), ct);
            }
            return (nextAt, extra);
        }
        var now = Clock.UtcNow;
        if (wait && nextAt > now)
        {
            await SetAsync(CookieState.Sleeping, null, nextAt, ct);
            if (await signals.WaitAsync(nextAt - now, ct))
            {
                // Triggers close together make one round (§4.1); a message is answered sooner.
                var peek = signals.Peek();
                await Task.Delay(peek.Any(r => r.StartsWith("message", StringComparison.Ordinal) || r == "run now") ? GroupMessagesFor : GroupFor, ct);
            }
            if (!(await RuntimeAsync(ct)).Running)
            {
                return (DateTime.MinValue, 0);
            }
        }
        var reasons = signals.Take();
        var trigger = reasons.Count > 0 ? string.Join("; ", reasons) : "timer";
        var sleep = TimeSpan.FromMinutes(Math.Max(1, (await settings.GetAsync<ModelSettings>(ct)).SleepMinutes));
        await SetAsync(CookieState.Working, null, null, ct);
        try
        {
            var result = await round.RunAsync(trigger, reasons.Contains("run now") || reasons.Contains("started"), ct);
            await FailuresAsync(0, null, ct);
            var more = result.MoreWork && extra < MaxExtraRounds;
            var next = more ? Clock.UtcNow : Clock.UtcNow + sleep;
            await SetAsync(CookieState.Sleeping, null, next, ct);
            return (next, more ? extra + 1 : 0);
        }
        catch (CookiePausedException e)
        {
            await SetAsync(CookieState.Paused, e.Message, null, ct);
            return (Clock.UtcNow.AddMinutes(1), 0);
        }
        catch (CookieBudgetException e)
        {
            await SetAsync(CookieState.Paused, e.Message, null, ct);
            return (Clock.UtcNow.AddMinutes(10), 0);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failures = (await RuntimeAsync(ct)).Failures + 1;
            logger.LogWarning("Cookie's round failed ({Failures} in a row): {Error}", failures, e.Message);
            if (failures >= MaxFailures)
            {
                var until = Clock.UtcNow.AddMinutes(10);
                await FailuresAsync(failures, until, ct);
                await SetAsync(CookieState.Paused, $"{failures} rounds failed in a row, the last with: {CaseBook.Cut(e.Message, 300)} Cookie tries again at " +
                    $"{Clock.ToLocal(until):HH:mm}.", null, ct);
                return (until, 0);
            }
            await FailuresAsync(failures, null, ct);
            await SetAsync(CookieState.Sleeping, null, Clock.UtcNow.AddMinutes(1), ct);
            return (Clock.UtcNow.AddMinutes(1), 0);
        }
    }

    /// <summary>Why Cookie can't work now, or null.</summary>
    private async Task<string?> BlockedAsync(CookieRuntimeEntity runtime, CancellationToken ct)
    {
        if (!ai.CurrentValue.Usable)
        {
            return "The AI is off or has no key (Ai__Enabled, Ai__ApiKey).";
        }
        if (runtime.PausedUntil is { } until && until > Clock.UtcNow)
        {
            return runtime.StateReason ?? $"Paused until {Clock.ToLocal(until):HH:mm}.";
        }
        if ((await settings.GetAsync<CookieRightsSettings>(ct)).GroupId is null)
        {
            return "Choose Cookie's group on Admin → Cookie → Rights: Cookie reads with its rights.";
        }
        var limit = await model.BudgetLimitAsync(ct);
        if (limit > 0 && await model.SpentAsync(ct) is var spent && spent >= limit)
        {
            return $"Cookie's budget is spent: {spent:0.00} of {limit:0.00} USD this month.";
        }
        return null;
    }

    private async Task<CookieRuntimeEntity> RuntimeAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await EnsureAsync(db, ct);
        return await db.CookieRuntime.AsNoTracking().SingleAsync(ct);
    }

    /// <summary>The one row, created without racing the case book.</summary>
    public static Task EnsureAsync(PnmtdDbContext db, CancellationToken ct) => db.Database.ExecuteSqlRawAsync(
        """INSERT INTO cookie_runtime ("SingletonId", "Running", "State", "CodeStatus", "CodeReason", "CodeCases", "CookieCases", "Failures") """ +
        """VALUES (1, false, 'Stopped', 'AllFine', '', ARRAY[]::uuid[], ARRAY[]::uuid[], 0) ON CONFLICT DO NOTHING""", ct);

    private async Task SetAsync(CookieState state, string? reason, DateTime? nextAt, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.CookieRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.State, state).SetProperty(r => r.StateReason, CaseBook.Cut(reason, 500))
            .SetProperty(r => r.NextRoundAt, nextAt), ct);
    }

    private async Task FailuresAsync(int failures, DateTime? pausedUntil, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.CookieRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.Failures, failures).SetProperty(r => r.PausedUntil, pausedUntil), ct);
    }

    /// <summary>Once a day: the trace and rounds after 30 days, closed cases and the outbox after a year.</summary>
    private async Task RetentionAsync(CancellationToken ct)
    {
        if (Clock.UtcNow - retentionAt < TimeSpan.FromDays(1))
        {
            return;
        }
        retentionAt = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var month = Clock.UtcNow.AddDays(-30);
        var year = Clock.UtcNow.AddDays(-365);
        await db.CookieTrace.Where(t => t.At < month).ExecuteDeleteAsync(ct);
        await db.CookieRounds.Where(r => r.StartedAt < month).ExecuteDeleteAsync(ct);
        await db.CookieOutbox.Where(o => o.CreatedAt < year).ExecuteDeleteAsync(ct);
        await db.CookieCases.Where(c => !c.Open && c.ClosedAt < year).ExecuteDeleteAsync(ct);
        // The decision log and ended instructions (§21.2): a year, like the outbox.
        await db.CookieDecisions.Where(d => d.At < year).ExecuteDeleteAsync(ct);
        await db.CookieInstructions.Where(i => (i.EndedAt ?? i.Until) < year).ExecuteDeleteAsync(ct);
    }
}
