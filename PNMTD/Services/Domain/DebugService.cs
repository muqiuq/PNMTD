using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Notifications;

namespace PNMTD.Services.Domain;

/// <summary>
/// Admin → Debug (docs/ai-operations-plan.md §8): test sends through the same providers as the notifications,
/// with the provider's answer. Every send is audited, with the rule's name or "a typed key", never the key.
/// </summary>
public sealed class DebugService(PnmtdDbContext db, PushoverNotification pushover, EMailNotification mail, IConfiguration configuration,
    IHttpContextAccessor accessor)
{
    public async Task<DebugStatus> StatusAsync(CancellationToken ct)
    {
        var key = pushover.ApiKey;
        var rules = await db.NotificationRules.AsNoTracking()
            .Where(r => r.Recipient.StartsWith(PushoverNotification.Prefix))
            .OrderBy(r => r.Name)
            .Select(r => new Ref(r.Id, r.Name))
            .ToListAsync(ct);
        return new DebugStatus(
            new DebugPushoverStatus(!string.IsNullOrWhiteSpace(key), key is { Length: > 4 } ? key[^4..] : null, pushover.Endpoint),
            new DebugMailStatus(!string.IsNullOrWhiteSpace(mail.Host) && !string.IsNullOrWhiteSpace(mail.Sender), mail.Host, mail.Port, mail.Sender,
                mail.Username, !string.IsNullOrEmpty(configuration["Smtp:Password"])),
            rules);
    }

    public async Task<DebugResult> PushoverAsync(DebugPushoverRequest request, CancellationToken ct)
    {
        var v = new ApiValidator();
        v.Check(request.RuleId is not null ^ !string.IsNullOrWhiteSpace(request.UserKey), "userKey", "A notification rule, or a user key.");
        v.Check(request.Priority is >= -1 and <= 1, "priority", "−1, 0 or 1.");
        v.Check(request.UserKey is null || request.UserKey.Trim().Length is >= 1 and <= 64, "userKey", "At most 64 characters.");
        v.Check(request.Device is null || request.Device.Length <= 64, "device", "At most 64 characters.");
        v.Check(request.Title is null || request.Title.Length <= 250, "title", "At most 250 characters.");
        v.Check(request.Message is null || request.Message.Length <= 1024, "message", "At most 1024 characters.");
        v.ThrowIfInvalid();
        string recipient;
        string target;
        if (request.RuleId is { } ruleId)
        {
            var rule = await db.NotificationRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ruleId, ct)
                ?? throw ApiException.NotFound("Notification rule");
            if (!PushoverNotification.Matches(rule.Recipient))
            {
                throw ApiException.Invalid("ruleId", "That rule does not notify by Pushover.");
            }
            recipient = rule.Recipient;
            target = $"rule {rule.Name}";
        }
        else
        {
            recipient = PushoverNotification.Prefix + request.UserKey!.Trim();
            target = "a typed key";
        }
        var (title, message) = Texts(request.Title, request.Message);
        var result = await pushover.SendAsync(new NotificationMessage(recipient, title, message, message, Priority: request.Priority, Device: request.Device), ct);
        return Audit("pushover", target, result);
    }

    public async Task<DebugResult> MailAsync(DebugMailRequest request, CancellationToken ct)
    {
        var recipient = request.Recipient?.Trim();
        var v = new ApiValidator();
        v.Check(recipient is { Length: > 0 and <= 254 } && EMailNotification.Matches(recipient), "recipient", "A mail address.");
        v.Check(request.Subject is null || request.Subject.Length <= 250, "subject", "At most 250 characters.");
        v.Check(request.Body is null || request.Body.Length <= 10_000, "body", "At most 10,000 characters.");
        v.ThrowIfInvalid();
        var (subject, body) = Texts(request.Subject, request.Body);
        var result = await mail.SendAsync(new NotificationMessage(recipient!, subject, body, body), ct);
        return Audit("email", recipient!, result);
    }

    private static (string Title, string Message) Texts(string? title, string? message)
    {
        var at = Clock.LocalNow.ToString("yyyy-MM-dd HH:mm:ss");
        return (string.IsNullOrWhiteSpace(title) ? $"PNMT test from {ServerInfo.Identity}" : title.Trim(),
            string.IsNullOrWhiteSpace(message) ? $"PNMT test from {ServerInfo.Identity} at {at}." : message.Trim());
    }

    private DebugResult Audit(string provider, string target, DeliveryResult result)
    {
        UdpViews.Audit(db, accessor, AuthEvents.DebugNotificationSent, $"{provider} to {target}: {(result.Ok ? "sent" : "failed")}");
        db.SaveChanges();
        return new DebugResult(result.Ok, provider, target, result.Detail, (int)result.Duration.TotalMilliseconds, Clock.UtcNow);
    }
}
