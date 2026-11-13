using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Ai;

/// <summary>Runs one purpose's work items (§3.5): returns the result as JSON, or throws.</summary>
public interface IAiWorkHandler
{
    string Purpose { get; }

    Task<string?> RunAsync(AiWorkEntity work, CancellationToken ct);
}

/// <summary>The work is no longer needed (the mail was handled meanwhile): cancelled, not failed.</summary>
public sealed class AiWorkStaleException(string reason) : Exception(reason);

/// <summary>Adds work items: one open item per purpose and subject.</summary>
public sealed class AiWorkQueue(IDbContextFactory<PnmtdDbContext> dbs, AiWorker worker)
{
    public static readonly AiWorkState[] Open = [AiWorkState.Queued, AiWorkState.Running, AiWorkState.WaitingRetry, AiWorkState.WaitingBudget];

    public async Task<Guid> EnqueueAsync(string purpose, string? subjectType, Guid? subjectId, string? inputHash, CancellationToken ct, DateTime? notBefore = null)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        if (subjectId is not null)
        {
            var open = await db.AiWork.Where(w => w.Purpose == purpose && w.SubjectType == subjectType && w.SubjectId == subjectId && Open.Contains(w.State))
                .Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);
            if (open is { } existing)
            {
                return existing;
            }
        }
        var work = new AiWorkEntity
        {
            Id = Guid.NewGuid(), Purpose = purpose, SubjectType = subjectType, SubjectId = subjectId, InputHash = inputHash,
            State = AiWorkState.Queued, CreatedAt = Clock.UtcNow, NextAttemptAt = notBefore ?? Clock.UtcNow,
        };
        db.AiWork.Add(work);
        await db.SaveChangesAsync(ct);
        worker.Wake();
        return work.Id;
    }
}

