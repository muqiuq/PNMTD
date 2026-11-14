using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>The Notification Manager (docs/ai-operations-plan.md §11.3): every role reads, a User assigns.</summary>
[Route("api/v1/notifications")]
[RequireRight(Area.Monitoring, Level.View)]
public sealed class NotificationsController(NotificationManagerService manager) : V1ControllerBase
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
}
