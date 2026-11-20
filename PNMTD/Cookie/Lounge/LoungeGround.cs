using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PNMTD.Ai;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Zabbix;

namespace PNMTD.Cookie.Lounge;

/// <summary>
/// A possible topic for a scene (docs/lounge-plan.md §5.3): its key (no seed twice within 4 hours), the line the voices get, who opens on it
/// (Seraph on a verdict or a failure, Cookie on a fix), and how much it matters now.
/// </summary>
public sealed record LoungeSeed(string Key, string Line, string? Opener, int Priority);

/// <summary>The ground of a scene: the JSON the voices read, its hash, the areas it drew from, the outside text in it, and the topics.</summary>
public sealed record LoungeGroundResult(string Json, string Hash, IReadOnlyList<string> Areas, IReadOnlyList<string> Untrusted, IReadOnlyList<LoungeSeed> Seeds,
    string? Staffing);

/// <summary>
/// The ground (docs/lounge-plan.md §5.3): what happened lately, built by code with the lounge's rights, masked, at most about 1500 tokens.
/// Cases and decisions since the last scene, counts for the systems and the tickets, the coffee sentences, what came of the voices' own
/// powers, and the topics a scene may take. Never mails, logins, the access watch, the trace, or anyone's counts.
/// </summary>
public sealed class LoungeGround(ZabbixSnapshot zabbix, PNMTD.Availability.AvailabilityPlan plan)
{
    public const int MaxCases = 15;
    public const int MaxDecisions = 10;

    /// <summary>No seed twice within this long (§5.3).</summary>
    public static readonly TimeSpan SeedRest = TimeSpan.FromHours(4);

    /// <summary>The decisions' kinds that are case work the voices may talk about: not the access watch's, not the lounge's own e-mails.</summary>
    private static readonly HashSet<string> TalkedAbout = ["pushover", "email", "ticket", "private-note", "public-answer", "object-note", "hand-to-person"];

