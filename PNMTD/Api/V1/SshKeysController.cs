using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>
/// The SSH key manager (docs/linux-sensor-plan.md §6.4, §6.5, §10, §11.4): the register, keys found and not registered, users' keys,
/// the page's strip, and removing keys from servers. Admin, audited: a registered key no longer raises 465, so nobody may vouch for a
/// key by registering it themselves. A user's own keys are <c>…/mine</c> (<see cref="SshKeysMineController"/>).
/// </summary>
[Route("api/v1/ssh-keys")]
[RequireRight(Area.SshKeys, Level.Manage)]
public sealed class SshKeysController(SshKeyService keys) : V1ControllerBase
{
    /// <summary>The registered keys, each with where it is authorized (counts), last use and notes. Search: name, fingerprint, comment, owner, or a pasted public key.</summary>
    [HttpGet]
    public async Task<ActionResult<Paged<SshKeyItem>>> List([FromQuery] SshKeyQuery query, CancellationToken ct) => await keys.ListAsync(query, ct);

    /// <summary>The page's strip: registered, unregistered, retired still authorized, deactivated users' keys still authorized, weak, unused, pending removals.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<SshKeySummary>> Summary(CancellationToken ct) => await keys.SummaryAsync(ct);

    /// <summary>Keys found on servers and not registered, by fingerprint, with where they are authorized.</summary>
    [HttpGet("unregistered")]
    public async Task<ActionResult<Paged<SshKeyUnregisteredItem>>> Unregistered([FromQuery] SshKeyUnregisteredQuery query, CancellationToken ct) =>
        await keys.UnregisteredAsync(query, ct);

    /// <summary><i>Add keys…</i>'s preview, as the text is typed: per line the fingerprint, type, bits, weakness, a name from the comment, and why it would be refused. Nothing is stored.</summary>
    [HttpPost("parse")]
    public async Task<ActionResult<SshKeyParseResult>> Parse(SshKeyParseRequest request, CancellationToken ct) => await keys.ParseAsync(request, ct);

    /// <summary>Registers one or more pasted public keys, each with its name. Refuses private keys (stored and logged nowhere), lines that aren't keys and keys registered before. Audited.</summary>
    [HttpPost]
    public async Task<ActionResult<IReadOnlyList<SshKeyItem>>> Register(SshKeyRegisterRequest request, CancellationToken ct) =>
        Ok(await keys.RegisterAsync(request, ct));

    /// <summary>A key's drawer: every authorization (options, last use, removability), the recent logins with it, its history and removals.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SshKeyDetail>> Get(Guid id, CancellationToken ct) => await keys.DetailAsync(id, ct);

    /// <summary>The name, owner, kind and note. Audited.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<SshKeyDetail>> Update(Guid id, SshKeyUpdateRequest request, CancellationToken ct) => await keys.UpdateAsync(id, request, ct);

    /// <summary>Retires the key: it stays where it is authorized, flagged there, and 465 says so wherever it appears again. Audited.</summary>
    [HttpPost("{id:guid}/retire")]
    public async Task<ActionResult<SshKeyDetail>> Retire(Guid id, CancellationToken ct) => await keys.RetireAsync(id, ct);

    [HttpPost("{id:guid}/reactivate")]
    public async Task<ActionResult<SshKeyDetail>> Reactivate(Guid id, CancellationToken ct) => await keys.ReactivateAsync(id, ct);

    /// <summary>Deletes the registration: the key is unregistered again and stays authorized wherever it is. Audited.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await keys.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>"Remove from every server…": every place the key is authorized, as the remove dialog's preview.</summary>
    [HttpPost("{id:guid}/removal-items")]
    public async Task<ActionResult<SshKeyRemovalPreview>> KeyRemovalItems(Guid id, CancellationToken ct) => await keys.KeyRemovalItemsAsync(id, ct);

    /// <summary>A user's keys and where each is authorized: for <i>Admin → Users</i> and offboarding.</summary>
    [HttpGet("users/{userId:guid}")]
    public async Task<ActionResult<SshKeyUserKeys>> UserKeys(Guid userId, CancellationToken ct) => await keys.UserKeysAsync(userId, ct);

    /// <summary>Offboarding's preview: every place any key of the user is authorized.</summary>
    [HttpPost("users/{userId:guid}/removal-items")]
    public async Task<ActionResult<SshKeyRemovalPreview>> UserRemovalItems(Guid userId, CancellationToken ct) => await keys.UserRemovalItemsAsync(userId, ct);

    /// <summary>Offboarding: retires every key of the user and removes each from every server and account. <c>confirm</c>: the number of removals. Audited.</summary>
    [HttpPost("users/{userId:guid}/offboard")]
    public async Task<ActionResult<SshKeyOffboardResult>> Offboard(Guid userId, SshKeyOffboardRequest request, CancellationToken ct) =>
        await keys.OffboardAsync(userId, request, ct);

    /// <summary>The remove dialog: each item allowed or refused (from the server's rights), and its warnings.</summary>
    [HttpPost("remove/preview")]
    public async Task<ActionResult<SshKeyRemovalPreview>> RemovePreview(SshKeyRemovalPreviewRequest request, CancellationToken ct) => await keys.PreviewAsync(request, ct);

    /// <summary>
    /// Removes keys from servers: <c>confirm</c> must be the number of items. Each is sent to a connected agent, pending for an offline
    /// server (sent when it connects, at most Agents__KeyRemovalPendingDays), or refused where the server doesn't allow it. Audited.
    /// </summary>
    [HttpPost("remove")]
    public async Task<ActionResult<SshKeyRemoveResult>> Remove(SshKeyRemoveRequest request, CancellationToken ct) => await keys.RemoveAsync(request, ct);
}

/// <summary>Every signed-in user's own keys (docs/linux-sensor-plan.md §6.4): names, where each is authorized, last use. Read-only.</summary>
[Route("api/v1/ssh-keys/mine")]
[Authorize]
public sealed class SshKeysMineController(SshKeyService keys) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SshKeyMineItem>>> Mine(CancellationToken ct) => Ok(await keys.MineAsync(ct));
}

/// <summary>The removals' history (docs/linux-sensor-plan.md §6.5, §11.4): permanent, it outlives its servers. Admin.</summary>
[Route("api/v1/ssh-key-removals")]
[RequireRight(Area.SshKeys, Level.Manage)]
public sealed class SshKeyRemovalsController(SshKeyService keys) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<SshKeyRemovalItem>>> List([FromQuery] SshKeyRemovalQuery query, CancellationToken ct) => await keys.RemovalsAsync(query, ct);
}
