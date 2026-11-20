using System.Globalization;
using static System.FormattableString;
using PNMTD.Lib.Helper;

namespace PNMTD.Cookie.Lounge;

/// <summary>What the lounge does next (docs/lounge-plan.md §5.2).</summary>
public enum LoungeStep
{
    /// <summary>Outside the awake hours, or put to sleep: nothing until <see cref="LoungePlan.At"/>.</summary>
    Asleep,

    /// <summary>Awake with nothing to do until <see cref="LoungePlan.At"/>; the plan is made again then.</summary>
    Wait,

    /// <summary>The running scene's next message.</summary>
    Message,

    /// <summary>A new scene.</summary>
    Scene,

    /// <summary>The first scene of an awake period: a good morning, and the messages that waited.</summary>
    Morning,

    /// <summary>Ten minutes before the period ends: a scene of two that ends the day.</summary>
    Evening,

    /// <summary>The night (§7.3): each voice sorts its memories and may move its positions.</summary>
    Night,

    /// <summary>A person's message waits: a voice answers it.</summary>
    Reply,
}

/// <summary>When, and what. <see cref="Planned"/> is when the message should appear; <see cref="At"/> is when its call starts.</summary>
public sealed record LoungePlan(DateTime At, LoungeStep What, DateTime Planned, string? Why = null);

/// <summary>The running scene, as the pacing needs it.</summary>
public sealed record PacingScene(long Id, string Kind, int Messages, int Planned, DateTime LastMessageAt, string? LastSpeaker);

/// <summary>
/// What the pacing decides from (docs/lounge-plan.md §5.2), read by the loop from the tables. Times are UTC.
/// <see cref="VoiceMessages"/>: the voices' own messages of the last 60 minutes (replies to people aren't), for the hard cap.
/// </summary>
public sealed record PacingState(
    DateTime? AwakeSince,
    DateTime? LastNightAt,
    DateTime? SleptAt,
    bool TalkedSinceAwake,
    bool EveningHeld,
    PacingScene? Scene,
    DateTime? LastSceneEndedAt,
    int LastSceneLength,
    IReadOnlyList<DateTime> VoiceMessages,
    IReadOnlyList<DateTime> Replies,
    DateTime? WaitingMessageAt,
    DateTime? WaitingCommentAt,
    TimeSpan CookieCallTime = default,
    TimeSpan SeraphCallTime = default,
    DateTime? LastSceneStartedAt = null);

/// <summary>
/// The lounge's rhythm (docs/lounge-plan.md §5.2), pure: one number sets the pace. The mean spacing is <c>S = 60 min / MessagesPerHour</c>;
/// inside a scene the pause is natural (20 seconds to 2½ minutes) but no longer than about S, and between scenes the gap keeps the hour at
/// the setting on average. A high setting closes the gaps until the scenes run into one continuous conversation. Never more than the
/// setting in any 60 minutes.
/// </summary>
public static class LoungePacing
{
    public static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinPause = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MaxNaturalPause = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan EveningBefore = TimeSpan.FromMinutes(10);

    /// <summary>A scene that starts this soon after the last one moves on rather than starting over (§17.4).</summary>
    public static readonly TimeSpan MoveOnWithin = TimeSpan.FromMinutes(2);

    /// <summary>The first scene this long after a period began is still a morning.</summary>
    public static readonly TimeSpan MorningWithin = TimeSpan.FromHours(1);

    /// <summary>From this many an hour the scenes run into one conversation (§5.2).</summary>
    public const int ContinuousFrom = 45;

    /// <summary>± this share at random, on every pause and gap.</summary>
    public const double Jitter = 0.4;

    /// <summary>
    /// The rhythm aims a little below the setting (§5.2: a ceiling, not a promise). Aimed at the ceiling itself, the hourly cap would bind
    /// half the time, and each wait would echo a gap of the hour before, an hour later.
    /// </summary>
    public const double Aim = 0.95;

