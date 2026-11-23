using Microsoft.EntityFrameworkCore;
using PNMTD.Availability;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Cookie.Away;

/// <summary>
/// The away watch's finding (docs/cookie-redmine-tab-plan.md §6.2, §6.3), inside the case book's read of the watched tickets: it keeps
/// each case's newest journal, and turns a new comment on a ticket whose assignee is away into a row for Cookie to judge.
/// </summary>
public sealed class AwayWatch(AvailabilityPlan plan, CookieSettings settings)
{
    /// <summary>One read of the tickets: the switch and settings once, the plan read when first needed.</summary>
    public sealed class Run
    {
        internal bool On { get; init; }

        internal AwayWatchSettings Settings { get; init; } = new();

        internal int? CookieRedmineId { get; init; }

        internal PlanSnapshot? Plan { get; set; }

        /// <summary>The comments found in this read.</summary>
        public int Found { get; internal set; }
    }

    public async Task<Run> StartAsync(RedmineWorker worker, RedmineCalls calls, CancellationToken ct)
    {
        var on = (await settings.GetAsync<ActionSettings>(ct)).Duties.AwayComments;
        return new Run
        {
            On = on,
            Settings = (await settings.GetAsync<CookieRedmineSettings>(ct)).Away,
            // Cookie's own notes are never comments to judge (§6.2); without the write key, Cookie writes nothing.
            CookieRedmineId = on ? await worker.CookieUserIdAsync(calls) : null,
        };
    }

    /// <summary>
    /// A ticket read with its journals: keeps the newest journal on the case, and, while the watch is on, records the new comments
    /// made while the assignee is away. A case seen the first time starts at its newest journal, so nothing old is judged.
    /// </summary>
    public async Task SeeAsync(Run run, PnmtdDbContext db, CookieCaseEntity c, RmIssue issue, bool firstSight, DateTime now, CancellationToken ct)
    {
        if (issue.Journals is null)
        {
            // Read without its journals (the closing read while the watch is off): nothing is known of them, so nothing moves.
            return;
        }
        var last = c.LastJournalId;
        var newest = (issue.Journals ?? []).Select(j => j.Id).DefaultIfEmpty(0).Max();
        c.LastJournalId = Math.Max(last ?? 0, newest);
        if (firstSight || last is null || !run.On || issue.AssignedTo is not { } assignedTo)
        {
            return;
        }
        var comments = AwayRules.NewComments(issue.Journals, last.Value, assignedTo.Id, run.CookieRedmineId, run.Settings.PrivateNotes);
        if (comments.Count == 0)
        {
            return;
        }
        if (await RedmineLinks.UserOfAsync(db, assignedTo.Id, assignedTo.Name, ct) is not { } assignee)
        {
            return;
        }
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        run.Plan ??= await plan.ReadAsync(today.AddDays(-1), today.AddDays(AwayRules.LookAhead + 1), ct);
        var snapshot = run.Plan;
        var inPlan = snapshot.People.Any(p => p.Id == assignee.Id);
        var known = await db.CookieAwayComments.Where(a => a.IssueId == issue.Id).Select(a => a.JournalId).ToListAsync(ct);
        foreach (var j in comments.Where(j => !known.Contains(j.Id)))
        {
            // Away on the day of the comment, as the plan has it.
            var at = Clock.AsUtc(j.CreatedOn);
            var day = DateOnly.FromDateTime(Clock.ToLocal(at));
            day = day < today.AddDays(-1) ? today.AddDays(-1) : day;
            if (!AwayRules.Away(inPlan, f => snapshot.Has(assignee.Id, day, f), run.Settings.AwayFlags))
            {
                continue;
            }
            var until = AwayRules.AwayUntil(day, d => snapshot.Works(assignee.Id, d), snapshot.Closed);
            db.CookieAwayComments.Add(new CookieAwayCommentEntity
            {
                CaseId = c.Id, IssueId = issue.Id, JournalId = j.Id, AssigneeUserId = assignee.Id, AuthorRedmineId = j.User?.Id,
                AuthorName = CaseBook.Cut(j.User?.Name, 200), IsPrivate = j.PrivateNotes, CommentAt = at, Text = CaseBook.Cut(j.Notes, AwayRules.MaxText),
                AwayUntil = until, State = AwayCommentState.Pending, CreatedAt = now,
            });
            CaseBook.Entry(db, c.Id, now, "away",
                $"A {(j.PrivateNotes ? "private " : "")}comment by {j.User?.Name ?? "someone"} while {assignee.DisplayName} is away {AwayRules.Until(until)}.");
            run.Found++;
        }
    }
}
