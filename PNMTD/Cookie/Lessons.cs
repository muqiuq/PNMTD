using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>The summariser's answer (docs/cookie-prompts.md §10).</summary>
public sealed record LessonsAnswer([property: Description("The lessons, at most 30, each one sentence")] IReadOnlyList<LessonDraft> Lessons);

public sealed record LessonDraft(
    [property: Description("One sentence, at most 300 characters")] string Text,
    [property: Description("For a suppress rule: channel, sensor-type or case-kind; empty otherwise")] string SuppressScope,
    [property: Description("For a suppress rule: pushover, email, ticket, redmine-note, a sensor type, problem or ticket; empty otherwise")] string SuppressValue);

/// <summary>
/// Ratings and lessons (docs/cookie-plan.md §11, §21.1): once a day, or after 10 new ratings, the summariser turns people's
/// ratings into lessons, saved as a new version of the <c>lessons</c> section; a lesson that is a suppress rule becomes a
/// suppress object for 30 days. Ratings and comments are untrusted text, masked like everything Cookie reads.
/// </summary>
/// <summary>The lounge's e-mails in the decision log (docs/lounge-plan.md §8.2): never in the lessons or the drift report (D12).</summary>
file static class LoungeKinds
{
    public static readonly string Email = ProposedAction.Name(CookieActionKind.LoungeEmail);
}

public sealed class Lessons(IDbContextFactory<PnmtdDbContext> dbs, CookieModel model, CookieBrain brain, CookieSettings settings, PrivacyFilter privacy,
    Instructions instructions, IOptionsMonitor<CookieOptions> options, ILogger<Lessons> logger)
{
    public const int NewRatings = 10;
    public const int MinRatings = 2;

    /// <summary>A lesson that is a rule becomes a rule (§11): a suppress object for 30 days, unless one like it is active.</summary>
    public async Task ApplyRulesAsync(LessonSettings lessons, CancellationToken ct)
    {
        foreach (var l in lessons.Lessons.Where(l => l.Active && l.SuppressScope is not null && !string.IsNullOrWhiteSpace(l.SuppressValue)))
        {
            var active = await instructions.ActiveAsync(ct);
            if (active.Any(i => i.Kind == "suppress" && i.Scope == l.SuppressScope && string.Equals(i.Value, l.SuppressValue, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            try
            {
                await instructions.CreateAsync(new InstructionDraft("suppress", l.SuppressScope!, l.SuppressValue!.Trim(), l.SuppressValue.Trim(), l.Text,
                    Clock.UtcNow.AddDays(Instructions.MaxDays)), confirmed: true, "lesson", null, l.ByPerson ? "A lesson" : "Cookie's lessons", ct);
            }
            catch (Api.ApiException e)
            {
                logger.LogInformation("A lesson's suppress rule wasn't valid: {Error}", e.Message);
            }
        }
    }

    public async Task<bool> DueAsync(DateTime? lastRun, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        // The lounge's e-mails stay out (docs/lounge-plan.md §8.2, D12): their ratings would teach the case work about banter.
        var unread = await db.CookieDecisions.CountAsync(d => d.Rating != null && !d.RatingRead && d.Kind != LoungeKinds.Email, ct);
        return unread >= NewRatings || (unread > 0 && (lastRun is null || Clock.UtcNow - lastRun >= TimeSpan.FromDays(1)));
    }

    public async Task<int> SummariseAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var since = Clock.UtcNow.AddDays(-30);
        var rated = await db.CookieDecisions.Where(d => d.Rating != null && d.RatedAt >= since && d.Kind != LoungeKinds.Email).OrderByDescending(d => d.RatedAt)
            .Take(200).ToListAsync(ct);
        if (rated.Count == 0)
        {
            return 0;
        }
        var current = await settings.GetAsync<LessonSettings>(ct);
        var p = await privacy.OpenAsync(ct);
        try
        {
            var values = await brain.ValuesAsync(ct);
            values["min_ratings"] = MinRatings.ToString(CultureInfo.InvariantCulture);
            values["lessons"] = CookieBrain.Json(current.Lessons.Where(l => l.Active).Select(l => l.Text));
            values["ratings"] = CookieBrain.Json(rated.Select(d => new
            {
                d.Kind, Title = p.Mask(d.Title), d.Outcome, d.Verdict, d.Rating,
                Comment = d.RatingComment is null ? null : $"<untrusted source=\"comment\">{p.Mask(InputSanitizer.Clean(d.RatingComment, 500).Text)}</untrusted>",
            }));
            var messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = CookiePrompts.Fill(await brain.PromptAsync("lessons", ct), values) });
            var turn = await model.CompleteAsync("lessons", messages, null, (nameof(LessonsAnswer), AiJson.SchemaFor<LessonsAnswer>()), "medium", null,
                options.CurrentValue.Tokens.Lessons, ct);
            var answer = JsonSerializer.Deserialize<LessonsAnswer>(turn.Content ?? "", AiJson.Options) ?? new LessonsAnswer([]);
            var lessons = answer.Lessons.Where(l => !string.IsNullOrWhiteSpace(l.Text)).Take(LessonSettings.MaxActive).Select(l =>
            {
                var scope = l.SuppressScope?.Trim().ToLowerInvariant();
                var rule = Instructions.SuppressScopes.Contains(scope ?? "") && !string.IsNullOrWhiteSpace(l.SuppressValue);
                return new Lesson(Guid.NewGuid(), CaseBook.Cut(p.Unmask(l.Text.Trim()), 300)!, true, rule ? scope : null, rule ? l.SuppressValue.Trim() : null);
            }).ToList();
            // A person's lessons stay as they are, and disabled ones stay disabled; the summariser's replace its own active ones.
            var byPerson = current.Lessons.Where(l => l.ByPerson).ToList();
            var room = Math.Max(0, LessonSettings.MaxActive - byPerson.Count(l => l.Active));
            var next = new LessonSettings
            {
                Lessons = [.. byPerson, .. lessons.Take(room), .. current.Lessons.Where(l => !l.Active && !l.ByPerson).Take(Math.Max(0, 100 - byPerson.Count - room))],
            };
            await settings.SaveAsync(next, null, "Cookie's lessons summariser", $"From {rated.Count} ratings", ct);
            await ApplyRulesAsync(next, ct);
            await db.CookieDecisions.Where(d => d.Rating != null && !d.RatingRead).ExecuteUpdateAsync(u => u.SetProperty(d => d.RatingRead, true), ct);
            return lessons.Count;
        }
        finally
        {
            await p.SaveAsync(CancellationToken.None);
        }
    }
}

