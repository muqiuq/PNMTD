using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Cookie.Access;

/// <summary>
/// The access watch's API (docs/cookie-access-plan.md §10.3): an access case's logins, the contacts' answers and <i>Remember
/// this</i>, <i>This wasn't expected</i>, a ticket when asked, known access, the routers' switches, a router's <i>Access</i> tab,
/// and the Access tab's warnings.
/// </summary>
public sealed class AccessService(PnmtdDbContext db, CookieSettings settings, IOptionsMonitor<CookieOptions> options, IConfiguration configuration,
    IServiceProvider services)
{
    public const string Expected = "expected";
    public const string NotExpected = "not-expected";

    private static readonly JsonSerializerOptions Json = CookieSettings.Json;

    private async Task<CookieCaseEntity> AccessCaseAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await db.CookieCases.SingleOrDefaultAsync(x => x.Id == id && x.Kind == CookieCaseKind.Access, ct);
        if (c is null || !CookieAccess.CanSee(user.Rights(), user.UserId(), c))
        {
            throw ApiException.NotFound("Case");
        }
        return c;
    }

    public async Task<AccessCaseDetail> CaseAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await AccessCaseAsync(id, user, ct);
        var reviews = await db.AccessReviews.AsNoTracking().Where(r => r.CaseId == id).OrderByDescending(r => r.At).Take(100).ToListAsync(ct);
        var people = reviews.Select(r => r.PersonId).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var target = reviews.FirstOrDefault()?.Target ?? (c.SensorId is null ? AccessTargetKind.Pnmt : AccessTargetKind.Server);
        var notices = await db.CookieOutbox.AsNoTracking().Where(o => o.CaseId == id
                && (o.Channel == CookieChannel.AccessEmail || o.Channel == CookieChannel.AccessPushover || o.Channel == CookieChannel.WasThisYou))
            .OrderByDescending(o => o.CreatedAt).Take(50)
            .Select(o => new AccessNoticeItem(o.CreatedAt, o.Channel, o.Recipient, o.Subject, o.State, o.Detail, o.Source)).ToListAsync(ct);
        var rights = user.Rights();
        var s = await settings.GetAsync<AccessSettings>(ct);
        return new AccessCaseDetail(c.Id, target, reviews.FirstOrDefault()?.TargetName ?? c.Title, c.SensorId, c.AccessVerdict, c.AccessAnswer, c.AccessAnsweredAt,
            c.AccessAnsweredBy, c.AccessNotifiedVerdict, c.AccessNotifiedAt, c.AccessPendingVerdict, c.TicketAskedAt,
            [.. reviews.Select(r => Item(r, r.PersonId is { } p && names.TryGetValue(p, out var n) ? n : null))], Links(c, target),
            notices, MayAnswer(rights, user.UserId(), s, target), c.Open && c.TicketAskedAt is null && rights.Has(Area.Cookie, Level.Instruct) && rights.Has(Area.Redmine, Level.Link));
    }

    public static AccessReviewItem Item(AccessReviewEntity r, string? person) => new(r.Id, r.Target, r.SensorId, r.TargetName, r.At, r.Account, r.IsAdmin, r.Way, r.Group,
        r.Source, AccessSignals.IsPublic(r.Source), r.Device, r.KeyFingerprint, r.TargetUser,
        [.. AccessWatch.SignalsOf(r).Select(x => new AccessSignalItem(x.Key, x.Strength, x.Text))], r.Strong, r.Late, r.Flagged, r.State, r.Confidence, r.Reason,
        r.JudgedBy, r.JudgedAt, AccessWatch.Describe(r), person, r.AskedAt, r.PersonAnswer, r.PersonAnsweredAt);

    /// <summary>What a person can do (§7), as links; Cookie never does any of them.</summary>
    private static List<AccessLinkItem> Links(CookieCaseEntity c, AccessTargetKind target) => target switch
    {
        AccessTargetKind.Server when c.SensorId is { } s =>
        [
            new("The server's logins", $"/sensors/{s}/access"),
            new("The server's SSH keys", $"/sensors/{s}/ssh-keys"),
        ],
        AccessTargetKind.Router when c.SensorId is { } s =>
        [
            new("The router's logins", $"/sensors/{s}/access"),
            new("Run a command on the router (as an admin, to look at /user or end a session)", "/admin/mikrotik"),
        ],
        AccessTargetKind.Pnmt =>
        [
            new("The users: deactivate, reset the 2FA", "/admin/users"),
            new("MCP: revoke all tokens", "/admin/mcp/status"),
            new("The audit log's sign-ins", "/admin/audit-log"),
        ],
        _ => [],
    };

    /// <summary>A contact for the target, or <i>Cookie: Instruct</i>; and the target's right (§7).</summary>
    public static bool MayAnswer(UserRights rights, Guid? userId, AccessSettings s, AccessTargetKind target) =>
        AccessNotices.MaySee(rights, target) && (rights.Has(Area.Cookie, Level.Instruct) || userId is { } id && s.Contacts.Any(c => c.UserId == id && c.For(target)));

    private async Task<bool> IsContactAsync(Guid? userId, AccessSettings s, AccessTargetKind target, CancellationToken ct)
    {
        if (userId is not { } id)
        {
            return false;
        }
        if (s.Contacts.Any(c => c.UserId == id && c.For(target)))
        {
            return true;
        }
        var groups = s.Contacts.Where(c => c.GroupId is not null && c.For(target)).Select(c => c.GroupId!.Value).ToList();
        return groups.Count > 0 && await db.UserGroups.AnyAsync(m => m.UserId == id && groups.Contains(m.GroupId), ct);
    }

    /// <summary>
    /// <i>Expected</i> closes the case, and <i>Remember this</i> adds known access; <i>Not expected</i> keeps it open, important and an
    /// alert, and Cookie proposes nothing (§7). Both rate the verdicts.
    /// </summary>
    public async Task AnswerAsync(Guid id, AccessAnswerRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await AccessCaseAsync(id, user, ct);
        var s = await settings.GetAsync<AccessSettings>(ct);
        var reviews = await db.AccessReviews.AsNoTracking().Where(r => r.CaseId == id).OrderByDescending(r => r.At).ToListAsync(ct);
        var target = reviews.FirstOrDefault()?.Target ?? AccessTargetKind.Server;
        var rights = user.Rights();
        if (!AccessNotices.MaySee(rights, target) || !rights.Has(Area.Cookie, Level.Instruct) && !await IsContactAsync(user.UserId(), s, target, ct))
        {
            throw ApiException.Forbidden("Only a contact for this target, or Cookie: Instruct, answers.");
        }
        var v = new ApiValidator();
        v.Check((request.Comment ?? "").Length <= 1000, "comment", "At most 1000 characters.");
        if (request.Remember is { } remember)
        {
            v.Check(request.Expected, "remember", "Only what was expected is remembered.");
            v.Check(remember.Scope is "target" or "location" or "all", "remember.scope", "target, location or all.");
            v.Check(remember.Scope != "location" || c.LocationId is not null, "remember.scope", "The target has no location.");
            v.Check(remember.Days is null or 30 or 90 or 365, "remember.days", "30, 90, 365, or none for no end.");
        }
        v.ThrowIfInvalid();
        var now = Clock.UtcNow;
        var name = user.Identity?.Name ?? "someone";
        c.AccessAnswer = request.Expected ? Expected : NotExpected;
        c.AccessAnsweredAt = now;
        c.AccessAnsweredBy = CaseBook.Cut(name, 200);
        c.ChangedAt = now;
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? "" : $": {request.Comment.Trim()}";
        if (request.Expected)
        {
            c.Open = false;
            c.ClosedAt = now;
            CaseBook.Entry(db, c.Id, now, "answer", $"Expected{comment}", "person", name);
            if (request.Remember is { } r && reviews.FirstOrDefault() is { } login)
            {
                var known = Remembered(c, login, r, user, now, request.Comment);
                db.AccessKnown.Add(known);
                CaseBook.Entry(db, c.Id, now, "remembered", $"Remembered: {known.Label}, {known.Source ?? "any source"}, {(known.Groups.Count == 0 ? "any way" : string.Join(", ", known.Groups))}" +
                    $"{(known.Until is { } u ? $", until {Clock.ToLocal(u):yyyy-MM-dd}" : ", without end")}.", "person", name);
                AuditLog.Change("known access", null, $"{known.Label}: {known.Account}");
            }
        }
        else
        {
            c.Open = true;
            c.ClosedAt = null;
            c.StoppedAt = null;
            c.Importance = Lib.Models.Enum.Importance.High;
            CaseBook.Entry(db, c.Id, now, "answer", $"Not expected{comment}", "person", name);
        }
        await WasThisYou.RateAsync(db, c.Id, request.Expected, name, $"{(request.Expected ? "Expected" : "Not expected")}{comment}", ct);
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"{(request.Expected ? "Expected" : "Not expected")}: {c.Title}{comment}");
        services.GetRequiredService<CookieSignals>().Raise($"access: {c.Title} answered");
    }

    private static AccessKnownEntity Remembered(CookieCaseEntity c, AccessReviewEntity login, AccessRememberRequest r, ClaimsPrincipal user, DateTime now, string? comment) => new()
    {
        Id = Guid.NewGuid(), Scope = r.Scope, Target = login.Target, SensorId = r.Scope == "target" ? login.SensorId : null,
        LocationId = r.Scope == "location" ? c.LocationId : null, Account = login.Account, Source = r.AnySource ? null : AccessSignals.NetworkOf(login.Source),
        Groups = r.AnyWay ? [] : [login.Group], Until = r.Days is { } d ? now.AddDays(d) : null,
        Comment = CaseBook.Cut(string.IsNullOrWhiteSpace(comment) ? $"Expected, from the case {c.Title}" : comment.Trim(), 500)!,
        Label = CaseBook.Cut(r.Scope switch
        {
            "target" => login.Target == AccessTargetKind.Pnmt ? AccessWatch.PnmtName : login.TargetName,
            "location" => "its location",
            _ => login.Target == AccessTargetKind.Pnmt ? AccessWatch.PnmtName : "every target",
        }, 300)!,
        CreatedAt = now, CreatedById = user.UserId(), CreatedByName = CaseBook.Cut(user.Identity?.Name ?? "", 200)!, FromCaseId = c.Id,
    };

    /// <summary>
    /// <i>This wasn't expected</i> (§7) on a server's or router's login: its review, judged normal or never reviewed, becomes a case
    /// answered <i>Not expected</i>. That is how a missed break-in teaches Cookie.
    /// </summary>
    public async Task<Guid> NotExpectedAsync(Guid loginId, AccessNotExpectedRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var login = await db.AgentLogins.AsNoTracking().SingleOrDefaultAsync(l => l.Id == loginId && l.Outcome == LogonOutcome.Success, ct)
            ?? throw ApiException.NotFound("Login");
        var sensor = await db.Sensors.AsNoTracking().Include(x => x.Parent).SingleAsync(x => x.Id == login.SensorId, ct);
        var router = login.Channel == RouterLogins.Channel;
        var now = Clock.UtcNow;
        var name = user.Identity?.Name ?? "someone";
        var review = await db.AccessReviews.FirstOrDefaultAsync(r => r.AgentLoginId == loginId, ct);
        var account = login.Domain is { Length: > 0 } d ? $"{d}\\{login.User}" : login.User;
        var key = AccessWatch.KeyOf(login.SensorId, account);
        var c = review?.CaseId is { } caseId ? await db.CookieCases.SingleOrDefaultAsync(x => x.Id == caseId, ct) : null;
        if (c is null || !c.Open && await db.CookieCases.AnyAsync(x => x.Open && x.Key == key, ct))
        {
            c = await db.CookieCases.SingleOrDefaultAsync(x => x.Open && x.Key == key, ct);
        }
        if (c is null)
        {
            c = new CookieCaseEntity
            {
                Id = Guid.NewGuid(), Kind = CookieCaseKind.Access, Key = key, Open = true, Title = CaseBook.Cut($"Login: {account} on {sensor.Parent.Name}", 300)!,
                HostId = sensor.Parent.Id, LocationId = sensor.Parent.LocationId, SensorId = login.SensorId, OpenedAt = now, ChangedAt = now, ProgressAt = now,
                Areas = ["Monitoring", CookieAccess.LoginsArea],
            };
            db.CookieCases.Add(c);
        }
        if (review is null)
        {
            review = new AccessReviewEntity
            {
                Id = Guid.NewGuid(), CreatedAt = now, Target = router ? AccessTargetKind.Router : AccessTargetKind.Server, SensorId = login.SensorId,
                TargetName = CaseBook.Cut(sensor.Parent.Name, 300)!, AgentLoginId = login.Id, At = login.At, Account = CaseBook.Cut(account, 520)!,
                IsAdmin = login.IsAdmin, Way = AccessSignals.WayOf(login.Kind), Group = AccessSignals.GroupOf(router ? AccessTargetKind.Router : AccessTargetKind.Server, login.Kind, ""),
                Source = login.SourceAddress, Device = login.Workstation, KeyFingerprint = login.KeyFingerprint, TargetUser = login.TargetUser,
                State = AccessReviewState.Normal, JudgedBy = "code", JudgedAt = now, Reason = "No signal: the history settled it.",
            };
            db.AccessReviews.Add(review);
        }
        review.CaseId = c.Id;
        c.Open = true;
        c.ClosedAt = null;
        c.AccessAnswer = NotExpected;
        c.AccessAnsweredAt = now;
        c.AccessAnsweredBy = CaseBook.Cut(name, 200);
        c.AccessVerdict ??= AccessSignals.Name(AccessVerdict.Normal);
        c.Importance = Lib.Models.Enum.Importance.High;
        c.ChangedAt = now;
        CaseBook.Entry(db, c.Id, now, "answer",
            $"This wasn't expected: {AccessWatch.Describe(review)}{(string.IsNullOrWhiteSpace(request.Comment) ? "" : $": {request.Comment.Trim()}")}", "person", name);
        await db.SaveChangesAsync(ct);
        await WasThisYou.RateAsync(db, c.Id, expected: false, name, "This wasn't expected.", ct);
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"This wasn't expected: {AccessWatch.Describe(review)}");
        return c.Id;
    }

    /// <summary><i>Ask Cookie for a ticket</i> (§7): Cookie writes one in its next round, through the executor at the ticket's level.</summary>
    public async Task AskTicketAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await AccessCaseAsync(id, user, ct);
        if (!c.Open)
        {
            throw ApiException.Conflict("cookie_case_closed", "The case is closed.");
        }
        var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        if (!options.CurrentValue.Enabled || runtime is not { Running: true })
        {
            throw ApiException.Conflict("cookie_stopped", "Cookie is stopped: an admin starts it on Admin → Cookie.");
        }
        c.TicketAskedAt = Clock.UtcNow;
        c.TicketAskedBy = CaseBook.Cut(user.Identity?.Name ?? "someone", 200);
        c.ChangedAt = Clock.UtcNow;
        CaseBook.Entry(db, c.Id, Clock.UtcNow, "ticket-asked", "Asked Cookie for a ticket.", "person", c.TicketAskedBy);
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"Asked Cookie for a ticket: {c.Title}");
        services.GetRequiredService<CookieSignals>().Raise($"ticket asked: {c.Title}");
    }

    // --- Known access ----------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AccessKnownItem>> KnownAsync(CancellationToken ct) =>
        (await db.AccessKnown.AsNoTracking().OrderBy(k => k.EndedAt != null).ThenByDescending(k => k.CreatedAt).Take(500).ToListAsync(ct)).Select(Item).ToList();

    private static AccessKnownItem Item(AccessKnownEntity k) => new(k.Id, k.Scope, k.Target, k.SensorId, k.LocationId, k.Account, k.Source, k.Groups, k.Until, k.Comment,
        k.Label, k.CreatedAt, k.CreatedByName, k.FromCaseId, k.EndedAt, k.EndedByName);

    public async Task<AccessKnownItem> AddKnownAsync(AccessKnownRequest r, ClaimsPrincipal user, CancellationToken ct)
    {
        var v = new ApiValidator();
        v.Check(r.Scope is "target" or "location" or "all", "scope", "target, location or all.");
        v.Check(r.Scope != "target" || r.Target == AccessTargetKind.Pnmt || r.SensorId is not null, "sensorId", "A target needs its server or router.");
        v.Check(r.Scope != "location" || r.LocationId is not null, "locationId", "A location.");
        v.Check(r.Source is null || IPNetwork.TryParse(r.Source.Trim(), out _) || IPAddress.TryParse(r.Source.Trim(), out _), "source", "A network such as 10.1.2.0/24, or empty for any.");
        v.Check((r.Account ?? "").Length <= 520, "account", "At most 520 characters.");
        v.Check(r.Comment.Trim().Length is > 0 and <= 500, "comment", "Say why, in at most 500 characters.");
        v.Check((r.Groups ?? []).All(g => AccessSignals.Groups.Contains(g)), "groups", $"Any of {string.Join(", ", AccessSignals.Groups)}.");
        v.Check(r.Until is null || r.Until > Clock.UtcNow, "until", "In the future, or empty for no end.");
        v.ThrowIfInvalid();
        string label = r.Scope switch
        {
            "target" when r.Target == AccessTargetKind.Pnmt => AccessWatch.PnmtName,
            "target" => await db.Sensors.AsNoTracking().Where(s => s.Id == r.SensorId).Select(s => s.Parent.Name).FirstOrDefaultAsync(ct) ?? throw ApiException.NotFound("Server or router"),
            "location" => await db.Locations.AsNoTracking().Where(l => l.Id == r.LocationId).Select(l => l.Name).FirstOrDefaultAsync(ct) ?? throw ApiException.NotFound("Location"),
            _ => r.Target is { } t ? $"every {t.ToString().ToLowerInvariant()}" : "every target",
        };
        var k = new AccessKnownEntity
        {
            Id = Guid.NewGuid(), Scope = r.Scope, Target = r.Target, SensorId = r.Scope == "target" ? r.SensorId : null, LocationId = r.Scope == "location" ? r.LocationId : null,
            Account = string.IsNullOrWhiteSpace(r.Account) ? null : r.Account.Trim(), Source = string.IsNullOrWhiteSpace(r.Source) ? null : r.Source.Trim(),
            Groups = [.. (r.Groups ?? []).Distinct()], Until = r.Until?.ToUniversalTime(), Comment = r.Comment.Trim(), Label = CaseBook.Cut(label, 300)!,
            CreatedAt = Clock.UtcNow, CreatedById = user.UserId(), CreatedByName = CaseBook.Cut(user.Identity?.Name ?? "", 200)!,
        };
        db.AccessKnown.Add(k);
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"Known access: {k.Label}, {k.Account ?? "any account"}, {k.Source ?? "any source"}: {k.Comment}");
        return Item(k);
    }

    public async Task<AccessKnownItem> EndKnownAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var k = await db.AccessKnown.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Known access");
        if (k.EndedAt is null)
        {
            k.EndedAt = Clock.UtcNow;
            k.EndedByName = CaseBook.Cut(user.Identity?.Name ?? "", 200);
            await db.SaveChangesAsync(ct);
        }
        AuditLog.Summary($"Known access ended: {k.Label}, {k.Account ?? "any account"}");
        return Item(k);
    }

    // --- Routers ---------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AccessRouterItem>> RoutersAsync(CancellationToken ct) =>
        await db.MikrotikRouters.AsNoTracking().OrderBy(r => r.Sensor.Parent.Name)
            .Select(r => new AccessRouterItem(r.SensorId, r.Sensor.Name, r.Sensor.Parent.Name, r.Sensor.Parent.LocationId,
                r.Sensor.Parent.AssignedLocation != null ? r.Sensor.Parent.AssignedLocation.Name : null, r.WatchLogins, r.LoginsFirstReadAt, r.LogGap))
            .ToListAsync(ct);

    public async Task<int> SwitchRoutersAsync(IReadOnlyCollection<Guid> ids, bool watch, CancellationToken ct)
    {
        if (ids.Count is 0 or > 2000)
        {
            throw ApiException.Invalid("sensorIds", "1 to 2000 routers.");
        }
        var routers = await db.MikrotikRouters.Include(r => r.Sensor).ThenInclude(s => s.Parent).Where(r => ids.Contains(r.SensorId)).ToListAsync(ct);
        foreach (var r in routers.Where(r => r.WatchLogins != watch))
        {
            r.WatchLogins = watch;
            AuditLog.Change("Cookie watches its logins", (!watch).ToString(), watch.ToString(), r.Sensor.Parent.Name);
        }
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"Cookie {(watch ? "watches" : "doesn't watch")} the logins of {routers.Count} {(routers.Count == 1 ? "router" : "routers")}: " +
            string.Join(", ", routers.Take(10).Select(r => r.Sensor.Parent.Name)));
        return routers.Count;
    }

    /// <summary>A router's <i>Access</i> tab (§10.4): its switch, logins with their reviews, users, who is logged in, the gap.</summary>
    public async Task<RouterAccessView> RouterAsync(Guid id, AgentLoginQuery query, ClaimsPrincipal user, CancellationToken ct)
    {
        var router = await db.MikrotikRouters.AsNoTracking().SingleOrDefaultAsync(r => r.SensorId == id, ct) ?? throw ApiException.NotFound("Router");
        var q = db.AgentLogins.AsNoTracking().Where(l => l.SensorId == id && l.Channel == RouterLogins.Channel);
        if (query.Outcome is { } outcome)
        {
            q = q.Where(l => l.Outcome == outcome);
        }
        if (query.Kind is { } kind)
        {
            q = q.Where(l => l.Kind == kind);
        }
        if (query.Admin is { } admin)
        {
            q = q.Where(l => l.IsAdmin == admin);
        }
        if (Text.Pattern(query.User) is { } u)
        {
            q = q.Where(l => EF.Functions.ILike(l.User, u));
        }
        if (Text.Pattern(query.Source) is { } source)
        {
            q = q.Where(l => EF.Functions.ILike(l.SourceAddress ?? "", source));
        }
        var page = await Paging.PageAsync(q.OrderByDescending(l => l.LastAt ?? l.At).Select(l => new AgentLoginItem(l.Id, l.At, l.LastAt, l.Count, l.Outcome,
            l.FailureReason, l.User, l.Domain, l.IsAdmin, l.Kind, l.AuthPackage, l.SourceAddress, l.SourcePort, l.Workstation, l.Elevated, l.LoggedOffAt, l.Channel,
            null, null, null, null, null, null, null, null)), query, ct);
        page = await AgentServerService.WithReviewsAsync(db, page, ct);
        var users = Parse<List<RouterUserItem>>(router.RouterUsers) ?? [];
        var active = Parse<List<RouterActiveUser>>(router.ActiveUsers) ?? [];
        return new RouterAccessView(router.WatchLogins, user.Rights().Has(Area.Cookie, Level.Manage), router.LoginsFirstReadAt, router.LogGap, router.LogUnmatched,
            router.PnmtAddress, users, active, page);
    }

    private static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // --- The tab's warnings -----------------------------------------------------------------------------------------------------

    public async Task<AccessAdminState> StateAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync<AccessSettings>(ct);
        var cookie = await services.GetRequiredService<CookieRound>().RightsAsync(ct) ?? new UserRights(false, new Dictionary<Area, Level>());
        var warnings = new List<AccessContactWarning>();
        foreach (var contact in s.Contacts)
        {
            if (contact.UserId is { } uid)
            {
                var u = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == uid, ct);
                if (u is null || !u.IsActive)
                {
                    warnings.Add(new AccessContactWarning(u?.DisplayName ?? "A deleted user", "isn't an active user."));
                    continue;
                }
                var rights = (await SessionCache.LoadAsync(db, uid))?.Rights;
                foreach (var target in new[] { AccessTargetKind.Server, AccessTargetKind.Router, AccessTargetKind.Pnmt }.Where(contact.For))
                {
                    if (rights is null || !AccessNotices.MaySee(rights, target))
                    {
                        warnings.Add(new AccessContactWarning(u.DisplayName, target == AccessTargetKind.Pnmt
                            ? "lacks Cookie: Use or Audit log: View, so gets no notices about PNMT's sign-ins."
                            : $"lacks Cookie: Use or Monitoring: Edit, so gets no notices about {(target == AccessTargetKind.Server ? "servers" : "routers")}."));
                        break;
                    }
                }
                if (string.IsNullOrWhiteSpace(u.Email) && string.IsNullOrWhiteSpace(u.PushoverKey))
                {
                    warnings.Add(new AccessContactWarning(u.DisplayName, "has neither an e-mail address nor a Pushover key."));
                }
            }
            else if (contact.GroupId is { } gid && !await db.Groups.AnyAsync(g => g.Id == gid, ct))
            {
                warnings.Add(new AccessContactWarning("A deleted group", "is still a contact."));
            }
        }
        var monthAgo = Clock.UtcNow.AddDays(-30);
        var addresses = await db.AuditLog.AsNoTracking().Where(a => a.EventType == AuditEvents.LoginSucceeded && a.CreatedAt >= monthAgo && a.IpAddress != null)
            .Select(a => a.IpAddress).Distinct().Take(2).ToListAsync(ct);
        var today = Clock.ToUtc(Clock.LocalNow.Date);
        var level = (await settings.GetAsync<ActionSettings>(ct)).Levels.AccessNotice.Level;
        return new AccessAdminState(cookie.Has(Area.Monitoring, Level.Edit), cookie.Has(Area.AuditLog, Level.View),
            !string.IsNullOrWhiteSpace(configuration["PublicBaseUrl"]),
            s.Contacts.Any(c => c.OutsideHours) || s.Notices.Suspicious.Hours == AccessHours.Everyone, level == ActionLevel.Auto,
            addresses.Count == 1 && await db.AuditLog.CountAsync(a => a.EventType == AuditEvents.LoginSucceeded && a.CreatedAt >= monthAgo, ct) >= 20,
            await db.MikrotikRouters.CountAsync(r => !r.WatchLogins, ct),
            await db.MikrotikRouters.AsNoTracking().Where(r => r.LogGap).Select(r => r.Sensor.Parent.Name).Take(20).ToListAsync(ct),
            warnings,
            await db.CookieCases.CountAsync(c => c.Open && c.Kind == CookieCaseKind.Access, ct),
            await db.AccessReviews.CountAsync(r => r.CreatedAt >= today, ct),
            await db.CookieOutbox.CountAsync(o => o.CreatedAt >= today && (o.Channel == CookieChannel.AccessEmail || o.Channel == CookieChannel.AccessPushover), ct));
    }
}
