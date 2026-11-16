using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>
/// The Notification Manager (docs/ai-operations-plan.md §11.3): every role reads, a User assigns. The Pushover priorities
/// (docs/cookie-object-notes-plan.md §5) are its settings: readers see them, Monitoring's Edit changes them.
/// </summary>
[Route("api/v1/notifications")]
[RequireRight(Area.Monitoring, Level.View)]
public sealed class NotificationsController(NotificationManagerService manager, NotificationSettingsService settings) : V1ControllerBase
{
    [HttpGet("matrix")]
    public async Task<ActionResult<NotificationMatrix>> Matrix([FromQuery] NotificationMatrixQuery query, CancellationToken ct) =>
        await manager.MatrixAsync(query, CanSeeSecrets, ct);

    [HttpGet("coverage")]
    public async Task<ActionResult<NotificationCoverage>> Coverage(CancellationToken ct) => await manager.UncoveredAsync(ct);

    [HttpPost("assign")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.NotificationsAssigned)]
    public async Task<ActionResult<NotificationAssignResult>> Assign(NotificationAssignRequest request, CancellationToken ct) =>
        await manager.AssignAsync(request, ct);

    /// <summary>The Pushover priority of each importance, and emergency's repeat (docs/cookie-object-notes-plan.md §5.3).</summary>
    [HttpGet("settings")]
    public async Task<ActionResult<NotificationSettings>> Settings(CancellationToken ct) => await settings.GetAsync(ct);

    [HttpPut("settings")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.NotificationSettingsChanged)]
    public async Task<ActionResult<NotificationSettings>> UpdateSettings(NotificationSettingsRequest request, CancellationToken ct) =>
        await settings.UpdateAsync(request, User, ct);
}