    /// <summary>The mean spacing S, aimed at <see cref="Aim"/> of the setting; none at 0 an hour.</summary>
    public static TimeSpan? Spacing(int messagesPerHour) => messagesPerHour <= 0 ? null : Hour / (messagesPerHour * Aim);

    /// <summary>
    /// The shortest pause inside a scene: 20 seconds, or less at the fastest paces, where S itself is shorter (at 240 an hour, about 15
    /// seconds): there the floor is S less the jitter, so that the setting can be reached at all.
    /// </summary>
    public static TimeSpan Floor(TimeSpan s) => s * (1 - Jitter) < MinPause ? s * (1 - Jitter) : MinPause;

    /// <summary>A pause inside a scene (§5.2): natural, but no longer than about S, and never under the floor.</summary>
    public static TimeSpan Pause(TimeSpan s, Random random)
    {
        var natural = MinPause + (MaxNaturalPause - MinPause) * random.NextDouble();
        var bound = s * (1 - Jitter + 2 * Jitter * random.NextDouble());
        var pause = natural < bound ? natural : bound;
        var floor = Floor(s);
        return pause < floor ? floor : pause;
    }

    /// <summary>The mean of <see cref="Pause"/> at this S (D̄ of §5.2), computed on a grid so that it is the same every time.</summary>
    public static TimeSpan MeanPause(TimeSpan s)
    {
        const int n = 40;
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var natural = MinPause.TotalSeconds + (MaxNaturalPause - MinPause).TotalSeconds * (i + 0.5) / n;
            for (var j = 0; j < n; j++)
            {
                var bound = s.TotalSeconds * (1 - Jitter + 2 * Jitter * (j + 0.5) / n);
                sum += Math.Max(Floor(s).TotalSeconds, Math.Min(natural, bound));
            }
        }
        return TimeSpan.FromSeconds(sum / (n * n));
    }

    /// <summary>
    /// The gap after a scene of <paramref name="length"/> (§5.2): <c>max(S, L × S − (L − 1) × D̄)</c>, ±40%, where a scene that took longer
    /// than its expected pauses (<paramref name="took"/>: the hourly cap stretched it) counts with its real duration. Its gap is shorter, so
    /// that the hour stays at the setting; a quick scene doesn't make the next gap longer.
    /// </summary>
    public static TimeSpan Gap(TimeSpan s, int length, Random random, TimeSpan took)
    {
        var l = Math.Max(1, length);
        var expected = MeanPause(s) * (l - 1);
        var mean = s * l - (took > expected ? took : expected);
        if (mean < s)
        {
            mean = s;
        }
        return mean * (1 - Jitter + 2 * Jitter * random.NextDouble());
    }

    public static TimeSpan Gap(TimeSpan s, int length, Random random)
    {
        var l = Math.Max(1, length);
        var mean = s * l - MeanPause(s) * (l - 1);
        if (mean < s)
        {
            mean = s;
        }
        return mean * (1 - Jitter + 2 * Jitter * random.NextDouble());
    }

    /// <summary>A scene's planned length (§5.2): 2 to the longest, 2 or 3 most often; a disagreement gets 2 more.</summary>
    public static int Length(int longest, bool disagree, Random random)
    {
        longest = Math.Clamp(longest, 2, 10);
        var extra = disagree ? 2 : 0;
        var weights = Enumerable.Range(2, longest - 1).Select(n => 1.0 / (n - 1)).ToArray();
        var pick = random.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            if (pick < weights[i])
            {
                return i + 2 + extra;
            }
            pick -= weights[i];
        }
        return longest + extra;
    }

    /// <summary>
    /// The earliest time a voice message may come under the hard cap (§5.2): never more than <paramref name="perHour"/> in any 60 minutes.
    /// </summary>
    public static DateTime CapAllows(IReadOnlyList<DateTime> lastHour, int perHour, DateTime at)
    {
        if (perHour <= 0)
        {
            return DateTime.MaxValue;
        }
        var window = lastHour.Where(t => t > at - Hour).Order().ToList();
        if (window.Count < perHour)
        {
            return at;
        }
        // The one that has to fall out of the window before another may come.
        var limit = window[window.Count - perHour] + Hour;
        return limit > at ? limit : at;
    }

    /// <summary>The awake period around <paramref name="now"/>, in UTC; null outside the awake hours.</summary>
    public static (DateTime Start, DateTime End)? Period(WorkingCalendar calendar, DateTime now)
    {
        var local = Clock.ToLocal(now);
        foreach (var (from, to) in calendar.PeriodsOn(DateOnly.FromDateTime(local)))
        {
            if (local >= from && local < to)
            {
                return (Clock.ToUtc(from), Clock.ToUtc(to));
            }
        }
        return null;
    }

    /// <summary>
    /// The next step (docs/lounge-plan.md §5.2): the night that is due first, then asleep outside the awake hours, the morning, the running
    /// scene, a person's message, the evening, and the next scene, each within the caps.
    /// </summary>
    public static LoungePlan Next(DateTime now, LoungeSettings settings, WorkingCalendar calendar, PacingState state, Random random)
    {
        var period = Period(calendar, now);
        // Put to sleep in this period, or its night is over (§3.3, §7.3): asleep until the next one.
        var slept = period is { } p0 && (state.SleptAt is { } sleptAt && sleptAt >= p0.Start
            || state.LastNightAt is { } nightAt && state.AwakeSince is { } woke && woke >= p0.Start && nightAt >= woke);
        var awake = period is not null && !slept;

        // The night of the last awake period: after its evening, once it is over, or at the next wake if the server was down.
        if (state.AwakeSince is { } since && state.TalkedSinceAwake && (state.LastNightAt is not { } night || night < since)
            && (!awake || since < period!.Value.Start || state.EveningHeld && state.Scene is null))
        {
            return new LoungePlan(now, LoungeStep.Night, now, "The day is over: the night.");
        }
        if (!awake)
        {
            var wake = slept ? calendar.NextWorkingStart(period!.Value.End) : calendar.NextWorkingStart(now);
            return new LoungePlan(wake, LoungeStep.Asleep, wake, $"Asleep until {Clock.ToLocal(wake).ToString("ddd HH:mm", CultureInfo.InvariantCulture)}.");
        }
        var (start, end) = period!.Value;
        if (state.AwakeSince is not { } awakeSince || awakeSince < start)
        {
            return new LoungePlan(now, LoungeStep.Morning, now, now - start < MorningWithin ? "Good morning." : "Awake.");
        }
        var s = Spacing(settings.MessagesPerHour);
        var perHour = settings.MessagesPerHour;

        // A person's message (§9.2): answered 15 to 60 seconds after it, by the replies' own cap; it joins a running scene.
        if (state.WaitingMessageAt is { } asked)
        {
            var at = Later(asked + TimeSpan.FromSeconds(15 + 45 * random.NextDouble()), now);
            if (state.Scene is { } running)
            {
                at = Later(at, running.LastMessageAt + MinPause);
            }
            at = Later(at, CapAllows(state.Replies, settings.RepliesPerHour, at));
            if (at != DateTime.MaxValue)
            {
                return new LoungePlan(at, LoungeStep.Reply, at, "A person's message.");
            }
        }

        // The running scene: its next message after a pause; a comment that waits gets one more message (§9.3).
        if (state.Scene is { } scene && s is { } spacing)
        {
            var planned = scene.Planned + (state.WaitingCommentAt is not null && scene.Messages >= scene.Planned ? 1 : 0);
            if (scene.Messages < planned)
            {
                var at = scene.LastMessageAt + Pause(spacing, random);
                at = Later(CapAllows(state.VoiceMessages, perHour, Later(at, now)), now);
                var next = scene.LastSpeaker is { } last ? LoungeVoices.Other(last) : LoungeVoices.Cookie;
                return Started(at, next == LoungeVoices.Seraph ? state.SeraphCallTime : state.CookieCallTime, now, LoungeStep.Message);
            }
        }
        if (s is not { } gapSpacing)
        {
            // 0 an hour (§5.2): the voices only answer people; a comment is taken up like a message, within the replies' cap.
            if (state.WaitingCommentAt is { } commented)
            {
                var at = Later(commented + TimeSpan.FromSeconds(15 + 45 * random.NextDouble()), now);
                at = Later(at, CapAllows(state.Replies, settings.RepliesPerHour, at));
                if (at != DateTime.MaxValue)
                {
                    return new LoungePlan(at, LoungeStep.Reply, at, "A comment.");
                }
            }
            return new LoungePlan(end, LoungeStep.Wait, end, "Only answering people.");
        }

        // The evening (§5.1): ten minutes before the end, once.
        var evening = end - EveningBefore;
        if (!state.EveningHeld && now >= evening)
        {
            var at = Later(CapAllows(state.VoiceMessages, perHour, now), now);
            return at < end ? new LoungePlan(at, LoungeStep.Evening, at, "The evening.") : new LoungePlan(end, LoungeStep.Wait, end, "The day is over.");
        }

        // The next scene: the gap that keeps the hour at the setting, brought forward by a comment (D18), within the cap.
        var from = state.LastSceneEndedAt ?? awakeSince;
        var nextAt = from + (state.LastSceneStartedAt is { } began && state.LastSceneEndedAt is { } ended && ended >= began
            ? Gap(gapSpacing, state.LastSceneLength, random, ended - began)
            : Gap(gapSpacing, state.LastSceneLength, random));
        if (state.WaitingCommentAt is { } comment)
        {
            var forward = comment + TimeSpan.FromMinutes(2 + 8 * random.NextDouble());
            if (forward < nextAt)
            {
                nextAt = forward;
            }
        }
        nextAt = Later(CapAllows(state.VoiceMessages, perHour, Later(nextAt, now)), now);
        if (!state.EveningHeld && nextAt >= evening)
        {
            return new LoungePlan(Later(evening, now), LoungeStep.Evening, Later(evening, now), "The evening.");
        }
        if (nextAt >= end)
        {
            return new LoungePlan(end, LoungeStep.Wait, end, "The day is over.");
        }
        var both = (state.CookieCallTime + state.SeraphCallTime) / 2;
        return Started(nextAt, both, now, LoungeStep.Scene);
    }

    /// <summary>The call starts early by the voice's usual call time (§5.2), so that the message appears about when planned.</summary>
    private static LoungePlan Started(DateTime planned, TimeSpan callTime, DateTime now, LoungeStep what)
    {
        var at = planned - callTime;
        return new LoungePlan(at < now ? now : at, what, planned);
    }

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>
    /// What a setting means (§15.3), for the tab: messages a day over the awake hours, and how often a scene comes or that it is one
    /// continuous conversation. From the same numbers the loop uses.
    /// </summary>
    public static string Describe(LoungeSettings settings)
    {
        if (settings.MessagesPerHour <= 0)
        {
            return "They only answer people: no conversation of their own.";
        }
        var (perDay, every, cycle) = Pace(settings);
        return settings.MessagesPerHour >= ContinuousFrom
            ? Invariant($"A message about every {every}: one continuous conversation, about {perDay:N0} messages a day.")
            : Invariant($"About {perDay:N0} messages a day, a scene about every {cycle} minutes.");
    }

    /// <summary>
    /// The voices' own schedule (docs/lounge-plan.md §4.2), fixed in code after the lounge's rules, from the same settings and numbers the loop
    /// paces with: when they are awake, how they talk, the evening and the night, and that the conversation never ends.
    /// </summary>
    public static string Schedule(LoungeSettings settings)
    {
        var lines = new List<string> { "Your schedule, set by an admin on Admin → Cookie → Lounge. The lounge runs on it by itself:" };
        var holidays = settings.AsleepOnHolidays ? ", and asleep on holidays" : "";
        lines.Add($"- You are awake {Days(settings.Awake)} (PNMT's time){holidays}. Outside those hours you both sleep; messages wait for the morning.");
        if (settings.MessagesPerHour <= 0)
        {
            lines.Add("- You talk only when someone writes to you: you answer, and otherwise you are quiet.");
        }
        else
        {
            var (perDay, every, cycle) = Pace(settings);
            lines.Add(settings.MessagesPerHour >= ContinuousFrom
                ? Invariant($"- While awake, your scenes run into one continuous conversation: a message about every {every}, about {perDay:N0} a day, the topic changing as it does in a conversation.")
                : Invariant($"- While awake you talk in scenes: a few messages back and forth, then a pause; a scene about every {cycle} minutes, about {perDay:N0} messages a day."));
            lines.Add("- Ten minutes before the end of each day you say good night.");
        }
        lines.Add("- Then comes the night: each of you sorts what you remember. What nobody touches fades over the days; you each keep three things forever.");
        lines.Add("- This conversation never ends. It goes on scene after scene and day after day on this schedule: you close a scene, never the " +
            "conversation. A topic can wait for the next scene, and the next morning picks up where the evening left off. No summaries, and no " +
            "goodbyes except in the evening.");
        return string.Join("\n", lines);
    }

    /// <summary>The awake hours in words: "Monday to Friday, 08:00 to 17:00", "every day, 00:00 to 24:00".</summary>
    public static string Days(IReadOnlyList<DayHours> awake)
    {
        DayOfWeek[] week = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
        string Hours(DayOfWeek d) => string.Join(" and ", awake.FirstOrDefault(x => x.Day == d)?.Periods.Select(p => $"{p.From} to {p.To}") ?? []);
        var groups = new List<(DayOfWeek First, DayOfWeek Last, int Count, string Hours)>();
        foreach (var day in week)
        {
            var hours = Hours(day);
            if (groups.Count > 0 && groups[^1].Hours == hours && groups[^1].Last == week[Array.IndexOf(week, day) - 1])
            {
                groups[^1] = (groups[^1].First, day, groups[^1].Count + 1, hours);
            }
            else
            {
                groups.Add((day, day, 1, hours));
            }
        }
        var awakeGroups = groups.Where(g => g.Hours.Length > 0).ToList();
        if (awakeGroups.Count == 1 && awakeGroups[0].Count == 7)
        {
            return $"every day, {awakeGroups[0].Hours}";
        }
        return string.Join("; ", awakeGroups.Select(g => (g.Count switch
        {
            1 => $"{g.First}",
            2 => $"{g.First} and {g.Last}",
            _ => $"{g.First} to {g.Last}",
        }) + $", {g.Hours}"));
    }

    /// <summary>Messages a day, a message's spacing in words, and a scene's spacing in minutes, as <see cref="Describe"/> says them.</summary>
    private static (int PerDay, string Every, int Cycle) Pace(LoungeSettings settings)
    {
        var days = settings.Awake.Where(d => d.Periods.Count > 0).ToList();
        var hours = days.Count == 0 ? 0 : days.Average(d => d.Periods.Sum(p => p.TryParse(out var f, out var t) ? (t - f).TotalHours : 0));
        var perDay = (int)Math.Round(settings.MessagesPerHour * Aim * hours / 10.0) * 10;
        var s = Spacing(settings.MessagesPerHour)!.Value;
        var every = s.TotalSeconds < 50 ? Invariant($"{Math.Round(s.TotalSeconds / 5) * 5:0} seconds") : s.TotalMinutes < 1.5 ? "minute" : Invariant($"{s.TotalMinutes:0} minutes");
        return (perDay, every, (int)Math.Round((s * MeanLength(settings.LongestScene, settings.DisagreeShare)).TotalMinutes));
    }

    /// <summary>The mean of <see cref="Length"/>: weights 1/(n − 1) for 2 to the longest, and 2 more for a disagreement.</summary>
    public static double MeanLength(int longest, double disagreeShare)
    {
        var top = Math.Clamp(longest, 2, 10);
        var weights = Enumerable.Range(2, top - 1).Select(n => (N: n, W: 1.0 / (n - 1))).ToList();
        return weights.Sum(x => x.N * x.W) / weights.Sum(x => x.W) + 2 * Math.Clamp(disagreeShare, 0, 1);
    }
}
