using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

[Route("api/v1/sensors")]
[RequireRight(Area.Monitoring, Level.View)]
public sealed class SensorsController(SensorService sensors, EventService events) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<SensorListItem>>> List([FromQuery] SensorQuery query, CancellationToken ct) =>
        await sensors.ListAsync(query, ct);

    /// <summary>For a Viewer without <c>integration</c> (tokens and URLs).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SensorDetail>> Get(Guid id, CancellationToken ct) => await sensors.GetAsync(id, CanSeeSecrets, ct);

    [HttpPost]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<SensorDetail>> Create(SensorRequest request, CancellationToken ct)
    {
        var sensor = await sensors.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = sensor.Id }, sensor);
    }

    [HttpPut("{id:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<SensorDetail>> Update(Guid id, SensorRequest request, CancellationToken ct) =>
        await sensors.UpdateAsync(id, request, CanSeeSecrets, ct);

    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await sensors.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/tokens/rotate")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<SensorIntegration>> RotateTokens(Guid id, RotateTokensRequest request, CancellationToken ct) =>
        await sensors.RotateTokensAsync(id, request, ct);

    [HttpPost("lookup")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<TokenLookupResult>> Lookup(TokenLookupRequest request, CancellationToken ct) =>
        await sensors.LookupAsync(request, ct);

    [HttpGet("{id:guid}/events")]
    public async Task<ActionResult<Paged<EventItem>>> Events(Guid id, [FromQuery] EventQuery query, CancellationToken ct)
    {
        await sensors.EnsureExistsAsync(id, ct);
        query.SensorId = id;
        return await events.ListAsync(query, ct);
    }

    [HttpGet("{id:guid}/notification-rules")]
    public async Task<ActionResult<IReadOnlyList<NotificationRuleItem>>> NotificationRules(Guid id, CancellationToken ct) =>
        Ok(await sensors.NotificationRulesAsync(id, CanSeeSecrets, ct));

    [HttpPut("{id:guid}/notification-rules/{ruleId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<IActionResult> LinkRule(Guid id, Guid ruleId, CancellationToken ct)
    {
        await sensors.LinkRuleAsync(id, ruleId, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/notification-rules/{ruleId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<IActionResult> UnlinkRule(Guid id, Guid ruleId, CancellationToken ct)
    {
        await sensors.UnlinkRuleAsync(id, ruleId, ct);
        return NoContent();
    }
    /// <summary>Many sensors' importance at once (docs/ai-operations-plan.md §11.1): how many changed.</summary>
    [HttpPut("importance")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<int>> Importance(ImportanceRequest request, CancellationToken ct) => Ok(await sensors.SetImportanceAsync(request, ct));
}
