using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Outbound;

namespace PNMTD.Api.V1;

/// <summary>
/// <i>System health → Outbound</i> (docs/redmine-queue-outbound-plan.md §7, §8): the notification log, the Redmine log, a
/// record in full, and what runs now. <c>System: Manage</c>, the page's right: the records hold message bodies, addresses,
/// webhook URLs and ticket contents.
/// </summary>
[Route("api/v1/system/outbound")]
[Authorize]
[RequireRight(Area.System, Level.Manage)]
public sealed class OutboundController(OutboundQueries outbound) : V1ControllerBase
{
    [HttpGet("notifications")]
    public async Task<ActionResult<Paged<OutboundNotificationItem>>> Notifications([FromQuery] OutboundNotificationQuery query, CancellationToken ct) =>
        await outbound.NotificationsAsync(query, ct);

    [HttpGet("notifications/{id:long}")]
    public async Task<ActionResult<OutboundDetail>> Notification(long id, CancellationToken ct) => await outbound.NotificationAsync(id, ct);

    [HttpGet("redmine")]
    public async Task<ActionResult<Paged<RedmineCallItem>>> Redmine([FromQuery] RedmineCallQuery query, CancellationToken ct) =>
        await outbound.RedmineAsync(query, ct);

    [HttpGet("redmine/{id:long}")]
    public async Task<ActionResult<OutboundDetail>> RedmineCall(long id, CancellationToken ct) => await outbound.RedmineCallAsync(id, ct);

    [HttpGet("live")]
    public async Task<ActionResult<OutboundLive>> Live(CancellationToken ct) => await outbound.LiveAsync(ct);
}