/// <summary>
/// Runs due work items, <c>Ai:Parallelism</c> at a time (docs/ai-operations-plan.md §3.5). After a failed attempt:
/// 5 retries a minute apart, then 1 hour, 3 hours, and every 24 hours after (<c>Ai:Retry</c>). The sixth failure of the
/// first item in an outage writes 421 on the AI sensor; the next success anywhere writes 220. A spent budget makes
/// items wait for the next period without counting an attempt. Not a monitoring task: it runs while the uplink is down,
/// and its requests simply fail and are retried.
/// </summary>
public sealed class AiWorker(IDbContextFactory<PnmtdDbContext> dbs, IEnumerable<IAiWorkHandler> handlers, IOptionsMonitor<AiOptions> options,
    AiBudget budget, SystemSensors sensors, ILogger<AiWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim wake = new(0);
    private readonly SemaphoreSlim sensorGate = new(1, 1);
    private readonly Dictionary<Guid, Task> running = [];
    private DateTime lastBudgetCheck = DateTime.MinValue;

    public void Wake()
    {
        if (wake.CurrentCount == 0)
        {
            wake.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetOrphansAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AiWorker");
            }
            await wake.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Items that were running when PNMTD stopped run again.</summary>
    private async Task ResetOrphansAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            await db.AiWork.Where(w => w.State == AiWorkState.Running).ExecuteUpdateAsync(u => u.SetProperty(w => w.State, AiWorkState.Queued), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AiWorker: could not reset running items");
        }
    }

    /// <summary>One round: the budget's period, then the due items. Public for the tests.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        foreach (var done in running.Where(r => r.Value.IsCompleted).Select(r => r.Key).ToList())
        {
            running.Remove(done);
        }
        var o = options.CurrentValue;
        if (!o.Usable)
        {
            return;
        }
        if (Clock.UtcNow - lastBudgetCheck > TimeSpan.FromMinutes(1))
        {
            lastBudgetCheck = Clock.UtcNow;
            await budget.CheckAsync(ct);
        }
        var free = Math.Max(1, o.Parallelism) - running.Count;
        if (free <= 0)
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var now = Clock.UtcNow;
        var due = await db.AiWork
            .Where(w => (w.State == AiWorkState.Queued || w.State == AiWorkState.WaitingRetry || w.State == AiWorkState.WaitingBudget) && w.NextAttemptAt <= now)
            .OrderBy(w => w.NextAttemptAt).Take(free).ToListAsync(ct);
        foreach (var work in due)
        {
            work.State = AiWorkState.Running;
        }
        await db.SaveChangesAsync(ct);
        foreach (var work in due)
        {
            running[work.Id] = RunOneAsync(work.Id, ct);
        }
    }

    /// <summary>Runs every due item and waits for them: for tests and <c>Retry now</c>.</summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        await TickAsync(ct);
        await Task.WhenAll(running.Values.ToList());
    }

    private async Task RunOneAsync(Guid id, CancellationToken ct)
    {
        await Task.Yield();
        await using var db = await dbs.CreateDbContextAsync(ct);
        var work = await db.AiWork.SingleAsync(w => w.Id == id, ct);
        var o = options.CurrentValue;
        try
        {
            var handler = handlers.FirstOrDefault(h => h.Purpose == work.Purpose)
                ?? throw new AiNotConfiguredException($"No handler for {work.Purpose}.");
            await budget.EnsureAvailableAsync(ct);
            work.Result = await handler.RunAsync(work, ct);
            work.State = AiWorkState.Succeeded;
            work.FinishedAt = Clock.UtcNow;
            work.LastError = null;
            await db.SaveChangesAsync(ct);
            await AvailableAsync(ct);
        }
        catch (AiBudgetExceededException ex)
        {
            work.State = AiWorkState.WaitingBudget;
            work.NextAttemptAt = ex.Until;
            work.LastError = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (AiWorkStaleException ex)
        {
            work.State = AiWorkState.Cancelled;
            work.FinishedAt = Clock.UtcNow;
            work.LastError = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            work.State = AiWorkState.Queued;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            var permanent = ex is AiNotConfiguredException || ex is AiRequestException { Retryable: false };
            work.LastError = Truncate(ex.Message);
            if (permanent)
            {
                work.State = AiWorkState.Failed;
                work.FinishedAt = Clock.UtcNow;
                logger.LogError("AI work {Purpose} failed for good: {Error}", work.Purpose, ex.Message);
                await db.SaveChangesAsync(CancellationToken.None);
                await FailingAsync($"AI work failed for good ({work.Purpose}): {Truncate(ex.Message, 300)}", CancellationToken.None);
                return;
            }
            work.Attempts++;
            work.State = AiWorkState.WaitingRetry;
            work.NextAttemptAt = Clock.UtcNow + Delay(work.Attempts, o.Retry);
            var notify = work.Attempts >= o.Retry.QuickAttempts + 1 && !work.Notified;
            if (notify)
            {
                work.Notified = true;
            }
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning("AI work {Purpose} attempt {Attempt} failed, next at {Next:u}: {Error}", work.Purpose, work.Attempts, work.NextAttemptAt, ex.Message);
            if (notify)
            {
                await FailingAsync($"AI requests keep failing ({o.Retry.QuickAttempts} retries a minute apart): {Truncate(ex.Message, 300)}", CancellationToken.None);
            }
        }
    }

    /// <summary>The wait after failed attempt <paramref name="failures"/> (1-based): quick, then the long delays, the last repeating.</summary>
    public static TimeSpan Delay(int failures, AiOptions.RetryOptions retry)
    {
        if (failures <= retry.QuickAttempts)
        {
            return TimeSpan.FromSeconds(Math.Max(1, retry.QuickDelaySeconds));
        }
        var longDelays = retry.LongDelays;
        var index = Math.Min(failures - retry.QuickAttempts - 1, longDelays.Count - 1);
        return TimeSpan.FromHours(longDelays[index]);
    }

    /// <summary>421 once per outage: not while the AI sensor's last event is 421 already.</summary>
    private async Task FailingAsync(string message, CancellationToken ct)
    {
        await sensorGate.WaitAsync(ct);
        try
        {
            if ((await sensors.LastAsync(SystemSensorKeys.Ai, ct))?.Code != PNMTStatusCodes.AI_FAILING)
            {
                await sensors.WriteAsync(SystemSensorKeys.Ai, PNMTStatusCodes.AI_FAILING, message, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The AI system sensor could not be written");
        }
        finally
        {
            sensorGate.Release();
        }
    }

    /// <summary>220 after an outage, or the first time; otherwise it only moves the time of the last 220.</summary>
    private async Task AvailableAsync(CancellationToken ct)
    {
        await sensorGate.WaitAsync(ct);
        try
        {
            var last = await sensors.LastAsync(SystemSensorKeys.Ai, ct);
            await sensors.WriteAsync(SystemSensorKeys.Ai, PNMTStatusCodes.SYSTEM_OK,
                last?.Code == PNMTStatusCodes.AI_FAILING ? "AI available again." : "AI available.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The AI system sensor could not be written");
        }
        finally
        {
            sensorGate.Release();
        }
    }

    private static string Truncate(string text, int max = 1000) => text.Length <= max ? text : text[..max];
}
