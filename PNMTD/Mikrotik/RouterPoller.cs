using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Enums;
using PNMTD.Tasks.Helpers;

namespace PNMTD.Mikrotik;

/// <summary>
/// Polls the routers that are due (docs/mikrotik-plan.md §5.2): a timer every 10 seconds, one run at a
/// time, paused while PNMTD's own uplink is down (the routers would all look unreachable). A router is
/// due when its Interval has passed since its last poll; up to <c>MaxConcurrentSessions</c> run at once.
/// </summary>
public sealed class RouterPoller(
    IDbContextFactory<PnmtdDbContext> dbs,
    RouterPollService polls,
    IOptionsMonitor<MikrotikOptions> options,
    ILogger<RouterPoller> logger) : IHostedService, IDisposable
{
    public const int MinimumIntervalSeconds = 60;

    private readonly RunGuard runGuard = new();
    private readonly TaskRunDecisionMaker uplink = new();
    private readonly CancellationTokenSource stopping = new();
    private Timer? timer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10));
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
            logger.LogError(ex, "The MikroTik poller failed");
        }
        finally
        {
            runGuard.Exit();
        }
    }

    /// <summary>One round: every router that is due. Public for the tests.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            return 0;
        }
        List<Guid> due;
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            if (!uplink.DetermineIfTaskShouldRun(db))
            {
                return 0;
            }
            var now = Clock.UtcNow;
            var candidates = await db.MikrotikRouters.AsNoTracking()
                .Where(r => r.Sensor.Enabled && (r.Mode == MikrotikMode.DirectSsh || r.Mode == MikrotikMode.WireGuardSsh))
                .Select(r => new { r.SensorId, r.LastPollAt, r.Sensor.Interval })
                .ToListAsync(ct);
            due = candidates
                .Where(r => r.LastPollAt is null || now - r.LastPollAt.Value >= TimeSpan.FromSeconds(Math.Max(MinimumIntervalSeconds, r.Interval)))
                .OrderBy(r => r.LastPollAt ?? DateTime.MinValue)
                .Select(r => r.SensorId)
                .ToList();
            db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_MIKROTIK_POLL_RUN);
            await db.SaveChangesAsync(ct);
        }
        await Parallel.ForEachAsync(due, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.CurrentValue.Ssh.MaxConcurrentSessions),
            CancellationToken = ct,
        }, async (id, token) =>
        {
            try
            {
                await polls.PollAsync(id, wait: false, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Polling MikroTik router {SensorId} failed", id);
            }
        });
        return due.Count;
    }
}
