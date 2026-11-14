using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>A router's own endpoints (docs/mikrotik-plan.md §10): its scripts, the host key, poll now, its backups.</summary>
[Route("api/v1/sensors/{id:guid}/mikrotik")]
[Authorize]
public sealed class SensorMikrotikController(MikrotikRouterService routers, MikrotikBackupService backups, MikrotikJobService jobs) : V1ControllerBase
{
    /// <summary>The router's update jobs, newest first (§7).</summary>
    [HttpGet("jobs")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<MikrotikJobItem>>> Jobs(Guid id, [FromQuery] MikrotikJobQuery query, CancellationToken ct)
    {
        await routers.EnsureRouterAsync(id, ct);
        // Updates only: a router's commands need CanWrite, and admin runs Admin (§8.3).
        return await jobs.ListAsync(new MikrotikJobQuery { SensorId = id, Kind = MikrotikJobKind.Update, State = query.State, Page = query.Page, PageSize = query.PageSize }, ct);
    }

    /// <summary>A job of the router, with its log.</summary>
    [HttpGet("jobs/{jobId:guid}")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<MikrotikJobDetail>> Job(Guid id, Guid jobId, CancellationToken ct) => await jobs.GetAsync(jobId, id, ct);

    /// <summary>The changelog of a release (by default the newest of the router's channel), from MikroTik's update server.</summary>
    [HttpGet("changelog")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<MikrotikChangelog>> Changelog(Guid id, [FromQuery] string version, CancellationToken ct) =>
        await jobs.ChangelogAsync(version, ct);

    /// <summary>
    /// The join script (§4.3): pasted into the router's terminal, or imported as a .rsc file. In the
    /// WireGuard modes it holds the router's private key; every view is audit-logged.
    /// </summary>
    [HttpGet("join-script")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<MikrotikScript>> JoinScript(Guid id, CancellationToken ct) =>
        await routers.ScriptAsync(id, removal: false, ct);

    /// <summary>The script that removes everything PNMT created on the router (§4.6).</summary>
    [HttpGet("removal-script")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<MikrotikScript>> RemovalScript(Guid id, CancellationToken ct) =>
        await routers.ScriptAsync(id, removal: true, ct);

    /// <summary>Polls the router now, and answers when the poll is done.</summary>
    [HttpPost("poll")]
    [RequireRight(Area.Commands, Level.Run)]
    [Audit(AuditEvents.MikrotikPollRequested)]
    public async Task<ActionResult<MikrotikRouterDetail>> Poll(Guid id, CancellationToken ct) =>
        await routers.PollNowAsync(id, ct);

    /// <summary>New tunnel keys (§3.2): every copy of the join script stops working, and so does the router until it gets the new one. Audit-logged.</summary>
    [HttpPost("regenerate-keys")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.WireGuardKeysRegenerated)]
    public async Task<ActionResult<MikrotikRouterDetail>> RegenerateKeys(Guid id, CancellationToken ct) =>
        await routers.RegenerateKeysAsync(id, ct);

    /// <summary>Accepts the host key the router presented instead of the pinned one (§4.4). Audit-logged.</summary>
    [HttpPost("host-key/accept")]
    [RequireRight(Area.Mikrotik, Level.Manage)]
    [Audit(AuditEvents.MikrotikHostKeyAccepted)]
    public async Task<ActionResult<MikrotikRouterDetail>> AcceptHostKey(Guid id, CancellationToken ct) =>
        await routers.AcceptHostKeyAsync(id, ct);

    /// <summary>The stored versions, newest first, without their content (§6.3).</summary>
    [HttpGet("backups")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<MikrotikBackupItem>>> Backups(Guid id, [FromQuery] MikrotikBackupQuery query, CancellationToken ct) =>
        await backups.ListAsync(id, query, ct);

    /// <summary>Exports now (§6.1): a new version when the configuration changed, else nothing new.</summary>
    [HttpPost("backups")]
    [RequireRight(Area.Commands, Level.Run)]
    [Audit(AuditEvents.MikrotikBackupRequested)]
    public async Task<ActionResult<MikrotikBackupResult>> BackUp(Guid id, CancellationToken ct) => await backups.BackupNowAsync(id, ct);

    /// <summary>An export's text. One with secrets needs Admin, and the view is audit-logged (§6.6).</summary>
    [HttpGet("backups/{backupId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<MikrotikBackupContent>> Backup(Guid id, Guid backupId, CancellationToken ct) =>
        await backups.ContentAsync(id, backupId, ct);

    /// <summary>The changes to this version from <paramref name="against"/>, by default from the version before (§6.4).</summary>
    [HttpGet("backups/{backupId:guid}/diff")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<MikrotikBackupDiff>> Diff(Guid id, Guid backupId, [FromQuery] Guid? against, CancellationToken ct) =>
        await backups.DiffAsync(id, backupId, against, ct);

    [HttpGet("backups/{backupId:guid}/download")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "text/plain", "application/octet-stream")]
    public async Task<IActionResult> Download(Guid id, Guid backupId, CancellationToken ct)
    {
        var (content, fileName, contentType) = await backups.DownloadAsync(id, backupId, ct);
        return File(content, contentType, fileName);
    }

    [HttpDelete("backups/{backupId:guid}")]
    [RequireRight(Area.Mikrotik, Level.Manage)]
    [Audit(AuditEvents.MikrotikBackupDeleted)]
    public async Task<IActionResult> DeleteBackup(Guid id, Guid backupId, CancellationToken ct)
    {
        await backups.DeleteAsync(id, backupId, ct);
        return NoContent();
    }
}

/// <summary>The admin page's endpoints (§11.2): the overview and the fleet.</summary>
[Route("api/v1/mikrotik")]
[RequireRight(Area.Mikrotik, Level.Manage)]
public sealed class MikrotikController(MikrotikRouterService routers, MikrotikJobService jobs) : V1ControllerBase
{
    /// <summary>The releases with a security fix and the routers behind each (docs/ai-operations-plan.md §10.4).</summary>
    [HttpGet("releases")]
    public async Task<ActionResult<IReadOnlyList<MikrotikReleaseItem>>> Releases(CancellationToken ct) => Ok(await routers.ReleasesAsync(ct));

    /// <summary>
    /// Updates one router or several (§7): the credentials are checked on every router first, and the answer
    /// lists the routers refused. HTTPS only: the password never travels in plain text. Audit-logged.
    /// </summary>
    [HttpPost("jobs")]
    [Audit(AuditEvents.MikrotikUpdateStarted)]
    public async Task<ActionResult<MikrotikJobsCreated>> StartJobs(MikrotikJobRequest request, CancellationToken ct) => await jobs.CreateAsync(request, ct);

    [HttpGet("jobs")]
    public async Task<ActionResult<Paged<MikrotikJobItem>>> Jobs([FromQuery] MikrotikJobQuery query, CancellationToken ct) => await jobs.ListAsync(query, ct);

    [HttpGet("jobs/{id:guid}")]
    public async Task<ActionResult<MikrotikJobDetail>> Job(Guid id, CancellationToken ct) => await jobs.GetAsync(id, null, ct);

    /// <summary>Cancels a job until its reboot. Audit-logged.</summary>
    [HttpDelete("jobs/{id:guid}")]
    [Audit(AuditEvents.MikrotikUpdateCancelled)]
    public async Task<IActionResult> CancelJob(Guid id, CancellationToken ct)
    {
        await jobs.CancelAsync(id, ct);
        return NoContent();
    }

    [HttpGet("status")]
    public async Task<ActionResult<MikrotikStatus>> Status(CancellationToken ct) => await routers.StatusAsync(ct);

    /// <summary>PNMTD's SSH keys, and the routers that may still need an older one (§4.2).</summary>
    [HttpGet("ssh-keys")]
    public async Task<ActionResult<MikrotikSshKeyStatus>> SshKeys(CancellationToken ct) => await routers.SshKeysAsync(ct);

    /// <summary>A new key pair for join scripts; the older keys stay offered until retired. Audit-logged.</summary>
    [HttpPost("ssh-keys/rotate")]
    [Audit(AuditEvents.MikrotikSshKeyRotated)]
    public async Task<ActionResult<MikrotikSshKeyStatus>> RotateSshKey(CancellationToken ct) => await routers.RotateSshKeyAsync(ct);

    /// <summary>Stops offering the older keys; refused while routers may need them, unless forced. Audit-logged.</summary>
    [HttpPost("ssh-keys/retire")]
    [Audit(AuditEvents.MikrotikSshKeyRetired)]
    public async Task<ActionResult<MikrotikSshKeyStatus>> RetireSshKeys([FromQuery] bool force, CancellationToken ct) =>
        await routers.RetireSshKeysAsync(force, ct);

    [HttpGet("routers")]
    public async Task<ActionResult<Paged<MikrotikFleetItem>>> Routers([FromQuery] MikrotikFleetQuery query, CancellationToken ct) =>
        await routers.FleetAsync(query, ct);
}

/// <summary>
/// The routers' backup settings for the installation (docs/mikrotik-backup-plan.md §2.2): reading the defaults
/// needs CanWrite (the new-router form starts with them), changing them and many routers at once needs Admin.
/// </summary>
[Route("api/v1/mikrotik")]
[Authorize]
public sealed class MikrotikBackupSettingsController(MikrotikBackupSettingsService settings) : V1ControllerBase
{
    [HttpGet("backup-settings")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<MikrotikBackupSettings>> BackupSettings(CancellationToken ct) => await settings.GetAsync(ct);

    [HttpPut("backup-settings")]
    [RequireRight(Area.Mikrotik, Level.Manage)]
    [Audit(AuditEvents.MikrotikBackupSettingsChanged, AuditEvents.MikrotikBackupEncryptionChanged)]
    public async Task<ActionResult<MikrotikBackupSettings>> UpdateBackupSettings(MikrotikBackupSettingsRequest request, CancellationToken ct) =>
        await settings.UpdateAsync(request, ct);

    /// <summary>The backup schedule of many routers at once.</summary>
    [HttpPost("routers/backup-settings")]
    [RequireRight(Area.Mikrotik, Level.Manage)]
    [Audit(AuditEvents.MikrotikBackupSettingsChanged, AuditEvents.MikrotikBackupEncryptionChanged)]
    public async Task<ActionResult<MikrotikRouterBackupResult>> ApplyBackupSettings(MikrotikRouterBackupRequest request, CancellationToken ct) =>
        await settings.ApplyAsync(request, ct);

    /// <summary>The public key new backups are encrypted with (docs/mikrotik-backup-plan.md §3.5). HTTPS only; audit-logged.</summary>
    [HttpPost("backup-keys")]
    [RequireRight(Area.Mikrotik, Level.Manage)]
    [Audit(AuditEvents.MikrotikBackupKeySet)]
    public async Task<ActionResult<MikrotikBackupSettings>> SetBackupKey(MikrotikBackupKeyRequest request, CancellationToken ct) =>
        await settings.SetKeyAsync(request, ct);

    /// <summary>Encryption off: the key is retired, and every router's backups are plaintext again. Audit-logged.</summary>
    [HttpPost("backup-keys/retire")]
    [RequireRight(Area.Mikrotik, Level.Manage)]
    [Audit(AuditEvents.MikrotikBackupKeyRetired)]
    public async Task<ActionResult<MikrotikBackupSettings>> RetireBackupKey(CancellationToken ct) => await settings.RetireKeyAsync(ct);
}

/// <summary>
/// Remote commands (§8, §10): as <c>pnmt</c> for CanWrite, as a router's admin for Admins (checked in the
/// service). Admin runs are seen by Admins only. Starting, cancelling and redacting are audit-logged.
/// </summary>
[Route("api/v1/mikrotik/commands")]
[Authorize]
public sealed class MikrotikCommandsController(MikrotikCommandService commands) : V1ControllerBase
{
    /// <summary>
    /// Runs a command on one router or several: one job per router in one batch. An admin run is refused over
    /// plain HTTP and checks the credentials on every router first; the answer lists the routers refused.
    /// </summary>
    [HttpPost]
    [RequireRight(Area.Commands, Level.Run)]
    [Audit(AuditEvents.MikrotikCommandStarted)]
    public async Task<ActionResult<MikrotikCommandsCreated>> Run(MikrotikCommandRequest request, CancellationToken ct) =>
        await commands.CreateAsync(request, ct);

    [HttpGet]
    [RequireRight(Area.Commands, Level.Run)]
    public async Task<ActionResult<Paged<MikrotikCommandItem>>> List([FromQuery] MikrotikCommandQuery query, CancellationToken ct) =>
        await commands.ListAsync(query, ct);

    /// <summary>A command on one router, with its output, its log and what it changed.</summary>
    [HttpGet("{id:guid}")]
    [RequireRight(Area.Commands, Level.Run)]
    public async Task<ActionResult<MikrotikCommandDetail>> Get(Guid id, CancellationToken ct) => await commands.GetAsync(id, ct);

    /// <summary>A batch's results grouped by outcome and output (§8.3).</summary>
    [HttpGet("batches/{batchId:guid}")]
    [RequireRight(Area.Commands, Level.Run)]
    public async Task<ActionResult<MikrotikCommandBatch>> Batch(Guid batchId, CancellationToken ct) => await commands.BatchAsync(batchId, ct);

    /// <summary>Cancels a queued or running command: one's own, or any as an Admin.</summary>
    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Commands, Level.Run)]
    [Audit(AuditEvents.MikrotikCommandCancelled)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await commands.CancelAsync(id, ct);
        return NoContent();
    }

    /// <summary>Replaces the text and output with who redacted them and when (§8.3).</summary>
    [HttpPost("{id:guid}/redact")]
    [RequireRight(Area.Mikrotik, Level.Manage)]
    [Audit(AuditEvents.MikrotikCommandRedacted)]
    public async Task<ActionResult<MikrotikCommandItem>> Redact(Guid id, CancellationToken ct) => await commands.RedactAsync(id, ct);
}
