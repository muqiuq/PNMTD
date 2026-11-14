using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

[Route("api/v1/dns-zones")]
[RequireRight(Area.Monitoring, Level.View)]
public sealed class DnsZonesController(DnsZoneService zones) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<DnsZoneItem>>> List([FromQuery] PageQuery query, CancellationToken ct) =>
        await zones.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DnsZoneDetail>> Get(Guid id, CancellationToken ct) => await zones.GetAsync(id, ct);

    [HttpPost]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<DnsZoneDetail>> Create(DnsZoneRequest request, CancellationToken ct)
    {
        var zone = await zones.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = zone.Id }, zone);
    }

    [HttpPut("{id:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<DnsZoneDetail>> Update(Guid id, DnsZoneRequest request, CancellationToken ct) =>
        await zones.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await zones.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/force-update")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<IActionResult> ForceUpdate(Guid id, CancellationToken ct)
    {
        await zones.ForceUpdateAsync(id, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/records/{recordId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<DnsRecord>> UpdateRecord(Guid id, Guid recordId, DnsRecordRequest request, CancellationToken ct) =>
        await zones.UpdateRecordAsync(id, recordId, request, ct);

    [HttpGet("{id:guid}/log")]
    public async Task<ActionResult<Paged<DnsLogEntry>>> Log(Guid id, [FromQuery] PageQuery query, CancellationToken ct) =>
        await zones.LogAsync(id, query, ct);

    [HttpGet("log")]
    public async Task<ActionResult<Paged<DnsLogEntry>>> AllLogs([FromQuery] PageQuery query, CancellationToken ct) =>
        await zones.LogAsync(null, query, ct);
}
