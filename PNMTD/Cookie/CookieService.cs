using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Cookie;

/// <summary>
/// Who may see what Cookie writes (docs/cookie-plan.md §9.2): a case, a note or a summary carries the areas its sources came
/// from, and only a reader with every one of them sees it.
/// </summary>
public static class CookieAccess
{
    /// <summary>An access case's servers' and routers' logins (docs/cookie-access-plan.md §9): names and addresses, <i>Monitoring: Details</i>.</summary>
    public const string LoginsArea = "Logins";

    /// <summary>An access case's PNMT sign-ins: the audit log's, <i>Audit log: View</i>.</summary>
    public const string AuditLogArea = "AuditLog";

    public static (Area Area, Level Level)? RightOf(string area) => area switch
    {
        LoginsArea => (Area.Monitoring, Level.Details),
        AuditLogArea => (Area.AuditLog, Level.View),
        "Monitoring" => (Area.Monitoring, Level.View),
        "Zabbix" => (Area.Zabbix, Level.View),
        "Redmine" => (Area.Redmine, Level.View),
        "Mail" => (Area.Mail, Level.Read),
        "KnowledgeBase" => (Area.KnowledgeBase, Level.View),
        "Locations" => (Area.Locations, Level.View),
        "Availability" => (Area.Availability, Level.View),
        _ => null,
    };

    public static bool CanSee(UserRights rights, IEnumerable<string> areas) =>
        areas.All(a => RightOf(a) is { } r ? rights.Has(r.Area, r.Level) : rights.IsAdmin);

    public static bool CanSee(UserRights rights, Guid? userId, CookieCaseEntity c) => c.Kind == CookieCaseKind.Request
        ? c.UserId == userId || rights.Has(Area.Cookie, Level.Manage)
        : CanSee(rights, c.Areas);
}

