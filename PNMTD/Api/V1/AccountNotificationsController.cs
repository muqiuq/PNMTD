using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Notifications;

namespace PNMTD.Api.V1;

/// <summary>
/// One's own notification choices (docs/knowledge-base-extensions-plan.md §10.2), with one's account: which of the kinds one's rights
/// allow reach one by e-mail.
/// </summary>
[Route("api/v1/account/notifications")]
[Authorize]
public sealed class AccountNotificationsController(UserNotifications notifications) : V1ControllerBase
{
    [HttpGet]
    public async Task<UserNotificationSettings> Get(CancellationToken ct) => await notifications.SettingsAsync(User, ct);

    [HttpPut]
    [Audit(AuditEvents.NotificationPreferencesChanged)]
    public async Task<UserNotificationSettings> Save(UserNotificationSettingsRequest request, CancellationToken ct) =>
        await notifications.SaveSettingsAsync(User, request, ct);
}
