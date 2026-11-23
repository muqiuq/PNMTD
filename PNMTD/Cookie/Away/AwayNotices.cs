using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Availability;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Cookie.Away;

/// <summary>
/// The away notice (docs/cookie-redmine-tab-plan.md §6.5, §6.6): to whom the settings name, never anyone a model named; in working
/// hours only; one per ticket within 4 hours, later comments joining the next; and nothing once the assignee is back. Code writes it
/// from the comments; Cookie's verdict is its reason. Both Cookie's notices (through the executor) and code's come here, as rows
/// <see cref="AwayCommentState.Waiting"/>.
/// </summary>
public sealed class AwayNotices(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, Outbox outbox, AvailabilityPlan plan,
    IOptionsMonitor<RedmineOptions> redmineOptions, IConfiguration configuration)
{
    /// <summary>A comment to tell: Cookie found it important, or nobody could judge it and the setting says tell.</summary>
    public static void ToTell(CookieAwayCommentEntity a, string judgedBy, string? importance, double? confidence, string? reason, DateTime now,
        long? decisionId = null)
    {
        a.State = AwayCommentState.Waiting;
        a.JudgedBy = judgedBy;
        a.JudgedAt = now;
        a.Importance = importance;
        a.Confidence = confidence;
        a.Reason = CaseBook.Cut(reason, 300);
        a.DecisionId = decisionId;
    }

    /// <summary>The notices that are due: in working hours, per ticket, the assignee still away. Returns how many tickets were told.</summary>
    public async Task<int> SendDueAsync(CancellationToken ct)
    {
        var now = Clock.UtcNow;
        if (!(await plan.CalendarAsync(ct)).IsWorking(now))
        {
            return 0;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var waiting = await db.CookieAwayComments.Where(a => a.State == AwayCommentState.Waiting).OrderBy(a => a.CommentAt).ToListAsync(ct);
        if (waiting.Count == 0)
        {
            return 0;
        }
        var s = (await settings.GetAsync<CookieRedmineSettings>(ct)).Away;
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var snapshot = await plan.ReadAsync(today, today, ct, s.PersonId is { } person ? [person] : null);
        var told = 0;
        foreach (var ticket in waiting.GroupBy(a => a.IssueId))
        {
            var rows = ticket.ToList();
            var last = await db.CookieAwayComments.Where(a => a.IssueId == ticket.Key && a.NotifiedAt != null).MaxAsync(a => (DateTime?)a.NotifiedAt, ct);
            if (last is { } l && now - l < AwayRules.AgainAfter)
            {
                continue;
            }
            var assignee = rows[^1].AssigneeUserId;
            if (!AwayRules.Away(snapshot.People.Any(p => p.Id == assignee), f => snapshot.Has(assignee, today, f), s.AwayFlags))
            {
                foreach (var r in rows)
                {
                    r.State = AwayCommentState.Back;
                    r.Detail = "The assignee is back: not sent.";
                }
                CaseBook.Entry(db, rows[0].CaseId, now, "away", "The assignee is back: the notice isn't sent.");
                continue;
            }
            await NotifyAsync(db, rows, s, snapshot, today, now, ct);
            told++;
        }
        await db.SaveChangesAsync(ct);
        return told;
    }

    private async Task NotifyAsync(PnmtdDbContext db, List<CookieAwayCommentEntity> rows, AwayWatchSettings s, PlanSnapshot snapshot, DateOnly today,
        DateTime now, CancellationToken ct)
    {
        var (caseId, assigneeId) = (rows[0].CaseId, rows[^1].AssigneeUserId);
        var c = await db.CookieCases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId, ct);
        var assignee = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == assigneeId, ct);
        var notThese = new HashSet<Guid> { assigneeId };
        foreach (var r in rows)
        {
            if (await RedmineLinks.UserOfAsync(db, r.AuthorRedmineId, r.AuthorName, ct) is { } author)
            {
                notThese.Add(author.Id);
            }
        }
        var ids = AwayRules.Recipients(s, snapshot.ResponsibleAt(now).UserIds, s.PersonId is { } p && snapshot.Works(p, today), notThese);
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.IsActive).ToListAsync(ct);
        var reachable = users.Where(u => !string.IsNullOrWhiteSpace(u.Email) || s.Pushover && !string.IsNullOrWhiteSpace(u.PushoverKey)).ToList();
        if (reachable.Count == 0)
        {
            var why = ids.Count == 0
                ? "Nobody to tell: nobody is responsible today, and the chosen person isn't set or doesn't work."
                : "Nobody to tell: whom the settings name has no e-mail address.";
            foreach (var r in rows)
            {
                r.State = AwayCommentState.Nobody;
                r.Detail = why;
            }
            CaseBook.Entry(db, rows[0].CaseId, now, "away", why);
            return;
        }
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        int sent = 0, held = 0;
        foreach (var u in reachable)
        {
            // The comment's words only to a reader who may see the ticket (§6.6); anyone else gets the links.
            var quote = (await SessionCache.LoadAsync(db, u.Id))?.Rights.Has(Area.Redmine, Level.View) == true;
            var (subject, body, line) = Text(c, rows, assignee?.DisplayName ?? "the assignee", quote, identity.Name);
            var channels = new List<(CookieChannel Channel, string? Address, string Body)> { (CookieChannel.AwayEmail, u.Email, body) };
            if (s.Pushover)
            {
                channels.Add((CookieChannel.AwayPushover, u.PushoverKey, line));
            }
            foreach (var (channel, address, text) in channels.Where(x => !string.IsNullOrWhiteSpace(x.Address)))
            {
                var row = await outbox.SendAsync(new OutboxRequest(channel, subject, text, rows[0].CaseId, UserId: u.Id, Recipient: u.DisplayName,
                    Address: address, IssueId: rows[0].IssueId, Source: rows.Any(r => r.JudgedBy == "cookie") ? "cookie" : "code"), ct);
                if (row.State is CookieOutboxState.Sent or CookieOutboxState.Simulated)
                {
                    sent++;
                }
                else if (row.State == CookieOutboxState.HeldBack)
                {
                    held++;
                }
            }
        }
        var names = string.Join(", ", reachable.Select(u => u.DisplayName));
        var detail = $"To {names}: {sent} sent{(held > 0 ? $", {held} held back by a limit" : "")}.";
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].State = i == 0 ? AwayCommentState.Notified : AwayCommentState.Joined;
            rows[i].NotifiedAt = now;
            rows[i].Recipients = CaseBook.Cut(names, 500);
            rows[i].Detail = CaseBook.Cut(i == 0 ? detail : $"In the notice of {Clock.ToLocal(now):HH:mm}. {detail}", 500);
        }
        CaseBook.Entry(db, rows[0].CaseId, now, "notice",
            $"{(rows.Count == 1 ? "A comment" : $"{rows.Count} comments")} while {assignee?.DisplayName ?? "the assignee"} is away: {detail}",
            rows.Any(r => r.JudgedBy == "cookie") ? "cookie" : "code");
    }

    /// <summary>The notice (§6.6): the ticket, each comment (quoted for a reader with Redmine: View), the verdict, the absence, the links.</summary>
    public (string Subject, string Body, string Line) Text(CookieCaseEntity? c, IReadOnlyList<CookieAwayCommentEntity> rows, string assignee, bool quote,
        string signature)
    {
        var issue = rows[0].IssueId;
        var judged = rows.Any(r => r.Importance == "high");
        var subject = $"#{issue} while {assignee} is away: {(rows.Count == 1 ? "a comment" : $"{rows.Count} comments")}"
            + (judged ? " Cookie found important" : " to look at");
        var text = new StringBuilder();
        text.AppendLine($"{c?.Title ?? $"#{issue}"}{(c?.StatusName is { } status ? $" · {status}" : "")}");
        text.AppendLine();
        foreach (var r in rows)
        {
            text.AppendLine($"{r.AuthorName ?? "Someone"} {(r.IsPrivate ? "wrote a private note" : "commented")} at {Clock.ToLocal(r.CommentAt).ToString("ddd HH:mm", CultureInfo.InvariantCulture)}:");
            text.AppendLine(quote ? $"“{AwayRules.Cut(r.Text?.Trim(), AwayRules.QuoteChars)}”" : "(The comment is on the ticket.)");
            text.AppendLine(r.Importance == "high"
                ? $"Cookie: {r.Reason}{(r.Confidence is { } conf ? $" ({conf.ToString("0%", CultureInfo.InvariantCulture)} sure)" : "")}"
                : $"Not judged: {r.Reason ?? "Cookie couldn't read it in time."}");
            text.AppendLine();
        }
        text.AppendLine($"{assignee} is away {AwayRules.Until(rows[^1].AwayUntil)}.");
        if (configuration["PublicBaseUrl"] is { Length: > 0 } b)
        {
            text.AppendLine($"In PNMT: {b.TrimEnd('/')}/pnmt-admin/redmine/issues/{issue}");
        }
        if (redmineOptions.CurrentValue is { Enabled: true } o)
        {
            text.AppendLine($"In Redmine: {o.BrowserUrl}/issues/{issue}");
        }
        text.AppendLine();
        text.Append($"— {signature}");
        var line = $"#{issue}: {(judged ? "an important comment" : "a comment")} while {assignee} is away" + (judged && rows[0].Reason is { } why ? $" — {why}" : "");
        return (subject, text.ToString(), AwayRules.Cut(line, 250));
    }
}
