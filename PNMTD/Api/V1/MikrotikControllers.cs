using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>A router's own endpoints (docs/mikrotik-plan.md §10): its scripts, the host key, poll now, its backups.</summary>
[Route("api/v1/sensors/{id:guid}/mikrotik")]
[Authorize(Policy = Policies.CanRead)]
public sealed class SensorMikrotikController(MikrotikRouterService routers, MikrotikBackupService backups) : V1ControllerBase
{
    /// <summary>
    /// The join script (§4.3): pasted into the router's terminal, or imported as a .rsc file. In the
    /// WireGuard modes it holds the router's private key; every view is audit-logged.
    /// </summary>
    [HttpGet("join-script")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<MikrotikScript>> JoinScript(Guid id, CancellationToken ct) =>
        await routers.ScriptAsync(id, removal: false, ct);

    /// <summary>The script that removes everything PNMT created on the router (§4.6).</summary>
    [HttpGet("removal-script")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<MikrotikScript>> RemovalScript(Guid id, CancellationToken ct) =>
        await routers.ScriptAsync(id, removal: true, ct);

    /// <summary>Polls the router now, and answers when the poll is done.</summary>
    [HttpPost("poll")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<MikrotikRouterDetail>> Poll(Guid id, CancellationToken ct) =>
        await routers.PollNowAsync(id, ct);

    /// <summary>New tunnel keys (§3.2): every copy of the join script stops working, and so does the router until it gets the new one. Audit-logged.</summary>
    [HttpPost("regenerate-keys")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<MikrotikRouterDetail>> RegenerateKeys(Guid id, CancellationToken ct) =>
        await routers.RegenerateKeysAsync(id, ct);

    /// <summary>Accepts the host key the router presented instead of the pinned one (§4.4). Audit-logged.</summary>
    [HttpPost("host-key/accept")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<MikrotikRouterDetail>> AcceptHostKey(Guid id, CancellationToken ct) =>
        await routers.AcceptHostKeyAsync(id, ct);

    /// <summary>The stored versions, newest first, without their content (§6.3).</summary>
    [HttpGet("backups")]
    public async Task<ActionResult<Paged<MikrotikBackupItem>>> Backups(Guid id, [FromQuery] MikrotikBackupQuery query, CancellationToken ct) =>
        await backups.ListAsync(id, query, ct);

    /// <summary>Exports now (§6.1): a new version when the configuration changed, else nothing new.</summary>
    [HttpPost("backups")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<MikrotikBackupResult>> BackUp(Guid id, CancellationToken ct) => await backups.BackupNowAsync(id, ct);

    /// <summary>An export's text. One with secrets needs Admin, and the view is audit-logged (§6.6).</summary>
    [HttpGet("backups/{backupId:guid}")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<MikrotikBackupContent>> Backup(Guid id, Guid backupId, CancellationToken ct) =>
        await backups.ContentAsync(id, backupId, ct);

    /// <summary>The changes to this version from <paramref name="against"/>, by default from the version before (§6.4).</summary>
    [HttpGet("backups/{backupId:guid}/diff")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<MikrotikBackupDiff>> Diff(Guid id, Guid backupId, [FromQuery] Guid? against, CancellationToken ct) =>
        await backups.DiffAsync(id, backupId, against, ct);

    [HttpGet("backups/{backupId:guid}/download")]
    [Authorize(Policy = Policies.CanWrite)]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "text/plain", "application/octet-stream")]
    public async Task<IActionResult> Download(Guid id, Guid backupId, CancellationToken ct)
    {
        var (content, fileName, contentType) = await backups.DownloadAsync(id, backupId, ct);
        return File(content, contentType, fileName);
    }

    [HttpDelete("backups/{backupId:guid}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> DeleteBackup(Guid id, Guid backupId, CancellationToken ct)
    {
        await backups.DeleteAsync(id, backupId, ct);
        return NoContent();
    }
}

/// <summary>The admin page's endpoints (§11.2): the overview and the fleet.</summary>
[Route("api/v1/mikrotik")]
[Authorize(Policy = Policies.Admin)]
public sealed class MikrotikController(MikrotikRouterService routers) : V1ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<MikrotikStatus>> Status(CancellationToken ct) => await routers.StatusAsync(ct);

    [HttpGet("routers")]
    public async Task<ActionResult<Paged<MikrotikFleetItem>>> Routers([FromQuery] MikrotikFleetQuery query, CancellationToken ct) =>
        await routers.FleetAsync(query, ct);
}
