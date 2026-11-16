using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>The personal summary (docs/cookie-prompts.md §6): at most 5 lines, each with its case.</summary>
public sealed record SummaryAnswer([property: Description("At most 5 lines, what needs the reader first")] IReadOnlyList<SummaryLine> Lines);

public sealed record SummaryLine(
    [property: Description("One line, at most 200 characters, without the case's id")] string Text,
    [property: Description("The case's id from the list, or empty")] string CaseId);

/// <summary>A summary line for the reader, placeholders restored.</summary>
public sealed record CookieSummaryLine(string Text, Guid? CaseId, bool NeedsYou);

public sealed record CookieSummaryItem(IReadOnlyList<CookieSummaryLine> Lines, string Source, DateTime At);

/// <summary>
/// The personal summary (docs/cookie-plan.md §8, §19.3): written when the reader opens the dashboard and kept for 15 minutes,
/// from the cases both the reader and Cookie may see. What needs the reader comes first: tickets assigned to them (by their
/// contact's Redmine name), and what recipient rules send them. Without Cookie running, code writes the lines.
/// </summary>
public sealed partial class Summaries(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, CookieModel model, CookieBrain brain,
    PrivacyFilter privacy, CookieRound round, IMemoryCache cache, IOptionsMonitor<CookieOptions> options, IOptionsMonitor<AiOptions> ai,
    PNMTD.Availability.AvailabilityPlan plan, ILogger<Summaries> logger)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(15);
    public const int MaxCases = 30;

    public async Task<CookieSummaryItem> ForAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.UserId() ?? Guid.Empty;
        var key = $"cookie-summary:{userId}";
        if (cache.TryGetValue(key, out CookieSummaryItem? cached) && cached is not null)
        {
            return cached;
        }
        var lastVisit = cache.TryGetValue($"{key}:visit", out DateTime visit) ? visit : (DateTime?)null;
        cache.Set($"{key}:visit", Clock.UtcNow, TimeSpan.FromDays(7));
        var summary = await WriteAsync(user, userId, lastVisit, ct);
        // Cookie's is kept 15 minutes (§8); the code's only a minute, so that a summary written before the case book ran doesn't stay.
        cache.Set(key, summary, summary.Source == "cookie" ? KeepFor : TimeSpan.FromMinutes(1));
        return summary;
    }

    private async Task<CookieSummaryItem> WriteAsync(ClaimsPrincipal user, Guid userId, DateTime? lastVisit, CancellationToken ct)
    {
        var reader = user.Rights();
        var cookie = await round.RightsAsync(ct);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var open = (await db.CookieCases.AsNoTracking().Where(c => c.Open && c.Kind != CookieCaseKind.Request).ToListAsync(ct))
            .Where(c => CookieAccess.CanSee(reader, c.Areas)).ToList();
        // "Needs you" (§8.5): the tickets assigned to the reader, and the open problems while the reader is responsible today.
        var redmineName = (await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.RedmineName).FirstOrDefaultAsync(ct))?.Trim();
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var responsible = (await plan.DayAsync(today, ct)).ResponsibleOn(today).UserIds.Contains(userId);
        bool NeedsYou(CookieCaseEntity c) => c.StoppedAt is null
            && ((redmineName is { Length: > 0 } && string.Equals(c.AssigneeName?.Trim(), redmineName, StringComparison.OrdinalIgnoreCase))
                || (responsible && c.Kind == CookieCaseKind.Problem));
        var ordered = open.OrderByDescending(NeedsYou).ThenByDescending(c => c.Importance).ThenByDescending(c => c.ChangedAt).Take(MaxCases).ToList();

        var running = (await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(ct))?.Running == true;
        if (running && options.CurrentValue.Enabled && ai.CurrentValue.Usable && cookie is not null && ordered.Count > 0)
        {
            try
            {
                return await ByCookieAsync(user, userId, reader, cookie, ordered, NeedsYou, lastVisit, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Cookie couldn't write the summary: {Error}", e.Message);
            }
        }
        return ByCode(ordered, NeedsYou);
    }

    private async Task<CookieSummaryItem> ByCookieAsync(ClaimsPrincipal user, Guid userId, UserRights reader, UserRights cookie,
        List<CookieCaseEntity> ordered, Func<CookieCaseEntity, bool> needsYou, DateTime? lastVisit, CancellationToken ct)
    {
        // Only what both may see: Cookie writes with its group's rights, for a reader with theirs (§10.2).
        var cases = ordered.Where(c => CookieAccess.CanSee(cookie, c.Areas)).ToList();
        if (cases.Count == 0)
        {
            return ByCode(ordered, needsYou);
        }
        var p = await privacy.OpenAsync(ct);
        try
        {
            var values = await brain.ValuesAsync(ct);
            values["reader"] = p.Mask(user.Identity?.Name ?? "the reader");
            values["last_visit"] = lastVisit is { } v ? Clock.ToLocal(v).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "a while ago";
            values["reader_profile"] = $"May see: {string.Join(", ", RightsCatalog.Areas.Where(a => reader.Levels.ContainsKey(a.Area) || reader.IsAdmin).Select(a => a.Label))}.";
            values["cases"] = CookieBrain.Json(cases.Select(c => new
            {
                c.Id, c.Kind, Title = p.Mask(c.Title), c.Importance, c.OpenedAt, LastChange = c.ChangedAt, NeedsYou = needsYou(c),
                AssignedTo = p.Mask(c.AssigneeName), CookiesNote = p.Mask(c.CookieNote),
            }));
            var system = await brain.BaseAsync(values, ct);
            var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = CookiePrompts.Fill(await brain.PromptAsync("summary", ct), values) });
            var models = await settings.GetAsync<ModelSettings>(ct);
            var turn = await model.CompleteAsync("summary", messages, null, (nameof(SummaryAnswer), AiJson.SchemaFor<SummaryAnswer>()), models.TriageEffort, null,
                options.CurrentValue.Tokens.Summary, ct);
            var answer = JsonSerializer.Deserialize<SummaryAnswer>(turn.Content ?? "", AiJson.Options)
                ?? throw new AiRequestException(AiOutcome.InvalidOutput, "The summary is empty.");
            var byId = cases.ToDictionary(c => c.Id.ToString(), StringComparer.OrdinalIgnoreCase);
            var lines = answer.Lines.Take(5).Select(l =>
            {
                var id = CaseLink().Match(l.Text) is { Success: true } m ? m.Groups[1].Value : l.CaseId;
                var c = byId.GetValueOrDefault(id.Trim());
                return new CookieSummaryLine(CaseBook.Cut(p.Unmask(CaseLink().Replace(l.Text, "").Trim()), 300)!, c?.Id, c is not null && needsYou(c));
            }).Where(l => l.Text.Length > 0).ToList();
            await using var db = await dbs.CreateDbContextAsync(ct);
            db.CookieTrace.Add(new CookieTraceEntity
            {
                At = Clock.UtcNow, Kind = "summary", Title = $"Summary for {values["reader"]}", Body = string.Join("\n", answer.Lines.Select(l => l.Text)),
                Areas = [.. cases.SelectMany(c => c.Areas).Distinct().Order()],
            });
            await db.SaveChangesAsync(ct);
            return new CookieSummaryItem(lines, "cookie", Clock.UtcNow);
        }
        finally
        {
            await p.SaveAsync(CancellationToken.None);
        }
    }

    /// <summary>The code's lines: what needs the reader, then the most important, at most 5.</summary>
    public static CookieSummaryItem ByCode(IReadOnlyList<CookieCaseEntity> ordered, Func<CookieCaseEntity, bool> needsYou)
    {
        if (ordered.Count == 0)
        {
            return new CookieSummaryItem([new CookieSummaryLine("Nothing needs you, and nothing is open that you can see.", null, false)], "code", Clock.UtcNow);
        }
        var lines = ordered.Take(5).Select(c => new CookieSummaryLine(needsYou(c) ? $"Needs you: {c.Title}" : c.Title, c.Id, needsYou(c))).ToList();
        if (ordered.Count > 5)
        {
            lines.Add(new CookieSummaryLine($"{ordered.Count - 5} more open cases.", null, false));
        }
        return new CookieSummaryItem(lines, "code", Clock.UtcNow);
    }

    [GeneratedRegex(@"\[case:([0-9a-fA-F-]{36})\]")]
    private static partial Regex CaseLink();
}