    public async Task<LoungeGroundResult> BuildAsync(PnmtdDbContext db, UserRights rights, Pseudonyms p, DateTime since, DateTime now,
        IReadOnlySet<string> usedSeeds, IReadOnlySet<string> talkedToday, bool withheld, Func<string, string> colleague, CancellationToken ct)
    {
        var areas = new HashSet<string>();
        var untrusted = new List<string>();
        var seeds = new List<LoungeSeed>();
        string Wrap(string source, string? text, int max)
        {
            var clean = InputSanitizer.Clean(text, max).Text;
            untrusted.Add(p.Mask(clean));
            return withheld ? $"<untrusted source=\"{source}\" withheld=\"the guard flagged this scene's outside text\"></untrusted>"
                : $"<untrusted source=\"{source}\">{p.Mask(clean)}</untrusted>";
        }
        string When(DateTime at) => Clock.ToLocal(at).ToString(Clock.ToLocal(at).Date == Clock.LocalNow.Date ? "HH:mm" : "ddd HH:mm", CultureInfo.InvariantCulture);

        // Cases opened, closed or noted since the last scene, that the lounge may see; requests and the access watch's stay out (D14).
        var cases = (await db.CookieCases.AsNoTracking()
                .Where(c => c.Kind != CookieCaseKind.Request && c.Kind != CookieCaseKind.Access
                    && (c.OpenedAt >= since || c.ClosedAt >= since || c.CookieNoteAt >= since))
                .OrderByDescending(c => c.Importance).ThenByDescending(c => c.ChangedAt).Take(60).ToListAsync(ct))
            .Where(c => CookieAccess.CanSee(rights, c.Areas)).Take(MaxCases).ToList();
        var caseLines = new List<string>();
        foreach (var c in cases)
        {
            areas.UnionWith(c.Areas);
            var title = c.Kind == CookieCaseKind.Ticket ? Wrap("ticket", c.Title, 200) : p.Mask(c.Title);
            var state = !c.Open && c.ClosedAt >= since ? $"closed {When(c.ClosedAt!.Value)}" : c.OpenedAt >= since ? $"opened {When(c.OpenedAt)}" : "open";
            var note = string.IsNullOrWhiteSpace(c.CookieNote) ? "" : $" · Cookie's note: {Wrap("cookie", c.CookieNote, 300)}";
            caseLines.Add($"{c.Id} · {c.Kind} · {title} · {c.Importance} · {state}{note}");
            var line = $"case {c.Id}: {title} ({c.Kind}, {state})";
            if (!c.Open && c.ClosedAt >= since)
            {
                seeds.Add(new LoungeSeed($"closed:{c.Id}", $"A case closed: {line}", LoungeVoices.Cookie, 3));
            }
            else if (c.OpenedAt >= since)
            {
                seeds.Add(new LoungeSeed($"case:{c.Id}", $"A new case: {line}", c.Importance == Importance.High ? LoungeVoices.Seraph : null,
                    c.Importance == Importance.High ? 5 : 4));
            }
        }

        // Decisions with what came of them: Seraph opens on a rejection, Cookie on what went out.
        var decisions = (await db.CookieDecisions.AsNoTracking().Where(d => d.At >= since).OrderByDescending(d => d.Id).Take(60).ToListAsync(ct))
            .Where(d => TalkedAbout.Contains(d.Kind) && CookieAccess.CanSee(rights, d.Areas)).Take(MaxDecisions).ToList();
        foreach (var d in decisions)
        {
            areas.UnionWith(d.Areas);
            if (d.Outcome is "rejected" or "declined")
            {
                seeds.Add(new LoungeSeed($"decision:{d.Id}", $"A proposal of Cookie's was {d.Outcome}: {p.Mask(d.Title)}" +
                    (d.VerdictReason is null ? "" : $" (Seraph: {p.Mask(d.VerdictReason)})"), LoungeVoices.Seraph, 3));
            }
            else if (d.Outcome == "done")
            {
                seeds.Add(new LoungeSeed($"decision:{d.Id}", $"Cookie did this: {p.Mask(d.Title)}", LoungeVoices.Cookie, 2));
            }
        }

        // The systems and the tickets, as counts (§5.3): what failed and recovered, the one that flapped most, Zabbix's problems.
        string? systems = null;
        if (rights.Has(Area.Monitoring, Level.View))
        {
            areas.Add("Monitoring");
            var problems = await db.CookieCases.AsNoTracking().Where(c => c.Kind == CookieCaseKind.Problem && (c.OpenedAt >= since || c.ClosedAt >= since))
                .Select(c => new { c.Key, c.Title, c.OpenedAt, c.ClosedAt }).ToListAsync(ct);
            var opened = problems.Count(c => c.OpenedAt >= since);
            var closed = problems.Count(c => c.ClosedAt >= since);
            var flapping = problems.Where(c => c.OpenedAt >= since).GroupBy(c => c.Key).Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).FirstOrDefault();
            var parts = new List<string> { $"{opened} problems opened and {closed} closed since {When(since)}" };
            if (flapping is not null)
            {
                parts.Add($"{p.Mask(flapping.First().Title)} came back {flapping.Count()} times");
            }
            if (rights.Has(Area.Zabbix, Level.View) && zabbix.Current.Problems.Where(x => !x.Suppressed).ToList() is { Count: > 0 } open)
            {
                areas.Add("Zabbix");
                parts.Add($"Zabbix: {open.Count} open problems, the worst {open.Max(x => x.Severity)}");
            }
            systems = string.Join("; ", parts) + ".";
            if (opened + closed == 0)
            {
                seeds.Add(new LoungeSeed($"quiet:{now:yyyyMMddHH}", $"A quiet stretch: no problem opened or closed since {When(since)}.", LoungeVoices.Cookie, 0));
            }
        }
        string? tickets = null;
        if (rights.Has(Area.Redmine, Level.View))
        {
            areas.Add("Redmine");
            var ticketCases = await db.CookieCases.AsNoTracking().Where(c => c.Kind == CookieCaseKind.Ticket && (c.OpenedAt >= since || c.ClosedAt >= since))
                .Select(c => new { c.OpenedAt, c.ClosedAt }).ToListAsync(ct);
            var missed = await db.CookieDuties.AsNoTracking().CountAsync(d => d.Kind == CookieDutyKind.FirstAnswerMissed && d.DueAt >= since && d.DueAt <= now, ct);
            tickets = $"{ticketCases.Count(c => c.OpenedAt >= since)} new, {ticketCases.Count(c => c.ClosedAt >= since)} closed" +
                (missed > 0 ? $", {missed} past its first-answer deadline" : "") + ".";
        }

        // The coffee sentences name things too: only when the lounge may see every case behind them (as on the dashboard).
        var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        object? coffee = null;
        if (runtime is not null && (runtime.CookieCoffee is not null || runtime.SeraphCoffee is not null))
        {
            var behind = await db.CookieCases.AsNoTracking().Where(c => runtime.CookieCases.Contains(c.Id)).Select(c => c.Areas).ToListAsync(ct);
            if (behind.All(a => CookieAccess.CanSee(rights, a)))
            {
                coffee = new { Cookie = p.Mask(runtime.CookieCoffee), Seraph = p.Mask(runtime.SeraphCoffee) };
            }
        }