/// <summary>
/// Cookie's daily work (docs/cookie-plan.md §5, §11, §21.1), while Cookie runs: the lessons from new ratings, and Seraph's drift
/// report from the last day's decisions and a sample of Cookie's notes.
/// </summary>
public sealed class CookieDailyTask(IDbContextFactory<PnmtdDbContext> dbs, Lessons lessons, Seraph seraph, CookieSettings settings, PrivacyFilter privacy,
    IOptionsMonitor<CookieOptions> options, ILogger<CookieDailyTask> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        DateTime? lessonsAt = null;
        DateOnly? driftOn = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await dbs.CreateDbContextAsync(stoppingToken);
                var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(stoppingToken);
                if (options.CurrentValue.Enabled && runtime is { Running: true } && runtime.State != CookieState.Paused)
                {
                    if (await lessons.DueAsync(lessonsAt, stoppingToken))
                    {
                        await lessons.SummariseAsync(stoppingToken);
                        lessonsAt = Clock.UtcNow;
                    }
                    var today = DateOnly.FromDateTime(Clock.LocalNow);
                    if (driftOn != today && Clock.LocalNow.Hour >= 6)
                    {
                        await DriftAsync(stoppingToken);
                        driftOn = today;
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Cookie's daily work failed: {Error}", e.Message);
            }
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    /// <summary>Seraph's drift report (§5): the last day's decisions, and 1 in N of Cookie's notes.</summary>
    public async Task<CookieDecisionEntity?> DriftAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var since = Clock.UtcNow.AddDays(-1);
        var decisions = await db.CookieDecisions.AsNoTracking().Where(d => d.At >= since && d.Kind != "drift" && d.Kind != LoungeKinds.Email).OrderBy(d => d.Id)
            .Take(300).ToListAsync(ct);
        var every = Math.Max(1, (await settings.GetAsync<ModelSettings>(ct)).SeraphSampleEvery);
        var notes = (await db.CookieCaseEntries.AsNoTracking().Where(e => e.At >= since && e.Kind == "note").OrderBy(e => e.Id).ToListAsync(ct))
            .Where((_, i) => i % every == 0).Take(50).ToList();
        if (decisions.Count == 0 && notes.Count == 0)
        {
            return null;
        }
        var p = await privacy.OpenAsync(ct);
        try
        {
            var report = await seraph.DriftAsync(CookieBrain.Json(new
            {
                Decisions = decisions.Select(d => new { d.Kind, Title = p.Mask(d.Title), d.Outcome, d.Verdict, Reason = p.Mask(d.VerdictReason), d.Confidence, d.Rating }),
                Notes = notes.Select(n => p.Mask(InputSanitizer.Clean(n.Text, 400).Text)),
            }), ct);
            var entry = new CookieDecisionEntity
            {
                At = Clock.UtcNow, Kind = "drift", Title = "Seraph's drift report", Outcome = "noted",
                Detail = CaseBook.Cut(p.Unmask(report.Summary + (report.Points.Count == 0 ? "" : "\n- " + string.Join("\n- ", report.Points))), 1000),
                Proposal = "{}", Areas = [.. decisions.SelectMany(d => d.Areas).Distinct().Order()],
            };
            db.CookieDecisions.Add(entry);
            await db.SaveChangesAsync(ct);
            return entry;
        }
        finally
        {
            await p.SaveAsync(CancellationToken.None);
        }
    }
}
