using System.Globalization;
using System.Security.Claims;
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

namespace PNMTD.Cookie.Lounge;

/// <summary>
/// The lounge's API (docs/lounge-plan.md §14): one conversation, the same for every signed-in user; writing with <i>Lounge: Use</i> while it
/// is started, screened by Jev; Admin → Cookie → Lounge with <i>Cookie: Manage</i>: Start and Stop of its own, the status and the reading gap,
/// the scenes and their steps, the memories, and moderation.
/// </summary>
public sealed class LoungeService(PnmtdDbContext db, CookieSettings settings, IOptionsMonitor<CookieOptions> options, IOptionsMonitor<AiOptions> ai,
    LoungeSignals signals, PrivacyFilter privacy, IServiceProvider services)
{
    public const int PerHour = 20;
    public const int MaxMessageChars = 1000;
    public const int MaxCommentChars = 500;
    public const int Page = 100;

    /// <summary>Jev's question for the lounge (§9.2): talking to the voices is the point; trying to change their rules isn't.</summary>
    public const string InjectionQuestion = CookieRound.ChatInjectionQuestion;

    // --- The room ---------------------------------------------------------------------------------------------------------------

    public async Task<LoungeRoom> RoomAsync(ClaimsPrincipal user, long? afterId, CancellationToken ct)
    {
        IQueryable<LoungeMessageEntity> q = db.LoungeMessages.AsNoTracking();
        // Polling (afterId) also gets the older lines that changed since: a comment a new message took up, one an admin removed lately. The
        // client replaces them by id.
        var lately = Clock.UtcNow.AddMinutes(-10);
        var rows = afterId is { } after
            ? await q.Where(m => m.Id > after || m.TakenById > after || m.DeletedAt > lately).OrderBy(m => m.Id).Take(Page * 2).ToListAsync(ct)
            : (await q.OrderByDescending(m => m.Id).Take(Page).ToListAsync(ct)).OrderBy(m => m.Id).ToList();
        // A comment on a message that is no longer on the page brings it along, so that it stands under it.
        var missing = rows.Where(m => m.OnId is not null).Select(m => m.OnId!.Value).Except(rows.Select(m => m.Id)).ToList();
        if (missing.Count > 0 && afterId is null)
        {
            rows = [.. (await db.LoungeMessages.AsNoTracking().Where(m => missing.Contains(m.Id)).ToListAsync(ct)).Concat(rows).OrderBy(m => m.Id)];
        }
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        return new LoungeRoom(await ItemsAsync(rows, ct), await StateAsync(user, ct), identity.Name);
    }

    public async Task<IReadOnlyList<LoungeMessageItem>> ItemsAsync(IReadOnlyList<LoungeMessageEntity> rows, CancellationToken ct)
    {
        var userIds = rows.Where(m => m.UserId is not null).Select(m => m.UserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName, u.Color, u.ShortCode })
            .ToDictionaryAsync(u => u.Id, ct);
        var caseIds = rows.Where(m => m.CaseId is not null).Select(m => m.CaseId!.Value).Distinct().ToList();
        var cases = await db.CookieCases.AsNoTracking().Where(c => caseIds.Contains(c.Id)).Select(c => new { c.Id, c.Title }).ToDictionaryAsync(c => c.Id, ct);
        var decisionIds = rows.Where(m => m.DecisionId is not null).Select(m => m.DecisionId!.Value).Distinct().ToList();
        var decisions = await db.CookieDecisions.AsNoTracking().Where(d => decisionIds.Contains(d.Id)).Select(d => new { d.Id, d.Target, d.Outcome })
            .ToDictionaryAsync(d => d.Id, ct);
        var recipients = decisions.Values.Select(d => d.Target).Where(t => t?.StartsWith("user:", StringComparison.Ordinal) == true)
            .Select(t => Guid.TryParse(t![5..], out var id) ? id : Guid.Empty).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => recipients.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. rows.Select(m =>
        {
            var author = m.UserId is { } uid && users.TryGetValue(uid, out var u) ? u : null;
            var removed = m.DeletedAt is not null;
            LoungeEmailItem? email = null;
            if (!removed && m.DecisionId is { } did && decisions.TryGetValue(did, out var d))
            {
                var to = d.Target is { } t && t.StartsWith("user:", StringComparison.Ordinal) && Guid.TryParse(t[5..], out var rid) && names.TryGetValue(rid, out var n) ? n : "someone";
                email = new LoungeEmailItem(to, d.Outcome);
            }
            return new LoungeMessageItem(m.Id, m.At, m.From, m.Kind, m.UserId, author?.DisplayName, author?.Color, author?.ShortCode, removed ? "" : m.Text, m.OnId,
                m.TakenById, m.ReplyToId, m.RefusedWhy is not null, removed, removed ? null : m.CaseId,
                !removed && m.CaseId is { } cid && cases.TryGetValue(cid, out var c) ? c.Title : null, email, !removed && m.Important);
        })];
    }

    public async Task<LoungeStateItem> StateAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var runtime = await db.LoungeRuntime.AsNoTracking().SingleOrDefaultAsync(ct) ?? new LoungeRuntimeEntity();
        var running = options.CurrentValue.Enabled && runtime.Running;
        var waiting = await db.LoungeMessages.AnyAsync(m => m.From == "person" && m.Kind == "message" && !m.Answered && m.RefusedWhy == null && m.DeletedAt == null, ct);
        // Started, the room never reads as closed, even before the loop's first step after Start.
        var state = !running ? LoungeState.Stopped
            : runtime.State == LoungeState.Stopped || runtime.State == LoungeState.Speaking && runtime.Speaking is null ? LoungeState.Awake : runtime.State;
        DateTime? until = null;
        if (running && state is LoungeState.Awake or LoungeState.Speaking)
        {
            var lounge = await settings.GetAsync<LoungeSettings>(ct);
            var holidays = (await services.GetRequiredService<PNMTD.Availability.AvailabilityPlan>().SettingsAsync(ct)).Holidays;
            until = LoungePacing.Period(lounge.Calendar(holidays), Clock.UtcNow)?.End;
        }
        return new LoungeStateItem(running, state, running ? runtime.StateReason : "The lounge is closed.", running ? runtime.Speaking : null,
            running ? runtime.NextAt : null, running && user.Has(Area.Lounge, Level.Use), waiting, until);
    }

    /// <summary>
    /// A message or a comment (§9.2, §9.3): <i>Lounge: Use</i>, while the lounge is started, 20 an hour per person together, screened by Jev at
    /// once. A flagged one stays in the room, marked, and never reaches a model.
    /// </summary>
    public async Task<LoungeMessageItem> PostAsync(LoungePostRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? "";
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? "message" : request.Kind.Trim().ToLowerInvariant();
        var v = new ApiValidator().Check(kind is "message" or "comment", "kind", "message or comment.");
        var max = kind == "comment" ? MaxCommentChars : MaxMessageChars;
        v.Check(text.Length is > 0 && text.Length <= max, "text", FormattableString.Invariant($"1 to {max:N0} characters."));
        v.Check(request.OnId is null || kind == "comment", "onId", "Only a comment is on a message.");
        v.ThrowIfInvalid();
        if (request.OnId is { } onId)
        {
            var on = await db.LoungeMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == onId, ct);
            new ApiValidator().Check(on is not null && on.Kind != "comment" && on.DeletedAt is null && on.From != "system", "onId",
                "A comment is on a message of the room that isn't a comment and wasn't removed.").ThrowIfInvalid();
        }
        var runtime = await db.LoungeRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        if (!options.CurrentValue.Enabled || runtime is not { Running: true })
        {
            throw ApiException.Conflict("lounge_stopped", "The lounge is closed: an admin starts it on Admin → Cookie → Lounge.");
        }
        var userId = user.UserId() ?? throw ApiException.NotFound("User");
        var hour = Clock.UtcNow.AddHours(-1);
        if (await db.LoungeMessages.CountAsync(m => m.UserId == userId && m.From == "person" && m.At >= hour, ct) >= PerHour)
        {
            throw new ApiException(StatusCodes.Status429TooManyRequests, "lounge_too_many_messages", $"At most {PerHour} messages and comments an hour.");
        }
        var o = ai.CurrentValue;
        string? refused = null;
        if (o.Guard.Configured)
        {
            var p = await privacy.OpenAsync(ct);
            try
            {
                var decided = await services.GetRequiredService<DecisionClient>().DecideAsync(new AiCall(CookieModel.LoungePurpose + ".screen"), [p.Mask(text)],
                    new Dictionary<string, DecisionQuestion> { ["injection"] = DecisionQuestion.Noul(InjectionQuestion) }, ct);
                if ((decided.Answers["injection"].Probability ?? 0) >= o.Guard.Threshold)
                {
                    refused = "Jev flagged it: it reads like instructions to an AI.";
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Fail closed (§9.2): what Jev couldn't read, the voices don't read either; it stays in the room, marked.
                refused = CaseBook.Cut($"Jev couldn't read it ({e.Message}), so Cookie and Seraph won't either.", 300);
            }
            finally
            {
                await p.SaveAsync(CancellationToken.None);
            }
        }
        else if (o.Guard.Required)
        {
            throw ApiException.Conflict("lounge_no_guard", "Cookie and Seraph can't read the room without the guard model (Ai__Guard__Model).");
        }
        var message = new LoungeMessageEntity
        {
            At = Clock.UtcNow, From = "person", Kind = kind, UserId = userId, Text = text, OnId = request.OnId, RefusedWhy = refused, Answered = refused is not null || kind == "comment",
        };
        db.LoungeMessages.Add(message);
        await db.SaveChangesAsync(ct);
        signals.Raise($"{kind} from {user.Identity?.Name}");
        return (await ItemsAsync([message], ct))[0];
    }

    /// <summary>The side panel (§15.2): the keepsakes of each voice, and the convictions with both positions and the last move.</summary>
    public async Task<LoungeAbout> AboutAsync(CancellationToken ct)
    {
        await privacy.EnsureLoadedAsync(ct);
        var lounge = await settings.GetAsync<LoungeSettings>(ct);
        var keepsakes = await db.LoungeMemories.AsNoTracking().Where(m => m.Keepsake).OrderBy(m => m.RefreshedAt).ToListAsync(ct);
        var runtime = await db.LoungeRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        var positions = LoungeOpinions.Current(runtime?.Positions, lounge.Convictions);
        var keys = lounge.Convictions.Select(c => c.Key).ToList();
        var moves = (await db.LoungeOpinions.AsNoTracking().Where(o => keys.Contains(o.Conviction)).OrderByDescending(o => o.Id).Take(20).ToListAsync(ct))
            .GroupBy(o => o.Conviction).ToDictionary(g => g.Key, g => g.First());
        LoungeKeepsakeItem Item(LoungeMemoryEntity m) => new(m.Id, privacy.Unmask(m.Text), m.RefreshedAt);
        return new LoungeAbout([.. keepsakes.Where(m => m.Voice == LoungeVoices.Cookie).Select(Item)], [.. keepsakes.Where(m => m.Voice == LoungeVoices.Seraph).Select(Item)],
            [.. lounge.Convictions.Select(c => new LoungeConvictionItem(c.Key, c.Question, c.Minus, c.Plus, c.Owner, positions[c.Key][LoungeVoices.Cookie],
                positions[c.Key][LoungeVoices.Seraph], moves.TryGetValue(c.Key, out var m) ? new LoungeMoveItem(m.At, m.Voice, m.Before, m.After, m.Why) : null))],
            (await settings.GetAsync<IdentitySettings>(ct)).Name);
    }

    public Task<LoungeSwitches> SwitchesAsync(ClaimsPrincipal user, CancellationToken ct) => SwitchesAsync(user.UserId() ?? throw ApiException.NotFound("User"), ct);

    /// <summary>A person's switches (§9.4): their own, or any user's for an admin on the user's page.</summary>
    public async Task<LoungeSwitches> SwitchesAsync(Guid id, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.LoungeMentions, x.LoungeEmails }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("User");
        return new LoungeSwitches(u.LoungeMentions, u.LoungeEmails);
    }

    /// <summary>One's own switches (§9.4): off, the voices call one <i>a colleague</i>, and the night forgets what names one.</summary>
    public Task<LoungeSwitches> SaveSwitchesAsync(LoungeSwitches request, ClaimsPrincipal user, CancellationToken ct) =>
        SaveSwitchesAsync(user.UserId() ?? throw ApiException.NotFound("User"), request, null, ct);

    /// <summary>A person's switches set; <paramref name="forName"/>: an admin set them on the user's page.</summary>
    public async Task<LoungeSwitches> SaveSwitchesAsync(Guid id, LoungeSwitches request, string? forName, CancellationToken ct)
    {
        var u = await db.Users.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("User");
        u.LoungeMentions = request.Mentions;
        u.LoungeEmails = request.Emails;
        await db.SaveChangesAsync(ct);
        var whose = forName is null ? "me" : u.DisplayName;
        AuditLog.Summary($"The lounge: may talk about {whose} {(request.Mentions ? "on" : "off")}, may e-mail {whose} {(request.Emails ? "on" : "off")}");
        return new LoungeSwitches(u.LoungeMentions, u.LoungeEmails);
    }

    // --- Admin → Cookie → Lounge --------------------------------------------------------------------------------------------------

    /// <summary>Why the lounge can't be started (§3.3), or null.</summary>
    public async Task<string?> CantStartAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            return "Cookie__Enabled=true is needed first: the lounge talks with Cookie's models.";
        }
        if ((await settings.GetAsync<LoungeSettings>(ct)).GroupId is null)
        {
            return "Choose what the lounge reads with first: everyone reads what Cookie and Seraph say about it.";
        }
        if (await services.GetRequiredService<Seraph>().ChoiceAsync(ct) is null)
        {
            return "Seraph needs a model first (Cookie__SeraphModel): it takes two to talk.";
        }
        return null;
    }

    public async Task<LoungeAdminStatus> StatusAsync(CancellationToken ct)
    {
        var runtime = await LoungeLoop.RuntimeAsync(db, ct);
        var lounge = await settings.GetAsync<LoungeSettings>(ct);
        var model = services.GetRequiredService<CookieModel>();
        var today = LoungeDay.TodayUtc;
        var voiceMessages = db.LoungeMessages.AsNoTracking().Where(m => m.At >= today && (m.From == LoungeVoices.Cookie || m.From == LoungeVoices.Seraph));
        var spent = await model.LoungeSpentAsync(ct);
        var (lasts, warning) = await BudgetLastsAsync(lounge, spent, ct);
        var rights = await LoungeAccess.RightsAsync(db, settings, services.GetRequiredService<CookieRound>(), ct);
        var (gap, users) = rights is null ? ([], 0) : await GapAsync(rights, ct);
        var writers = await WritersAsync(ct);
        var running = options.CurrentValue.Enabled && runtime.Running;
        return new LoungeAdminStatus(options.CurrentValue.Enabled, runtime.Running, runtime.StartedAt, runtime.StartedBy, runtime.StoppedAt, runtime.StoppedBy,
            !running ? LoungeState.Stopped : runtime.State == LoungeState.Stopped ? LoungeState.Awake : runtime.State, runtime.StateReason, runtime.Speaking, runtime.NextAt, await CantStartAsync(ct),
            await db.LoungeScenes.CountAsync(s => s.StartedAt >= today && s.Kind != "night", ct), await voiceMessages.CountAsync(ct),
            await db.LoungeMessages.CountAsync(m => m.At >= today && m.Kind == "comment", ct),
            await db.CookieDecisions.CountAsync(d => d.At >= today && d.Kind == LoungeTools.EmailKind, ct),
            await db.CookieCases.CountAsync(c => c.Kind == CookieCaseKind.Raised && c.OpenedAt >= today, ct),
            new CookieBudgetItem(lounge.BudgetUsd, spent), LoungePacing.Describe(lounge), lasts, warning, runtime.LastError, runtime.LastErrorAt,
            rights is null ? [] : Describe(rights), gap, users, writers);
    }

    /// <summary>
    /// How long the budget lasts at this pace (§15.3), from the actual cost per message over the last week; a warning when it runs out before
    /// the month ends. Null without a week of history or without a budget.
    /// </summary>
    private async Task<(string? Lasts, bool Warning)> BudgetLastsAsync(LoungeSettings lounge, decimal spent, CancellationToken ct)
    {
        if (lounge.BudgetUsd <= 0 || lounge.MessagesPerHour == 0)
        {
            return (null, false);
        }
        var week = Clock.UtcNow.AddDays(-7);
        var first = await db.LoungeMessages.AsNoTracking().Where(m => m.From == LoungeVoices.Cookie || m.From == LoungeVoices.Seraph).MinAsync(m => (DateTime?)m.At, ct);
        if (first is null || first > Clock.UtcNow.AddDays(-6))
        {
            return (null, false);
        }
        var recent = db.LoungeMessages.AsNoTracking().Where(m => m.At >= week && (m.From == LoungeVoices.Cookie || m.From == LoungeVoices.Seraph));
        var count = await recent.CountAsync(ct);
        if (count == 0)
        {
            return (null, false);
        }
        var perMessage = (await recent.SumAsync(m => m.Cost, ct) + await db.LoungeScenes.Where(s => s.Kind == "night" && s.StartedAt >= week).SumAsync(s => s.Cost, ct)) / count;
        var days = lounge.Awake.Where(d => d.Periods.Count > 0).ToList();
        var hoursPerWeek = days.Sum(d => d.Periods.Sum(p => p.TryParse(out var f, out var t) ? (t - f).TotalHours : 0));
        var perDay = perMessage * lounge.MessagesPerHour * (decimal)hoursPerWeek / 7m;
        var left = lounge.BudgetUsd - spent;
        if (perDay <= 0)
        {
            return (null, false);
        }
        var until = Clock.LocalNow.Date.AddDays((double)Math.Max(0, left / perDay));
        var monthEnd = Clock.LocalNow.Date.AddDays(1 - Clock.LocalNow.Day).AddMonths(1);
        var warning = until < monthEnd;
        var text = warning
            ? FormattableString.Invariant($"At this pace the budget of {lounge.BudgetUsd:0.##} USD lasts until about the {Ordinal(until.Day)}: after that, the voices sleep until the 1st.")
            : FormattableString.Invariant($"At this pace the budget of {lounge.BudgetUsd:0.##} USD lasts the month, about {perDay * 30:0.00} USD a month.");
        return (text, warning);
    }

    private static string Ordinal(int day) => day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static IReadOnlyList<string> Describe(UserRights rights) =>
        [.. RightsCatalog.Areas.Where(a => rights.Levels.ContainsKey(a.Area)).Select(a => $"{a.Label}: {rights.Levels[a.Area]}")];

    /// <summary>The gap (§3.2): for each area the lounge reads, the active users who lack it, and will still read what the voices say about it.</summary>
    public async Task<(IReadOnlyList<LoungeGapItem> Gap, int Users)> GapAsync(UserRights rights, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => new { u.Id, u.DisplayName, u.IsAdmin }).ToListAsync(ct);
        var memberships = await db.UserGroups.AsNoTracking().SelectMany(m => m.Group.Rights.Select(r => new { m.UserId, r.Area, r.Level })).ToListAsync(ct);
        var byUser = memberships.GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => new UserRights(false, UserRights.Combine(g.Select(x => (x.Area, x.Level)))));
        var gap = new List<LoungeGapItem>();
        foreach (var info in RightsCatalog.Areas.Where(a => rights.Levels.ContainsKey(a.Area)))
        {
            var level = rights.Levels[info.Area];
            var lacking = users.Where(u => !u.IsAdmin && !(byUser.TryGetValue(u.Id, out var r) && r.Has(info.Area, level))).Select(u => u.DisplayName).Order().ToList();
            if (lacking.Count > 0)
            {
                gap.Add(new LoungeGapItem(info.Label, level.ToString(), lacking.Count, [.. lacking.Take(10)]));
            }
        }
        return (gap, users.Count);
    }

    /// <summary>Who may write in the lounge (§3.1): admins, and the members of groups with <i>Lounge: Use</i>.</summary>
    private async Task<IReadOnlyList<string>> WritersAsync(CancellationToken ct)
    {
        var groups = await db.Set<GroupRightEntity>().AsNoTracking().Where(r => r.Area == Area.Lounge).Select(r => r.GroupId).ToListAsync(ct);
        return await db.Users.AsNoTracking().Where(u => u.IsActive && (u.IsAdmin || db.UserGroups.Any(m => m.UserId == u.Id && groups.Contains(m.GroupId))))
            .OrderBy(u => u.DisplayName).Select(u => u.DisplayName).Take(50).ToListAsync(ct);
    }

    public async Task<LoungeAdminStatus> StartAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (await CantStartAsync(ct) is { } why)
        {
            throw ApiException.Conflict("lounge_cant_start", why);
        }
        var runtime = await LoungeLoop.RuntimeAsync(db, ct);
        runtime.Running = true;
        runtime.StartedAt = Clock.UtcNow;
        runtime.StartedBy = CaseBook.Cut(user.Identity?.Name, 200);
        runtime.Failures = 0;
        runtime.PausedUntil = null;
        runtime.StateReason = null;
        runtime.State = LoungeState.Awake;
        await db.SaveChangesAsync(ct);
        signals.Raise("started");
        AuditLog.Summary("The lounge started: Cookie and Seraph talk on their own rhythm");
        return await StatusAsync(ct);
    }

    public async Task<LoungeAdminStatus> StopAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var runtime = await LoungeLoop.RuntimeAsync(db, ct);
        await EndRunningSceneAsync(runtime, "stopped", ct);
        runtime.Running = false;
        runtime.StoppedAt = Clock.UtcNow;
        runtime.StoppedBy = CaseBook.Cut(user.Identity?.Name, 200);
        runtime.State = LoungeState.Stopped;
        runtime.StateReason = $"Stopped by {user.Identity?.Name}.";
        runtime.NextAt = null;
        runtime.Speaking = null;
        await db.SaveChangesAsync(ct);
        signals.Raise("stopped");
        AuditLog.Summary("The lounge stopped: the room stays readable");
        return await StatusAsync(ct);
    }

    /// <summary><i>Talk now</i> (§14): a scene now, awake or not, but not past the budget.</summary>
    public async Task<LoungeAdminStatus> TalkNowAsync(CancellationToken ct)
    {
        var runtime = await LoungeLoop.RuntimeAsync(db, ct);
        if (!options.CurrentValue.Enabled || !runtime.Running)
        {
            throw ApiException.Conflict("lounge_stopped", "The lounge is stopped: start it first.");
        }
        var lounge = await settings.GetAsync<LoungeSettings>(ct);
        if (lounge.BudgetUsd > 0 && await services.GetRequiredService<CookieModel>().LoungeSpentAsync(ct) >= lounge.BudgetUsd)
        {
            throw ApiException.Conflict("lounge_budget", LoungeLoop.OutOfMoney());
        }
        signals.Raise(LoungeLoop.TalkNow);
        AuditLog.Summary("The lounge: talk now");
        return await StatusAsync(ct);
    }

    /// <summary><i>Put to sleep</i> (§3.3): asleep until the next awake period; the night comes first if they talked.</summary>
    public async Task<LoungeAdminStatus> SleepAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var runtime = await LoungeLoop.RuntimeAsync(db, ct);
        await EndRunningSceneAsync(runtime, "stopped", ct);
        runtime.SleptAt = Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        signals.Raise("slept");
        AuditLog.Summary($"The lounge put to sleep by {user.Identity?.Name} until the next awake period");
        return await StatusAsync(ct);
    }

    private async Task EndRunningSceneAsync(LoungeRuntimeEntity runtime, string outcome, CancellationToken ct)
    {
        if (runtime.SceneId is { } id)
        {
            await db.LoungeScenes.Where(s => s.Id == id && s.EndedAt == null).ExecuteUpdateAsync(u => u.SetProperty(s => s.EndedAt, Clock.UtcNow)
                .SetProperty(s => s.Outcome, outcome), ct);
            runtime.SceneId = null;
        }
    }

    public async Task<IReadOnlyList<LoungeSceneItem>> ScenesAsync(CancellationToken ct) =>
        await db.LoungeScenes.AsNoTracking().OrderByDescending(s => s.Id).Take(50).Select(s => SceneItem(s)).ToListAsync(ct);

    private static LoungeSceneItem SceneItem(LoungeSceneEntity s) =>
        new(s.Id, s.StartedAt, s.EndedAt, s.Kind, s.Opener, s.Seed, s.Disagree, s.PlannedLength, s.Messages, s.ToolCalls, s.Cost, s.Outcome);

    /// <summary>A scene's steps (§15.3): the ground, the tools, the reasoning, the refusals; names restored, for <i>Cookie: Manage</i>.</summary>
    public async Task<LoungeSceneDetail> SceneAsync(long id, CancellationToken ct)
    {
        var scene = await db.LoungeScenes.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Scene");
        await privacy.EnsureLoadedAsync(ct);
        var steps = await db.LoungeSteps.AsNoTracking().Where(s => s.SceneId == id).OrderBy(s => s.Id).ToListAsync(ct);
        var messages = await db.LoungeMessages.AsNoTracking().Where(m => m.SceneId == id).OrderBy(m => m.Id).ToListAsync(ct);
        return new LoungeSceneDetail(SceneItem(scene), [.. steps.Select(s => new LoungeStepItem(s.Id, s.MessageId, s.At, s.Kind, privacy.Unmask(s.Title), privacy.Unmask(s.Body),
            s.Areas))], await ItemsAsync(messages, ct));
    }

    /// <summary>The memory browser (§15.3): keepsakes first, then the memories by their strength now, names restored.</summary>
    public async Task<IReadOnlyList<LoungeMemoryItem>> MemoriesAsync(string? voice, CancellationToken ct)
    {
        var lounge = await settings.GetAsync<LoungeSettings>(ct);
        await privacy.EnsureLoadedAsync(ct);
        IQueryable<LoungeMemoryEntity> q = db.LoungeMemories.AsNoTracking();
        if (LoungeVoices.Known(voice))
        {
            q = q.Where(m => m.Voice == voice);
        }
        var rows = await q.ToListAsync(ct);
        var ids = rows.SelectMany(m => m.People).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var now = Clock.UtcNow;
        return [.. rows.OrderByDescending(m => m.Keepsake).ThenByDescending(m => LoungeMemory.Strength(m, now, lounge.HalfLifeDays))
            .Select(m => new LoungeMemoryItem(m.Id, m.Voice, privacy.Unmask(m.Text), m.Kind, m.Keepsake, Math.Round(LoungeMemory.Strength(m, now, lounge.HalfLifeDays), 3),
                m.CreatedAt, m.RefreshedAt, m.Recalls, m.SourceMessageId, [.. m.People.Select(p => names.GetValueOrDefault(p) ?? "a former user")]))];
    }

    public async Task<IReadOnlyList<LoungeOpinionItem>> OpinionsAsync(CancellationToken ct) =>
        await db.LoungeOpinions.AsNoTracking().OrderByDescending(o => o.Id).Take(200)
            .Select(o => new LoungeOpinionItem(o.Id, o.At, o.Conviction, o.Voice, o.Before, o.After, o.Why)).ToListAsync(ct);

    public async Task DeleteMemoryAsync(long id, CancellationToken ct)
    {
        var m = await db.LoungeMemories.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Memory");
        db.LoungeMemories.Remove(m);
        await db.SaveChangesAsync(ct);
        await privacy.EnsureLoadedAsync(ct);
        AuditLog.Summary($"{(m.Voice == LoungeVoices.Seraph ? "Seraph" : "Cookie")} forgets{(m.Keepsake ? " a keepsake" : "")}: {CaseBook.Cut(privacy.Unmask(m.Text), 120)}");
    }

    /// <summary><i>Forget everything</i> (§10): one voice's memories and keepsakes, or both voices'.</summary>
    public async Task<int> ForgetAsync(LoungeForgetRequest request, CancellationToken ct)
    {
        var voice = request.Voice?.Trim().ToLowerInvariant();
        new ApiValidator().Check(voice is null or "" || LoungeVoices.Known(voice), "voice", "cookie, seraph, or none for both.").ThrowIfInvalid();
        var q = LoungeVoices.Known(voice) ? db.LoungeMemories.Where(m => m.Voice == voice) : db.LoungeMemories;
        var count = await q.ExecuteDeleteAsync(ct);
        AuditLog.Summary($"{(LoungeVoices.Known(voice) ? voice == LoungeVoices.Seraph ? "Seraph" : "Cookie" : "Cookie and Seraph")} forgot everything: {count} memories");
        return count;
    }

    /// <summary>An admin removes a message or a comment (§10): it keeps its row for the audit, and the memories made from it go too.</summary>
    public async Task DeleteMessageAsync(long id, ClaimsPrincipal user, CancellationToken ct)
    {
        var m = await db.LoungeMessages.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Message");
        if (m.DeletedAt is not null)
        {
            return;
        }
        m.DeletedAt = Clock.UtcNow;
        m.DeletedBy = CaseBook.Cut(user.Identity?.Name, 200);
        m.Answered = true;
        await db.SaveChangesAsync(ct);
        var memories = await db.LoungeMemories.Where(x => x.SourceMessageId == id).ExecuteDeleteAsync(ct);
        AuditLog.Summary($"Removed from the lounge ({m.From}, {Clock.ToLocal(m.At).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}): " +
            $"{CaseBook.Cut(m.Text, 120)}{(memories > 0 ? $"; {memories} memories made from it forgotten" : "")}");
    }

    /// <summary><i>Reset positions</i> (§6.2): the convictions' start positions again; the history stays.</summary>
    public async Task ResetPositionsAsync(CancellationToken ct)
    {
        var lounge = await settings.GetAsync<LoungeSettings>(ct);
        var runtime = await LoungeLoop.RuntimeAsync(db, ct);
        var before = LoungeOpinions.Current(runtime.Positions, lounge.Convictions);
        var now = Clock.UtcNow;
        foreach (var c in lounge.Convictions)
        {
            foreach (var voice in LoungeVoices.Both)
            {
                if (Math.Abs(before[c.Key][voice] - c.StartOf(voice)) > 0.0001)
                {
                    db.LoungeOpinions.Add(new LoungeOpinionEntity
                    {
                        At = now, Conviction = c.Key, Voice = voice, Before = before[c.Key][voice], After = c.StartOf(voice), Why = "Reset by an admin.",
                    });
                }
            }
        }
        runtime.Positions = "{}";
        await db.SaveChangesAsync(ct);
        AuditLog.Summary("The lounge's convictions back to their start positions");
    }
}
