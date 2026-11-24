using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Lounge;

/// <summary>The rights the lounge reads with (docs/lounge-plan.md §3.2): the lower of Cookie's group's and the lounge's group's.</summary>
public static class LoungeAccess
{
    public static async Task<UserRights?> RightsAsync(PnmtdDbContext db, CookieSettings settings, CookieRound round, CancellationToken ct)
    {
        if ((await settings.GetAsync<LoungeSettings>(ct)).GroupId is not { } groupId)
        {
            return null;
        }
        var levels = await db.Set<GroupRightEntity>().AsNoTracking().Where(r => r.GroupId == groupId).Select(r => new { r.Area, r.Level }).ToListAsync(ct);
        var group = new UserRights(false, UserRights.Combine(levels.Select(l => (l.Area, l.Level))));
        return CookieRound.Lower(await round.RightsAsync(ct) ?? UserRights.None, group);
    }
}

/// <summary>
/// The lounge's loop (docs/lounge-plan.md §3.3, §5, §13): stopped until an admin starts it, independent of Cookie's, and resuming after a
/// restart if it was running; paused, with the reason shown, without the AI, Seraph's model, the group or budget. Each turn reads the state,
/// asks <see cref="LoungePacing"/> what comes next and when, sleeps until then or a signal, and does one step: a message, a scene, a reply,
/// the morning, the evening or the night.
/// </summary>
public sealed class LoungeLoop(IDbContextFactory<PnmtdDbContext> dbs, LoungeSignals signals, CookieSettings settings, IOptionsMonitor<CookieOptions> options,
    IOptionsMonitor<AiOptions> ai, CookieModel model, Seraph seraph, CookieRound round, LoungeVoice voice, LoungeNight night, LoungeGround ground,
    PrivacyFilter privacy, DecisionClient guard, PNMTD.Availability.AvailabilityPlan plan, ILogger<LoungeLoop> logger) : BackgroundService
{
    public const int MaxFailures = 3;
    public static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(30);

    /// <summary>At most this many comments go to one message (§5.2); the rest to the following ones.</summary>
    public const int CommentsPerMessage = 3;

    public const string TalkNow = "talk now";

    /// <summary>The running scene's ground, built once per scene (§5.3).</summary>
    private (long SceneId, LoungeGroundResult Ground)? current;
    private bool talkNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StepAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "The lounge's loop failed");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    /// <summary>One turn of the loop. Public for the tests, which call it with a wait of none: the planned step then runs at once.</summary>
    public async Task<LoungeStep?> StepAsync(CancellationToken ct, bool wait = true)
    {
        talkNow |= signals.Take().Contains(TalkNow);
        var runtime = await RuntimeAsync(ct);
        var o = options.CurrentValue;
        if (!o.Enabled || !runtime.Running)
        {
            talkNow = false;
            await SetAsync(LoungeState.Stopped, o.Enabled ? "An admin starts the lounge on Admin → Cookie → Lounge." : "Cookie__Enabled is not set.", null, ct);
            if (wait)
            {
                await signals.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return null;
        }
        var lounge = await settings.GetAsync<LoungeSettings>(ct);
        if (await BlockedAsync(runtime, lounge, ct) is { } why)
        {
            talkNow = false;
            await SetAsync(LoungeState.Paused, why, null, ct);
            if (wait)
            {
                await signals.WaitAsync(TimeSpan.FromMinutes(1), ct);
            }
            return null;
        }
        var now = Clock.UtcNow;
        var calendar = lounge.Calendar((await plan.SettingsAsync(ct)).Holidays);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var state = await StateAsync(db, runtime, calendar, now, ct);
        LoungePlan next;
        if (talkNow)
        {
            // Talk now (§14): a scene now, awake or not; a running one goes on at once.
            next = new LoungePlan(now, state.Scene is null ? LoungeStep.Scene : LoungeStep.Message, now, "Talk now.");
        }
        else
        {
            next = LoungePacing.Next(now, lounge, calendar, state, new Random(Seed(state)));
            if (state.Scene is { } running && next.What is LoungeStep.Asleep or LoungeStep.Wait or LoungeStep.Night)
            {
                // A scene that runs past the awake hours (the evening, Talk now) ends as it was planned.
                if (running.Messages >= running.Planned)
                {
                    await EndSceneAsync(db, running.Id, "talked", ct);
                    return null;
                }
                var at = running.LastMessageAt + LoungePacing.Pause(LoungePacing.Spacing(lounge.MessagesPerHour) ?? TimeSpan.FromMinutes(1), new Random(Seed(state)));
                next = new LoungePlan(at < now ? now : at, LoungeStep.Message, at);
            }
        }
        if (wait && next.At > now)
        {
            await SetAsync(next.What == LoungeStep.Asleep ? LoungeState.Asleep : LoungeState.Awake, next.What == LoungeStep.Asleep ? next.Why : null, next.At, ct);
            var left = next.At - now;
            await signals.WaitAsync(left < TimeSpan.FromMinutes(10) ? left : TimeSpan.FromMinutes(10), ct);
            return null;
        }
        if (next.What is LoungeStep.Asleep or LoungeStep.Wait)
        {
            await SetAsync(next.What == LoungeStep.Asleep ? LoungeState.Asleep : LoungeState.Awake, next.What == LoungeStep.Asleep ? next.Why : null, next.At, ct);
            return next.What;
        }
        talkNow = false;
        try
        {
            switch (next.What)
            {
                case LoungeStep.Night:
                    await NightAsync(db, runtime, lounge, ct);
                    break;
                case LoungeStep.Morning:
                    await SceneAsync(db, runtime, lounge, calendar, now - LoungePacing.Period(calendar, now)!.Value.Start < LoungePacing.MorningWithin ? "morning" : "scene",
                        true, ct);
                    break;
                case LoungeStep.Evening:
                    await SceneAsync(db, runtime, lounge, calendar, "evening", false, ct);
                    break;
                case LoungeStep.Scene:
                    await SceneAsync(db, runtime, lounge, calendar, "scene", false, ct);
                    break;
                case LoungeStep.Message:
                    await MessageAsync(db, runtime, lounge, calendar, null, ct);
                    break;
                case LoungeStep.Reply:
                    await ReplyAsync(db, runtime, lounge, calendar, ct);
                    break;
            }
            if (runtime.Failures > 0)
            {
                await db.LoungeRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.Failures, 0).SetProperty(r => r.PausedUntil, (DateTime?)null), ct);
            }
        }
        catch (LoungeBudgetException)
        {
            await SystemLineAsync(db, OutOfMoney(), ct);
            await SetAsync(LoungeState.Paused, OutOfMoney(), null, ct);
        }
        catch (LoungePausedException e)
        {
            await SetAsync(LoungeState.Paused, e.Message, null, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A failed call skips the message (§11); three in a row, and the lounge sleeps 30 minutes.
            var failures = runtime.Failures + 1;
            logger.LogWarning("The lounge's {Step} failed ({Failures} in a row): {Error}", next.What, failures, e.Message);
            var until = failures >= MaxFailures ? Clock.UtcNow + FailurePause : (DateTime?)null;
            var reason = until is { } u
                ? $"{failures} calls failed in a row, the last with: {CaseBook.Cut(e.Message, 300)} The lounge tries again at {Clock.ToLocal(u):HH:mm}."
                : null;
            await db.LoungeRuntime.ExecuteUpdateAsync(x => x.SetProperty(r => r.Failures, failures).SetProperty(r => r.PausedUntil, until)
                .SetProperty(r => r.LastError, CaseBook.Cut(e.Message, 500)).SetProperty(r => r.LastErrorAt, Clock.UtcNow)
                .SetProperty(r => r.StateReason, reason), ct);
            if (await db.LoungeRuntime.AsNoTracking().Select(r => r.SceneId).SingleAsync(ct) is { } sceneId)
            {
                db.LoungeSteps.Add(new LoungeStepEntity { SceneId = sceneId, At = Clock.UtcNow, Kind = "error", Title = $"{next.What} failed", Body = e.Message });
                await db.SaveChangesAsync(ct);
                if (until is not null)
                {
                    await EndSceneAsync(db, sceneId, "failed", ct);
                }
            }
        }
        finally
        {
            await db.LoungeRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.Speaking, (string?)null)
                .SetProperty(r => r.State, r => r.State == LoungeState.Speaking ? LoungeState.Awake : r.State), CancellationToken.None);
        }
        return next.What;
    }

    /// <summary>Why the lounge can't talk now (§3.3), or null.</summary>
    private async Task<string?> BlockedAsync(LoungeRuntimeEntity runtime, LoungeSettings lounge, CancellationToken ct)
    {
        if (!ai.CurrentValue.Usable)
        {
            return "The AI is off or has no key (Ai__Enabled, Ai__ApiKey).";
        }
        if (runtime.PausedUntil is { } until && until > Clock.UtcNow)
        {
            return runtime.StateReason ?? $"Paused until {Clock.ToLocal(until):HH:mm}.";
        }
        if (lounge.GroupId is null)
        {
            return "Choose what the lounge reads with on Admin → Cookie → Lounge: everyone reads what Cookie and Seraph say.";
        }
        if (await seraph.ChoiceAsync(ct) is null)
        {
            return "Seraph has no model (Cookie__SeraphModel): it takes two to talk.";
        }
        if (lounge.BudgetUsd > 0 && await model.LoungeSpentAsync(ct) >= lounge.BudgetUsd)
        {
            return OutOfMoney();
        }
        return null;
    }

    /// <summary>The room's line for a spent budget (§11), in their words.</summary>
    public static string OutOfMoney()
    {
        var next = Clock.LocalNow.Date.AddDays(1 - Clock.LocalNow.Day).AddMonths(1);
        return $"Cookie and Seraph are out of coffee money until {next.ToString("d MMMM", CultureInfo.InvariantCulture)}.";
    }

    // --- The state the pacing decides from ------------------------------------------------------------------------------------

    private static async Task<PacingState> StateAsync(PnmtdDbContext db, LoungeRuntimeEntity runtime, WorkingCalendar calendar, DateTime now, CancellationToken ct)
    {
        var period = LoungePacing.Period(calendar, now);
        var hourAgo = now - LoungePacing.Hour;
        var lastHour = await db.LoungeMessages.AsNoTracking().Where(m => (m.From == LoungeVoices.Cookie || m.From == LoungeVoices.Seraph) && m.At >= hourAgo)
            .Select(m => new { m.At, Reply = m.ReplyToId != null }).ToListAsync(ct);
        PacingScene? scene = null;
        if (runtime.SceneId is { } sceneId && await db.LoungeScenes.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sceneId && s.EndedAt == null, ct) is { } s)
        {
            var last = await db.LoungeMessages.AsNoTracking().Where(m => m.SceneId == sceneId && (m.From == LoungeVoices.Cookie || m.From == LoungeVoices.Seraph))
                .OrderByDescending(m => m.Id).Select(m => new { m.At, m.From }).FirstOrDefaultAsync(ct);
            scene = new PacingScene(s.Id, s.Kind, s.Messages, s.PlannedLength, last?.At ?? s.StartedAt, last?.From);
        }
        var ended = await db.LoungeScenes.AsNoTracking().Where(x => x.EndedAt != null && x.Kind != "night").OrderByDescending(x => x.EndedAt)
            .Select(x => new { x.EndedAt, x.Messages }).FirstOrDefaultAsync(ct);
        var eveningHeld = period is { } p && await db.LoungeScenes.AsNoTracking().AnyAsync(x => x.Kind == "evening" && x.StartedAt >= p.Start, ct);
        var talked = runtime.AwakeSince is { } since
            && await db.LoungeMessages.AsNoTracking().AnyAsync(m => (m.From == LoungeVoices.Cookie || m.From == LoungeVoices.Seraph) && m.At >= since, ct);
        var waiting = await db.LoungeMessages.AsNoTracking().Where(m => m.From == "person" && m.Kind == "message" && !m.Answered && m.RefusedWhy == null && m.DeletedAt == null)
            .MinAsync(m => (DateTime?)m.At, ct);
        var comment = await db.LoungeMessages.AsNoTracking().Where(m => m.From == "person" && m.Kind == "comment" && m.TakenById == null && m.RefusedWhy == null
            && m.DeletedAt == null).MinAsync(m => (DateTime?)m.At, ct);
        return new PacingState(runtime.AwakeSince, runtime.LastNightAt, runtime.SleptAt, talked, eveningHeld, scene, ended?.EndedAt, ended?.Messages ?? 0,
            [.. lastHour.Where(m => !m.Reply).Select(m => m.At)], [.. lastHour.Where(m => m.Reply).Select(m => m.At)], waiting, comment,
            await CallTimeAsync(db, LoungeVoices.Cookie, now, ct), await CallTimeAsync(db, LoungeVoices.Seraph, now, ct));
    }

    /// <summary>A voice's median call time over the last day (§5.2): its next call starts that much early.</summary>
    private static async Task<TimeSpan> CallTimeAsync(PnmtdDbContext db, string who, DateTime now, CancellationToken ct)
    {
        var purpose = $"{CookieModel.LoungePurpose}.{who}";
        var day = now.AddDays(-1);
        var times = await db.AiRequests.AsNoTracking().Where(r => r.Purpose == purpose && r.At >= day && r.Outcome == AiOutcome.Ok).OrderByDescending(r => r.Id)
            .Take(50).Select(r => r.DurationMs).ToListAsync(ct);
        if (times.Count == 0)
        {
            return TimeSpan.Zero;
        }
        times.Sort();
        return TimeSpan.FromMilliseconds(Math.Min(times[times.Count / 2], 120_000));
    }

    /// <summary>The same state gives the same plan, so that waking for a signal doesn't move the next message.</summary>
    private static int Seed(PacingState s) =>
        HashCode.Combine(s.Scene?.Id ?? 0, s.Scene?.Messages ?? 0, s.LastSceneEndedAt, s.WaitingMessageAt, s.WaitingCommentAt, s.AwakeSince, s.LastNightAt);

    // --- Scenes and messages ----------------------------------------------------------------------------------------------------

    /// <summary>A new scene (§5.2, §5.3): its ground, seed, opener, length and whether it is a disagreement; then its first message.</summary>
    private async Task SceneAsync(PnmtdDbContext db, LoungeRuntimeEntity runtime, LoungeSettings lounge, WorkingCalendar calendar, string kind, bool morning,
        CancellationToken ct, LoungeMessageEntity? replyTo = null)
    {
        var now = Clock.UtcNow;
        var tracked = await db.LoungeRuntime.SingleAsync(ct);
        if (morning && LoungePacing.Period(calendar, now) is { } period)
        {
            tracked.AwakeSince = period.Start;
        }
        if (tracked.SceneId is { } open)
        {
            await EndSceneAsync(db, open, "talked", ct);
        }
        var p = await privacy.OpenAsync(ct);
        try
        {
            var rights = await LoungeAccess.RightsAsync(db, settings, round, ct) ?? UserRights.None;
            var people = await LoungePeople.BuildAsync(db, p, privacy.Off, ct);
            var last = await db.LoungeScenes.AsNoTracking().Where(s => s.Kind != "night").OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);
            var since = kind == "morning" ? (tracked.LastNightAt?.AddMinutes(-15) ?? now.AddHours(-16)) : last?.StartedAt ?? now.AddHours(-4);
            if (since < now.AddDays(-2))
            {
                since = now.AddDays(-2);
            }
            var rest = now - LoungeGround.SeedRest;
            var today = LoungeDay.TodayUtc;
            var recentSeeds = await db.LoungeScenes.AsNoTracking().Where(s => s.StartedAt >= (today < rest ? today : rest) && s.SeedKey != null)
                .Select(s => new { s.SeedKey, s.StartedAt }).ToListAsync(ct);
            var used = recentSeeds.Where(s => s.StartedAt >= rest).Select(s => s.SeedKey!).ToHashSet();
            var talkedToday = recentSeeds.Where(s => s.StartedAt >= today).Select(s => s.SeedKey!).ToHashSet();
            var g = await GroundAsync(db, tracked, rights, p, people, since, now, used, talkedToday, ct);
            var random = new Random(HashCode.Combine(now.Ticks, last?.Id ?? 0));
            var comments = await WaitingCommentsAsync(db, ct);

            // The seed (§5.3): a comment that waits, the morning's or evening's own, else new and important first, then the others at random.
            LoungeSeed? seed = kind switch
            {
                "morning" => new LoungeSeed($"morning:{now:yyyyMMdd}", "You just woke up: what happened since yesterday evening, and what you remember of yesterday.", null, 0),
                "evening" => new LoungeSeed($"evening:{now:yyyyMMdd}", "The day is ending.", null, 0),
                _ when replyTo is not null => null,
                _ when comments.Count > 0 => new LoungeSeed($"comment:{comments[0].Id}", "A comment in the room, below: take it up.", null, 5),
                _ => await PickSeedAsync(db, g, lounge, people, used, random, now, ct),
            };
            var lastSpeaker = await LastSpeakerAsync(db, ct);
            var opener = replyTo is not null ? Addressed(replyTo, await IdentityNameAsync(ct), lastSpeaker) : seed?.Opener ?? await QuieterAsync(db, ct);
            if (replyTo is null && opener == lastSpeaker && last?.EndedAt is { } lastEnded && now - lastEnded < TimeSpan.FromMinutes(10))
            {
                // Right after the last scene, the other voice opens: nobody answers themselves in a conversation that goes on.
                opener = LoungeVoices.Other(opener);
            }
            var disagree = kind == "scene" && replyTo is null && random.NextDouble() < lounge.DisagreeShare;
            var planned = kind switch
            {
                "morning" => 2 + random.Next(2),
                "evening" => 2,
                "reply" => lounge.MessagesPerHour > 0 ? 2 : 1,
                _ => LoungePacing.Length(lounge.LongestScene, disagree, random),
            };
            var scene = new LoungeSceneEntity
            {
                StartedAt = now, Kind = kind, Opener = opener, SeedKey = seed?.Key, Seed = seed is null ? null : CaseBook.Cut(p.Unmask(seed.Line), 300), Disagree = disagree,
                PlannedLength = planned, GroundHash = g.Hash, Outcome = "talking",
                MoveOn = kind == "scene" && last?.EndedAt is { } ended && now - ended <= LoungePacing.MoveOnWithin,
            };
            db.LoungeScenes.Add(scene);
            await db.SaveChangesAsync(ct);
            tracked.SceneId = scene.Id;
            await db.SaveChangesAsync(ct);
            db.LoungeSteps.Add(new LoungeStepEntity
            {
                SceneId = scene.Id, At = now, Kind = "ground", Title = $"The ground{(seed is null ? ", free talk" : $": {CaseBook.Cut(seed.Line, 200)}")}", Body = g.Json,
                Areas = [.. g.Areas],
            });
            await db.SaveChangesAsync(ct);
            current = (scene.Id, g);
            await MessageAsync(db, tracked, lounge, calendar, replyTo, ct, scene, p, people, rights);
        }
        finally
        {
            await p.SaveAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The running scene's next message (§5.2): the other voice than the last, the code's direction, up to three comments to take up, and
    /// a person's message to answer first.
    /// </summary>
    private async Task MessageAsync(PnmtdDbContext db, LoungeRuntimeEntity runtime, LoungeSettings lounge, WorkingCalendar calendar, LoungeMessageEntity? replyTo,
        CancellationToken ct, LoungeSceneEntity? scene = null, Pseudonyms? privacyIn = null, LoungePeople? peopleIn = null, UserRights? rightsIn = null)
    {
        var tracked = await db.LoungeRuntime.SingleAsync(ct);
        scene ??= tracked.SceneId is { } id ? await db.LoungeScenes.SingleOrDefaultAsync(s => s.Id == id && s.EndedAt == null, ct) : null;
        if (scene is null)
        {
            tracked.SceneId = null;
            await db.SaveChangesAsync(ct);
            return;
        }
        var p = privacyIn ?? await privacy.OpenAsync(ct);
        try
        {
            var rights = rightsIn ?? await LoungeAccess.RightsAsync(db, settings, round, ct) ?? UserRights.None;
            var people = peopleIn ?? await LoungePeople.BuildAsync(db, p, privacy.Off, ct);
            var now = Clock.UtcNow;
            if (current is not { } held || held.SceneId != scene.Id)
            {
                // After a restart: the scene's ground again, from when it began.
                var g0 = await GroundAsync(db, tracked, rights, p, people, scene.StartedAt.AddHours(-2), now, new HashSet<string>(), new HashSet<string>(), ct);
                current = (scene.Id, g0);
            }
            var g = current!.Value.Ground;
            var cookieName = await IdentityNameAsync(ct);
            var lastSpeaker = await db.LoungeMessages.AsNoTracking().Where(m => m.SceneId == scene.Id && (m.From == LoungeVoices.Cookie || m.From == LoungeVoices.Seraph))
                .OrderByDescending(m => m.Id).Select(m => m.From).FirstOrDefaultAsync(ct);
            var who = replyTo is not null ? Addressed(replyTo, cookieName, lastSpeaker)
                : scene.Messages == 0 || lastSpeaker is null ? scene.Opener : LoungeVoices.Other(lastSpeaker);
            var other = LoungeVoice.Name(LoungeVoices.Other(who), cookieName);
            var comments = await WaitingCommentsAsync(db, ct);
            var index = scene.Messages;

            // The direction (§17.4): code's, one or more of its fixed lines.
            var directions = new List<string>();
            if (replyTo is not null)
            {
                directions.Add($"{people.As(replyTo.UserId, p)} wrote to you: answer them first.");
            }
            else if (index == 0)
            {
                directions.Add(scene.Kind switch
                {
                    "morning" => "Good morning: you just woke up." + (scene.Seed is null ? "" : " Start a conversation about the topic above."),
                    "evening" => "It's the end of the day: say good night.",
                    _ when scene.SeedKey is null => "Start a conversation.",
                    _ when scene.MoveOn => "Move on to the topic above, as a conversation does.",
                    _ => "Start a conversation about the topic above.",
                });
            }
            else
            {
                directions.Add($"Answer {other}.");
                if (index == 1 && scene.Disagree)
                {
                    directions.Add($"In this scene you don't agree with {other} on one point. Push back, with an argument from the data; look it up if you're not sure.");
                }
            }
            var byId = comments.Where(c => c.OnId is not null).Select(c => c.OnId!.Value).Distinct().ToList();
            var targets = byId.Count == 0 ? new Dictionary<long, LoungeMessageEntity>()
                : await db.LoungeMessages.AsNoTracking().Where(m => byId.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
            foreach (var c in comments)
            {
                var author = people.As(c.UserId, p);
                var on = c.OnId is { } onId && targets.TryGetValue(onId, out var t)
                    ? t.From == who ? "your message" : LoungeVoices.Known(t.From) ? $"{other}'s message" : $"{people.As(t.UserId, p)}'s message"
                    : "your conversation";
                directions.Add($"{author} commented on {on}: take it into your conversation with {other}. React to it, use it, argue about it. " +
                    $"You don't have to answer {author}.");
            }
            var more = await db.LoungeMessages.AsNoTracking().CountAsync(m => m.From == "person" && m.Kind == "comment" && m.TakenById == null && m.RefusedWhy == null
                && m.DeletedAt == null, ct) > comments.Count;
            var last = index + 1 >= scene.PlannedLength && !more && replyTo is null;
            if (last && scene.Kind != "evening")
            {
                directions.Add("This is the last message for now: close it naturally.");
            }

            var period = LoungePacing.Period(calendar, now);
            // Where they are in their schedule (§4.2): until when they are awake, and when they wake next.
            string When(DateTime at) => Clock.ToLocal(at).Date == Clock.LocalNow.Date.AddDays(1) ? $"tomorrow {Clock.ToLocal(at).ToString("HH:mm", CultureInfo.InvariantCulture)}"
                : Clock.ToLocal(at).ToString("dddd HH:mm", CultureInfo.InvariantCulture);
            var awake = period is { } x
                ? $"You are awake until {Clock.ToLocal(x.End).ToString("HH:mm", CultureInfo.InvariantCulture)}, then you sleep until {When(calendar.NextWorkingStart(x.End))}"
                : $"It is outside your awake hours: an admin asked you to talk now. You wake again {When(calendar.NextWorkingStart(now))}";
            string? yesterday = null;
            if (scene.Kind == "morning" && tracked.LastNightAt is { } nightAt)
            {
                var made = await db.LoungeMemories.AsNoTracking().Where(m => m.Voice == who && m.CreatedAt >= nightAt.AddMinutes(-5)).Select(m => m.Text).Take(5)
                    .ToListAsync(ct);
                yesterday = made.Count == 0 ? null : string.Join("\n", made.Select(t => $"- {t}"));
            }
            var raisedInScene = await db.LoungeMessages.AsNoTracking().AnyAsync(m => m.SceneId == scene.Id && m.CaseId != null, ct);
            var today = LoungeDay.TodayUtc;
            var offerCase = !raisedInScene && lounge.CasesPerDay > 0
                && await db.CookieCases.CountAsync(c => c.Kind == CookieCaseKind.Raised && c.OpenedAt >= today, ct) < lounge.CasesPerDay;
            var positions = LoungeOpinions.Current(tracked.Positions, lounge.Convictions);
            await db.LoungeRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.State, LoungeState.Speaking).SetProperty(r => r.Speaking, who)
                .SetProperty(r => r.StateReason, (string?)null), ct);
            var length = LoungeVoice.LengthFor(scene.Kind, replyTo is not null, new Random(HashCode.Combine(scene.Id, index, replyTo?.Id)));
            var importantLeft = Math.Max(0, lounge.ImportantPerDay
                - await db.LoungeMessages.CountAsync(m => m.From == who && m.Important && m.CaseId == null && m.At >= today, ct));
            // docs/board-messages-plan.md §8.6: send_board_message only for a person who holds Boards: Message and whom this voice answers.
            IReadOnlyList<object>? mayBoard = null;
            string? asker = null;
            if (replyTo is { From: "person", UserId: { } askerId } && await SessionCache.LoadAsync(db, askerId) is { IsActive: true } entry
                && entry.Rights.Has(Area.Boards, Level.Message))
            {
                var actions = await settings.GetAsync<ActionSettings>(ct);
                if (actions.Levels.BoardMessage.Level != ActionLevel.Off && CookieBrain.BoardsOpen(actions, await round.RightsAsync(ct))
                    && await CookieBrain.BoardsYouMayMessageAsync(db, actions, p, ct) is { Count: > 0 } boards)
                {
                    mayBoard = boards;
                    asker = people.As(askerId, p);
                }
            }
            var result = await voice.SpeakAsync(new VoiceCall(who, scene, g, scene.Seed is null ? null : people.Hide(p.Mask(scene.Seed)), directions,
                last || replyTo is not null || comments.Count > 0, replyTo, comments, rights, p, people, lounge, positions, awake,
                await MayEmailAsync(db, lounge, people, p, ct), offerCase, yesterday, length, importantLeft, mayBoard, asker), ct);

            scene.Messages += result.Message is null ? 0 : 1;
            scene.ToolCalls += result.ToolCalls;
            scene.Cost += result.Cost;
            if (result.Message is null)
            {
                await db.SaveChangesAsync(ct);
                await EndSceneAsync(db, scene.Id, "passed", ct);
            }
            else if (scene.Messages >= scene.PlannedLength && !more && (await WaitingCommentsAsync(db, ct)).Count == 0)
            {
                await db.SaveChangesAsync(ct);
                await EndSceneAsync(db, scene.Id, "talked", ct);
            }
            else
            {
                if (scene.Messages >= scene.PlannedLength)
                {
                    // A comment that waits gets one more message (§5.2).
                    scene.PlannedLength = scene.Messages + 1;
                }
                await db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            if (privacyIn is null)
            {
                await p.SaveAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// A person's message (§9.2): answered by the voice it addresses, or the one that didn't speak last; it joins the running scene, or
    /// starts one of its own. With 0 an hour, a comment is taken up the same way.
    /// </summary>
    private async Task ReplyAsync(PnmtdDbContext db, LoungeRuntimeEntity runtime, LoungeSettings lounge, WorkingCalendar calendar, CancellationToken ct)
    {
        var asked = await db.LoungeMessages.AsNoTracking().Where(m => m.From == "person" && m.Kind == "message" && !m.Answered && m.RefusedWhy == null && m.DeletedAt == null)
            .OrderBy(m => m.Id).FirstOrDefaultAsync(ct);
        var tracked = await db.LoungeRuntime.SingleAsync(ct);
        var scene = tracked.SceneId is { } id ? await db.LoungeScenes.SingleOrDefaultAsync(s => s.Id == id && s.EndedAt == null, ct) : null;
        if (scene is null)
        {
            await SceneAsync(db, tracked, lounge, calendar, "reply", false, ct, asked);
            return;
        }
        // Joins the running scene: one more for the reply, and the other voice may chime in.
        scene.PlannedLength += lounge.MessagesPerHour > 0 ? 2 : 1;
        await db.SaveChangesAsync(ct);
        await MessageAsync(db, tracked, lounge, calendar, asked, ct, scene);
    }

    /// <summary>The comments the voices haven't taken up, oldest first, at most three for one message.</summary>
    private static async Task<List<LoungeMessageEntity>> WaitingCommentsAsync(PnmtdDbContext db, CancellationToken ct) =>
        await db.LoungeMessages.AsNoTracking().Where(m => m.From == "person" && m.Kind == "comment" && m.TakenById == null && m.RefusedWhy == null && m.DeletedAt == null)
            .OrderBy(m => m.Id).Take(CommentsPerMessage).ToListAsync(ct);

    /// <summary>@Cookie, @Seraph, or a name at the start; otherwise the voice that didn't speak last (§9.2).</summary>
    public static string Addressed(LoungeMessageEntity m, string cookieName, string? lastSpeaker)
    {
        var text = m.Text.TrimStart();
        bool Starts(string name) => text.StartsWith($"@{name}", StringComparison.OrdinalIgnoreCase) || text.StartsWith($"{name},", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase);
        if (Starts("Seraph"))
        {
            return LoungeVoices.Seraph;
        }
        if (Starts(cookieName) || Starts("Cookie"))
        {
            return LoungeVoices.Cookie;
        }
        if (text.Contains("@seraph", StringComparison.OrdinalIgnoreCase))
        {
            return LoungeVoices.Seraph;
        }
        if (text.Contains($"@{cookieName}", StringComparison.OrdinalIgnoreCase) || text.Contains("@cookie", StringComparison.OrdinalIgnoreCase))
        {
            return LoungeVoices.Cookie;
        }
        return lastSpeaker is null ? LoungeVoices.Cookie : LoungeVoices.Other(lastSpeaker);
    }

    private static async Task<string?> LastSpeakerAsync(PnmtdDbContext db, CancellationToken ct) =>
        await db.LoungeMessages.AsNoTracking().Where(m => m.From == LoungeVoices.Cookie || m.From == LoungeVoices.Seraph).OrderByDescending(m => m.Id)
            .Select(m => m.From).FirstOrDefaultAsync(ct);

    /// <summary>Whoever spoke less today opens a scene without a seed's opener (§5.2).</summary>
    private static async Task<string> QuieterAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var today = LoungeDay.TodayUtc;
        var counts = await db.LoungeMessages.AsNoTracking().Where(m => m.At >= today && (m.From == LoungeVoices.Cookie || m.From == LoungeVoices.Seraph))
            .GroupBy(m => m.From).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var cookie = counts.FirstOrDefault(c => c.Key == LoungeVoices.Cookie)?.Count ?? 0;
        var seraphs = counts.FirstOrDefault(c => c.Key == LoungeVoices.Seraph)?.Count ?? 0;
        return seraphs < cookie ? LoungeVoices.Seraph : LoungeVoices.Cookie;
    }

    /// <summary>
    /// The seed (§5.3): one scene in five is free talk; otherwise what is new and important first, then the others weighted by how much they
    /// matter: open cases not talked about today, the day, a memory or a keepsake, a conviction, who is in. None twice within 4 hours.
    /// </summary>
    private static async Task<LoungeSeed?> PickSeedAsync(PnmtdDbContext db, LoungeGroundResult g, LoungeSettings lounge, LoungePeople people, IReadOnlySet<string> used,
        Random random, DateTime now, CancellationToken ct)
    {
        if (random.NextDouble() < 0.2)
        {
            return null;
        }
        var seeds = g.Seeds.ToList();
        var memories = await db.LoungeMemories.AsNoTracking().OrderByDescending(m => m.RefreshedAt).Take(60).ToListAsync(ct);
        foreach (var m in memories.Where(m => m.Keepsake).Take(6))
        {
            seeds.Add(new LoungeSeed($"keepsake:{m.Id}", $"Something {(m.Voice == LoungeVoices.Seraph ? "Seraph" : "Cookie")} will never forget: {people.Hide(m.Text)}", m.Voice, 0));
        }
        foreach (var m in memories.Where(m => !m.Keepsake).OrderByDescending(m => LoungeMemory.Strength(m, now, lounge.HalfLifeDays)).Take(4))
        {
            seeds.Add(new LoungeSeed($"memory:{m.Id}", $"Something {(m.Voice == LoungeVoices.Seraph ? "Seraph" : "Cookie")} remembers: {people.Hide(m.Text)}", m.Voice, 0));
        }
        foreach (var c in lounge.Convictions)
        {
            seeds.Add(new LoungeSeed($"conviction:{c.Key}", $"The old argument: {c.Question}", c.Owner, 0));
        }
        seeds = [.. seeds.Where(s => !used.Contains(s.Key))];
        if (seeds.Count == 0)
        {
            return null;
        }
        var top = seeds.Max(s => s.Priority);
        if (top >= 3)
        {
            var best = seeds.Where(s => s.Priority == top).ToList();
            return best[random.Next(best.Count)];
        }
        var total = seeds.Sum(s => s.Priority + 1.0);
        var pick = random.NextDouble() * total;
        foreach (var s in seeds)
        {
            pick -= s.Priority + 1.0;
            if (pick <= 0)
            {
                return s;
            }
        }
        return seeds[^1];
    }

    /// <summary>The ground, screened by Jev once per scene when it carries outside text (§5.3, hash-cached); flagged, the outside text is withheld.</summary>
    private async Task<LoungeGroundResult> GroundAsync(PnmtdDbContext db, LoungeRuntimeEntity tracked, UserRights rights, Pseudonyms p, LoungePeople people, DateTime since,
        DateTime now, IReadOnlySet<string> used, IReadOnlySet<string> talkedToday, CancellationToken ct)
    {
        var g = await ground.BuildAsync(db, rights, p, since, now, used, talkedToday, false, people.Hide, ct);
        if (g.Untrusted.Count == 0)
        {
            return g;
        }
        var o = ai.CurrentValue;
        var flaggedHash = Hash("flagged:" + g.Hash);
        bool flagged;
        if (!o.Guard.Configured)
        {
            flagged = o.Guard.Required;
        }
        else if (tracked.GroundHash == g.Hash || tracked.GroundHash == flaggedHash)
        {
            flagged = tracked.GroundHash == flaggedHash;
        }
        else
        {
            var decided = await guard.DecideAsync(new AiCall(CookieModel.LoungePurpose + ".screen"), g.Untrusted,
                new Dictionary<string, DecisionQuestion> { ["injection"] = DecisionQuestion.Noul(AiPipeline.InjectionQuestion) }, ct);
            flagged = (decided.Answers["injection"].Probability ?? 0) >= o.Guard.Threshold;
            tracked.GroundHash = flagged ? flaggedHash : g.Hash;
            await db.SaveChangesAsync(ct);
        }
        return flagged ? await ground.BuildAsync(db, rights, p, since, now, used, talkedToday, true, people.Hide, ct) : g;
    }

    /// <summary>
    /// Who may get a lounge e-mail now (§8.2), as the voices see them: only at a level that isn't Off, with e-mails left today, in working
    /// hours, active users with an address who haven't opted out, work today, and had none this week.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> MayEmailAsync(PnmtdDbContext db, LoungeSettings lounge, LoungePeople people, Pseudonyms p, CancellationToken ct)
    {
        var none = new Dictionary<Guid, string>();
        if (lounge.EmailLevel == ActionLevel.Off || lounge.EmailsPerDay == 0 || lounge.EmailsPerPersonWeek == 0)
        {
            return none;
        }
        var now = Clock.UtcNow;
        if (!(await plan.CalendarAsync(ct)).IsWorking(now))
        {
            return none;
        }
        var today = LoungeDay.TodayUtc;
        var kind = LoungeTools.EmailKind;
        var counted = new[] { "done", "proposed", "running", "approved", "held-back" };
        if (await db.CookieDecisions.CountAsync(d => d.Kind == kind && d.At >= today && counted.Contains(d.Outcome), ct) >= lounge.EmailsPerDay)
        {
            return none;
        }
        var week = now - CookieExecutor.LoungeEmailEvery;
        var had = (await db.CookieDecisions.AsNoTracking().Where(d => d.Kind == kind && d.At >= week && counted.Contains(d.Outcome) && d.Target != null)
            .Select(d => d.Target!).ToListAsync(ct)).ToHashSet();
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive && u.LoungeEmails && u.LoungeMentions && u.Email != null && u.Email != "")
            .Select(u => u.Id).ToListAsync(ct);
        users = [.. users.Where(u => !had.Contains($"user:{u}"))];
        if (users.Count == 0)
        {
            return none;
        }
        var day = DateOnly.FromDateTime(Clock.LocalNow);
        var plan0 = await plan.DayAsync(day, ct, users);
        return users.Where(u => plan0.Closed(day) || plan0.Works(u, day)).Take(30).ToDictionary(u => u, u => people.As(u, p));
    }

    // --- The night ----------------------------------------------------------------------------------------------------------------

    private async Task NightAsync(PnmtdDbContext db, LoungeRuntimeEntity runtime, LoungeSettings lounge, CancellationToken ct)
    {
        var tracked = await db.LoungeRuntime.SingleAsync(ct);
        if (tracked.SceneId is { } open)
        {
            await EndSceneAsync(db, open, "talked", ct);
        }
        var scene = new LoungeSceneEntity { StartedAt = Clock.UtcNow, Kind = "night", Opener = LoungeVoices.Cookie, Outcome = "sleeping", PlannedLength = 0 };
        db.LoungeScenes.Add(scene);
        await db.SaveChangesAsync(ct);
        await db.LoungeRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.State, LoungeState.Asleep).SetProperty(r => r.StateReason, "The night: sorting memories."), ct);
        var p = await privacy.OpenAsync(ct);
        var positions = LoungeOpinions.Current(tracked.Positions, lounge.Convictions);
        var since = tracked.AwakeSince ?? Clock.UtcNow.AddHours(-16);
        var outcome = "slept";
        try
        {
            var people = await LoungePeople.BuildAsync(db, p, privacy.Off, ct);
            foreach (var who in LoungeVoices.Both)
            {
                try
                {
                    var result = await night.RunAsync(who, lounge, p, people, since, positions, scene.Id, ct);
                    scene.Cost += result.Cost;
                }
                catch (Exception e) when (e is not (OperationCanceledException or LoungeBudgetException or LoungePausedException))
                {
                    outcome = "failed";
                    logger.LogWarning("The lounge's night for {Voice} failed: {Error}", who, e.Message);
                    db.LoungeSteps.Add(new LoungeStepEntity { SceneId = scene.Id, At = Clock.UtcNow, Kind = "error", Title = $"The night for {who} failed", Body = e.Message });
                }
            }
        }
        finally
        {
            // One night per awake period, whatever came of it: a failed night isn't tried again and again.
            tracked.LastNightAt = Clock.UtcNow;
            tracked.Positions = LoungeOpinions.Json(positions);
            tracked.SceneId = null;
            scene.EndedAt = Clock.UtcNow;
            scene.Outcome = outcome;
            await db.SaveChangesAsync(CancellationToken.None);
            await p.SaveAsync(CancellationToken.None);
        }
    }

    // --- The runtime --------------------------------------------------------------------------------------------------------------

    private async Task EndSceneAsync(PnmtdDbContext db, long sceneId, string outcome, CancellationToken ct)
    {
        await db.LoungeScenes.Where(s => s.Id == sceneId && s.EndedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.EndedAt, Clock.UtcNow).SetProperty(s => s.Outcome, outcome), ct);
        await db.LoungeRuntime.Where(r => r.SceneId == sceneId).ExecuteUpdateAsync(u => u.SetProperty(r => r.SceneId, (long?)null), ct);
        if (current?.SceneId == sceneId)
        {
            current = null;
        }
    }

    /// <summary>A line of the room's own (§12): closed, out of budget. Not twice in a row.</summary>
    public static async Task SystemLineAsync(PnmtdDbContext db, string text, CancellationToken ct)
    {
        var last = await db.LoungeMessages.AsNoTracking().OrderByDescending(m => m.Id).Select(m => new { m.From, m.Text }).FirstOrDefaultAsync(ct);
        if (last is { From: "system" } && last.Text == text)
        {
            return;
        }
        db.LoungeMessages.Add(new LoungeMessageEntity { At = Clock.UtcNow, From = "system", Kind = "message", Text = text, Answered = true });
        await db.SaveChangesAsync(ct);
    }

    private async Task<string> IdentityNameAsync(CancellationToken ct) => (await settings.GetAsync<IdentitySettings>(ct)).Name;

    private async Task<LoungeRuntimeEntity> RuntimeAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await EnsureAsync(db, ct);
        return await db.LoungeRuntime.AsNoTracking().SingleAsync(ct);
    }

    /// <summary>The one row, created like Cookie's (the braces doubled: the raw SQL is a format string).</summary>
    public static Task EnsureAsync(PnmtdDbContext db, CancellationToken ct) => db.Database.ExecuteSqlRawAsync(
        """INSERT INTO lounge_runtime ("SingletonId", "Running", "State", "Positions", "Failures") VALUES (1, false, 'Stopped', '{{}}', 0) ON CONFLICT DO NOTHING""", ct);

    public static async Task<LoungeRuntimeEntity> RuntimeAsync(PnmtdDbContext db, CancellationToken ct)
    {
        await EnsureAsync(db, ct);
        return await db.LoungeRuntime.SingleAsync(ct);
    }

    private async Task SetAsync(LoungeState state, string? reason, DateTime? nextAt, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.LoungeRuntime.ExecuteUpdateAsync(u => u.SetProperty(r => r.State, state).SetProperty(r => r.StateReason, CaseBook.Cut(reason, 500))
            .SetProperty(r => r.NextAt, nextAt), ct);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
