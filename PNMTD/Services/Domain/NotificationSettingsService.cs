using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Notifications;

namespace PNMTD.Services.Domain;

/// <summary>
/// The notification settings (docs/cookie-object-notes-plan.md §5): the Pushover priority of each importance and emergency's
/// repeat, one row. <see cref="PushoverPriorities"/> reads it for every Pushover sent and drops its copy on a save; the audit
/// line names what changed.
/// </summary>
public sealed class NotificationSettingsService(PnmtdDbContext db, PushoverPriorities priorities)
{
    public const int MinRetrySeconds = 30;
    public const int MaxRetrySeconds = 3600;
    public const int MaxExpireSeconds = 10_800;

    public async Task<NotificationSettings> GetAsync(CancellationToken ct) =>
        View(await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new NotificationSettingsEntity());

    public async Task<NotificationSettings> UpdateAsync(NotificationSettingsRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var v = new ApiValidator();
        foreach (var (value, field) in new[] { (request.PushoverLow, "pushoverLow"), (request.PushoverNormal, "pushoverNormal"), (request.PushoverHigh, "pushoverHigh") })
        {
            v.Check(value is >= PushoverPrioritySettings.Lowest and <= PushoverPrioritySettings.Emergency, field, "A Pushover priority, −2 to 2.");
        }
        v.Check(request.EmergencyRetrySeconds is >= MinRetrySeconds and <= MaxRetrySeconds, "emergencyRetrySeconds", $"{MinRetrySeconds} to {MaxRetrySeconds} seconds.");
        v.Check(request.EmergencyExpireSeconds >= request.EmergencyRetrySeconds && request.EmergencyExpireSeconds <= MaxExpireSeconds, "emergencyExpireSeconds",
            $"At least the retry, at most {MaxExpireSeconds} seconds.");
        v.ThrowIfInvalid();
        var row = await db.NotificationSettings.SingleOrDefaultAsync(ct);
        if (row is null)
        {
            row = new NotificationSettingsEntity();
            db.NotificationSettings.Add(row);
        }
        var changes = new List<string>();
        void Set(string label, int before, int after, Action<int> apply, string unit = "")
        {
            if (before != after)
            {
                changes.Add($"{label} {before} → {after}{unit}");
                apply(after);
            }
        }
        Set("Low", row.PushoverLow, request.PushoverLow, x => row.PushoverLow = x);
        Set("Normal", row.PushoverNormal, request.PushoverNormal, x => row.PushoverNormal = x);
        Set("High", row.PushoverHigh, request.PushoverHigh, x => row.PushoverHigh = x);
        Set("retry", row.EmergencyRetrySeconds, request.EmergencyRetrySeconds, x => row.EmergencyRetrySeconds = x, " s");
        Set("expire", row.EmergencyExpireSeconds, request.EmergencyExpireSeconds, x => row.EmergencyExpireSeconds = x, " s");
        if (changes.Count == 0)
        {
            AuditLog.Summary("Pushover: nothing changed");
            return View(row);
        }
        row.ChangedAt = Clock.UtcNow;
        row.ChangedBy = user.Identity?.Name is { Length: > 0 } name ? name.Length > 200 ? name[..200] : name : "someone";
        await db.SaveChangesAsync(ct);
        priorities.Invalidate();
        AuditLog.Summary($"Pushover: {string.Join(", ", changes)}");
        return View(row);
    }

    private static NotificationSettings View(NotificationSettingsEntity row) =>
        new(row.PushoverLow, row.PushoverNormal, row.PushoverHigh, row.EmergencyRetrySeconds, row.EmergencyExpireSeconds,
            row.ChangedAt is { } at ? Instants.Utc(at) : null, row.ChangedBy);
}