/// <summary>Cookie's API (docs/cookie-plan.md §19.4): the status, the cases, and Admin → Cookie.</summary>
public sealed class CookieService(PnmtdDbContext db, CookieSettings settings, IOptionsMonitor<CookieOptions> options, IOptionsMonitor<AiOptions> ai,
    IOptionsMonitor<RedmineOptions> redmineOptions, IServiceProvider services)
{
    /// <summary>Cookie's status counts while it is this fresh; older, the code's is shown (§8).</summary>
    public static readonly TimeSpan CookieStatusFor = TimeSpan.FromMinutes(30);

    public async Task<CookieStatusItem> StatusAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(ct) ?? new CookieRuntimeEntity();
        var useCookie = runtime.Running && runtime.CookieStatus is not null && runtime.CookieStatusAt > Clock.UtcNow - CookieStatusFor;
        var (state, reason, ids, at, source) = useCookie
            ? (runtime.CookieStatus!.Value, runtime.CookieReason ?? "", runtime.CookieCases, runtime.CookieStatusAt, "cookie")
            : (runtime.CodeStatus, runtime.CodeReason, runtime.CodeCases, runtime.CodeStatusAt, "code");
        var rights = user.Rights();
        var cases = await db.CookieCases.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        var visible = cases.Where(c => CookieAccess.CanSee(rights, user.UserId(), c)).ToList();
        var seesAll = visible.Count == cases.Count;
        if (!seesAll)
        {
            // The reason names hosts and tickets: only for those who may see them all.
            reason = state == OverallState.AllFine ? reason : $"{cases.Count - visible.Count} of the cases behind this are outside your rights.";
        }
        // The coffee sentences (docs/adjustments1.md) name things too: the same rule, and only while Cookie's status is current.
        var cookieSays = useCookie && (seesAll || state == OverallState.AllFine) && !string.IsNullOrWhiteSpace(runtime.CookieCoffee)
            ? new CookieSaysItem(runtime.CookieCoffee, runtime.CookieStatusAt!.Value) : null;
        var seraphSays = useCookie && (seesAll || state == OverallState.AllFine) && !string.IsNullOrWhiteSpace(runtime.SeraphCoffee)
            && runtime.SeraphCoffeeAt is { } seraphAt && Clock.UtcNow - seraphAt < TimeSpan.FromHours(6)
            ? new CookieSaysItem(runtime.SeraphCoffee, seraphAt) : null;
        var o = options.CurrentValue;
        var overall = new OverallStateItem(state, reason, source, at,
            visible.OrderBy(c => ids.IndexOf(c.Id)).Select(c => new CookieCaseRef(c.Id, c.Title)).ToList());
        return new CookieStatusItem(overall, new CookieStateItem(o.Enabled, runtime.Running, o.Enabled ? runtime.State : CookieState.Stopped,
            o.Enabled ? runtime.StateReason : "Cookie__Enabled is not set.", runtime.LastRoundAt, runtime.NextRoundAt), cookieSays, seraphSays);
    }

    public async Task<Paged<CookieCaseItem>> CasesAsync(CookieCaseQuery query, ClaimsPrincipal user, CancellationToken ct)
    {
        var size = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        IQueryable<CookieCaseEntity> q = db.CookieCases.AsNoTracking();
        if (query.Open is { } open)
        {
            q = q.Where(c => c.Open == open);
        }
        if (query.Kind is { } kind)
        {
            q = q.Where(c => c.Kind == kind);
        }
        // Rights are filtered in code: few cases are open at a time, and closed ones are read a page at a time.
        var all = await q.OrderByDescending(c => c.Open).ThenByDescending(c => c.Importance).ThenByDescending(c => c.ChangedAt).Take(2000).ToListAsync(ct);
        var rights = user.Rights();
        var visible = all.Where(c => CookieAccess.CanSee(rights, user.UserId(), c)).ToList();
        var items = visible.Skip((page - 1) * size).Take(size).ToList();
        var next = await NextDutiesAsync(items.Select(c => c.Id).ToList(), ct);
        var names = await NamesAsync(items, ct);
        return new Paged<CookieCaseItem>(items.Select(c => Item(c, next.GetValueOrDefault(c.Id), names)).ToList(), page, size, visible.Count);
    }

    public async Task<CookieCaseDetail> CaseAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await db.CookieCases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !CookieAccess.CanSee(user.Rights(), user.UserId(), c))
        {
            throw ApiException.NotFound("Case");
        }
        var entries = await db.CookieCaseEntries.AsNoTracking().Where(e => e.CaseId == id).OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Take(500).Select(e => new CookieEntryItem(e.At, e.Kind, e.Text, e.Actor, e.ActorName)).ToListAsync(ct);
        var duties = await db.CookieDuties.AsNoTracking().Where(d => d.CaseId == id).OrderBy(d => d.DueAt)
            .Select(d => new CookieDutyItem(d.Id, d.Kind, d.Key, d.DueAt, d.State, d.DoneAt, d.Outcome)).ToListAsync(ct);
        var members = JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json) ?? [];
        var next = duties.Where(d => d.State == CookieDutyState.Due).OrderBy(d => d.DueAt).FirstOrDefault();
        var o = redmineOptions.CurrentValue;
        string? Url(int? issue) => issue is { } i && o.Enabled ? $"{o.BrowserUrl}/issues/{i}" : null;
        var ticket = c.Kind == CookieCaseKind.Ticket && c.IssueId is { } issueId
            ? new CookieTicketClock(issueId, c.AuthorName, c.AuthorIsStaff, c.AssigneeName, c.StatusName, c.TicketCreatedAt, c.FirstAnswerAt,
                c.LastStaffUpdateAt, c.OwnIssueId, Url(issueId))
            : null;
        var rights = user.Rights();
        return new CookieCaseDetail(Item(c, next is null ? null : (next.DueAt, next.Kind), await NamesAsync([c], ct)), members, entries, duties, ticket,
            Url(c.OwnIssueId), c.Open && rights.Has(Area.Cookie, Level.Use), c.Open && c.Kind != CookieCaseKind.Request && rights.Has(Area.Cookie, Level.Instruct),
            c.Solution is null || !CookieAccess.CanSee(rights, c.SolutionAreas) ? null : new CookieSolutionItem(c.Solution, c.SolutionAt ?? c.ChangedAt, c.SolutionModel),
            c.BigBrotherAskedAt,
            c.Open && c.Kind != CookieCaseKind.Request && c.StoppedAt is null && rights.Has(Area.Cookie, Level.Instruct));
    }

    /// <summary><i>Ask the big brother</i> (§15.2): the next round calls it for the case, within its limits and budget.</summary>
    public async Task AskBigBrotherAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await OpenCaseAsync(id, user, ct);
        if (c.Kind == CookieCaseKind.Request || c.StoppedAt is not null)
        {
            throw ApiException.Conflict("cookie_case_not_for_big_brother", "Not for a person's request or a stopped case.");
        }
        if (!(await settings.GetAsync<ActionSettings>(ct)).BigBrother)
        {
            throw ApiException.Conflict("cookie_big_brother_off", "The big brother is off on Admin → Cookie → Actions.");
        }
        var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        if (!options.CurrentValue.Enabled || runtime is not { Running: true })
        {
            throw ApiException.Conflict("cookie_stopped", "Cookie is stopped: an admin starts it on Admin → Cookie.");
        }
        c.BigBrotherAskedAt = Clock.UtcNow;
        c.BigBrotherAskedBy = CaseBook.Cut(user.Identity?.Name ?? "someone", 200);
        c.ChangedAt = Clock.UtcNow;
        CaseBook.Entry(db, c.Id, Clock.UtcNow, "asked", "Asked the big brother.", "person", user.Identity?.Name);
        await db.SaveChangesAsync(ct);
        services.GetRequiredService<CookieSignals>().Raise("the big brother asked");
        AuditLog.Summary($"Asked the big brother about \"{c.Title}\"");
    }

    // --- The decision log and ratings (§11, §21.1) ----------------------------------------------------------------------

    public async Task<Paged<CookieDecisionItem>> DecisionsAsync(CookieDecisionQuery query, ClaimsPrincipal user, CancellationToken ct)
    {
        var rights = user.Rights();
        var q = db.CookieDecisions.AsNoTracking();
        if (query.CaseId is { } caseId)
        {
            q = q.Where(d => d.CaseId == caseId);
        }
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            q = q.Where(d => d.Kind == query.Kind);
        }
        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            q = q.Where(d => d.Outcome == query.Outcome);
        }
        // Rights by the areas of the sources (§9.2): filtered after reading, a window of the newest.
        var rows = (await q.OrderByDescending(d => d.Id).Take(2000).ToListAsync(ct)).Where(d => CookieAccess.CanSee(rights, d.Areas)).ToList();
        var size = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        var items = rows.Skip((page - 1) * size).Take(size).ToList();
        var caseIds = items.Where(d => d.CaseId is not null).Select(d => d.CaseId!.Value).Distinct().ToList();
        var titles = await db.CookieCases.AsNoTracking().Where(c => caseIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Title, ct);
        var privacy = services.GetRequiredService<PrivacyFilter>();
        await privacy.EnsureLoadedAsync(ct);
        var canRate = rights.Has(Area.Cookie, Level.Use);
        return new Paged<CookieDecisionItem>([.. items.Select(d =>
        {
            ProposedAction? a = null;
            try
            {
                a = JsonSerializer.Deserialize<CookieProposal>(d.Proposal, AiJson.Options)?.Action;
            }
            catch (JsonException)
            {
            }
            return new CookieDecisionItem(d.Id, d.At, d.CaseId, d.CaseId is { } id ? titles.GetValueOrDefault(id) : null, d.Kind, d.Title,
                a is null ? null : privacy.Unmask(a.Text), a is null ? null : privacy.Unmask(a.Reason), d.Confidence, d.Verdict, privacy.Unmask(d.VerdictReason),
                d.Outcome, privacy.Unmask(d.Detail), d.Model, d.InboxDecisionId, d.Rating, d.RatingComment, d.RatedByName, canRate && d.Kind != "drift", d.OrderedBy,
                d.VerdictConfidence);
        })], page, size, rows.Count);
    }

    /// <summary><i>Right</i> or <i>Wrong</i> with a comment (§11), by whoever may see it; null takes it back.</summary>
    public async Task<CookieDecisionItem> RateAsync(long id, CookieRatingRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var d = await db.CookieDecisions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (d is null || !CookieAccess.CanSee(user.Rights(), d.Areas) || d.Kind == "drift")
        {
            throw ApiException.NotFound("Decision");
        }
        var rating = request.Rating?.Trim().ToLowerInvariant();
        new ApiValidator().Check(rating is null or "right" or "wrong", "rating", "right, wrong, or nothing.")
            .Check((request.Comment ?? "").Length <= 1000, "comment", "At most 1,000 characters.").ThrowIfInvalid();
        d.Rating = rating;
        d.RatingComment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        d.RatedById = rating is null ? null : user.UserId();
        d.RatedByName = rating is null ? null : CaseBook.Cut(user.Identity?.Name, 200);
        d.RatedAt = rating is null ? null : Clock.UtcNow;
        d.RatingRead = false;
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"Rated \"{d.Title}\": {rating ?? "taken back"}{(d.RatingComment is null ? "" : $": {d.RatingComment}")}");
        var privacy = services.GetRequiredService<PrivacyFilter>();
        await privacy.EnsureLoadedAsync(ct);
        ProposedAction? a = null;
        try
        {
            a = JsonSerializer.Deserialize<CookieProposal>(d.Proposal, AiJson.Options)?.Action;
        }
        catch (JsonException)
        {
        }
        var caseTitle = d.CaseId is { } caseId ? await db.CookieCases.AsNoTracking().Where(c => c.Id == caseId).Select(c => c.Title).FirstOrDefaultAsync(ct) : null;
        return new CookieDecisionItem(d.Id, d.At, d.CaseId, caseTitle, d.Kind, d.Title, a is null ? null : privacy.Unmask(a.Text), a is null ? null : privacy.Unmask(a.Reason),
            d.Confidence, d.Verdict, privacy.Unmask(d.VerdictReason), d.Outcome, privacy.Unmask(d.Detail), d.Model, d.InboxDecisionId, d.Rating, d.RatingComment,
            d.RatedByName, true, d.OrderedBy, d.VerdictConfidence);
    }

    /// <summary>A person's comment on a case (§4.3): in its timeline, and in Cookie's next round, screened.</summary>
    public async Task CommentAsync(Guid id, CookieCaseCommentRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await OpenCaseAsync(id, user, ct);
        var text = CommentText(request.Text, required: true);
        CaseBook.Entry(db, c.Id, Clock.UtcNow, "comment", text, "person", user.Identity?.Name);
        c.ChangedAt = Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        services.GetRequiredService<CookieSignals>().Raise("a comment on a case");
        AuditLog.Summary($"A comment on \"{c.Title}\"");
    }

    /// <summary>
    /// <i>Don't go on with this</i> (§4.3): Cookie leaves the case alone, its due duties close as stopped, nothing new falls due.
    /// The comment says why.
    /// </summary>
    public async Task StopAsync(Guid id, CookieCaseCommentRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await OpenCaseAsync(id, user, ct);
        if (c.Kind == CookieCaseKind.Request)
        {
            throw ApiException.Conflict("cookie_case_request", "A person's request can't be stopped.");
        }
        if (c.StoppedAt is not null)
        {
            throw ApiException.Conflict("cookie_case_stopped", "The case is stopped already.");
        }
        var text = CommentText(request.Text, required: true);
        var now = Clock.UtcNow;
        var name = CaseBook.Cut(user.Identity?.Name ?? "someone", 200);
        c.StoppedAt = now;
        c.StoppedMembers = [.. (JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json) ?? []).Select(m => m.Ref)];
        c.StoppedById = user.UserId();
        c.StoppedByName = name;
        c.StopComment = text;
        c.ChangedAt = now;
        foreach (var duty in await db.CookieDuties.Where(d => d.CaseId == c.Id && d.State == CookieDutyState.Due).ToListAsync(ct))
        {
            duty.State = CookieDutyState.Stopped;
            duty.Outcome = $"Stopped with its case by {name}.";
            duty.DoneAt = now;
        }
        CaseBook.Entry(db, c.Id, now, "stopped", $"Cookie doesn't go on with it: {text}", "person", name);
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"Stopped \"{c.Title}\": {text}");
    }

    /// <summary><i>Go on</i>: Cookie works the case again, and its duties are planned again.</summary>
    public async Task GoOnAsync(Guid id, CookieCaseCommentRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await OpenCaseAsync(id, user, ct);
        if (c.StoppedAt is null)
        {
            throw ApiException.Conflict("cookie_case_not_stopped", "The case isn't stopped.");
        }
        var text = CommentText(request.Text, required: false);
        var now = Clock.UtcNow;
        c.StoppedAt = null;
        c.StoppedById = null;
        c.StoppedByName = null;
        c.StopComment = null;
        c.StoppedMembers = [];
        c.ChangedAt = now;
        await db.CookieDuties.Where(d => d.CaseId == c.Id && d.State == CookieDutyState.Stopped).ExecuteDeleteAsync(ct);
        CaseBook.Entry(db, c.Id, now, "go-on", text is null ? "Cookie goes on with it." : $"Cookie goes on with it: {text}", "person", user.Identity?.Name);
        await db.SaveChangesAsync(ct);
        services.GetRequiredService<CookieSignals>().Raise("a case goes on");
        AuditLog.Summary($"Cookie goes on with \"{c.Title}\"{(text is null ? "" : ": " + text)}");
    }

    private async Task<CookieCaseEntity> OpenCaseAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await db.CookieCases.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !CookieAccess.CanSee(user.Rights(), user.UserId(), c))
        {
            throw ApiException.NotFound("Case");
        }
        if (!c.Open)
        {
            throw ApiException.Conflict("cookie_case_closed", "The case is closed.");
        }
        return c;
    }

    private static string? CommentText(string? text, bool required)
    {
        var t = text?.Trim() ?? "";
        if (required && t.Length == 0)
        {
            throw ApiException.Invalid("text", "Say why.");
        }
        if (t.Length > 1000)
        {
            throw ApiException.Invalid("text", "At most 1,000 characters.");
        }
        return t.Length == 0 ? null : t;
    }

    private async Task<Dictionary<Guid, (DateTime DueAt, CookieDutyKind Kind)>> NextDutiesAsync(List<Guid> ids, CancellationToken ct) =>
        (await db.CookieDuties.AsNoTracking().Where(d => ids.Contains(d.CaseId) && d.State == CookieDutyState.Due)
            .Select(d => new { d.CaseId, d.DueAt, d.Kind }).ToListAsync(ct))
        .GroupBy(d => d.CaseId).ToDictionary(g => g.Key, g => g.OrderBy(d => d.DueAt).Select(d => (d.DueAt, d.Kind)).First());

    private static CookieCaseItem Item(CookieCaseEntity c, (DateTime DueAt, CookieDutyKind Kind)? next,
        (Dictionary<Guid, string> Hosts, Dictionary<Guid, string> Locations) names) => new(c.Id, c.Kind, c.Open, c.Title, c.Importance,
        c.OpenedAt, c.ClosedAt, c.ChangedAt, c.ProgressAt, c.HostId, c.LocationId, c.IssueId ?? c.OwnIssueId,
        c.HostId is { } h ? names.Hosts.GetValueOrDefault(h) : null, c.LocationId is { } l ? names.Locations.GetValueOrDefault(l) : null, c.CookieNote,
        c.CookieUrgency, next is { } n ? new CookieNextDuty(n.Kind, n.DueAt) : null,
        c.StoppedAt is { } at ? new CookieStop(at, c.StoppedByName, c.StopComment) : null);

    private async Task<(Dictionary<Guid, string> Hosts, Dictionary<Guid, string> Locations)> NamesAsync(IReadOnlyList<CookieCaseEntity> cases, CancellationToken ct)
    {
        var hostIds = cases.Where(c => c.HostId is not null).Select(c => c.HostId!.Value).Distinct().ToList();
        var locationIds = cases.Where(c => c.LocationId is not null).Select(c => c.LocationId!.Value).Distinct().ToList();
        return (await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, ct),
            await db.Locations.AsNoTracking().Where(l => locationIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct));
    }

    /// <summary>The users and groups Admin → Cookie can name (contacts, rules, escalation groups, Cookie's group).</summary>
    public async Task<CookiePeople> PeopleAsync(CancellationToken ct) => new(
        await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).Select(u => new CookiePerson(u.Id, u.Username, u.DisplayName, u.IsActive)).ToListAsync(ct),
        await db.Groups.AsNoTracking().OrderBy(g => g.Name).Select(g => new CookieGroupRef(g.Id, g.Name)).ToListAsync(ct));

    // --- Admin → Cookie ------------------------------------------------------------------------------------------------

    public async Task<CookieAdminOverview> OverviewAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var status = await StatusAsync(user, ct);
        var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(ct) ?? new CookieRuntimeEntity();
        var now = Clock.UtcNow;
        var week = now.AddDays(-7);
        var open = await db.CookieCases.AsNoTracking().Where(c => c.Open).GroupBy(c => c.Kind).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var duties = await db.CookieDuties.AsNoTracking().Where(d => d.State == CookieDutyState.Due || d.DoneAt >= week)
            .GroupBy(d => new { d.Kind, d.State }).Select(g => new { g.Key.Kind, g.Key.State, Count = g.Count() }).ToListAsync(ct);
        var due = await db.CookieDuties.CountAsync(d => d.State == CookieDutyState.Due && d.DueAt <= now, ct);
        var switches = (await settings.GetAsync<ActionSettings>(ct)).Duties;
        int Count(CookieDutyKind k, CookieDutyState s) => duties.Where(d => d.Kind == k && d.State == s).Sum(d => d.Count);
        var counts = Enum.GetValues<CookieDutyKind>().Select(k => new CookieDutyCount(k, switches.On(k), Count(k, CookieDutyState.Due),
            Count(k, CookieDutyState.Done), Count(k, CookieDutyState.HeldBack), Count(k, CookieDutyState.Off),
            Count(k, CookieDutyState.Failed) + Count(k, CookieDutyState.NotPossible))).ToList();
        var r = redmineOptions.CurrentValue;
        var today = Clock.ToUtc(Clock.LocalNow.Date);
        var heldBack = await db.CookieOutbox.CountAsync(o => o.CreatedAt >= today && o.State == CookieOutboxState.HeldBack, ct);
        return new CookieAdminOverview(status, runtime.CodeReason, runtime.CodeStatus, runtime.CaseBookAt, runtime.TicketsAt, runtime.CaseBookError,
            open.Where(o => o.Key == CookieCaseKind.Problem).Sum(o => o.Count), open.Where(o => o.Key == CookieCaseKind.Ticket).Sum(o => o.Count),
            due, r.Enabled, r.CanWrite, ai.CurrentValue.Usable, counts, heldBack, await BudgetAsync(ct),
            (await settings.GetAsync<CookieRightsSettings>(ct)).GroupId is not null, await ActingAsync(today, ct));
    }

    private async Task<CookieActingItem> ActingAsync(DateTime today, CancellationToken ct)
    {
        var day = db.CookieDecisions.AsNoTracking().Where(d => d.At >= today);
        var model = services.GetRequiredService<CookieModel>();
        return new CookieActingItem(await db.CookieDecisions.CountAsync(d => d.Outcome == "proposed", ct), await day.CountAsync(d => d.Outcome == "done", ct),
            await day.CountAsync(d => d.Outcome == "rejected", ct), await day.CountAsync(d => d.Outcome == "declined", ct),
            await day.CountAsync(d => d.Outcome == "held-back", ct), await services.GetRequiredService<Seraph>().ChoiceAsync(ct) is not null,
            new CookieBudgetItem(options.CurrentValue.BigBrother.BudgetUsd, await model.BigBrotherSpentAsync(ct)),
            (await services.GetRequiredService<Instructions>().ActiveAsync(ct)).Count, (await settings.GetAsync<LessonSettings>(ct)).Lessons.Count(l => l.Active),
            Integrations(await services.GetRequiredService<CookieIntegrations>().ReadAsync(ct)));
    }

    /// <summary>What is available, for the admin tabs (docs/cookie-fallback-plan.md §6).</summary>
    public static CookieIntegrationsItem Integrations(IntegrationsReading r) => new(
        new CookieIntegrationItem(r.RedmineState, r.RedmineReason, r.RedmineSince), r.TicketProject,
        new CookieIntegrationItem(r.Zabbix ? "available" : "none", r.ZabbixReason, r.ZabbixSince));

    private async Task<CookieBudgetItem> BudgetAsync(CancellationToken ct)
    {
        var model = services.GetRequiredService<CookieModel>();
        return new CookieBudgetItem(await model.BudgetLimitAsync(ct), await model.SpentAsync(ct));
    }

    // --- Start, stop, run now (§9.3) -------------------------------------------------------------------------------------

    public async Task<CookieStatusItem> StartAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            throw ApiException.Conflict("cookie_not_enabled", "Cookie__Enabled=true is needed first.");
        }
        var runtime = await CaseBook.RuntimeAsync(db, ct);
        runtime.Running = true;
        runtime.StartedAt = Clock.UtcNow;
        runtime.StartedBy = user.Identity?.Name;
        runtime.Failures = 0;
        runtime.PausedUntil = null;
        await db.SaveChangesAsync(ct);
        services.GetRequiredService<CookieSignals>().Raise("started");
        AuditLog.Summary("Cookie started");
        return await StatusAsync(user, ct);
    }

    public async Task<CookieStatusItem> StopAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var runtime = await CaseBook.RuntimeAsync(db, ct);
        runtime.Running = false;
        runtime.StoppedAt = Clock.UtcNow;
        runtime.StoppedBy = user.Identity?.Name;
        runtime.State = CookieState.Stopped;
        runtime.StateReason = $"Stopped by {user.Identity?.Name}.";
        runtime.NextRoundAt = null;
        await db.SaveChangesAsync(ct);
        services.GetRequiredService<CookieSignals>().Raise("stopped");
        AuditLog.Summary("Cookie stopped: the case book and the duties keep running");
        return await StatusAsync(user, ct);
    }

    public async Task<CookieStatusItem> RunNowAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        if (runtime is not { Running: true })
        {
            throw ApiException.Conflict("cookie_stopped", "Cookie is stopped: start it first.");
        }
        services.GetRequiredService<CookieSignals>().Raise("run now");
        AuditLog.Summary("Cookie: run now");
        return await StatusAsync(user, ct);
    }

    public async Task<IReadOnlyList<CookieRoundItem>> RoundsAsync(CancellationToken ct) =>
        await db.CookieRounds.AsNoTracking().OrderByDescending(r => r.Id).Take(50)
            .Select(r => new CookieRoundItem(r.Id, r.StartedAt, r.EndedAt, r.Trigger, r.Outcome, r.Cases, r.Calls, r.ToolCalls, r.Cost, r.Error)).ToListAsync(ct);

    /// <summary>A prompt as the model receives it (§9.3, Identity): prompts 1 to 3 and the step's, or a kind of case's instructions.</summary>
    public async Task<CookiePromptPreview> PreviewAsync(string key, CancellationToken ct)
    {
        var brain = services.GetRequiredService<CookieBrain>();
        var values = await brain.ValuesAsync(ct);
        foreach (var name in new[] { "brief", "cases", "state", "reader", "last_visit", "reader_profile", "working_hours_state", "max_cases", "count", "days",
            "age", "summary", "elapsed", "status", "escalation_group", "ticket_project", "asker", "asker_rights", "rule_name", "rule_purpose", "case",
            "question", "proposal", "evidence", "ratings" })
        {
            values.TryAdd(name, $"[{name.Replace('_', ' ')}]");
        }
        if (CookiePrompts.IsLounge(key) && CookiePrompts.Defaults.ContainsKey(key))
        {
            // A voice in the lounge (docs/lounge-plan.md §4.2): Cookie's identity or Seraph's line, the lounge's rules, the character; then the step's.
            var voice = key == "loungeSeraph" ? Lounge.LoungeVoices.Seraph : Lounge.LoungeVoices.Cookie;
            values["other"] = voice == Lounge.LoungeVoices.Seraph ? values["name"] : "Seraph";
            values["not_mentioned"] = "[the people who opted out]";
            foreach (var name in new[] { "awake", "convictions", "keepsakes", "memories", "ground", "staffing", "seed", "direction", "drift", "day" })
            {
                values.TryAdd(name, $"[{name}]");
            }
            var system = await services.GetRequiredService<Lounge.LoungeVoice>().SystemAsync(voice, values, ct);
            return new CookiePromptPreview(key, key is "loungeScene" or "loungeNight" ? $"{system}\n\n{CookiePrompts.Fill(await brain.PromptAsync(key, ct), values)}" : system);
        }
        var basePrompt = await brain.BaseAsync(values, ct);
        string text;
        if (key.StartsWith("case:", StringComparison.Ordinal) && CookiePrompts.CaseKinds.ContainsKey(key[5..]))
        {
            // The preview shows the prompt as a case gets it now: what is missing, then what Cookie may propose (docs/cookie-fallback-plan.md §4).
            var reading = await services.GetRequiredService<CookieIntegrations>().ReadAsync(ct);
            text = $"{basePrompt}\n\n{CookieBrain.WithMissing(CookiePrompts.Fill(await brain.CaseInstructionAsync(key[5..], ct), values), reading)}\n\n" +
                $"{await brain.ActingAsync(reading, ct)}\n\nThe case:\n[the case]";
        }
        else if (CookiePrompts.Defaults.ContainsKey(key))
        {
            text = key is "identity" or "goals" or "rules" ? basePrompt : $"{basePrompt}\n\n{CookiePrompts.Fill(await brain.PromptAsync(key, ct), values)}";
        }
        else
        {
            throw ApiException.NotFound("Prompt");
        }
        return new CookiePromptPreview(key, text);
    }

    // --- The summary and the chat (§8, §10.2) ---------------------------------------------------------------------------

    public const int MessagesPerHour = 20;
    public const int MaxMessageChars = 4000;

    public Task<CookieSummaryItem> SummaryAsync(ClaimsPrincipal user, CancellationToken ct) =>
        services.GetRequiredService<Summaries>().ForAsync(user, ct);

    public async Task<CookieThread> MessagesAsync(ClaimsPrincipal user, long? afterId, CancellationToken ct)
    {
        var userId = user.UserId() ?? Guid.Empty;
        var q = db.CookieMessages.AsNoTracking().Where(m => m.UserId == userId);
        if (afterId is { } after)
        {
            q = q.Where(m => m.Id > after);
        }
        var rows = (await q.OrderByDescending(m => m.Id).Take(100).ToListAsync(ct)).OrderBy(m => m.Id).ToList();
        var drafts = rows.Where(m => m.InstructionId is not null).Select(m => m.InstructionId!.Value).ToList();
        var instructions = await db.CookieInstructions.AsNoTracking().Where(i => drafts.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        // What came of an order (docs/cookie-powers-plan.md §7): the reply's case's decisions, by rights the reader's own (it is their thread).
        var replies = rows.Where(m => m.From == "cookie" && m.CaseId != null).Select(m => m.CaseId!.Value).ToList();
        var ordered = replies.Count == 0 ? []
            : await db.CookieDecisions.AsNoTracking().Where(d => d.CaseId != null && replies.Contains(d.CaseId.Value) && d.OrderedBy != null).OrderBy(d => d.Id).ToListAsync(ct);
        var privacy = services.GetRequiredService<PrivacyFilter>();
        if (ordered.Count > 0)
        {
            await privacy.EnsureLoadedAsync(ct);
        }
        var actions = ordered.GroupBy(d => d.CaseId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CookieMessageActionItem>)[.. g.Select(d => new CookieMessageActionItem(d.Id, d.Kind, d.Title, d.Outcome, privacy.Unmask(d.Detail)))]);
        var messages = rows.Select(m => new CookieMessageItem(m.Id, m.At, m.From, m.Text, m.Answered, m.To,
            m.InstructionId is { } i && instructions.TryGetValue(i, out var x) ? InstructionItem(x) : null, m.Source, m.ReadOnly, m.RefusedWhy, m.Empowered,
            m.CaseId is { } caseId && actions.TryGetValue(caseId, out var a) ? a : null)).ToList();
        var waiting = await db.CookieMessages.AnyAsync(m => m.UserId == userId && m.From == "person" && !m.Answered, ct);
        var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        return new CookieThread(messages, waiting, options.CurrentValue.Enabled ? runtime?.State ?? CookieState.Stopped : CookieState.Stopped);
    }

    /// <summary>Whether Cookie takes messages now: on in ENV and started.</summary>
    public async Task<bool> RunningAsync(CancellationToken ct) =>
        options.CurrentValue.Enabled && await db.CookieRuntime.AsNoTracking().AnyAsync(r => r.Running, ct);

    /// <summary>
    /// A message over MCP that Jev refused (docs/mcp-plan.md §8.2): kept in the person's thread, marked, and never answered,
    /// so the person sees what was tried in their name.
    /// </summary>
    public async Task<long> RefusedMessageAsync(ClaimsPrincipal user, string text, string to, Guid tokenId, bool readOnly, string why, CancellationToken ct)
    {
        var message = new CookieMessageEntity
        {
            UserId = user.UserId() ?? throw ApiException.NotFound("User"), At = Clock.UtcNow, From = "person", Text = CaseBook.Cut(text, MaxMessageChars)!,
            To = to, Answered = true, Source = "mcp", McpTokenId = tokenId, ReadOnly = readOnly, RefusedWhy = CaseBook.Cut(why, 300),
        };
        db.CookieMessages.Add(message);
        await db.SaveChangesAsync(ct);
        return message.Id;
    }

    /// <summary>A message to Cookie: it wakes Cookie at once (§10.2). At most 20 an hour per person.</summary>
    public async Task<CookieMessageItem> PostMessageAsync(CookieMessageRequest request, ClaimsPrincipal user, CancellationToken ct) =>
        await PostMessageAsync(request, user, null, ct);

    /// <summary>
    /// An admin's message with Cookie's powers (docs/cookie-powers-plan.md §4): admins only, checked here behind the endpoint's attribute,
    /// never over MCP, to Cookie, audited; Cookie acts on it in its next round.
    /// </summary>
    public async Task<CookieMessageItem> PostEmpoweredAsync(CookieMessageRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!user.Rights().IsAdmin)
        {
            throw ApiException.Forbidden("Only an admin can give Cookie its powers for a message.");
        }
        var item = await PostMessageAsync(request with { To = "cookie" }, user, null, ct, empowered: true);
        AuditLog.Summary($"With powers: {CaseBook.Cut(item.Text, 120)}");
        return item;
    }

    /// <summary>
    /// The same over MCP (docs/mcp-plan.md §8.5): from the token, counted per token as well, and from a read-only token marked
    /// so that Cookie answers it as a question only.
    /// </summary>
    public async Task<CookieMessageItem> PostMessageAsync(CookieMessageRequest request, ClaimsPrincipal user, McpMessageOrigin? mcp, CancellationToken ct,
        bool empowered = false)
    {
        var text = request.Text?.Trim() ?? "";
        var to = string.IsNullOrWhiteSpace(request.To) ? "cookie" : request.To.Trim().ToLowerInvariant();
        new ApiValidator().Check(text.Length is > 0 and <= MaxMessageChars, "text", $"1 to {MaxMessageChars:N0} characters.")
            .Check(to is "cookie" or "seraph", "to", "cookie or seraph.").ThrowIfInvalid();
        var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        if (!options.CurrentValue.Enabled || runtime is not { Running: true })
        {
            throw ApiException.Conflict("cookie_stopped", "Cookie is stopped: an admin starts it on Admin → Cookie.");
        }
        var userId = user.UserId() ?? throw ApiException.NotFound("User");
        var hour = Clock.UtcNow.AddHours(-1);
        if (await db.CookieMessages.CountAsync(m => m.UserId == userId && m.From == "person" && m.At >= hour, ct) >= MessagesPerHour)
        {
            throw new ApiException(StatusCodes.Status429TooManyRequests, "cookie_too_many_messages", $"At most {MessagesPerHour} messages an hour.");
        }
        if (mcp is not null && await db.CookieMessages.CountAsync(m => m.McpTokenId == mcp.TokenId && m.At >= hour, ct) >= mcp.PerHour)
        {
            throw new ApiException(StatusCodes.Status429TooManyRequests, "cookie_too_many_messages", $"At most {mcp.PerHour} messages an hour over this token.");
        }
        var message = new CookieMessageEntity { UserId = userId, At = Clock.UtcNow, From = "person", Text = text, To = to, Empowered = empowered && mcp is null };
        if (mcp is not null)
        {
            message.Source = "mcp";
            message.McpTokenId = mcp.TokenId;
            message.ReadOnly = mcp.ReadOnly;
        }
        db.CookieMessages.Add(message);
        await db.SaveChangesAsync(ct);
        services.GetRequiredService<CookieSignals>().Raise($"message from {user.Identity?.Name}");
        return new CookieMessageItem(message.Id, message.At, message.From, message.Text, false, to, Empowered: message.Empowered);
    }

    // --- Mute, suppress and instructions (§10.3, §21.1) ------------------------------------------------------------------

    public static CookieInstructionItem InstructionItem(CookieInstructionEntity i) => new(i.Id, i.Kind, i.Scope, i.Value, i.Label, i.Text, i.Until, i.Confirmed,
        i.CreatedAt, i.CreatedByName, i.Source, i.EndedAt, i.EndedByName, i.Confirmed && i.EndedAt is null && i.Until > Clock.UtcNow);

    /// <summary>Admin → Cookie → Mute, suppress: the active ones and drafts first, then the last 30 days' ended ones.</summary>
    public async Task<IReadOnlyList<CookieInstructionItem>> InstructionsAsync(CancellationToken ct)
    {
        var since = Clock.UtcNow.AddDays(-30);
        return [.. (await db.CookieInstructions.AsNoTracking().Where(i => i.CreatedAt >= since || (i.EndedAt == null && i.Until > since))
            .OrderByDescending(i => i.CreatedAt).Take(500).ToListAsync(ct)).Select(InstructionItem)
            .OrderBy(i => i.Active ? 0 : !i.Confirmed && i.EndedAt is null && i.Until > Clock.UtcNow ? 1 : 2).ThenByDescending(i => i.CreatedAt)];
    }

    public async Task<CookieInstructionItem> CreateInstructionAsync(CookieInstructionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var label = request.Label;
        if (request.Kind == "mute" && Guid.TryParse(request.Value, out var id) && string.IsNullOrWhiteSpace(label))
        {
            label = request.Scope switch
            {
                "host" => await db.Hosts.Where(h => h.Id == id).Select(h => h.Name).FirstOrDefaultAsync(ct),
                "sensor" => await db.Sensors.Where(s => s.Id == id).Select(s => s.Parent.Name + " / " + s.Name).FirstOrDefaultAsync(ct),
                "location" => await db.Locations.Where(l => l.Id == id).Select(l => l.Name).FirstOrDefaultAsync(ct),
                "case" => await db.CookieCases.Where(c => c.Id == id).Select(c => c.Title).FirstOrDefaultAsync(ct),
                _ => null,
            };
        }
        var i = await services.GetRequiredService<Instructions>().CreateAsync(new InstructionDraft(request.Kind?.Trim() ?? "", request.Scope?.Trim() ?? "",
            request.Value, label, request.Text ?? "", request.Until), confirmed: true, "admin", user.UserId(), user.Identity?.Name ?? "", ct);
        AuditLog.Summary($"{i.Kind} {i.Scope} {i.Label ?? i.Value} until {Clock.ToLocal(i.Until):d MMM HH:mm}: {i.Text}".Trim());
        return InstructionItem(i);
    }

    /// <summary>A person confirms a draft Cookie filled in from their message (§10.3): only they, or <i>Cookie: Manage</i>.</summary>
    public async Task<CookieInstructionItem> ConfirmInstructionAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var i = await db.CookieInstructions.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Instruction");
        if (i.CreatedById != user.UserId() && !user.Has(Area.Cookie, Level.Manage))
        {
            throw ApiException.NotFound("Instruction");
        }
        if (i.Confirmed || i.EndedAt is not null || i.Until <= Clock.UtcNow)
        {
            throw ApiException.Conflict("cookie_instruction_not_draft", "Only a draft that hasn't ended can be confirmed.");
        }
        i.Confirmed = true;
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"Confirmed: {i.Kind} {i.Scope} {i.Label ?? i.Value} until {Clock.ToLocal(i.Until):d MMM HH:mm}: {i.Text}".Trim());
        return InstructionItem(i);
    }

    /// <summary><i>End now</i> (§10.3).</summary>
    public async Task<CookieInstructionItem> EndInstructionAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var i = await db.CookieInstructions.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Instruction");
        if (i.EndedAt is null)
        {
            i.EndedAt = Clock.UtcNow;
            i.EndedByName = CaseBook.Cut(user.Identity?.Name, 200);
            await db.SaveChangesAsync(ct);
        }
        AuditLog.Summary($"Ended: {i.Kind} {i.Scope} {i.Label ?? i.Value}".Trim());
        return InstructionItem(i);
    }

    // --- The trace (§9.2) ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The full trace, reasoning and tool results too, for <i>Cookie: Manage</i> and for readers whose rights cover all of
    /// Cookie's group's; everyone else sees the notes and the status of the cases they may see. Every entry only to readers
    /// with all the areas of its sources.
    /// </summary>
    public async Task<CookieTrace> TraceAsync(CookieTraceQuery query, ClaimsPrincipal user, CancellationToken ct)
    {
        var rights = user.Rights();
        var cookieRights = await services.GetRequiredService<CookieRound>().RightsAsync(ct);
        var full = rights.Has(Area.Cookie, Level.Manage) || (cookieRights is not null && cookieRights.Levels.All(l => rights.Has(l.Key, l.Value)));
        IQueryable<CookieTraceEntity> q = db.CookieTrace.AsNoTracking();
        if (query.CaseId is { } caseId)
        {
            q = q.Where(t => t.CaseId == caseId);
        }
        if (query.RoundId is { } roundId)
        {
            q = q.Where(t => t.RoundId == roundId);
        }
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            q = q.Where(t => t.Kind == query.Kind);
        }
        if (query.AfterId is { } after)
        {
            q = q.Where(t => t.Id > after);
        }
        if (!full)
        {
            q = q.Where(t => t.CaseId != null && (t.Kind == "note" || t.Kind == "status" || t.Kind == "reply"));
        }
        // Polling (afterId) gets the oldest new steps first, so that none is skipped; otherwise the newest.
        var limit = Math.Clamp(query.Limit, 1, 500);
        var rows = query.AfterId is null ? await q.OrderByDescending(t => t.Id).Take(limit * 2).ToListAsync(ct)
            : await q.OrderBy(t => t.Id).Take(limit * 2).ToListAsync(ct);
        var caseIds = rows.Where(t => t.CaseId is not null).Select(t => t.CaseId!.Value).Distinct().ToList();
        var cases = await db.CookieCases.AsNoTracking().Where(c => caseIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var privacy = services.GetRequiredService<PrivacyFilter>();
        await privacy.EnsureLoadedAsync(ct);
        var visible = rows.Where(t => CookieAccess.CanSee(rights, t.Areas)
                && (t.CaseId is not { } id || (cases.TryGetValue(id, out var c) && CookieAccess.CanSee(rights, user.UserId(), c))))
            .Take(limit)
            .Select(t => new CookieTraceItem(t.Id, t.RoundId, t.CaseId, t.CaseId is { } id && cases.TryGetValue(id, out var c) ? privacy.Unmask(c.Title) : null, t.At,
                t.Kind, privacy.Unmask(t.Title), privacy.Unmask(t.Body), t.Areas)).ToList();
        return new CookieTrace(visible, full);
    }

    public async Task<Paged<CookieOutboxItem>> OutboxAsync(CookieOutboxQuery query, CancellationToken ct)
    {
        var size = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        IQueryable<CookieOutboxEntity> q = db.CookieOutbox.AsNoTracking();
        if (query.State is { } state)
        {
            q = q.Where(o => o.State == state);
        }
        if (query.Channel is { } channel)
        {
            q = q.Where(o => o.Channel == channel);
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(o => o.Id).Skip((page - 1) * size).Take(size)
            .Select(o => new CookieOutboxItem(o.Id, o.CreatedAt, o.Channel, o.Recipient, o.IssueId, o.Subject, o.Body, o.State, o.Detail, o.Source, o.CaseId,
                o.SentAt, o.SentByName))
            .ToListAsync(ct);
        return new Paged<CookieOutboxItem>(items, page, size, total);
    }

    public async Task<CookieOutboxItem> SendHeldBackAsync(long id, ClaimsPrincipal user, CancellationToken ct)
    {
        var o = await services.GetRequiredService<Outbox>().SendHeldBackAsync(id, user.UserId(), user.Identity?.Name ?? "", ct);
        return new CookieOutboxItem(o.Id, o.CreatedAt, o.Channel, o.Recipient, o.IssueId, o.Subject, o.Body, o.State, o.Detail, o.Source, o.CaseId, o.SentAt,
            o.SentByName);
    }

    public async Task<CookieAllSettings> SettingsAsync(CancellationToken ct) => new(
        await settings.GetAsync<IdentitySettings>(ct), await settings.GetAsync<PromptSettings>(ct), await settings.GetAsync<CaseSettings>(ct),
        await settings.GetAsync<CookieRedmineSettings>(ct),
        await settings.GetAsync<ActionSettings>(ct), await settings.GetAsync<ModelSettings>(ct), await settings.GetAsync<CookieRightsSettings>(ct),
        await settings.GetAsync<LessonSettings>(ct), await settings.GetAsync<Access.AccessSettings>(ct), await settings.GetAsync<Lounge.LoungeSettings>(ct));

    public async Task<CookieSaved> SaveAsync<T>(T value, ClaimsPrincipal user, CancellationToken ct) where T : class, ICookieSection, new()
    {
        await CheckAsync(value, ct);
        if (value is LessonSettings lessons)
        {
            // A lesson a person wrote or changed is theirs: the summariser keeps it (§11, §21.8).
            var before = (await settings.GetAsync<LessonSettings>(ct)).Lessons.ToDictionary(l => l.Id);
            var marked = lessons with
            {
                Lessons = [.. lessons.Lessons.Select(l => l with
                {
                    ByPerson = before.TryGetValue(l.Id, out var old) && old with { Active = l.Active, ByPerson = false } == l with { ByPerson = false }
                        ? old.ByPerson : true,
                })],
            };
            value = (T)(object)marked;
        }
        var version = await settings.SaveAsync(value, user.UserId(), user.Identity?.Name ?? "", null, ct);
        if (value is LessonSettings saved)
        {
            await services.GetRequiredService<Lessons>().ApplyRulesAsync(saved, ct);
        }
        AuditLog.Summary($"Admin → Cookie, {Label(T.Section)}: version {version}");
        return new CookieSaved(T.Section, version);
    }

    public Task<IReadOnlyList<CookieSettingsVersion>> VersionsAsync(string section, CancellationToken ct) =>
        settings.VersionsAsync(Known(section), ct);

    public async Task<JsonElement> VersionAsync(string section, int version, CancellationToken ct)
    {
        var json = await settings.JsonAsync(Known(section), version, ct) ?? throw ApiException.NotFound("Version");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public async Task<CookieSaved> RollbackAsync(string section, int version, ClaimsPrincipal user, CancellationToken ct)
    {
        var saved = await settings.RollbackAsync(Known(section), version, user.UserId(), user.Identity?.Name ?? "", ct);
        AuditLog.Summary($"Admin → Cookie, {Label(section)}: version {saved}, the values of version {version}");
        return new CookieSaved(section, saved);
    }

    public CookieDefaults Defaults()
    {
        var o = options.CurrentValue;
        return new(
            CookiePrompts.Defaults.Select(p => new CookiePromptItem(p.Key, p.Value.Label, p.Value.UsedFor, p.Value.Text, CookiePrompts.Required(p.Key))).ToList(),
            CookiePrompts.CaseKinds.Select(p => new CookiePromptItem(p.Key, p.Value.Label, p.Value.UsedFor, p.Value.Text, [])).ToList(),
            OutputLimits.Ceilings, new OutputLimits(), WatchedProject.Default(0),
            new CookieEnvDefaults(o.Enabled, o.Privacy, o.Model, o.Providers, o.Zdr, o.MinQuantization, o.BudgetUsd, o.SeraphModel, o.SeraphProviders,
                o.BigBrother.Primary.Model, o.BigBrother.Backup.Model, o.BigBrother.BudgetUsd));
    }

    public async Task<CookieRedmineLookups> RedmineLookupsAsync(CancellationToken ct)
    {
        var o = redmineOptions.CurrentValue;
        if (!o.Enabled)
        {
            return new CookieRedmineLookups(false, false, [], [], [], [], null, null);
        }
        try
        {
            var redmine = services.GetRequiredService<RedmineService>();
            var worker = services.GetRequiredService<RedmineWorker>();
            var lookups = await redmine.LookupsAsync(ct);
            IReadOnlyList<RmRef> roles;
            string? rolesError = null;
            try
            {
                // Through the worker's queue (docs/redmine-queue-outbound-plan.md §3.2): someone waits on Cookie's settings page.
                roles = (await worker.RunAsync(new RedmineJobSpec("roles", PNMTD.Models.Db.RedmineLane.Interactive, "Cookie settings: roles", "PNMT"),
                    calls => calls.GetAsync<RmRoles>("/roles.json", "roles")).WaitAsync(ct)).Roles;
            }
            catch (RedmineException e)
            {
                roles = [];
                rolesError = $"Redmine's roles can't be read: {e.Message}";
            }
            return new CookieRedmineLookups(true, o.CanWrite,
                lookups.Projects.Where(p => lookups.Allowed.Contains(p.Id)).Select(p => new RedmineNamed(p.Id, p.Name)).ToList(),
                roles.Select(r => new RedmineNamed(r.Id, r.Name ?? $"#{r.Id}")).ToList(),
                lookups.Statuses.Select(s => new RedmineNamed(s.Id, s.Name)).ToList(),
                lookups.Trackers.Select(t => new RedmineNamed(t.Id, t.Name ?? $"#{t.Id}")).ToList(), null, rolesError);
        }
        catch (RedmineException e)
        {
            return new CookieRedmineLookups(true, o.CanWrite, [], [], [], [], e.Message, null);
        }
    }

    /// <summary>The checks that need the database: the users, groups and projects a section names exist.</summary>
    private async Task CheckAsync<T>(T value, CancellationToken ct)
    {
        var v = new ApiValidator();
        switch (value)
        {
            case CookieRedmineSettings r:
            {
                var groups = r.Projects.Where(p => p.EscalationGroupId is not null).Select(p => p.EscalationGroupId!.Value).Distinct().ToList();
                var known = await db.Groups.Where(g => groups.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
                v.Check(groups.All(known.Contains), "projects", "An escalation group that doesn't exist.");
                break;
            }
            case CookieRightsSettings { GroupId: { } groupId }:
                v.Check(await db.Groups.AnyAsync(g => g.Id == groupId, ct), "groupId", "No such group.");
                break;
            case Lounge.LoungeSettings { GroupId: { } loungeGroup }:
                v.Check(await db.Groups.AnyAsync(g => g.Id == loungeGroup, ct), "groupId", "No such group.");
                break;
            case Access.AccessSettings a:
            {
                // The contacts are PNMT's users and groups (docs/cookie-access-plan.md §6.2), by id.
                var users = a.Contacts.Where(c => c.UserId is not null).Select(c => c.UserId!.Value).Distinct().ToList();
                var groups = a.Contacts.Where(c => c.GroupId is not null).Select(c => c.GroupId!.Value).Distinct().ToList();
                v.Check(await db.Users.CountAsync(u => users.Contains(u.Id), ct) == users.Count, "contacts", "A user that doesn't exist.");
                v.Check(await db.Groups.CountAsync(g => groups.Contains(g.Id), ct) == groups.Count, "contacts", "A group that doesn't exist.");
                var routerWatchOn = a.On && a.Routers && !(await settings.GetAsync<Access.AccessSettings>(ct)).Routers;
                if (routerWatchOn || a.On && !(await settings.GetAsync<Access.AccessSettings>(ct)).On)
                {
                    // Switched on: the routers get Watch new routers (§4.2).
                    await db.MikrotikRouters.ExecuteUpdateAsync(u => u.SetProperty(r => r.WatchLogins, a.WatchNewRouters), ct);
                }
                break;
            }
        }
        v.ThrowIfInvalid();
    }

    private static string Known(string section) =>
        CookieSettings.Sections.Contains(section) ? section : throw ApiException.NotFound("Section");

    public static string Label(string section) => section switch
    {
        "identity" => "Identity",
        "prompts" => "Prompts",
        "cases" => "Cases",
        "redmine" => "Redmine",
        "actions" => "Actions",
        "models" => "Models",
        "lessons" => "Lessons",
        "rights" => "Rights",
        "access" => "Access",
        "lounge" => "Lounge",
        _ => section,
    };
}

/// <summary>Where a message over MCP came from (docs/mcp-plan.md §8.5): the token, whether it reads only, its limit an hour.</summary>
public sealed record McpMessageOrigin(Guid TokenId, bool ReadOnly, int PerHour);
