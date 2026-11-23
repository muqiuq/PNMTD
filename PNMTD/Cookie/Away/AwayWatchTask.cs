using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Availability;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Away;

/// <summary>What one step of the away watch's task did.</summary>
public sealed record AwayStep(int WithoutVerdict, int Expired, int Told, int Off);

/// <summary>
/// The away watch's own work (docs/cookie-redmine-tab-plan.md §6.7), every minute while <c>Cookie__Enabled</c> is set, Cookie started
/// or not: a comment Cookie didn't judge within 30 minutes (at once while Cookie is stopped or paused, or its notice is <i>Off</i>), or
/// one the screen withheld, is told marked <i>not judged</i> or only recorded, as set; the notices that are due go out; what wasn't told
/// within three working days expires; a comment's text goes after 30 days. Switched off, whatever waits ends as <i>off</i>.
/// </summary>
public sealed class AwayWatchTask(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, AwayNotices notices, AvailabilityPlan plan,
    IOptionsMonitor<CookieOptions> options, IConfiguration configuration, ILogger<AwayWatchTask> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("BackgroundTasks:Enabled", true))
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.Enabled)
                {
                    await StepAsync(stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "The away watch's step failed");
            }
            try
            {
                await Task.Delay(Every, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<AwayStep> StepAsync(CancellationToken ct)
    {
        var actions = await settings.GetAsync<ActionSettings>(ct);
        var s = (await settings.GetAsync<CookieRedmineSettings>(ct)).Away;
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var open = await db.CookieAwayComments
            .Where(a => a.State == AwayCommentState.Pending || a.State == AwayCommentState.Withheld || a.State == AwayCommentState.Waiting).ToListAsync(ct);
        if (!actions.Duties.AwayComments)
        {
            foreach (var a in open)
            {
                a.State = AwayCommentState.Off;
                a.Detail = "Switched off on Admin → Cookie → Actions.";
            }
            await db.SaveChangesAsync(ct);
            await PurgeAsync(db, now, ct);
            return new AwayStep(0, 0, 0, open.Count);
        }
        var runtime = await CaseBook.RuntimeAsync(db, ct);
        var cookieJudges = runtime.Running && runtime.State != CookieState.Paused && actions.Levels.AwayNotice.Level != ActionLevel.Off;
        var withoutVerdict = 0;
        // A comment whose notice waits for a person in the decisions inbox isn't code's (it expires below if nobody decides).
        foreach (var a in open.Where(a => a.State == AwayCommentState.Withheld || a.State == AwayCommentState.Pending && a.DecisionId is null
            && (!cookieJudges || now - a.CreatedAt >= AwayRules.CookieHas)))
        {
            var why = a.State == AwayCommentState.Withheld ? a.Detail ?? "Cookie didn't read it."
                : actions.Levels.AwayNotice.Level == ActionLevel.Off ? "Cookie's away notice is off: nobody judged it."
                : !cookieJudges ? "Cookie is stopped or paused."
                : "Cookie didn't judge it within 30 minutes.";
            if (s.WithoutVerdict == AwayWithoutVerdict.Tell)
            {
                AwayNotices.ToTell(a, "code", null, null, why, now);
            }
            else
            {
                a.State = AwayCommentState.Recorded;
                a.JudgedBy = "code";
                a.JudgedAt = now;
                a.Reason = CaseBook.Cut(why, 300);
                a.Detail = "Recorded only, as set: nobody was told.";
            }
            CaseBook.Entry(db, a.CaseId, now, "away", $"Not judged: {why} {(a.State == AwayCommentState.Waiting ? "It is told as such." : "Recorded only.")}");
            withoutVerdict++;
        }
        var calendar = await plan.CalendarAsync(ct);
        var expired = 0;
        foreach (var a in open.Where(a => (a.State == AwayCommentState.Waiting || a.State == AwayCommentState.Pending && a.DecisionId is not null)
            && calendar.WorkingTimeBetween(a.CreatedAt, now) >= AwayRules.ExpiresAfter))
        {
            a.Detail = a.State == AwayCommentState.Pending ? "Nobody decided Cookie's proposal within three working days." : "Not told within three working days.";
            a.State = AwayCommentState.Expired;
            CaseBook.Entry(db, a.CaseId, now, "away", "A comment's notice expired: not told within three working days.");
            expired++;
        }
        await db.SaveChangesAsync(ct);
        var told = await notices.SendDueAsync(ct);
        await PurgeAsync(db, now, ct);
        return new AwayStep(withoutVerdict, expired, told, 0);
    }

    /// <summary>A comment's words go after 30 days (§6.9); the verdict stays.</summary>
    private static async Task PurgeAsync(PnmtdDbContext db, DateTime now, CancellationToken ct)
    {
        var before = now - AwayRules.TextKept;
        await db.CookieAwayComments.Where(a => a.Text != null && a.CreatedAt < before).ExecuteUpdateAsync(u => u.SetProperty(a => a.Text, (string?)null), ct);
    }
}
