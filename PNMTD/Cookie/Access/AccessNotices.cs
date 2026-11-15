using System.Text;
using Microsoft.EntityFrameworkCore;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Availability;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Access;

/// <summary>What a notice came to: how many went out, were held back by a limit, whether it waits for working hours.</summary>
public sealed record AccessNoticeResult(int Sent, int HeldBack, bool Waiting, int Recipients, string Detail, long? FirstOutboxId = null);

/// <summary>
/// The access notice (docs/cookie-access-plan.md §6.1 to §6.3): to the contacts of the Access tab, never anyone a model named, as
/// the verdict's row says: by e-mail, Pushover or both; outside working hours to nobody until they begin, to the contacts who
/// allow it, or to every contact. Code writes the facts from the logins; Cookie's sentence is the reason. One notice per case and
/// verdict: again only when the verdict rises, or after 4 hours. Both Cookie's notices (through the executor) and code's
/// (<see cref="AccessWatchTask"/>) come here.
/// </summary>
public sealed class AccessNotices(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, Outbox outbox, AvailabilityPlan plan,
    IConfiguration configuration, IServiceProvider services, ILogger<AccessNotices> logger)
{
    public static readonly TimeSpan AgainAfter = TimeSpan.FromHours(4);

    public async Task<AccessNoticeResult> NotifyAsync(Guid caseId, AccessVerdict verdict, string? reason, string source, CancellationToken ct, bool force = false)
    {
        var s = await settings.GetAsync<AccessSettings>(ct);
        var rule = s.Notices.Of(verdict);
        if (rule is null)
        {
            return new AccessNoticeResult(0, 0, false, 0, "A normal login tells nobody.");
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var c = await db.CookieCases.SingleOrDefaultAsync(x => x.Id == caseId, ct);
        if (c is null)
        {
            return new AccessNoticeResult(0, 0, false, 0, "The case is gone.");
        }
        var now = Clock.UtcNow;
        var told = AccessSignals.VerdictOf(c.AccessNotifiedVerdict);
        if (!force && told is { } t && t >= verdict && c.AccessNotifiedAt is { } at && now - at < AgainAfter)
        {
            return new AccessNoticeResult(0, 0, false, 0, $"The contacts were told {AccessSignals.Name(t)} at {Clock.ToLocal(at):HH:mm}.");
        }
        if (!rule.Notify)
        {
            CaseBook.Entry(db, c.Id, now, "notice", $"{Label(verdict)}: this verdict tells no contacts (Admin → Cookie → Access).");
            await db.SaveChangesAsync(ct);
            return new AccessNoticeResult(0, 0, false, 0, "This verdict tells no contacts.");
        }
        var reviews = await db.AccessReviews.AsNoTracking().Where(r => r.CaseId == c.Id).OrderByDescending(r => r.At).Take(20).ToListAsync(ct);
        if (reviews.Count == 0)
        {
            return new AccessNoticeResult(0, 0, false, 0, "The case has no logins.");
        }
        var target = reviews[0].Target;
        var calendar = await plan.CalendarAsync(ct);
        var working = calendar.IsWorking(now);
        // A late login never wakes anyone (§4.5).
        var hours = reviews.All(r => r.Late) ? AccessHours.Wait : rule.Hours;
        if (!working && hours == AccessHours.Wait)
        {
            if (AccessSignals.VerdictOf(c.AccessPendingVerdict) is not { } pending || pending < verdict)
            {
                c.AccessPendingVerdict = AccessSignals.Name(verdict);
                c.AccessPendingReason = CaseBook.Cut(reason, 300);
                c.AccessPendingSource = source;
                CaseBook.Entry(db, c.Id, now, "notice", $"{Label(verdict)}: the notice waits for working hours.");
                await db.SaveChangesAsync(ct);
            }
            return new AccessNoticeResult(0, 0, true, 0, "Outside working hours: the notice waits for them.");
        }
        var recipients = await RecipientsAsync(db, s, target, working, hours, ct);
        var (subject, body) = Text(c, reviews, verdict, reason, source);
        var sent = 0;
        var held = 0;
        long? first = null;
        foreach (var user in recipients)
        {
            var channels = new List<(Models.Db.CookieChannel Channel, string? Address)>();
            if (rule.Email)
            {
                channels.Add((Models.Db.CookieChannel.AccessEmail, user.Email));
            }
            if (rule.Pushover)
            {
                channels.Add((Models.Db.CookieChannel.AccessPushover, user.PushoverKey));
            }
            foreach (var (channel, address) in channels.Where(x => !string.IsNullOrWhiteSpace(x.Address)))
            {
                var row = await outbox.SendAsync(new OutboxRequest(channel, subject, body, c.Id, UserId: user.Id, Recipient: user.DisplayName, Address: address,
                    Urgency: verdict >= AccessVerdict.Suspicious ? Urgency.Urgent : Urgency.Important, Source: source, Unlimited: verdict == AccessVerdict.Alarming), ct);
                first ??= row.Id;
                if (row.State is Models.Db.CookieOutboxState.Sent or Models.Db.CookieOutboxState.Simulated)
                {
                    sent++;
                }
                else if (row.State == Models.Db.CookieOutboxState.HeldBack)
                {
                    held++;
                }
            }
        }
        string detail;
        if (recipients.Count == 0)
        {
            detail = "Nobody to reach: no contact for this target, right and hour.";
            CaseBook.Entry(db, c.Id, now, "held-back", $"{Label(verdict)}: {detail}");
            await db.SaveChangesAsync(ct);
            await NobodyAsync(c, subject, body, ct);
            return new AccessNoticeResult(0, 0, false, 0, detail);
        }
        detail = $"{Label(verdict)} to {recipients.Count} {(recipients.Count == 1 ? "contact" : "contacts")}: {sent} sent{(held > 0 ? $", {held} held back by a limit" : "")}.";
        c.AccessNotifiedVerdict = AccessSignals.Name(verdict);
        c.AccessNotifiedAt = now;
        if (AccessSignals.VerdictOf(c.AccessPendingVerdict) is { } waiting && waiting <= verdict)
        {
            c.AccessPendingVerdict = null;
            c.AccessPendingReason = null;
            c.AccessPendingSource = null;
        }
        CaseBook.Entry(db, c.Id, now, "notice", detail, source == "code" ? "code" : "cookie");
        await db.SaveChangesAsync(ct);
        return new AccessNoticeResult(sent, held, false, recipients.Count, detail, first);
    }

    /// <summary>
    /// The contacts for a target and hour (§6.2): in working hours all, group members who don't work today skipped (a user named
    /// directly always told); outside them those who allow it, or everyone. Each needs <i>Cookie: Use</i> and the target's right.
    /// </summary>
    public async Task<List<UserEntity>> RecipientsAsync(PnmtdDbContext db, AccessSettings s, AccessTargetKind target, bool working, AccessHours hours,
        CancellationToken ct)
    {
        var contacts = s.Contacts.Where(c => c.For(target) && (working || hours == AccessHours.Everyone || c.OutsideHours)).ToList();
        var direct = contacts.Where(c => c.UserId is not null).Select(c => c.UserId!.Value).ToHashSet();
        var groups = contacts.Where(c => c.GroupId is not null).Select(c => c.GroupId!.Value).ToList();
        var members = await db.UserGroups.AsNoTracking().Where(m => groups.Contains(m.GroupId)).Select(m => m.UserId).Distinct().ToListAsync(ct);
        var ids = direct.Concat(members).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.IsActive).ToListAsync(ct);
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var snapshot = working ? await plan.ReadAsync(today, today, ct, [.. users.Select(u => u.Id)]) : null;
        var result = new List<UserEntity>();
        foreach (var u in users)
        {
            if (snapshot is not null && !direct.Contains(u.Id) && !snapshot.Closed(today) && !snapshot.Works(u.Id, today))
            {
                continue;
            }
            if (await SessionCache.LoadAsync(db, u.Id) is not { } entry || !MaySee(entry.Rights, target))
            {
                continue;
            }
            result.Add(u);
        }
        return result;
    }

    /// <summary>What a contact needs for a target's notices (§6.2): Cookie's window, and the logins' right.</summary>
    public static bool MaySee(UserRights rights, AccessTargetKind target) =>
        rights.Has(Area.Cookie, Level.Use) && (target == AccessTargetKind.Pnmt ? rights.Has(Area.AuditLog, Level.View) : rights.Has(Area.Monitoring, Level.Edit));

    /// <summary>The notice's text (§6.3): the facts from the logins, then Cookie's sentence, or code's signals.</summary>
    private (string Subject, string Body) Text(CookieCaseEntity c, List<AccessReviewEntity> reviews, AccessVerdict verdict, string? reason, string source)
    {
        var target = reviews[0].Target == AccessTargetKind.Pnmt ? AccessWatch.PnmtName : reviews[0].TargetName;
        var subject = $"{Label(verdict)} login on {target}";
        var text = new StringBuilder();
        foreach (var r in reviews.Take(3))
        {
            text.AppendLine(AccessWatch.Describe(r));
        }
        var strong = reviews.SelectMany(AccessWatch.SignalsOf).Where(x => x.Strength == SignalStrength.Strong).Select(x => x.Text).Distinct().Take(3).ToList();
        foreach (var signal in strong)
        {
            text.AppendLine($"· {signal}");
        }
        text.AppendLine();
        // Cookie's sentence; code's reason as it is (a person's answer); or, without one, the signals.
        text.AppendLine(string.IsNullOrWhiteSpace(reason)
            ? $"Cookie couldn't judge this login. Signals: {string.Join("; ", reviews.SelectMany(AccessWatch.SignalsOf).Select(x => x.Text).Distinct().Take(4))}."
            : source == "code" ? reason.Trim() : $"Cookie: {reason.Trim()}");
        text.AppendLine();
        var count = reviews.Count;
        text.Append($"{count} {(count == 1 ? "login" : "logins")} in this case · Expected? Not expected?");
        if (Link(c.Id) is { } link)
        {
            text.Append($" → {link}");
        }
        return (subject, text.ToString());
    }

    public string? Link(Guid caseId) => configuration["PublicBaseUrl"] is { Length: > 0 } b ? $"{b.TrimEnd('/')}/pnmt-admin/cookie/cases?case={caseId}" : null;

    public static string Label(AccessVerdict v) => v switch
    {
        AccessVerdict.Unusual => "Unusual",
        AccessVerdict.Suspicious => "Suspicious",
        AccessVerdict.Alarming => "Alarming",
        _ => "Normal",
    };

    /// <summary>Nobody could be reached (§6.2): the notice goes to the decisions inbox, for a person.</summary>
    private async Task NobodyAsync(CookieCaseEntity c, string subject, string body, CancellationToken ct)
    {
        try
        {
            var round = services.GetRequiredService<CookieRound>();
            var rights = await round.RightsAsync(ct) ?? new UserRights(false, new Dictionary<Area, Level>());
            var p = await services.GetRequiredService<PrivacyFilter>().OpenAsync(ct);
            var hand = new ProposedAction(CookieActionKind.HandToPerson, null, 0, c.Id.ToString(), subject, body, "The access notice reached nobody.", [], 1, Urgency.Urgent);
            await services.GetRequiredService<CookieExecutor>().HandleAsync(new ActionRequest(new CookieProposal(hand), c, null, rights, p, c.Areas), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "An access notice that reached nobody couldn't go to the inbox");
        }
    }

    /// <summary>The notices that waited for working hours (§6.1), now that they began.</summary>
    public async Task<int> PendingAsync(CancellationToken ct)
    {
        if (!(await plan.CalendarAsync(ct)).IsWorking(Clock.UtcNow))
        {
            return 0;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var waiting = await db.CookieCases.AsNoTracking().Where(c => c.Open && c.Kind == CookieCaseKind.Access && c.AccessPendingVerdict != null)
            .Select(c => new { c.Id, c.AccessPendingVerdict, c.AccessPendingReason, c.AccessPendingSource }).ToListAsync(ct);
        foreach (var w in waiting)
        {
            if (AccessSignals.VerdictOf(w.AccessPendingVerdict) is { } v)
            {
                await NotifyAsync(w.Id, v, w.AccessPendingReason, w.AccessPendingSource ?? "code", ct, force: true);
            }
        }
        return waiting.Count;
    }

    /// <summary>
    /// The hourly digest (§6.4): the access e-mails a limit held back in the hour, in one e-mail per contact, which no limit holds
    /// back. They stay held back in the outbox, where a person can still send them one by one.
    /// </summary>
    public async Task<int> DigestAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var runtime = await CaseBook.RuntimeAsync(db, ct);
        var now = Clock.UtcNow;
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var from = runtime.AccessDigestAt ?? hour.AddHours(-1);
        if (from >= hour)
        {
            return 0;
        }
        var held = await db.CookieOutbox.AsNoTracking().Where(o => o.Channel == Models.Db.CookieChannel.AccessEmail && o.State == Models.Db.CookieOutboxState.HeldBack
            && o.CreatedAt >= from && o.CreatedAt < hour && o.UserId != null).ToListAsync(ct);
        runtime.AccessDigestAt = hour;
        await db.SaveChangesAsync(ct);
        var sent = 0;
        foreach (var group in held.GroupBy(o => o.UserId!.Value))
        {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == group.Key && u.IsActive, ct);
            if (user?.Email is not { Length: > 0 } email)
            {
                continue;
            }
            var text = new StringBuilder($"A limit held back {group.Count()} access {(group.Count() == 1 ? "notice" : "notices")} in the last hour:\n\n");
            foreach (var o in group.OrderBy(o => o.CreatedAt))
            {
                text.AppendLine($"· {Clock.ToLocal(o.CreatedAt):HH:mm} {o.Subject}");
            }
            text.AppendLine("\nEach is in Admin → Cookie → Overview's outbox, with the whole text.");
            await outbox.SendAsync(new OutboxRequest(Models.Db.CookieChannel.AccessEmail, $"Access notices held back: {group.Count()}", text.ToString(), null,
                UserId: user.Id, Recipient: user.DisplayName, Address: email, Source: "code", Unlimited: true), ct);
            sent++;
        }
        return sent;
    }
}