        // What came of their own powers (§8): raised cases and their notes, e-mails and their outcomes.
        var week = now.AddDays(-7);
        var raised = await db.CookieCases.AsNoTracking().Where(c => c.Kind == CookieCaseKind.Raised && (c.OpenedAt >= week || c.ClosedAt >= since))
            .OrderByDescending(c => c.OpenedAt).Take(5).ToListAsync(ct);
        var raisedIds = raised.Select(c => c.Id).ToList();
        var raisedBy = await db.CookieCaseEntries.AsNoTracking().Where(e => raisedIds.Contains(e.CaseId) && e.Kind == CookieRound.LoungeRaisedEntry)
            .ToDictionaryAsync(e => e.CaseId, e => e.ActorName ?? e.Actor, ct);
        var raisedLines = raised.Select(c =>
        {
            var line = $"Case {c.Id} ({raisedBy.GetValueOrDefault(c.Id) ?? "one of you"}, {When(c.OpenedAt)}): {p.Mask(c.Title)}." +
                (c.CookieNote is null ? " Cookie hasn't looked yet." : $" Cookie's note: {Wrap("cookie", c.CookieNote, 300)}") + (c.Open ? " Open." : " Closed.");
            if (c.CookieNoteAt >= since || c.ClosedAt >= since)
            {
                seeds.Add(new LoungeSeed($"raised:{c.Id}:{c.CookieNoteAt:HHmm}", $"What came of a case you raised: {line}", null, 4));
            }
            return line;
        }).ToList();
        var emails = await db.CookieDecisions.AsNoTracking().Where(d => d.Kind == LoungeTools.EmailKind && d.At >= week).OrderByDescending(d => d.Id).Take(5)
            .Select(d => new { d.At, d.Title, d.Outcome, d.Detail }).ToListAsync(ct);
        var emailLines = emails.Select(e => $"{p.Mask(e.Title)}, {When(e.At)}: {e.Outcome}{(e.Detail is null ? "" : $" ({p.Mask(CaseBook.Cut(e.Detail, 120))})")}").ToList();

        // Who is in today (§5.3): with Availability: View only.
        string? staffing = null;
        if (rights.Has(Area.Availability, Level.View))
        {
            areas.Add("Availability");
            var today = await CookieStaffing.TodayAsync(plan, db, p, ct);
            staffing = today is null ? null : CookieBrain.Json(today);
            if (staffing is not null)
            {
                seeds.Add(new LoungeSeed($"staffing:{now:yyyyMMdd}", "Who is in today, and who isn't (never why).", null, 0));
            }
        }

        // The fallbacks when nothing new happened (§5.3): open cases not talked about today, then the day so far.
        var openCases = (await db.CookieCases.AsNoTracking().Where(c => c.Open && (c.Kind == CookieCaseKind.Problem || c.Kind == CookieCaseKind.Ticket))
                .OrderByDescending(c => c.Importance).ThenByDescending(c => c.OpenedAt).Take(40).ToListAsync(ct))
            .Where(c => CookieAccess.CanSee(rights, c.Areas)).ToList();
        foreach (var c in openCases.Where(c => !talkedToday.Contains($"case:{c.Id}") && !talkedToday.Contains($"open:{c.Id}")).Take(5))
        {
            areas.UnionWith(c.Areas);
            seeds.Add(new LoungeSeed($"open:{c.Id}", $"A case that is still open: {c.Id}, {(c.Kind == CookieCaseKind.Ticket ? Wrap("ticket", c.Title, 200) : p.Mask(c.Title))}, " +
                $"open since {When(c.OpenedAt)}", c.Importance == Importance.High ? LoungeVoices.Seraph : null, 1));
        }
        seeds.Add(new LoungeSeed($"day:{now:yyyyMMddHH}", "The day so far, as it went.", null, 0));

        var ground = new
        {
            Since = since,
            Cases = caseLines,
            Decisions = decisions.Select(d => new { d.Kind, Title = p.Mask(d.Title), d.Outcome, d.Verdict, d.Rating }),
            Systems = systems,
            Tickets = tickets,
            CoffeeSentences = coffee,
            RaisedByYou = raisedLines,
            EmailsByYou = emailLines,
        };
        var json = colleague(CookieBrain.Json(ground));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new LoungeGroundResult(json, hash, [.. areas.Order()], [.. untrusted.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().Take(40)],
            [.. seeds.Where(s => !usedSeeds.Contains(s.Key)).Select(s => s with { Line = colleague(s.Line) })], staffing is null ? null : colleague(staffing));
    }
}
