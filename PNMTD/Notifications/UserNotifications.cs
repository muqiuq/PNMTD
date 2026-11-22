using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;

namespace PNMTD.Notifications;

/// <summary>What happened, for whom: one row per recipient in the caller's transaction (§10.4).</summary>
public sealed record UserNotice(string Kind, IReadOnlyList<Guid> Recipients, Guid? SubjectId, string BatchKey, Guid? SourceId, Guid? ActorId,
    string ActorName, string? Detail = null);

/// <summary>
/// Notifications to people about their own work (docs/knowledge-base-extensions-plan.md §10): the one place that enqueues them, cancels
/// them, keeps each person's choices, and sends them. A feature enqueues in its own transaction (<see cref="EnqueueAsync"/>), so what
/// happened and its notifications are saved together or not at all. <see cref="RunOnceAsync"/> runs on <c>NotificationTask</c>'s
/// second timer, with a guard of its own, so a slow mail server for a person's e-mail never delays an alert. It sends through
/// <see cref="NotificationService"/>: every e-mail is in the notification log, and simulated with
/// <c>Development:DoNotSendNotifications</c>.
/// </summary>
public sealed class UserNotifications(IDbContextFactory<PnmtdDbContext> dbs, NotificationService notifications, IEnumerable<IUserNotificationRenderer> renderers,
    IOptionsMonitor<UserNotificationOptions> options, IConfiguration configuration, ILogger<UserNotifications> logger)
{
    /// <summary>The waits after a failed attempt; after the last, the batch fails for good.</summary>
    public static readonly TimeSpan[] Retries = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];

    /// <summary>A batch still sending after this long was interrupted (PNMTD stopped) and is sent again.</summary>
    public static readonly TimeSpan SendingTimeout = TimeSpan.FromMinutes(10);

    private DateTime lastCleanup = DateTime.MinValue;

    // --- enqueuing ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One row per recipient, saved with the caller's changes: never to the actor, and not to someone whose choice for the kind is off
    /// now (it is asked again when sending). A row joins the person's open batch of the same key, so it goes out with it.
    /// </summary>
    public async Task EnqueueAsync(PnmtdDbContext db, UserNotice notice, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var kind = UserNotificationKinds.Find(notice.Kind) ?? throw new ArgumentException($"No such kind: {notice.Kind}", nameof(notice));
        var recipients = notice.Recipients.Where(r => r != notice.ActorId).Distinct().ToList();
        if (!o.Enabled || recipients.Count == 0)
        {
            return;
        }
        var choices = await db.UserNotificationPreferences.AsNoTracking()
            .Where(p => recipients.Contains(p.UserId) && p.Kind == kind.Key && p.Channel == UserNotificationChannel.Email)
            .ToDictionaryAsync(p => p.UserId, p => p.Enabled, ct);
        var open = await db.UserNotifications.AsNoTracking()
            .Where(n => recipients.Contains(n.UserId) && n.BatchKey == notice.BatchKey && n.State == UserNotificationState.Pending)
            .GroupBy(n => n.UserId).Select(g => new { g.Key, DueAt = g.Min(n => n.DueAt) }).ToDictionaryAsync(g => g.Key, g => g.DueAt, ct);
        var now = Clock.UtcNow;
        foreach (var recipient in recipients.Where(r => choices.TryGetValue(r, out var on) ? on : kind.EmailByDefault))
        {
            db.UserNotifications.Add(new UserNotificationEntity
            {
                UserId = recipient, Kind = kind.Key, BatchKey = Cut(notice.BatchKey, 128), SubjectId = notice.SubjectId, SourceId = notice.SourceId,
                ActorUserId = notice.ActorId, ActorName = Cut(notice.ActorName, 200), Detail = notice.Detail is null ? null : Cut(notice.Detail, 300),
                CreatedAt = now, DueAt = open.TryGetValue(recipient, out var due) ? due : now.AddMinutes(Math.Max(0, o.BatchMinutes)), NextAttemptAt = now,
                State = UserNotificationState.Pending,
            });
        }
    }

    /// <summary>What an undone like or use made, and hasn't gone out yet: cancelled, in the caller's transaction.</summary>
    public static Task<int> CancelAsync(PnmtdDbContext db, Guid sourceId, CancellationToken ct) =>
        db.UserNotifications.Where(n => n.SourceId == sourceId && n.State == UserNotificationState.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.State, UserNotificationState.Cancelled).SetProperty(n => n.Outcome, "Undone before it went out."), ct);

    // --- a person's choices (§10.2) ---------------------------------------------------------------------------------------

    /// <summary>One's own choices: the kinds whose right one holds, each with e-mail on or off.</summary>
    public async Task<UserNotificationSettings> SettingsAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = principal.UserId() ?? throw ApiException.NotFound("User");
        var rights = UserRights.Of(principal);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var email = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.Email).SingleOrDefaultAsync(ct);
        var stored = await db.UserNotificationPreferences.AsNoTracking().Where(p => p.UserId == id).ToListAsync(ct);
        var o = options.CurrentValue;
        var kinds = UserNotificationKinds.All.Where(k => rights.Has(k.Area, k.Level)).Select(k => new UserNotificationKindItem(k.Key, k.Label, k.Help,
            [new UserNotificationChannelItem(UserNotificationChannel.Email,
                stored.FirstOrDefault(p => p.Kind == k.Key && p.Channel == UserNotificationChannel.Email)?.Enabled ?? k.EmailByDefault, k.EmailByDefault)]))
            .ToList();
        return new UserNotificationSettings(!string.IsNullOrWhiteSpace(email), o.Enabled, o.BatchMinutes, o.MaxPerUserDay, kinds);
    }

    /// <summary>One's own choices saved: a choice equal to the default removes the stored row.</summary>
    public async Task<UserNotificationSettings> SaveSettingsAsync(ClaimsPrincipal principal, UserNotificationSettingsRequest request, CancellationToken ct)
    {
        var id = principal.UserId() ?? throw ApiException.NotFound("User");
        var rights = UserRights.Of(principal);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var said = new List<string>();
        foreach (var choice in request.Items ?? [])
        {
            var kind = UserNotificationKinds.Find(choice.Kind ?? "");
            if (kind is null || !rights.Has(kind.Area, kind.Level))
            {
                throw ApiException.Invalid("items", $"“{choice.Kind}” is not a notification you can choose.");
            }
            var row = await db.UserNotificationPreferences.SingleOrDefaultAsync(p => p.UserId == id && p.Kind == kind.Key && p.Channel == choice.Channel, ct);
            if (choice.Enabled == kind.EmailByDefault)
            {
                if (row is not null)
                {
                    db.UserNotificationPreferences.Remove(row);
                }
            }
            else if (row is null)
            {
                db.UserNotificationPreferences.Add(new UserNotificationPreferenceEntity
                {
                    UserId = id, Kind = kind.Key, Channel = choice.Channel, Enabled = choice.Enabled, ChangedAt = Clock.UtcNow,
                });
            }
            else
            {
                row.Enabled = choice.Enabled;
                row.ChangedAt = Clock.UtcNow;
            }
            said.Add($"{kind.Label} by e-mail {(choice.Enabled ? "on" : "off")}");
        }
        await db.SaveChangesAsync(ct);
        AuditLog.Summary(Cut($"Notifications: {string.Join("; ", said)}", 1000));
        return await SettingsAsync(principal, ct);
    }

    // --- sending (§10.5) ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// One run of the notification task's second timer: the due batches, oldest first, at most <c>PerRun</c>, each as one e-mail. A run
    /// stops at its first failure: the mail server is likely down, and the rest waits. How many e-mails went out.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_USER_NOTIFICATION_RUN);
        var o = options.CurrentValue;
        var now = Clock.UtcNow;
        var interrupted = now - SendingTimeout;
        await db.UserNotifications.Where(n => n.State == UserNotificationState.Sending && n.NextAttemptAt < interrupted)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.State, UserNotificationState.Pending), ct);
        if (now - lastCleanup > TimeSpan.FromDays(1))
        {
            lastCleanup = now;
            var before = now.AddDays(-Math.Max(1, o.KeepDays));
            await db.UserNotifications.Where(n => n.State != UserNotificationState.Pending && n.State != UserNotificationState.Sending && n.CreatedAt < before)
                .ExecuteDeleteAsync(ct);
        }
        if (!o.Enabled)
        {
            return 0;
        }
        var due = await db.UserNotifications.AsNoTracking()
            .Where(n => n.State == UserNotificationState.Pending && n.DueAt <= now && n.NextAttemptAt <= now)
            .GroupBy(n => new { n.UserId, n.BatchKey }).Select(g => new { g.Key.UserId, g.Key.BatchKey, First = g.Min(n => n.CreatedAt) })
            .OrderBy(g => g.First).Take(Math.Max(1, o.PerRun)).ToListAsync(ct);
        var sent = 0;
        foreach (var batch in due)
        {
            var rows = await db.UserNotifications
                .Where(n => n.UserId == batch.UserId && n.BatchKey == batch.BatchKey && n.State == UserNotificationState.Pending && n.DueAt <= now && n.NextAttemptAt <= now)
                .OrderBy(n => n.CreatedAt).ToListAsync(ct);
            if (rows.Count == 0)
            {
                continue;
            }
            foreach (var row in rows)
            {
                row.State = UserNotificationState.Sending;
                row.NextAttemptAt = now;
            }
            await db.SaveChangesAsync(ct);
            var outcome = await DeliverAsync(db, rows, o, ct);
            await db.SaveChangesAsync(ct);
            if (outcome == Outcome.Sent)
            {
                sent++;
            }
            else if (outcome == Outcome.Failed)
            {
                break;
            }
        }
        return sent;
    }

    private enum Outcome { Sent, Skipped, Failed }

    private async Task<Outcome> DeliverAsync(PnmtdDbContext db, List<UserNotificationEntity> rows, UserNotificationOptions o, CancellationToken ct)
    {
        var first = rows[0];
        var kind = UserNotificationKinds.Find(first.Kind);
        if (kind is null)
        {
            return Skip(rows, $"No such kind: {first.Kind}.");
        }
        var person = await db.Users.AsNoTracking().Where(u => u.Id == first.UserId).Select(u => new { u.Id, u.DisplayName, u.IsActive, u.IsAdmin, u.Email })
            .SingleOrDefaultAsync(ct);
        if (person is null)
        {
            return Skip(rows, "The person was removed.");
        }
        if (!person.IsActive)
        {
            return Skip(rows, $"{person.DisplayName} is deactivated.");
        }
        if (string.IsNullOrWhiteSpace(person.Email))
        {
            return Skip(rows, $"{person.DisplayName} has no e-mail address.");
        }
        var choice = await db.UserNotificationPreferences.AsNoTracking()
            .Where(p => p.UserId == person.Id && p.Kind == kind.Key && p.Channel == UserNotificationChannel.Email).Select(p => (bool?)p.Enabled).SingleOrDefaultAsync(ct);
        if (!(choice ?? kind.EmailByDefault))
        {
            return Skip(rows, $"{person.DisplayName} turned it off.");
        }
        var levels = await db.UserGroups.AsNoTracking().Where(m => m.UserId == person.Id).SelectMany(m => m.Group.Rights.Select(r => new { r.Area, r.Level }))
            .ToListAsync(ct);
        if (!new UserRights(person.IsAdmin, UserRights.Combine(levels.Select(l => (l.Area, l.Level)))).Has(kind.Area, kind.Level))
        {
            return Skip(rows, $"{person.DisplayName} no longer has {RightsCatalog.Of(kind.Area).Label}: {kind.Level}.");
        }
        var today = Clock.ToUtc(Clock.LocalNow.Date);
        var sentToday = await db.UserNotifications.AsNoTracking()
            .Where(n => n.UserId == person.Id && n.SentAt >= today && n.MessageId != null
                && (n.State == UserNotificationState.Sent || n.State == UserNotificationState.Simulated))
            .Select(n => n.MessageId).Distinct().CountAsync(ct);
        if (sentToday >= o.MaxPerUserDay)
        {
            return Skip(rows, $"The daily limit of {o.MaxPerUserDay} e-mails.");
        }
        var renderer = renderers.LastOrDefault(r => r.Kind == kind.Key);
        var baseUrl = Text.Trimmed(configuration["PublicBaseUrl"])?.TrimEnd('/');
        var rendered = renderer is null ? null
            : await renderer.RenderAsync(db, new UserNotificationBatch(kind, person.Id, person.DisplayName, first.SubjectId, rows), baseUrl, ct);
        if (rendered is null)
        {
            return Skip(rows, renderer is null ? $"Nothing writes {kind.Key}." : "Nothing left to say.");
        }
        var settings = baseUrl is null ? "Account → Notifications in PNMT" : $"{baseUrl}/pnmt-admin/account#notifications";
        var body = $"{rendered.Body.TrimEnd()}\n\n—\nYou get this because {rendered.Because}. Turn it off: {settings}\n";
        var message = new NotificationMessage(person.Email.Trim(), rendered.Subject, rendered.Subject, body, Importance.Low);
        var simulate = configuration.GetValue("Development:DoNotSendNotifications", false);
        var result = await notifications.SendAsync(message, Cut($"{rendered.Purpose} → {person.DisplayName}", 300), simulate, ct);
        var now = Clock.UtcNow;
        if (result is null)
        {
            return Skip(rows, "No provider takes the address.");
        }
        if (result.Ok)
        {
            var messageId = Guid.CreateVersion7();
            foreach (var row in rows)
            {
                row.State = simulate ? UserNotificationState.Simulated : UserNotificationState.Sent;
                row.SentAt = now;
                row.MessageId = messageId;
                row.Outcome = Cut(result.Detail, 500);
            }
            return Outcome.Sent;
        }
        foreach (var row in rows)
        {
            row.Attempts++;
            row.Outcome = Cut(result.Detail, 500);
            if (row.Attempts > Retries.Length)
            {
                row.State = UserNotificationState.Failed;
            }
            else
            {
                row.State = UserNotificationState.Pending;
                row.NextAttemptAt = now + Retries[row.Attempts - 1];
            }
        }
        logger.LogWarning("A user notification ({Kind}) failed, attempt {Attempt}: {Detail}", kind.Key, rows.Max(r => r.Attempts), result.Detail);
        return Outcome.Failed;
    }

    private static Outcome Skip(List<UserNotificationEntity> rows, string why)
    {
        foreach (var row in rows)
        {
            row.State = UserNotificationState.Skipped;
            row.Outcome = Cut(why, 500);
        }
        return Outcome.Skipped;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
