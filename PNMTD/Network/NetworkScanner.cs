using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Network;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Tasks.Helpers;

namespace PNMTD.Network;

/// <summary>
/// The scan's worker (docs/network-map-plan.md §7.2): one router at a time over all runs, runs of one router first, then
/// locations, then all. For each router it holds the router's lock (its polls pause), opens one session as <c>pnmt</c>, runs
/// the tables in order, writing each step for the Scans tab, and stores per table. A router that was running when PNMTD
/// stopped is queued again: a scan only reads. Runs with <c>Mikrotik:Enabled</c> and <c>NetworkMap:Enabled</c>, not under
/// BackgroundTasks:Enabled, as <see cref="JobRunner"/> does, so the tests can run it (<see cref="RunHereAsync"/>).
/// </summary>
public sealed class NetworkScanner(
    IDbContextFactory<PnmtdDbContext> dbs,
    RouterPollService polls,
    MikrotikKeyStore keys,
    NetworkAnalysis analysis,
    IOptionsMonitor<NetworkMapOptions> options,
    IOptionsMonitor<MikrotikOptions> mikrotik,
    ILogger<NetworkScanner> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim wake = new(0);
    private readonly SemaphoreSlim one = new(1, 1);
    private readonly TaskRunDecisionMaker uplink = new();
    private (Guid ScanId, CancellationTokenSource Cts)? running;

    public bool Enabled => options.CurrentValue.Enabled && mikrotik.CurrentValue.Enabled;

    /// <summary>A new run: start it now rather than at the next tick.</summary>
    public void Wake() => wake.Release();

    /// <summary>Closes the session of the run's router that is being scanned now, if any.</summary>
    public void Cancel(Guid scanId)
    {
        if (running is { } r && r.ScanId == scanId)
        {
            r.Cts.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Enabled && options.CurrentValue.Scan.Background)
                {
                    while (await RunNextAsync(stoppingToken))
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The network scanner failed");
            }
            try
            {
                await wake.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Every queued router here and now (the tests); with <paramref name="scanId"/>, only that run's (the CLI).</summary>
    public async Task RunHereAsync(CancellationToken ct, Guid? scanId = null)
    {
        while (await RunNextAsync(ct, scanId))
        {
        }
    }

    /// <summary>Routers that were running when PNMTD stopped: queued again.</summary>
    private async Task RequeueAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.NetworkScanItems.Where(i => i.State == NetworkScanItemState.Running)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.State, NetworkScanItemState.Queued).SetProperty(i => i.Step, (string?)null), ct);
    }

    /// <summary>Scans the next queued router, if there is one; false when there is none.</summary>
    public async Task<bool> RunNextAsync(CancellationToken ct, Guid? scanId = null)
    {
        await one.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            var next = await db.NetworkScanItems
                .Join(db.NetworkScans, i => i.ScanId, s => s.Id, (i, s) => new { Item = i, Scan = s })
                .Where(x => x.Item.State == NetworkScanItemState.Queued && (x.Scan.State == NetworkScanState.Queued || x.Scan.State == NetworkScanState.Running))
                .Where(x => scanId == null || x.Scan.Id == scanId)
                .OrderBy(x => x.Scan.Scope).ThenBy(x => x.Scan.CreatedAt).ThenBy(x => x.Item.Position)
                .Select(x => x.Item.Id)
                .FirstOrDefaultAsync(ct);
            if (next == Guid.Empty)
            {
                await FinishEmptyAsync(db, ct);
                return false;
            }
            var claimed = await db.NetworkScanItems.Where(i => i.Id == next && i.State == NetworkScanItemState.Queued)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.State, NetworkScanItemState.Running).SetProperty(i => i.StartedAt, Clock.UtcNow), ct);
            if (claimed == 0)
            {
                return true;
            }
            await ScanAsync(next, ct);
            return true;
        }
        finally
        {
            one.Release();
        }
    }

    /// <summary>Runs whose routers are all done (or that had none left after a cancel) are finished.</summary>
    private static async Task FinishEmptyAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var open = await db.NetworkScans.Where(s => s.State == NetworkScanState.Queued || s.State == NetworkScanState.Running)
            .Where(s => !db.NetworkScanItems.Any(i => i.ScanId == s.Id && (i.State == NetworkScanItemState.Queued || i.State == NetworkScanItemState.Running)))
            .ToListAsync(ct);
        foreach (var scan in open)
        {
            scan.State = NetworkScanState.Done;
            scan.FinishedAt ??= Clock.UtcNow;
            scan.CurrentSensorId = null;
            scan.CurrentRouterName = null;
            scan.CurrentStep = null;
            scan.CurrentStepNumber = null;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ScanAsync(Guid itemId, CancellationToken stopping)
    {
        await using var db = await dbs.CreateDbContextAsync(stopping);
        var item = await db.NetworkScanItems.SingleAsync(i => i.Id == itemId, stopping);
        var scan = await db.NetworkScans.SingleAsync(s => s.Id == item.ScanId, stopping);
        if (scan.State == NetworkScanState.Queued)
        {
            scan.State = NetworkScanState.Running;
            scan.StartedAt = Clock.UtcNow;
        }
        scan.CurrentSensorId = item.SensorId;
        scan.CurrentRouterName = item.RouterName;
        scan.CurrentStep = "connecting";
        scan.CurrentStepNumber = 0;
        await db.SaveChangesAsync(stopping);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        running = (scan.Id, cts);
        var watch = Stopwatch.StartNew();
        try
        {
            var (state, error, reading) = await ScanRouterAsync(db, item, scan, cts.Token);
            item.State = state;
            item.Error = Cut(error, 2000);
            if (reading is not null)
            {
                item.Counts = JsonSerializer.Serialize(NetworkScanStore.Counts(reading), Json);
                item.TableErrors = reading.Errors.Count == 0 ? null
                    : JsonSerializer.Serialize(reading.Errors.ToDictionary(e => e.Key.ToString(), e => e.Value), Json);
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            item.State = NetworkScanItemState.Cancelled;
            item.Error = "Cancelled.";
        }
        finally
        {
            running = null;
        }
        if (stopping.IsCancellationRequested)
        {
            return;
        }
        item.FinishedAt = Clock.UtcNow;
        item.DurationMs = (int)watch.ElapsedMilliseconds;
        item.Step = null;
        await db.Entry(scan).ReloadAsync(stopping);
        switch (item.State)
        {
            case NetworkScanItemState.Succeeded:
                scan.Succeeded++;
                break;
            case NetworkScanItemState.Failed:
                scan.Failed++;
                break;
            case NetworkScanItemState.Skipped:
                scan.Skipped++;
                break;
        }
        var left = await db.NetworkScanItems.AnyAsync(i => i.ScanId == scan.Id && i.Id != item.Id && i.State == NetworkScanItemState.Queued, stopping);
        if (!left && scan.State != NetworkScanState.Cancelled)
        {
            scan.State = NetworkScanState.Done;
        }
        if (!left || scan.State == NetworkScanState.Cancelled)
        {
            scan.FinishedAt ??= Clock.UtcNow;
            scan.CurrentSensorId = null;
            scan.CurrentRouterName = null;
            scan.CurrentStep = null;
            scan.CurrentStepNumber = null;
        }
        await db.SaveChangesAsync(stopping);

        // The analysis: at the end of a run, and during a long one at most every Analysis:IntervalMinutes (§5.1).
        if (!left || scan.State == NetworkScanState.Cancelled || analysis.Due)
        {
            try
            {
                await analysis.RunAsync(stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The network analysis failed");
            }
        }
    }

    private async Task<(NetworkScanItemState State, string? Error, NetworkScanReading? Reading)> ScanRouterAsync(PnmtdDbContext db, NetworkScanItemEntity item,
        NetworkScanEntity scan, CancellationToken ct)
    {
        var router = item.SensorId is { } id ? await db.MikrotikRouters.Include(r => r.Sensor).SingleOrDefaultAsync(r => r.SensorId == id, ct) : null;
        if (router is null)
        {
            return (NetworkScanItemState.Skipped, "The router no longer exists.", null);
        }
        var skip = !router.Sensor.Enabled ? "The sensor is disabled."
            : router.Mode == MikrotikMode.WireGuardOnly ? "WireGuard only: there is no login."
            : router.SetupState != MikrotikSetupState.Ready ? router.SetupState == MikrotikSetupState.NeedsAttention
                ? "The router needs attention: PNMTD's login is refused (see its Setup tab)."
                : "Not ready yet: PNMTD hasn't logged in to it."
            : !uplink.DetermineIfTaskShouldRun(db) ? "PNMTD's uplink is down."
            : null;
        if (skip is not null)
        {
            return (NetworkScanItemState.Skipped, skip, null);
        }
        var (target, targetError) = await polls.TargetAsync(db, router, RouterPoll.UserName, await polls.PnmtCredentialsAsync(ct), ct);
        if (target is null)
        {
            return (NetworkScanItemState.Skipped, targetError, null);
        }

        IAsyncDisposable routerLock;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.Scan.LockWaitSeconds)));
            try
            {
                routerLock = await polls.LockAsync(router.SensorId, wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (NetworkScanItemState.Skipped, "Busy: a poll or a job holds the router.", null);
            }
        }
        await using (routerLock)
        {
            var installation = await keys.GetAsync(ct);
            var o = options.CurrentValue.Scan;
            var reading = new NetworkScanReading();
            IRouterSession session;
            try
            {
                session = await polls.Connector.ConnectAsync(target, ct);
            }
            catch (RouterSessionException ex)
            {
                return (NetworkScanItemState.Failed, ex.Message, null);
            }
            await using (session)
            {
                var number = 0;
                foreach (var table in NetworkScanScript.Order)
                {
                    number++;
                    await StepAsync(db, item, scan, table, number, ct);
                    string? error;
                    try
                    {
                        var exec = await session.ExecAsync(NetworkScanScript.Script(table, installation.Comment), TimeSpan.FromSeconds(Math.Max(5, o.TableTimeoutSeconds)),
                            Math.Max(64 * 1024, o.MaxOutputBytes), ct);
                        error = exec.TimedOut ? $"Timed out after {o.TableTimeoutSeconds} s."
                            : exec.Truncated ? $"Output over {o.MaxOutputBytes / (1024 * 1024)} MiB."
                            : !exec.Succeeded ? $"Exit status {exec.ExitStatus}: {Excerpt(exec.Output + exec.Error)}"
                            : null;
                        if (error is null)
                        {
                            reading.Add(router.SensorId, table, exec.Output, Clock.UtcNow);
                        }
                    }
                    catch (FormatException ex)
                    {
                        error = ex.Message;
                    }
                    catch (RouterSessionException ex)
                    {
                        error = ex.Message;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        error = $"Timed out after {o.TableTimeoutSeconds} s.";
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelled: the tables finished so far are kept (§7.2).
                        await KeepAsync(db, router, reading);
                        throw;
                    }
                    if (error is not null)
                    {
                        reading.Errors[table] = error;
                        if (NetworkScanScript.Required(table))
                        {
                            return (NetworkScanItemState.Failed, $"{NetworkScanScript.Step(table)}: {error}", reading);
                        }
                    }
                }
            }
            reading.Complete();
            await NetworkScanStore.SaveAsync(db, router.SensorId, router.Version, reading, Clock.UtcNow, ct);
            return (NetworkScanItemState.Succeeded, null, reading);
        }
    }

    private async Task KeepAsync(PnmtdDbContext db, MikrotikRouterEntity router, NetworkScanReading reading)
    {
        if (!reading.Read.Contains(ScanTable.Interfaces))
        {
            return;
        }
        try
        {
            reading.Complete();
            await NetworkScanStore.SaveAsync(db, router.SensorId, router.Version, reading, Clock.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Keeping the cancelled scan of {Router} failed", router.Sensor.Name);
        }
    }

    private static async Task StepAsync(PnmtdDbContext db, NetworkScanItemEntity item, NetworkScanEntity scan, ScanTable table, int number, CancellationToken ct)
    {
        var step = NetworkScanScript.Step(table);
        item.Step = step;
        scan.CurrentStep = step;
        scan.CurrentStepNumber = number;
        await db.SaveChangesAsync(ct);
    }

    private static string Excerpt(string text)
    {
        var t = text.Trim();
        return t.Length <= 300 ? t : t[..300] + "…";
    }

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
