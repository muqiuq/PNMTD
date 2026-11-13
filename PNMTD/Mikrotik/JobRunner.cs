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

/// <summary>
/// Runs the update jobs (docs/mikrotik-plan.md §7.3), one at a time, step by step, holding the router (the
/// poller pauses), with every step in the permanent job log. A batch that stops at its first failure cancels
/// the jobs after it. A job whose admin password is gone (a restart of PNMTD, its lifetime) fails at the
/// first step that needs it. Not a monitoring task: it runs without BackgroundTasks, so the tests can run jobs.
/// </summary>
public sealed class JobRunner(
    IDbContextFactory<PnmtdDbContext> dbs,
    RouterPollService polls,
    CredentialVault vault,
    IOptionsMonitor<MikrotikOptions> options,
    ILogger<JobRunner> logger) : BackgroundService
{
    private readonly SemaphoreSlim wake = new(0);

    /// <summary>A new job: run it now rather than at the next tick.</summary>
    public void Wake() => wake.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailOrphansAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await RunNextAsync(stoppingToken))
                {
                }
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
            // After the reboot began, the router carries on without PNMTD: the next poll shows the version (242).
            Finish(db, job, MikrotikJobState.Failed, job.Step is >= MikrotikJobStep.Reboot and <= MikrotikJobStep.Done
                ? "PNMTD restarted while the router was rebooting for the update: the update goes on without PNMTD, and the next poll shows the version."
                : "PNMTD restarted during the job, and the admin password is gone with it: start the update again.");
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Runs the oldest queued job, if any. Public for the tests.</summary>
    public async Task<bool> RunNextAsync(CancellationToken ct)
    {
        Guid jobId;
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            var job = await db.MikrotikJobs.Where(j => j.State == MikrotikJobState.Queued).OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
            if (job is null)
            {
                return false;
            }
            var stop = Options(job).StopAtFirstFailure
                && await db.MikrotikJobs.AnyAsync(j => j.BatchId == job.BatchId && j.State == MikrotikJobState.Failed, ct);
            if (stop)
            {
                Finish(db, job, MikrotikJobState.Cancelled, "Not started: an earlier router of the batch failed.");
                await db.SaveChangesAsync(ct);
                await ForgetIfDoneAsync(db, job.BatchId, ct);
                return true;
            }
            job.State = MikrotikJobState.Running;
            job.StartedAt = Clock.UtcNow;
            await db.SaveChangesAsync(ct);
            jobId = job.Id;
        }
        await RunAsync(jobId, ct);
        return true;
    }

    private static UpdateJobOptions Options(MikrotikJobEntity job) =>
        job.Options is { } json ? JsonSerializer.Deserialize<UpdateJobOptions>(json, VersionCatalog.Json) ?? new UpdateJobOptions() : new UpdateJobOptions();

    private sealed class StepFailed(string message) : Exception(message);

    private sealed class Cancelled() : Exception("Cancelled");

    private async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var job = await db.MikrotikJobs.SingleAsync(j => j.Id == jobId, ct);
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
                    throw new StepFailed("Commands arrive with phase M6.");
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
            await LogAsync(db, job, job.Step, MikrotikJobOutcome.Info, "Cancelled before the reboot: nothing on the router changed but what the log shows.", null, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is StepFailed or RouterSessionException or FormatException or TimeoutException)
        {
            var message = ex.Message + (downloaded
                ? " The downloaded package installs at the router's next reboot, whoever causes it; /file remove of the .npk file prevents that."
                : "");
            await LogAsync(db, job, job.Step, MikrotikJobOutcome.Failed, message, null, ct);
            Finish(db, job, MikrotikJobState.Failed, message);
            await db.SaveChangesAsync(ct);
            if (job.SensorId is { } id && job.Kind == MikrotikJobKind.Update)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await SensorEventWriter.WriteAsync(db, id, PNMTStatusCodes.MIKROTIK_UPDATE_FAILED,
                    $"Update to {job.ToVersion} failed at {job.Step}: {ex.Message}", $"mikrotik-job:{job.Id}", ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
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
        var opts = Options(job);
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
        var export = await AsPnmtAsync(db, router, ct, (session, token) => polls.ExportAsync(session, false, token));
        if (export.Error is not null)
        {
            throw new StepFailed(export.Error);
        }
        var stored = await BackupStore.SaveExportAsync(db, router, router.Sensor.Name, export.Output!, false, MikrotikBackupTrigger.BeforeUpdate,
            job.RequestedBy, job.RequestedByName, Clock.UtcNow, ct);
        if (stored.Backup is { } b)
        {
            b.JobId = job.Id;
        }
        await db.SaveChangesAsync(ct);
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
