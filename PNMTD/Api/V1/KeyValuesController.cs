using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

[Route("api/v1/key-values")]
[Authorize(Policy = Policies.Admin)]
public sealed class KeyValuesController(KeyValueService values) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<KeyValueItem>>> List([FromQuery] PageQuery query, CancellationToken ct) =>
        await values.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<KeyValueItem>> Get(Guid id, CancellationToken ct) => await values.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<KeyValueItem>> Create(KeyValueRequest request, CancellationToken ct)
    {
        var entry = await values.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = entry.Id }, entry);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<KeyValueItem>> Update(Guid id, KeyValueRequest request, CancellationToken ct) =>
        await values.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await values.DeleteAsync(id, ct);
        return NoContent();
    }
}
