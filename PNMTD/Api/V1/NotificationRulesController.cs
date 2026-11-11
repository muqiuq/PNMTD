using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

[Route("api/v1/notification-rules")]
[Authorize(Policy = Policies.CanRead)]
public sealed class NotificationRulesController(NotificationRuleService rules) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<NotificationRuleItem>>> List([FromQuery] PageQuery query, CancellationToken ct) =>
        await rules.ListAsync(query, CanSeeSecrets, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<NotificationRuleDetail>> Get(Guid id, CancellationToken ct) => await rules.GetAsync(id, CanSeeSecrets, ct);

    [HttpPost]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<NotificationRuleDetail>> Create(NotificationRuleRequest request, CancellationToken ct)
    {
        var rule = await rules.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = rule.Id }, rule);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<NotificationRuleDetail>> Update(Guid id, NotificationRuleRequest request, CancellationToken ct) =>
        await rules.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await rules.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/sensors")]
    public async Task<ActionResult<IReadOnlyList<LinkedSensor>>> Sensors(Guid id, CancellationToken ct) => Ok(await rules.SensorsAsync(id, ct));

    [HttpPut("{id:guid}/sensors/{sensorId:guid}")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<IActionResult> Link(Guid id, Guid sensorId, CancellationToken ct)
    {
        await rules.LinkAsync(id, sensorId, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/sensors/{sensorId:guid}")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<IActionResult> Unlink(Guid id, Guid sensorId, CancellationToken ct)
    {
        await rules.UnlinkAsync(id, sensorId, ct);
        return NoContent();
    }
}
