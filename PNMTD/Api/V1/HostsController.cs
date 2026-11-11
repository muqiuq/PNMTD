using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

[Route("api/v1/hosts")]
[Authorize(Policy = Policies.CanRead)]
public sealed class HostsController(HostService hosts) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<HostListItem>>> List([FromQuery] HostQuery query, CancellationToken ct) =>
        await hosts.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<HostDetail>> Get(Guid id, CancellationToken ct) => await hosts.GetAsync(id, ct);

    [HttpPost]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<HostDetail>> Create(HostRequest request, CancellationToken ct)
    {
        var host = await hosts.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = host.Id }, host);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<HostDetail>> Update(Guid id, HostRequest request, CancellationToken ct) =>
        await hosts.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await hosts.DeleteAsync(id, ct);
        return NoContent();
    }
}
