using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

[Route("api/v1/notification-rules")]
[RequireRight(Area.Monitoring, Level.View)]
public sealed class NotificationRulesController(NotificationRuleService rules, NotificationManagerService manager) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<NotificationRuleItem>>> List([FromQuery] PageQuery query, CancellationToken ct) =>
        await rules.ListAsync(query, CanSeeSecrets, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<NotificationRuleDetail>> Get(Guid id, CancellationToken ct) => await rules.GetAsync(id, CanSeeSecrets, ct);

    [HttpPost]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.NotificationRuleCreated)]
    public async Task<ActionResult<NotificationRuleDetail>> Create(NotificationRuleRequest request, CancellationToken ct)
    {
        var rule = await rules.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = rule.Id }, rule);
    }

    [HttpPut("{id:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.NotificationRuleUpdated)]
    public async Task<ActionResult<NotificationRuleDetail>> Update(Guid id, NotificationRuleRequest request, CancellationToken ct) =>
        await rules.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.NotificationRuleDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await rules.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/sensors")]
    public async Task<ActionResult<IReadOnlyList<LinkedSensor>>> Sensors(Guid id, CancellationToken ct) => Ok(await rules.SensorsAsync(id, ct));

    [HttpPut("{id:guid}/sensors/{sensorId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.NotificationRuleSensorLinked)]
    public async Task<IActionResult> Link(Guid id, Guid sensorId, CancellationToken ct)
    {
        await rules.LinkAsync(id, sensorId, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/sensors/{sensorId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.NotificationRuleSensorUnlinked)]
    public async Task<IActionResult> Unlink(Guid id, Guid sensorId, CancellationToken ct)
    {
        await rules.UnlinkAsync(id, sensorId, ct);
        return NoContent();
    }
    /// <summary>Replaces the rule's scopes (docs/ai-operations-plan.md §11.2).</summary>
    [HttpPut("{id:guid}/scopes")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.NotificationRuleScopesChanged)]
    public async Task<IActionResult> Scopes(Guid id, IReadOnlyList<NotificationScopeRequest> scopes, CancellationToken ct)
    {
        await manager.SetScopesAsync(id, scopes, ct);
        return NoContent();
    }

    /// <summary>What the scopes would cover now, before they are saved.</summary>
    [HttpPost("{id:guid}/scopes/preview")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [NotAudited("a preview: changes nothing")]
    public async Task<ActionResult<NotificationScopePreview>> Preview(Guid id, IReadOnlyList<NotificationScopeRequest> scopes, CancellationToken ct) =>
        await manager.PreviewAsync(id, scopes, ct);

    [HttpPut("{id:guid}/exclusions/{sensorId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.NotificationRuleExclusionAdded)]
    public async Task<IActionResult> Exclude(Guid id, Guid sensorId, CancellationToken ct)
    {
        await manager.ExcludeAsync(id, sensorId, exclude: true, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/exclusions/{sensorId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.NotificationRuleExclusionRemoved)]
    public async Task<IActionResult> Include(Guid id, Guid sensorId, CancellationToken ct)
    {
        await manager.ExcludeAsync(id, sensorId, exclude: false, ct);
        return NoContent();
    }
}
