using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Events;

namespace PNMTD.Mikrotik;

/// <summary>An update's options (docs/mikrotik-plan.md §7.1).</summary>
public sealed record UpdateJobOptions(bool BinaryBackup = true, bool Firmware = true, bool StopAtFirstFailure = true);

/// <summary>A remote command's options (§8.2): exports before and after, and one router at a time with a stop at the first failure.</summary>
public sealed record CommandJobOptions(bool BackupBeforeAndAfter = false, bool OneAtATime = false);

/// <summary>
/// Runs the jobs (docs/mikrotik-plan.md §7.3, §8.2) step by step, holding the router (the poller pauses), with
/// every step in the permanent job log. Up to <c>Commands:Parallelism</c> jobs run at once, one per router. A
/// batch of updates, or of commands run one at a time, runs in order, and stops at its first failure by
/// cancelling the jobs after it. A job whose admin password is gone (a restart of PNMTD, its lifetime) fails at
/// the first step that needs it. Not a monitoring task: it runs without BackgroundTasks, so the tests can run jobs.
/// </summary>
public sealed class JobRunner(
    IDbContextFactory<PnmtdDbContext> dbs,
    RouterPollService polls,
    CredentialVault vault,
    IOptionsMonitor<MikrotikOptions> options,
    ILogger<JobRunner> logger) : BackgroundService
{
    private readonly SemaphoreSlim wake = new(0);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> active = new();

    /// <summary>A new job, or a finished one: start what may run now rather than at the next tick.</summary>
    public void Wake() => wake.Release();

    /// <summary>Closes a running command's SSH channel (§8.2). Whatever the router did stays done.</summary>
    public void Cancel(Guid jobId)
    {
        if (active.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailOrphansAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartReadyAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The MikroTik job runner failed");
            }
            try
            {
                await wake.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Jobs that were running when PNMTD stopped: their password is gone (§7.5).</summary>
    private async Task FailOrphansAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var running = await db.MikrotikJobs.Where(j => j.State == MikrotikJobState.Running).ToListAsync(ct);
        foreach (var job in running)
        {
            Finish(db, job, MikrotikJobState.Failed, job.Kind switch
            {
                // After the reboot began, the router carries on without PNMTD: the next poll shows the version (242).
                MikrotikJobKind.Update when job.Step is >= MikrotikJobStep.Reboot and <= MikrotikJobStep.Done =>
                    "PNMTD restarted while the router was rebooting for the update: the update goes on without PNMTD, and the next poll shows the version.",
                MikrotikJobKind.Update => "PNMTD restarted during the job, and the admin password is gone with it: start the update again.",
                _ when job.Step >= MikrotikJobStep.Run => "PNMTD restarted while the command ran: the router may have run it, but its output is lost.",
                _ => "PNMTD restarted before the command ran: run it again.",
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Starts every queued job that may run now: within the parallelism, not on a router with a running job, and
    /// for a batch in order only after its job before. Returns how many started.
    /// </summary>
    private async Task<int> StartReadyAsync(CancellationToken stopping)
    {
        var limit = Math.Max(1, options.CurrentValue.Commands.Parallelism);
        if (active.Count >= limit)
        {
            return 0;
        }
        await using var db = await dbs.CreateDbContextAsync(stopping);
        var queued = await db.MikrotikJobs.Where(j => j.State == MikrotikJobState.Queued).OrderBy(j => j.CreatedAt).Take(1000).ToListAsync(stopping);
        if (queued.Count == 0)
        {
            return 0;
        }
        var running = await db.MikrotikJobs.Where(j => j.State == MikrotikJobState.Running).Select(j => new { j.BatchId, j.SensorId }).ToListAsync(stopping);
        var busyRouters = running.Where(r => r.SensorId != null).Select(r => r.SensorId!.Value).ToHashSet();
        var busyBatches = running.Select(r => r.BatchId).ToHashSet();
        var batchIds = queued.Select(j => j.BatchId).Distinct().ToList();
        var failedBatches = (await db.MikrotikJobs.Where(j => batchIds.Contains(j.BatchId) && j.State == MikrotikJobState.Failed)
            .Select(j => j.BatchId).Distinct().ToListAsync(stopping)).ToHashSet();
        var inOrderSeen = new HashSet<Guid>();
        var started = 0;
        foreach (var job in queued)
        {
            var (inOrder, stopAtFailure) = Order(job);
            if (inOrder && !inOrderSeen.Add(job.BatchId))
            {
                continue;
            }
            if (stopAtFailure && failedBatches.Contains(job.BatchId))
            {
                // The canary failed (§7.4): the rest of the batch does not start.
                foreach (var rest in queued.Where(j => j.BatchId == job.BatchId))
                {
                    Finish(db, rest, MikrotikJobState.Cancelled, "Not started: an earlier router of the batch failed.");
                }
                await db.SaveChangesAsync(stopping);
                vault.Remove(job.BatchId);
                continue;
            }
            if (active.Count >= limit || (inOrder && busyBatches.Contains(job.BatchId)) || (job.SensorId is { } busy && busyRouters.Contains(busy)))
            {
                continue;
            }
            // Claimed only while still queued: a Cancel in between wins.
            var now = Clock.UtcNow;
            var claimed = await db.MikrotikJobs.Where(j => j.Id == job.Id && j.State == MikrotikJobState.Queued)
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.State, MikrotikJobState.Running).SetProperty(j => j.StartedAt, now), stopping);
            if (claimed == 0)
            {
                continue;
            }
            busyBatches.Add(job.BatchId);
            if (job.SensorId is { } sensorId)
            {
                busyRouters.Add(sensorId);
            }
            Launch(job.Id, stopping);
            started++;
        }
        return started;
    }

    private void Launch(Guid jobId, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        active[jobId] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(jobId, cts.Token, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // PNMTD stops: the next start fails the job (FailOrphansAsync).
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MikroTik job {JobId} failed unexpectedly", jobId);
                await FailUnexpectedAsync(jobId);
            }
            finally
            {
                active.TryRemove(jobId, out _);
                cts.Dispose();
                Wake();
            }
        }, CancellationToken.None);
    }

    /// <summary>A job is never left running by a fault of PNMTD's own.</summary>
    private async Task FailUnexpectedAsync(Guid jobId)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync();
            var job = await db.MikrotikJobs.SingleAsync(j => j.Id == jobId);
            if (job.State is MikrotikJobState.Running or MikrotikJobState.Queued)
            {
                Finish(db, job, MikrotikJobState.Failed, "PNMTD failed during the job; its log has the details.");
                await db.SaveChangesAsync();
            }
            await ForgetIfDoneAsync(db, job.BatchId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MikroTik job {JobId} could not be marked as failed", jobId);
        }
    }

    /// <summary>
    /// The CLI's batch (§12), claimed at its creation and run here, in order: the server's runner leaves running
    /// jobs alone. A batch run one at a time stops at its first failure.
    /// </summary>
    public async Task RunHereAsync(IReadOnlyList<Guid> jobIds, CancellationToken ct)
    {
        var stopped = false;
        foreach (var id in jobIds)
        {
            if (stopped || ct.IsCancellationRequested)
            {
                await using var db = await dbs.CreateDbContextAsync();
                var rest = await db.MikrotikJobs.SingleAsync(j => j.Id == id);
                Finish(db, rest, MikrotikJobState.Cancelled, stopped ? "Not started: an earlier router of the batch failed." : "Cancelled before it started.");
                await db.SaveChangesAsync();
                continue;
            }
            try
            {
                // ct cancels the job as the web's Cancel does (Ctrl-C); nothing stops the bookkeeping.
                await RunAsync(id, ct, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MikroTik job {JobId} failed unexpectedly", id);
                await FailUnexpectedAsync(id);
            }
            await using (var db = await dbs.CreateDbContextAsync())
            {
                var job = await db.MikrotikJobs.AsNoTracking().SingleAsync(j => j.Id == id);
                stopped = job.State == MikrotikJobState.Failed && Order(job).StopAtFailure;
            }
        }
        if (jobIds.Count > 0)
        {
            await using var db = await dbs.CreateDbContextAsync();
            var batchId = await db.MikrotikJobs.Where(j => j.Id == jobIds[0]).Select(j => j.BatchId).SingleAsync();
            await ForgetIfDoneAsync(db, batchId, CancellationToken.None);
        }
    }

    /// <summary>Whether a job's batch runs in order, and whether it stops at its first failure.</summary>
    private static (bool InOrder, bool StopAtFailure) Order(MikrotikJobEntity job)
    {
        if (job.Kind == MikrotikJobKind.Update)
        {
            return (true, UpdateOptions(job).StopAtFirstFailure);
        }
        var oneAtATime = CommandOptions(job).OneAtATime;
        return (oneAtATime, oneAtATime);
    }

    private static UpdateJobOptions UpdateOptions(MikrotikJobEntity job) =>
        job.Options is { } json ? JsonSerializer.Deserialize<UpdateJobOptions>(json, VersionCatalog.Json) ?? new UpdateJobOptions() : new UpdateJobOptions();

    private static CommandJobOptions CommandOptions(MikrotikJobEntity job) =>
        job.Options is { } json ? JsonSerializer.Deserialize<CommandJobOptions>(json, VersionCatalog.Json) ?? new CommandJobOptions() : new CommandJobOptions();

    private sealed class StepFailed(string message) : Exception(message);

    private sealed class Cancelled() : Exception("Cancelled");

    private async Task RunAsync(Guid jobId, CancellationToken ct, CancellationToken stopping)
    {
        await using var db = await dbs.CreateDbContextAsync(stopping);
        var job = await db.MikrotikJobs.SingleAsync(j => j.Id == jobId, stopping);
        var downloaded = false;
        try
        {
            if (job.SensorId is not { } sensorId)
            {
                throw new StepFailed("The router was deleted.");
            }
            await using (await polls.LockAsync(sensorId, ct))
            {
                if (job.Kind == MikrotikJobKind.Update)
                {
                    await UpdateAsync(db, job, sensorId, () => downloaded = true, ct);
                }
                else
                {
                    await CommandAsync(db, job, sensorId, ct);
                }
            }
            await db.SaveChangesAsync(ct);
            if (job.State == MikrotikJobState.Running)
            {
                // Step 9, before the job counts as done: the normal poll writes the new state, 242 for the new
                // version, and after an update a fresh export, a new backup version only if the upgrade changed it.
                try
                {
                    await polls.PollAsync(sensorId, wait: true, ct, job.Kind == MikrotikJobKind.Update
                        ? new PollRequest(ForceExport: true, Trigger: MikrotikBackupTrigger.AfterUpdate, UserId: job.RequestedBy, UserName: job.RequestedByName)
                        : null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The router did its part; the poller tries again at its next round.
                    logger.LogWarning(ex, "The poll after MikroTik job {JobId} failed", job.Id);
                }
                Finish(db, job, MikrotikJobState.Succeeded, null);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Cancelled)
        {
            Finish(db, job, MikrotikJobState.Cancelled, "Cancelled.");
            await LogAsync(db, job, job.Step, MikrotikJobOutcome.Info, job.Kind == MikrotikJobKind.Update
                ? "Cancelled before the reboot: nothing on the router changed but what the log shows."
                : "Cancelled before the command ran.", null, stopping);
            await db.SaveChangesAsync(stopping);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            // Cancel(jobId) closed the session of a running command (§8.2).
            Finish(db, job, MikrotikJobState.Cancelled, "Cancelled while it ran: the session was closed, and whatever the router did stays done.");
            await LogAsync(db, job, job.Step, MikrotikJobOutcome.Info, job.Error!, null, stopping);
            await db.SaveChangesAsync(stopping);
        }
        catch (Exception ex) when (ex is StepFailed or RouterSessionException or FormatException or TimeoutException)
        {
            var message = ex.Message + (downloaded
                ? " The downloaded package installs at the router's next reboot, whoever causes it; /file remove of the .npk file prevents that."
                : "");
            await LogAsync(db, job, job.Step, MikrotikJobOutcome.Failed, message, null, stopping);
            Finish(db, job, MikrotikJobState.Failed, message);
            await db.SaveChangesAsync(stopping);
            if (job.SensorId is { } id && job.Kind == MikrotikJobKind.Update)
            {
                await WriteEventAsync(db, id, PNMTStatusCodes.MIKROTIK_UPDATE_FAILED, $"Update to {job.ToVersion} failed at {job.Step}: {ex.Message}", job, stopping);
            }
        }
        finally
        {
            await ForgetIfDoneAsync(db, job.BatchId, CancellationToken.None);
        }
    }

    // --- an update (§7.3) ----------------------------------------------------------------------------------

    private async Task UpdateAsync(PnmtdDbContext db, MikrotikJobEntity job, Guid sensorId, Action downloaded, CancellationToken ct)
    {
        var o = options.CurrentValue.Updates;
        var opts = UpdateOptions(job);
        var router = await db.MikrotikRouters.Include(r => r.Sensor).SingleAsync(r => r.SensorId == sensorId, ct);
        var target = RouterOsVersion.TryParse(job.ToVersion, out var t) ? t : throw new StepFailed($"No target version for the channel {job.Channel}.");

        // 1. Check, as pnmt.
        await StepAsync(db, job, MikrotikJobStep.Check, "Reading the router as pnmt", ct);
        var reading = await AsPnmtAsync(db, router, ct, (session, token) => polls.ReadAsync(session, token));
        job.FromVersion = reading.VersionText;
        if (reading.Version is not { } installed || installed.IsBelowFloor)
        {
            throw new StepFailed($"RouterOS {reading.VersionText} is below 7.13.");
        }
        if (installed >= target)
        {
            await LogAsync(db, job, MikrotikJobStep.Check, MikrotikJobOutcome.Info, $"Already on {reading.VersionText}: nothing to do.", null, ct);
            Finish(db, job, MikrotikJobState.Skipped, null);
            return;
        }
        if (reading.FreeStorage is { } free && free < o.MinimumFreeStorageBytes)
        {
            throw new StepFailed($"Only {free / 1024 / 1024} MiB free storage; an update needs about {o.MinimumFreeStorageBytes / 1024 / 1024} MiB.");
        }
        await LogAsync(db, job, MikrotikJobStep.Check, MikrotikJobOutcome.Ok,
            $"{reading.Identity} ({reading.DisplayModel}{(reading.SerialNumber is { } sn ? $", {sn}" : "")}): RouterOS {reading.VersionText} → {target}", null, ct);
        await CheckCancelledAsync(db, job, ct);

        // 2. Export, as pnmt.
        await StepAsync(db, job, MikrotikJobStep.Export, "Exporting the configuration", ct);
        var (stored, _) = await ExportAsync(db, job, router, MikrotikBackupTrigger.BeforeUpdate, "", ct);
        await LogAsync(db, job, MikrotikJobStep.Export, MikrotikJobOutcome.Ok,
            stored.Backup is { } nb ? $"Stored as version {nb.Number}." : "The configuration is unchanged since the last backup.", null, ct);
        await CheckCancelledAsync(db, job, ct);

        var (username, password) = vault.Get(job.BatchId) ?? throw new StepFailed(
            "The admin password is gone (PNMTD restarted, or the job waited longer than its lifetime): start the update again.");

        // 3. The binary backup, as the admin (§6.5): encrypted with the admin's password.
        if (opts.BinaryBackup)
        {
            await StepAsync(db, job, MikrotikJobStep.BinaryBackup, "Saving a binary backup", ct);
            var file = $"pnmt-{job.Id.ToString("N")[..8]}.backup";
            var content = await AsAdminAsync(db, router, username, password, ct, async (session, token) =>
            {
                var save = await session.ExecAsync($"/system backup save name={RouterOsScript.Quote(file[..^7])} password={RouterOsScript.Quote(password)}",
                    TimeSpan.FromMinutes(2), 64 * 1024, token);
                if (!save.Succeeded || !save.Output.Contains("saved", StringComparison.OrdinalIgnoreCase))
                {
                    throw new StepFailed($"The backup was not saved: {First(save.Output)}");
                }
                var bytes = await session.DownloadAsync(file, o.MaxBinaryBackupBytes, token);
                await session.ExecAsync($"/file remove [find name={RouterOsScript.Quote(file)}]", TimeSpan.FromSeconds(30), 4096, token);
                return bytes;
            });
            if (content.Length < 64)
            {
                throw new StepFailed($"The binary backup has only {content.Length} bytes.");
            }
            var number = await db.MikrotikBackups.Where(x => x.SensorId == sensorId).Select(x => (int?)x.Number).MaxAsync(ct) ?? 0;
            db.MikrotikBackups.Add(new MikrotikBackupEntity
            {
                Id = Guid.NewGuid(), SensorId = sensorId, RouterName = router.Sensor.Name, Number = number + 1, Kind = MikrotikBackupKind.Binary,
                Trigger = MikrotikBackupTrigger.BeforeUpdate, JobId = job.Id, CreatedAt = Clock.UtcNow, CreatedBy = job.RequestedBy,
                CreatedByName = job.RequestedByName, RouterOsVersion = reading.VersionText, Content = content, Size = content.Length,
                Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), Encrypted = true,
            });
            await db.SaveChangesAsync(ct);
            await LogAsync(db, job, MikrotikJobStep.BinaryBackup, MikrotikJobOutcome.Ok,
                $"Stored as version {number + 1} ({content.Length / 1024} KiB), encrypted with {username}'s password.", null, ct);
            await CheckCancelledAsync(db, job, ct);
        }

        // 4. Download, as the admin: set the channel, check, download, and wait for it.
        await StepAsync(db, job, MikrotikJobStep.Download, $"Downloading RouterOS {target} ({job.Channel})", ct);
        await AsAdminAsync(db, router, username, password, ct, async (session, token) =>
        {
            await MustAsync(session, $"/system package update set channel={job.Channel}", token);
            await MustAsync(session, "/system package update check-for-updates once", token, TimeSpan.FromMinutes(2));
            var latest = (await session.ExecAsync(":put [/system package update get latest-version]", TimeSpan.FromSeconds(30), 4096, token)).Output.Trim();
            if (RouterOsVersion.TryParse(latest, out var l) && l != target)
            {
                await LogAsync(db, job, MikrotikJobStep.Download, MikrotikJobOutcome.Info, $"The router sees {latest} on {job.Channel}; PNMT expected {target}.", null, token);
            }
            await session.ExecAsync("/system package update download", TimeSpan.FromMinutes(Math.Max(1, o.DownloadTimeoutMinutes)), 64 * 1024, token);
            var deadline = Clock.UtcNow.AddMinutes(Math.Max(1, o.DownloadTimeoutMinutes));
            while (true)
            {
                var status = (await session.ExecAsync(":put [/system package update get status]", TimeSpan.FromSeconds(30), 4096, token)).Output.Trim();
                if (status.Contains("Downloaded", StringComparison.OrdinalIgnoreCase) || status.Contains("reboot", StringComparison.OrdinalIgnoreCase))
                {
                    downloaded();
                    await LogAsync(db, job, MikrotikJobStep.Download, MikrotikJobOutcome.Ok, status, null, token);
                    return 0;
                }
                if (status.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || status.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || status.Contains("not enough", StringComparison.OrdinalIgnoreCase))
                {
                    throw new StepFailed($"The download failed: {status}");
                }
                if (Clock.UtcNow > deadline)
                {
                    throw new StepFailed($"The download did not finish within {o.DownloadTimeoutMinutes} minutes: {status}");
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, o.PollSeconds)), token);
            }
        });
        await CheckCancelledAsync(db, job, ct);

        // 5.–7. Reboot, wait, verify.
        var after = await RebootAndWaitAsync(db, job, router, username, password, "the update", ct);
        await StepAsync(db, job, MikrotikJobStep.Verify, "Checking the version", ct);
        if (after.Version != target)
        {
            throw new StepFailed($"The router still runs {after.VersionText}: the package was not applied (its /log says why).");
        }
        await LogAsync(db, job, MikrotikJobStep.Verify, MikrotikJobOutcome.Ok, $"RouterOS {after.VersionText}", null, ct);

        // 8. The RouterBOARD firmware: always a second reboot (§7.3).
        if (opts.Firmware && after.IsRouterBoard && after.FirmwareCurrent is { } current && after.FirmwareUpgrade is { } upgrade && current != upgrade)
        {
            await StepAsync(db, job, MikrotikJobStep.Firmware, $"Upgrading the RouterBOARD firmware {current} → {upgrade}", ct);
            await AsAdminAsync(db, router, username, password, ct, async (session, token) =>
            {
                await MustAsync(session, "/system routerboard upgrade", token);
                return 0;
            });
            var final = await RebootAndWaitAsync(db, job, router, username, password, "the firmware", ct);
            if (final.FirmwareCurrent != upgrade)
            {
                throw new StepFailed($"The firmware is still {final.FirmwareCurrent}, not {upgrade}.");
            }
            await LogAsync(db, job, MikrotikJobStep.Firmware, MikrotikJobOutcome.Ok, $"Firmware {final.FirmwareCurrent}", null, ct);
        }
        else if (opts.Firmware)
        {
            await LogAsync(db, job, MikrotikJobStep.Firmware, MikrotikJobOutcome.Info,
                after.IsRouterBoard ? "The firmware is current." : "No RouterBOARD (CHR, x86): no firmware to upgrade.", null, ct);
        }
        job.Step = MikrotikJobStep.Done;
        await LogAsync(db, job, MikrotikJobStep.Done, MikrotikJobOutcome.Ok, $"Updated {job.FromVersion} → {after.VersionText}", null, ct);
    }

    /// <summary>Reboots as the admin, then waits until the router answers as pnmt with a new boot time.</summary>
    private async Task<RouterReading> RebootAndWaitAsync(PnmtdDbContext db, MikrotikJobEntity job, MikrotikRouterEntity router, string username,
        string password, string why, CancellationToken ct)
    {
        var o = options.CurrentValue.Updates;
        await StepAsync(db, job, MikrotikJobStep.Reboot, $"Rebooting for {why}", ct);
        var rebootAt = Clock.UtcNow;
        try
        {
            // Over a non-interactive exec RouterOS reboots at once, without its question (M5's check on 7.25.1).
            await AsAdminAsync(db, router, username, password, ct, async (session, token) =>
                await session.ExecAsync("/system reboot", TimeSpan.FromSeconds(20), 4096, token));
        }
        catch (RouterSessionException)
        {
            // The session ends with the reboot.
        }
        await StepAsync(db, job, MikrotikJobStep.Wait, "Waiting for the router", ct);
        var deadline = rebootAt.AddMinutes(Math.Max(1, o.RebootTimeoutMinutes));
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, o.PollSeconds)), ct);
        while (true)
        {
            try
            {
                var reading = await AsPnmtAsync(db, router, ct, (session, token) => polls.ReadAsync(session, token));
                if (reading.Uptime is { } up && Clock.UtcNow - up >= rebootAt.AddSeconds(-30))
                {
                    await LogAsync(db, job, MikrotikJobStep.Wait, MikrotikJobOutcome.Ok, $"Back after {(int)(Clock.UtcNow - rebootAt).TotalSeconds} s", null, ct);
                    return reading;
                }
            }
            catch (Exception ex) when (ex is RouterSessionException or FormatException or StepFailed)
            {
            }
            if (Clock.UtcNow > deadline)
            {
                throw new StepFailed($"The router did not answer within {o.RebootTimeoutMinutes} minutes of its reboot.");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, o.PollSeconds)), ct);
        }
    }

    // --- a remote command (§8.2) ----------------------------------------------------------------------------

    private async Task CommandAsync(PnmtdDbContext db, MikrotikJobEntity job, Guid sensorId, CancellationToken ct)
    {
        var o = options.CurrentValue.Commands;
        var opts = CommandOptions(job);
        var router = await db.MikrotikRouters.Include(r => r.Sensor).SingleAsync(r => r.SensorId == sensorId, ct);
        var asAdmin = job.AdminUsername is not null;

        // 1. Check: what PNMTD knows of the router. Reaching it is the next step's.
        await StepAsync(db, job, MikrotikJobStep.Check, $"Checking {router.Sensor.Name}", ct);
        var skip = !router.Mode.UsesSsh() ? "WireGuard only: there is no login."
            : router.HostKeyFingerprint is null ? "PNMTD has not logged in to it yet, so its host key is not pinned."
            : RouterOsVersion.TryParse(router.Version, out var v) && v.IsBelowFloor ? $"RouterOS {router.Version} is below {RouterOsVersion.Floor}."
            : null;
        if (skip is not null)
        {
            await SkipAsync(db, job, skip, ct);
            return;
        }
        var admin = asAdmin
            ? vault.Get(job.BatchId) ?? throw new StepFailed("The admin password is gone (PNMTD restarted, or the batch waited longer than its lifetime): run the command again.")
            : default;
        await LogAsync(db, job, MikrotikJobStep.Check, MikrotikJobOutcome.Ok, $"RouterOS {router.Version ?? "?"}, {router.Mode.Label()}", null, ct);
        await CheckCancelledAsync(db, job, ct);

        // 2. The export before (option): so the result shows what the command changed.
        if (opts.BackupBeforeAndAfter)
        {
            await StepAsync(db, job, MikrotikJobStep.Export, "Exporting the configuration before the command", ct);
            try
            {
                var (stored, current) = await ExportAsync(db, job, router, MikrotikBackupTrigger.BeforeCommand, "", ct);
                job.BackupBeforeId = current;
                await LogAsync(db, job, MikrotikJobStep.Export, MikrotikJobOutcome.Ok,
                    stored.Backup is { } nb ? $"Stored as version {nb.Number}." : "Unchanged since the last backup.", null, ct);
            }
            catch (RouterSessionException ex)
            {
                await SkipAsync(db, job, $"Not reached: {ex.Message}", ct);
                return;
            }
            await CheckCancelledAsync(db, job, ct);
        }

        // 3. Run: one exec of the text as one script, in a session of its own, with the user's rights.
        await StepAsync(db, job, MikrotikJobStep.Run, $"Running as {job.RunAs}", ct);
        var (target, error) = await polls.TargetAsync(db, router, asAdmin ? admin.Username : RouterPoll.UserName,
            asAdmin ? RouterCredentials.ForPassword(admin.Password) : await polls.PnmtCredentialsAsync(ct), ct);
        if (target is null)
        {
            await SkipAsync(db, job, error!, ct);
            return;
        }
        IRouterSession session;
        try
        {
            session = await polls.Connector.ConnectAsync(target, ct);
        }
        catch (RouterSessionException ex)
        {
            // Nothing ran: an unreachable router, or a refused login, is skipped (§8.2).
            await SkipAsync(db, job, ex.Kind == RouterFailureKind.AuthenticationFailed && asAdmin ? $"The router refused {job.RunAs}'s login." : $"Not reached: {ex.Message}", ct);
            return;
        }
        var timeout = TimeSpan.FromSeconds(job.TimeoutSeconds ?? o.DefaultTimeoutSeconds);
        RouterExecResult result;
        await using (session)
        {
            result = await session.ExecAsync(RouterOsScript.AsOneScript(job.Command ?? ""), timeout, Math.Max(1024, o.MaxOutputBytes), ct);
        }
        var raw = result.Error.Length == 0 ? result.Output
            : result.Output.Length == 0 || result.Output.EndsWith('\n') ? result.Output + result.Error
            : result.Output + "\n" + result.Error;
        var cleaned = CommandOutput.Clean(raw);
        var (kept, cut) = CommandOutput.Limit(cleaned, Math.Max(1024, o.MaxStoredOutputBytes));
        job.Output = kept;
        job.OutputTruncated = cut || result.Truncated;
        job.ExitStatus = result.ExitStatus;
        var routerError = CommandOutput.Error(cleaned);
        var (state, message) = result.TimedOut
            ? (MikrotikJobState.TimedOut, $"No end within {timeout.TotalSeconds:0} s: the session was closed, and the output so far is kept.")
            : result.ExitStatus is { } status and not 0
                ? (MikrotikJobState.Failed, routerError ?? CommandOutput.LastLine(cleaned) ?? $"Exit status {status}.")
                : routerError is not null
                    ? (MikrotikJobState.Failed, routerError)
                    : (MikrotikJobState.Succeeded, (string?)null);
        var size = System.Text.Encoding.UTF8.GetByteCount(cleaned);
        await LogAsync(db, job, MikrotikJobStep.Run, state == MikrotikJobState.Succeeded ? MikrotikJobOutcome.Ok : MikrotikJobOutcome.Failed,
            $"{(message ?? "Done")} ({result.Duration.TotalSeconds:0.0} s, exit status {(result.ExitStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")}, " +
            $"{(size < 1024 ? $"{size} bytes" : $"{size / 1024} KiB")} of output{(job.OutputTruncated ? ", cut" : "")})", null, ct);

        // 4. The export after (option), whatever the outcome: a script may change things before its error.
        if (opts.BackupBeforeAndAfter)
        {
            await StepAsync(db, job, MikrotikJobStep.ExportAfter, "Exporting the configuration after the command", ct);
            try
            {
                var (stored, current) = await ExportAsync(db, job, router, MikrotikBackupTrigger.AfterCommand,
                    $" by a command of {job.RequestedByName ?? "?"} as {job.RunAs}", ct);
                job.BackupAfterId = current;
                await LogAsync(db, job, MikrotikJobStep.ExportAfter, MikrotikJobOutcome.Ok, stored.Backup is { } nb
                    ? $"Stored as version {nb.Number}: +{stored.Added} −{stored.Removed} lines."
                    : "Unchanged: the command changed no configuration.", null, ct);
            }
            catch (Exception ex) when (ex is RouterSessionException or StepFailed)
            {
                // A command that rebooted the router, or cut PNMT off, leaves no export after (§8.2).
                await LogAsync(db, job, MikrotikJobStep.ExportAfter, MikrotikJobOutcome.Info, $"No export after the command: {ex.Message}", null, ct);
            }
        }

        // 5. Done.
        job.Step = MikrotikJobStep.Done;
        Finish(db, job, state, message);
        await LogAsync(db, job, MikrotikJobStep.Done, state == MikrotikJobState.Succeeded ? MikrotikJobOutcome.Ok : MikrotikJobOutcome.Failed,
            state switch
            {
                MikrotikJobState.Succeeded => "Succeeded",
                MikrotikJobState.TimedOut => "Timed out",
                _ => "Failed",
            }, null, ct);
    }

    private static async Task SkipAsync(PnmtdDbContext db, MikrotikJobEntity job, string reason, CancellationToken ct)
    {
        Finish(db, job, MikrotikJobState.Skipped, reason);
        await LogAsync(db, job, job.Step, MikrotikJobOutcome.Info, $"Skipped: {reason}", null, ct);
    }

    // --- exports and events --------------------------------------------------------------------------------

    /// <summary>
    /// An export as <c>pnmt</c>, stored as a new version if it changed, with 243 as the poll writes it (§6.1);
    /// <paramref name="cause"/> names the command. Returns the version the configuration is now.
    /// </summary>
    private async Task<(StoredExport Stored, Guid? Current)> ExportAsync(PnmtdDbContext db, MikrotikJobEntity job, MikrotikRouterEntity router,
        MikrotikBackupTrigger trigger, string cause, CancellationToken ct)
    {
        var (output, error, withSecrets) = await AsPnmtAsync(db, router, ct, (session, token) => polls.JobExportAsync(session, router, token));
        if (error is not null)
        {
            throw new StepFailed(error);
        }
        var stored = await BackupStore.SaveExportAsync(db, router, router.Sensor.Name, output!, withSecrets, trigger,
            job.RequestedBy, job.RequestedByName, Clock.UtcNow, ct);
        if (stored.Backup is { } b)
        {
            b.JobId = job.Id;
        }
        await db.SaveChangesAsync(ct);
        if (stored is { Changed: true, First: false, Backup: { } backup })
        {
            await WriteEventAsync(db, router.SensorId, PNMTStatusCodes.MIKROTIK_CONFIG_CHANGED,
                $"Configuration changed{cause}: +{stored.Added} −{stored.Removed} lines (version {backup.Number})", job, ct);
        }
        var current = stored.Backup?.Id ?? await db.MikrotikBackups.AsNoTracking()
            .Where(x => x.SensorId == router.SensorId && x.Kind == MikrotikBackupKind.Export)
            .OrderByDescending(x => x.Number).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        return (stored, current);
    }

    private static async Task WriteEventAsync(PnmtdDbContext db, Guid sensorId, int code, string message, MikrotikJobEntity job, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SensorEventWriter.WriteAsync(db, sensorId, code, message, $"mikrotik-job:{job.Id}", ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    // --- sessions ------------------------------------------------------------------------------------------

    private async Task<T> AsPnmtAsync<T>(PnmtdDbContext db, MikrotikRouterEntity router, CancellationToken ct, Func<IRouterSession, CancellationToken, Task<T>> work)
    {
        var (target, error) = await polls.TargetAsync(db, router, RouterPoll.UserName, await polls.PnmtCredentialsAsync(ct), ct);
        if (target is null)
        {
            throw new StepFailed(error!);
        }
        await using var session = await polls.Connector.ConnectAsync(target, ct);
        return await work(session, ct);
    }

    private async Task<T> AsAdminAsync<T>(PnmtdDbContext db, MikrotikRouterEntity router, string username, string password, CancellationToken ct,
        Func<IRouterSession, CancellationToken, Task<T>> work)
    {
        var (target, error) = await polls.TargetAsync(db, router, username, RouterCredentials.ForPassword(password), ct);
        if (target is null)
        {
            throw new StepFailed(error!);
        }
        await using var session = await polls.Connector.ConnectAsync(target, ct);
        return await work(session, ct);
    }

    private static async Task MustAsync(IRouterSession session, string command, CancellationToken ct, TimeSpan? timeout = null)
    {
        var result = await session.ExecAsync(command, timeout ?? TimeSpan.FromSeconds(60), 64 * 1024, ct);
        if (!result.Succeeded)
        {
            throw new StepFailed(result.TimedOut ? $"{Name(command)} timed out." : $"{Name(command)}: {First(result.Output)}");
        }
    }

    /// <summary>The command without its arguments: a log never carries a password.</summary>
    private static string Name(string command) => string.Join(' ', command.Split(' ').TakeWhile(w => !w.Contains('=')));

    private static string First(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no answer";

    // --- the log -------------------------------------------------------------------------------------------

    private static async Task StepAsync(PnmtdDbContext db, MikrotikJobEntity job, MikrotikJobStep step, string message, CancellationToken ct)
    {
        job.Step = step;
        await LogAsync(db, job, step, MikrotikJobOutcome.Started, message, null, ct);
    }

    private static async Task LogAsync(PnmtdDbContext db, MikrotikJobEntity job, MikrotikJobStep step, MikrotikJobOutcome outcome, string message,
        string? output, CancellationToken ct)
    {
        db.MikrotikJobLog.Add(new MikrotikJobLogEntity
        {
            JobId = job.Id, At = Clock.UtcNow, Step = step, Outcome = outcome, Message = message,
            Output = output is { Length: > 65536 } ? output[..65536] : output,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A cancel is honoured until the reboot (§7.3); after it, only the waiting would stop.</summary>
    private static async Task CheckCancelledAsync(PnmtdDbContext db, MikrotikJobEntity job, CancellationToken ct)
    {
        await db.Entry(job).ReloadAsync(ct);
        if (job.State == MikrotikJobState.Cancelled)
        {
            throw new Cancelled();
        }
    }

    private static void Finish(PnmtdDbContext db, MikrotikJobEntity job, MikrotikJobState state, string? error)
    {
        job.State = state;
        job.Error = error;
        job.FinishedAt = Clock.UtcNow;
    }

    private async Task ForgetIfDoneAsync(PnmtdDbContext db, Guid batchId, CancellationToken ct)
    {
        if (!await db.MikrotikJobs.AnyAsync(j => j.BatchId == batchId && (j.State == MikrotikJobState.Queued || j.State == MikrotikJobState.Running), ct))
        {
            vault.Remove(batchId);
        }
    }
}
