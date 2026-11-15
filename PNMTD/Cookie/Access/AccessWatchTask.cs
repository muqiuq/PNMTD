using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Access;

/// <summary>What one step of the access watch's task did.</summary>
public sealed record AccessStep(int SignIns, int CodeNotices, int NotJudged, int Pending, int Closed, int Digests);

/// <summary>
/// The access watch's own work, every 30 seconds while <c>Cookie__Enabled</c> is set, Cookie started or not (docs/cookie-access-plan.md
/// §4.3, §6.5): PNMT's new sign-ins; code's notice for a strong signal Cookie hasn't judged within 10 minutes (at once while the
/// notice's level isn't <i>Auto</i>); a medium one left unjudged for 24 hours closes as <i>not judged</i>; the notices that waited
/// for working hours; access cases quiet for 7 days close; and the hourly digest of what a limit held back.
/// </summary>
public sealed class AccessWatchTask(IDbContextFactory<PnmtdDbContext> dbs, AccessWatch watch, AccessNotices notices, WasThisYou ask, CookieSettings settings,
    IOptionsMonitor<CookieOptions> options, IConfiguration configuration, ILogger<AccessWatchTask> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CookieHas = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MediumWaits = TimeSpan.FromHours(24);
    public static readonly TimeSpan QuietFor = TimeSpan.FromDays(7);

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
                logger.LogWarning(e, "The access watch's step failed");
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

    public async Task<AccessStep> StepAsync(CancellationToken ct)
    {
        var signIns = await watch.SignInsAsync(ct);
        var (code, notJudged) = await FallbackAsync(ct);
        var pending = await notices.PendingAsync(ct);
        var closed = await CloseQuietAsync(ct);
        var digests = await notices.DigestAsync(ct);
        return new AccessStep(signIns, code, notJudged, pending, closed, digests);
    }

    /// <summary>When Cookie can't judge (§6.5): code's notice for strong signals, <i>not judged</i> for medium ones after 24 hours.</summary>
    public async Task<(int CodeNotices, int NotJudged)> FallbackAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync<AccessSettings>(ct);
        if (!s.On)
        {
            // Switched off: what waits stays waiting, and nobody is told.
            return (0, 0);
        }
        var level = (await settings.GetAsync<ActionSettings>(ct)).Levels.AccessNotice.Level;
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var waiting = await db.AccessReviews.Where(r => r.State == AccessReviewState.Waiting && r.CaseId != null).ToListAsync(ct);
        int code = 0, notJudged = 0;
        foreach (var group in waiting.GroupBy(r => r.CaseId!.Value))
        {
            var reviews = group.ToList();
            var oldest = reviews.Min(r => r.CreatedAt);
            var strong = reviews.Where(r => r.Strong).ToList();
            if (strong.Count > 0 && s.CodeNotice && (now - oldest >= CookieHas || level != ActionLevel.Auto))
            {
                var strongSignals = strong.SelectMany(AccessWatch.SignalsOf).Count(x => x.Strength == SignalStrength.Strong);
                var verdict = AccessSignals.CodeVerdict(strongSignals, reviews.Any(r => r.Flagged), reviews.All(r => r.Late));
                await JudgeAsync(db, group.Key, reviews, verdict, now, ct);
                await notices.NotifyAsync(group.Key, verdict, null, "code", ct);
                await ask.AskAsync(group.Key, verdict, ct);
                code++;
                continue;
            }
            if (strong.Count == 0 && now - oldest >= MediumWaits)
            {
                foreach (var r in reviews)
                {
                    r.State = AccessReviewState.NotJudged;
                    r.JudgedBy = "code";
                    r.JudgedAt = now;
                }
                var c = await db.CookieCases.SingleOrDefaultAsync(x => x.Id == group.Key, ct);
                if (c is not null)
                {
                    CaseBook.Entry(db, c.Id, now, "not-judged", $"Nobody judged {reviews.Count} {(reviews.Count == 1 ? "login" : "logins")} within 24 hours.");
                    if (c.Open && c.AccessVerdict is null && c.AccessAnswer is null
                        && !await db.AccessReviews.AnyAsync(r => r.CaseId == c.Id && r.State != AccessReviewState.NotJudged && r.State != AccessReviewState.Waiting, ct))
                    {
                        c.Open = false;
                        c.ClosedAt = now;
                        CaseBook.Entry(db, c.Id, now, "closed", "Closed as not judged.");
                    }
                }
                notJudged += reviews.Count;
                await db.SaveChangesAsync(ct);
            }
        }
        return (code, notJudged);
    }

    /// <summary>Code's verdict on a case's waiting logins, in the decision log like Cookie's, so it can be rated.</summary>
    private static async Task JudgeAsync(PnmtdDbContext db, Guid caseId, List<AccessReviewEntity> reviews, AccessVerdict verdict, DateTime now, CancellationToken ct)
    {
        var c = await db.CookieCases.SingleOrDefaultAsync(x => x.Id == caseId, ct);
        var log = new CookieDecisionEntity
        {
            At = now, CaseId = caseId, Kind = AccessJudge.DecisionKind, Title = CaseBook.Cut($"Access: {AccessSignals.Name(verdict)} (code) · {c?.Title}", 300)!,
            Target = $"case:{caseId}", Outcome = AccessSignals.Name(verdict), Detail = "Cookie couldn't judge within 10 minutes: code's verdict from the strong signals.",
            Areas = c?.Areas ?? [], Proposal = "{}",
        };
        db.CookieDecisions.Add(log);
        await db.SaveChangesAsync(ct);
        foreach (var r in reviews)
        {
            r.State = AccessSignals.StateOf(verdict);
            r.JudgedBy = "code";
            r.JudgedAt = now;
            r.DecisionId = log.Id;
            r.Reason = "Cookie couldn't judge: code's verdict from the strong signals.";
        }
        if (c is not null)
        {
            var before = AccessSignals.VerdictOf(c.AccessVerdict);
            c.AccessVerdict = AccessSignals.Name(before is { } b && b > verdict ? b : verdict);
            c.ChangedAt = now;
            CaseBook.Entry(db, c.Id, now, "verdict", $"{AccessNotices.Label(verdict)}, by code: Cookie couldn't judge within 10 minutes.");
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Access cases without a new login for 7 days close (§5.1), answered or not.</summary>
    private async Task<int> CloseQuietAsync(CancellationToken ct)
    {
        var before = Clock.UtcNow - QuietFor;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var quiet = await db.CookieCases.Where(c => c.Open && c.Kind == CookieCaseKind.Access && c.ChangedAt < before).Take(200).ToListAsync(ct);
        var now = Clock.UtcNow;
        foreach (var c in quiet)
        {
            c.Open = false;
            c.ClosedAt = now;
            CaseBook.Entry(db, c.Id, now, "closed", "No new login for 7 days.");
        }
        await db.SaveChangesAsync(ct);
        return quiet.Count;
    }
}
