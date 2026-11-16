using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Locations;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

[Route("api/v1/hosts")]
[Authorize]
public sealed class HostsController(HostService hosts, LocationService locations, Cookie.CookieObjectNoteService notes) : V1ControllerBase
{
    [HttpGet]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<HostListItem>>> List([FromQuery] HostQuery query, CancellationToken ct) =>
        await hosts.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<HostDetail>> Get(Guid id, CancellationToken ct) => await hosts.GetAsync(id, ct);

    /// <summary>Cookie's notes on the host (docs/cookie-object-notes-plan.md §5): those the reader may see, newest first.</summary>
    [HttpGet("{id:guid}/cookie-notes")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<IReadOnlyList<CookieObjectNoteItem>>> CookieNotes(Guid id, CancellationToken ct)
    {
        await hosts.GetAsync(id, ct);
        return Ok(await notes.ListAsync(id, User, ct));
    }

    [HttpPost]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.HostCreated)]
    public async Task<ActionResult<HostDetail>> Create(HostRequest request, CancellationToken ct)
    {
        var host = await hosts.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = host.Id }, host);
    }

    [HttpPut("{id:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.HostUpdated)]
    public async Task<ActionResult<HostDetail>> Update(Guid id, HostRequest request, CancellationToken ct) =>
        await hosts.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.HostDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await hosts.DeleteAsync(id, ct);
        return NoContent();
    }
    /// <summary>Many hosts' importance at once (docs/ai-operations-plan.md §11.1): how many changed.</summary>
    [HttpPut("importance")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.HostsImportanceChanged)]
    public async Task<ActionResult<int>> Importance(ImportanceRequest request, CancellationToken ct) => Ok(await hosts.SetImportanceAsync(request, ct));

    /// <summary>Many hosts' location at once (docs/locations-knowledge-plan.md §3.2), null for none: how many changed.</summary>
    [HttpPut("location")]
    [RequireRight(Area.Locations, Level.Edit)]
    [Audit(AuditEvents.HostsLocationChanged)]
    public async Task<ActionResult<int>> Location(HostLocationRequest request, CancellationToken ct) => Ok(await locations.SetHostsAsync(request, ct));
}
