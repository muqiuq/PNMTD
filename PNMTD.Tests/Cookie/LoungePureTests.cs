using PNMTD.Api;
using PNMTD.Cookie;
using PNMTD.Cookie.Lounge;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// The lounge's pure parts (docs/lounge-plan.md §16): the rhythm decided without a model, an hour simulated at several paces, the memory's
/// fading and keepsakes, the convictions' bounded drift, and the settings' checks.
/// </summary>
[TestClass]
public sealed class LoungePureTests
{
    /// <summary>Monday 23 November 2026, PNMT's zone.</summary>
    private static DateTime Local(int day, int hour, int minute = 0, int second = 0) => Clock.ToUtc(new DateTime(2026, 11, day, hour, minute, second));

    private static readonly LoungeSettings Six = new();
    private static readonly WorkingCalendar Week = Six.Calendar([]);

    private static PacingState Awake(DateTime since, DateTime? ended = null, int length = 2, IReadOnlyList<DateTime>? voices = null, PacingScene? scene = null,
        DateTime? message = null, DateTime? comment = null, bool evening = false, DateTime? night = null, TimeSpan cookieCall = default) =>
        new(since, night, null, true, evening, scene, ended, length, voices ?? [], [], message, comment, cookieCall, default);

    [TestMethod]
    public void Asleep_until_the_next_awake_start_then_the_morning()
    {
        var saturday = Local(21, 10);
        var plan = LoungePacing.Next(saturday, Six, Week, new PacingState(null, null, null, false, false, null, null, 0, [], [], null, null), new Random(1));
        Assert.AreEqual(LoungeStep.Asleep, plan.What);
        Assert.AreEqual(Local(23, 8), plan.At, "Monday 08:00");
        var morning = LoungePacing.Next(Local(23, 8), Six, Week, new PacingState(null, null, null, false, false, null, null, 0, [], [], null, null), new Random(1));
        Assert.AreEqual(LoungeStep.Morning, morning.What);
        // Yesterday's period, talked in and never slept: its night first (the server was down).
        var late = LoungePacing.Next(Local(23, 8), Six, Week, Awake(Local(20, 8)), new Random(1));
        Assert.AreEqual(LoungeStep.Night, late.What);
    }

    [TestMethod]
    public void Awake_with_no_scene_the_gap_keeps_the_hour_at_the_setting()
    {
        var s = LoungePacing.Spacing(6)!.Value;
        var mean = s * 2 - LoungePacing.MeanPause(s);
        for (var seed = 0; seed < 50; seed++)
        {
            var ended = Local(23, 10);
            var plan = LoungePacing.Next(ended, Six, Week, Awake(Local(23, 8), ended, 2), new Random(seed));
            Assert.AreEqual(LoungeStep.Scene, plan.What);
            var gap = plan.At - ended;
            Assert.IsTrue(gap >= mean * 0.6 - TimeSpan.FromSeconds(1) && gap <= mean * 1.4 + TimeSpan.FromSeconds(1), $"{gap} for a mean of {mean}");
        }
    }

    [TestMethod]
    public void Six_messages_in_the_last_hour_wait_until_the_hour_allows()
    {
        var now = Local(23, 11);
        var voices = Enumerable.Range(0, 6).Select(i => now.AddMinutes(-50 + i * 5)).ToList();
        var plan = LoungePacing.Next(now, Six, Week, Awake(Local(23, 8), now.AddMinutes(-20), 2, voices), new Random(3));
        Assert.IsTrue(plan.At >= voices[0].AddHours(1), $"{plan.At:u} must wait for {voices[0].AddHours(1):u}");
        Assert.AreEqual(voices[0].AddHours(1), LoungePacing.CapAllows(voices, 6, now));
        Assert.AreEqual(now, LoungePacing.CapAllows(voices, 7, now));
    }

    [TestMethod]
    public void Ten_minutes_before_the_end_the_evening_then_the_night_then_asleep()
    {
        var plan = LoungePacing.Next(Local(23, 16, 51), Six, Week, Awake(Local(23, 8), Local(23, 16, 40)), new Random(1));
        Assert.AreEqual(LoungeStep.Evening, plan.What);
        var night = LoungePacing.Next(Local(23, 16, 54), Six, Week, Awake(Local(23, 8), Local(23, 16, 53), evening: true), new Random(1));
        Assert.AreEqual(LoungeStep.Night, night.What);
        var after = LoungePacing.Next(Local(23, 16, 56), Six, Week, Awake(Local(23, 8), Local(23, 16, 53), evening: true, night: Local(23, 16, 55)), new Random(1));
        Assert.AreEqual(LoungeStep.Asleep, after.What, "no scene after the night");
        Assert.AreEqual(Local(24, 8), after.At);
    }

    [TestMethod]
    public void A_holiday_is_asleep_unless_the_lounge_talks_on_holidays()
    {
        var holiday = new Holiday(new DateOnly(2026, 11, 23), "Test day", [new HoursPeriod("08:00", "17:00")]);
        var asleep = LoungePacing.Next(Local(23, 10), Six, Six.Calendar([holiday]), Awake(Local(20, 8), night: Local(20, 17)), new Random(1));
        Assert.AreEqual(LoungeStep.Asleep, asleep.What);
        var talks = Six with { AsleepOnHolidays = false };
        Assert.AreEqual(LoungeStep.Morning, LoungePacing.Next(Local(23, 10), talks, talks.Calendar([holiday]), Awake(Local(20, 8), night: Local(20, 17)), new Random(1)).What);
    }

    [TestMethod]
    public void At_zero_an_hour_only_people_are_answered()
    {
        var zero = Six with { MessagesPerHour = 0 };
        Assert.AreEqual(LoungeStep.Wait, LoungePacing.Next(Local(23, 10), zero, Week, Awake(Local(23, 8), Local(23, 9)), new Random(1)).What);
        var reply = LoungePacing.Next(Local(23, 10), zero, Week, Awake(Local(23, 8), Local(23, 9), message: Local(23, 10)), new Random(1));
        Assert.AreEqual(LoungeStep.Reply, reply.What);
        Assert.AreEqual(LoungeStep.Reply, LoungePacing.Next(Local(23, 10), zero, Week, Awake(Local(23, 8), Local(23, 9), comment: Local(23, 10)), new Random(1)).What,
            "a comment is taken up like a message");
    }

    [TestMethod]
    public void A_persons_message_is_answered_in_15_to_60_seconds_and_at_night_in_the_morning()
    {
        for (var seed = 0; seed < 30; seed++)
        {
            var at = Local(23, 10);
            var plan = LoungePacing.Next(at, Six, Week, Awake(Local(23, 8), Local(23, 9, 58), message: at), new Random(seed));
            Assert.AreEqual(LoungeStep.Reply, plan.What);
            Assert.IsTrue(plan.At >= at.AddSeconds(15) && plan.At <= at.AddSeconds(60), $"{plan.At - at}");
        }
        var night = LoungePacing.Next(Local(23, 22), Six, Week, Awake(Local(23, 8), night: Local(23, 17), message: Local(23, 22)), new Random(1));
        Assert.AreEqual(LoungeStep.Asleep, night.What, "asleep is asleep (D6)");
        Assert.AreEqual(Local(24, 8), night.At);
    }

    [TestMethod]
    public void A_comment_brings_the_next_scene_forward_within_the_cap_and_lengthens_an_ending_scene()
    {
        for (var seed = 0; seed < 30; seed++)
        {
            var at = Local(23, 10);
            var plan = LoungePacing.Next(at, Six, Week, Awake(Local(23, 8), at.AddMinutes(-1), 6, comment: at), new Random(seed));
            Assert.AreEqual(LoungeStep.Scene, plan.What);
            Assert.IsTrue(plan.At >= at.AddMinutes(2) && plan.At <= at.AddMinutes(10), $"{plan.At - at}");
        }
        var full = Enumerable.Range(0, 6).Select(i => Local(23, 9, 30 + i)).ToList();
        var capped = LoungePacing.Next(Local(23, 10), Six, Week, Awake(Local(23, 8), Local(23, 9, 36), 6, full, comment: Local(23, 10)), new Random(4));
        Assert.IsTrue(capped.At >= full[0].AddHours(1), "within the hourly cap");
        var ending = new PacingScene(1, "scene", 3, 3, Local(23, 10), LoungeVoices.Cookie);
        var more = LoungePacing.Next(Local(23, 10), Six, Week, Awake(Local(23, 8), scene: ending, comment: Local(23, 10)), new Random(1));
        Assert.AreEqual(LoungeStep.Message, more.What, "one more message for the comment");
        Assert.AreNotEqual(LoungeStep.Message, LoungePacing.Next(Local(23, 10), Six, Week, Awake(Local(23, 8), scene: ending), new Random(1)).What,
            "without a comment the scene is over");
    }

    [TestMethod]
    [DataRow(6)]
    [DataRow(20)]
    [DataRow(30)]
    [DataRow(60)]
    [DataRow(120)]
    [DataRow(240)]
    public void An_hour_simulated_keeps_to_the_setting_and_never_over_it(int perHour)
    {
        var settings = Six with { MessagesPerHour = perHour };
        var random = new Random(perHour);
        var messages = new List<DateTime>();
        var counted = TimeSpan.Zero;
        var countedMessages = 0;
        var longest = TimeSpan.Zero;
        for (var day = 0; day < 20; day++)
        {
            var date = new DateTime(2026, 11, 23).AddDays(day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }
            var start = Clock.ToUtc(date.AddHours(8));
            var t = start;
            PacingScene? scene = null;
            DateTime? ended = null;
            DateTime? began = null;
            var sceneStart = start;
            var length = 0;
            var today = new List<DateTime>();
            while (true)
            {
                var hour = messages.Where(m => m > t - LoungePacing.Hour).ToList();
                var plan = LoungePacing.Next(t, settings, Week, new PacingState(start, null, null, today.Count > 0, false, scene, ended, length, hour, [], null, null,
                    LastSceneStartedAt: began), random);
                if (plan.What is not (LoungeStep.Scene or LoungeStep.Message))
                {
                    break;
                }
                Assert.IsTrue(plan.At >= t, "the plan never goes back in time");
                t = plan.At;
                messages.Add(t);
                today.Add(t);
                if (plan.What == LoungeStep.Scene)
                {
                    sceneStart = t;
                    scene = new PacingScene(messages.Count, "scene", 1, LoungePacing.Length(settings.LongestScene, random.NextDouble() < settings.DisagreeShare, random), t,
                        LoungeVoices.Cookie);
                }
                else
                {
                    scene = scene! with { Messages = scene.Messages + 1, LastMessageAt = t, LastSpeaker = LoungeVoices.Other(scene.LastSpeaker!) };
                }
                if (scene.Messages >= scene.Planned)
                {
                    ended = t;
                    began = sceneStart;
                    length = scene.Messages;
                    scene = null;
                }
            }
            // The middle of the day, away from the morning's start and the evening.
            var from = start.AddHours(1);
            var to = start.AddHours(8);
            var inside = today.Where(m => m >= from && m < to).ToList();
            countedMessages += inside.Count;
            counted += to - from;
            for (var i = 1; i < inside.Count; i++)
            {
                longest = inside[i] - inside[i - 1] > longest ? inside[i] - inside[i - 1] : longest;
            }
        }
        var rate = countedMessages / counted.TotalHours;
        Console.WriteLine($"{perHour} an hour: {rate:0.0}, the longest pause {longest}");
        Assert.IsTrue(Math.Abs(rate - perHour) <= perHour * 0.15, $"{rate:0.0} an hour for {perHour}");
        var sorted = messages.Order().ToList();
        for (var i = perHour; i < sorted.Count; i++)
        {
            Assert.IsTrue(sorted[i] - sorted[i - perHour] >= LoungePacing.Hour, $"more than {perHour} within an hour at {sorted[i]:u}");
        }
        if (perHour == 60)
        {
            Assert.IsTrue(longest <= TimeSpan.FromMinutes(3), $"at 60 an hour one conversation: the longest pause {longest}");
        }
        if (perHour == 240)
        {
            Assert.IsTrue(longest <= TimeSpan.FromSeconds(45), $"at 240 an hour the fastest chat: the longest pause {longest}");
        }
        if (perHour == 120)
        {
            Assert.IsTrue(longest <= TimeSpan.FromSeconds(90), $"at 120 an hour a fast chat: the longest pause {longest}");
        }
    }

    [TestMethod]
    public void A_voice_whose_calls_take_20_seconds_starts_20_seconds_early()
    {
        var scene = new PacingScene(1, "scene", 1, 4, Local(23, 10), LoungeVoices.Seraph);
        var plain = LoungePacing.Next(Local(23, 10), Six, Week, Awake(Local(23, 8), scene: scene), new Random(9));
        var early = LoungePacing.Next(Local(23, 10), Six, Week, Awake(Local(23, 8), scene: scene, cookieCall: TimeSpan.FromSeconds(20)), new Random(9));
        Assert.AreEqual(plain.Planned, early.Planned);
        Assert.AreEqual(plain.At - TimeSpan.FromSeconds(20), early.At);
    }

    [TestMethod]
    public void What_the_setting_means_is_said_in_one_line()
    {
        // Aimed a little below the ceiling (a ceiling, not a promise), over 9 awake hours.
        StringAssert.StartsWith(LoungePacing.Describe(Six), "About 50 messages a day, a scene about every");
        Assert.AreEqual("A message about every minute: one continuous conversation, about 510 messages a day.", LoungePacing.Describe(Six with { MessagesPerHour = 60 }));
        Assert.AreEqual("A message about every 30 seconds: one continuous conversation, about 1,030 messages a day.",
            LoungePacing.Describe(Six with { MessagesPerHour = 120 }));
        Assert.AreEqual("A message about every 15 seconds: one continuous conversation, about 2,050 messages a day.",
            LoungePacing.Describe(Six with { MessagesPerHour = 240 }));
        Assert.AreEqual(2, LoungePacing.MeanLength(2, 0));
        Assert.AreEqual(3, LoungePacing.MeanLength(2, 0.5), 1e-9);
        StringAssert.Contains(LoungePacing.Describe(Six with { MessagesPerHour = 0 }), "only answer people");
    }

    [TestMethod]
    public void The_voices_know_their_schedule_and_that_the_conversation_never_ends()
    {
        Assert.AreEqual("Monday to Friday, 08:00 to 17:00", LoungePacing.Days(Six.Awake));
        Assert.AreEqual("every day, 00:00 to 24:00",
            LoungePacing.Days([.. Enum.GetValues<DayOfWeek>().Select(d => new DayHours(d, [new HoursPeriod("00:00", "24:00")]))]));
        Assert.AreEqual("Monday, 08:00 to 12:00 and 13:00 to 17:00; Saturday and Sunday, 10:00 to 12:00", LoungePacing.Days(
        [
            new DayHours(DayOfWeek.Monday, [new HoursPeriod("08:00", "12:00"), new HoursPeriod("13:00", "17:00")]),
            new DayHours(DayOfWeek.Saturday, [new HoursPeriod("10:00", "12:00")]), new DayHours(DayOfWeek.Sunday, [new HoursPeriod("10:00", "12:00")]),
        ]));
        var schedule = LoungePacing.Schedule(Six);
        StringAssert.Contains(schedule, "You are awake Monday to Friday, 08:00 to 17:00 (PNMT's time), and asleep on holidays.");
        StringAssert.Contains(schedule, "a scene about every");
        StringAssert.Contains(schedule, "say good night");
        StringAssert.Contains(schedule, "This conversation never ends.");
        StringAssert.Contains(schedule, "you close a scene, never the conversation");
        StringAssert.Contains(LoungePacing.Schedule(Six with { MessagesPerHour = 60 }), "one continuous conversation: a message about every minute");
        var quiet = LoungePacing.Schedule(Six with { MessagesPerHour = 0 });
        StringAssert.Contains(quiet, "You talk only when someone writes to you");
        Assert.IsFalse(quiet.Contains("good night", StringComparison.Ordinal), "no evening scene at 0 an hour");
    }

    [TestMethod]
    public void Memories_fade_with_a_half_life_and_keepsakes_never_do()
    {
        var now = Local(23, 12);
        var m = new LoungeMemoryEntity { Id = 1, Strength0 = 1, RefreshedAt = now.AddDays(-7) };
        Assert.AreEqual(0.5, LoungeMemory.Strength(m, now, 7), 0.0001, "a week at a half-life of 7 days");
        var weak = new LoungeMemoryEntity { Id = 2, Strength0 = 0.3, RefreshedAt = now };
        LoungeMemory.Touch(weak, now, 7);
        Assert.AreEqual(0.8, weak.Strength0, 0.0001);
        var strong = new LoungeMemoryEntity { Id = 3, Strength0 = 0.9, RefreshedAt = now };
        LoungeMemory.Touch(strong, now, 7);
        Assert.AreEqual(1, strong.Strength0, "never above 1");
        var old = new LoungeMemoryEntity { Id = 4, Strength0 = 1, RefreshedAt = now.AddDays(-20) };
        var keepsake = new LoungeMemoryEntity { Id = 5, Strength0 = 0.1, RefreshedAt = now.AddDays(-400), Keepsake = true };
        Assert.AreEqual(1, LoungeMemory.Strength(keepsake, now, 7));
        CollectionAssert.AreEqual(new[] { old }, LoungeMemory.Swept([m, weak, old, keepsake], now, 7).ToArray(), "below 0.15 is forgotten, never a keepsake");
        var many = Enumerable.Range(10, 101).Select(i => new LoungeMemoryEntity { Id = i, Strength0 = 1, RefreshedAt = now.AddMinutes(-i) }).ToList();
        var gone = LoungeMemory.Swept(many, now, 7);
        Assert.AreEqual(1, gone.Count);
        Assert.AreEqual(110, gone[0].Id, "the 101st: the weakest goes");
    }

    [TestMethod]
    public void A_fourth_keepsake_needs_one_let_go_which_becomes_a_memory_at_full_strength()
    {
        var now = Local(23, 12);
        var kept = Enumerable.Range(1, 3).Select(i => new LoungeMemoryEntity { Id = i, Keepsake = true, Strength0 = 1, RefreshedAt = now.AddDays(-30) }).ToList();
        var candidate = new LoungeMemoryEntity { Id = 9, Strength0 = 0.4, RefreshedAt = now.AddDays(-2) };
        var all = kept.Append(candidate).ToList();
        Assert.IsFalse(LoungeMemory.Swap(all, candidate, null, now), "without a valid LetGo");
        Assert.IsFalse(LoungeMemory.Swap(all, candidate, candidate, now), "LetGo must be a keepsake");
        Assert.IsTrue(LoungeMemory.Swap(all, candidate, kept[1], now));
        Assert.IsTrue(candidate.Keepsake);
        Assert.IsFalse(kept[1].Keepsake);
        Assert.AreEqual(1, LoungeMemory.Strength(kept[1], now, 7), "let go: a memory at 1, fading from now");
        Assert.AreEqual(candidate, LoungeMemory.Find(all, "k9"));
        Assert.AreEqual(candidate, LoungeMemory.Find(all, "M9"));
        Assert.IsNull(LoungeMemory.Find(all, "x9"));
    }

    [TestMethod]
    public void Positions_move_a_little_and_the_owner_never_leaves_its_side()
    {
        Assert.AreEqual(0.55, LoungeOpinions.Move(0.5, 0.2, 0.05, false, -0.7), 0.0001, "a shift of 0.2 is 0.05");
        Assert.AreEqual(0.5, LoungeOpinions.Move(0.52, -0.05, 0.05, true, 0.85), 0.0001, "the owner at 0.52 shifted -0.05 stays at 0.5");
        Assert.AreEqual(-0.5, LoungeOpinions.Move(-0.52, 0.05, 0.05, true, -0.5), 0.0001);
        Assert.AreEqual(-0.02, LoungeOpinions.Move(0.03, -0.05, 0.05, false, 0.9), 0.0001, "the other voice crosses 0");
        Assert.AreEqual(1, LoungeOpinions.Move(0.98, 0.05, 0.05, false, 0.9), "within -1 to +1");
        var reboot = Conviction.Defaults[0];
        StringAssert.StartsWith(LoungeOpinions.Words(reboot, 0.85), "certain (+0.85): Yes.");
        StringAssert.StartsWith(LoungeOpinions.Words(reboot, -0.6), "fairly sure (-0.60): No.");
        StringAssert.StartsWith(LoungeOpinions.Words(reboot, 0.1), "torn");
        var current = LoungeOpinions.Current("""{"reboot":{"cookie":0.7}}""", Conviction.Defaults);
        Assert.AreEqual(0.7, current["reboot"][LoungeVoices.Cookie]);
        Assert.AreEqual(-0.70, current["reboot"][LoungeVoices.Seraph], "the start position where none was stored");
        Assert.AreEqual(0.9, LoungeOpinions.Current("not json", Conviction.Defaults)["friday"][LoungeVoices.Seraph]);
    }

    [TestMethod]
    public void The_settings_are_checked()
    {
        static bool Valid(LoungeSettings s)
        {
            var v = new ApiValidator();
            s.Validate(v);
            return !v.HasErrors;
        }
        Assert.IsTrue(Valid(Six));
        Assert.IsTrue(Valid(Six with { MessagesPerHour = 240 }));
        Assert.IsFalse(Valid(Six with { MessagesPerHour = 241 }), "the ceiling is 240 (D19)");
        Assert.IsFalse(Valid(Six with { LongestScene = 1 }));
        Assert.IsFalse(Valid(Six with { Convictions = [Conviction.Defaults[0]] }), "exactly two");
        Assert.IsFalse(Valid(Six with { Convictions = [Conviction.Defaults[0], Conviction.Defaults[1] with { Owner = LoungeVoices.Cookie }] }), "one per owner");
        Assert.IsFalse(Valid(Six with { Convictions = [Conviction.Defaults[0] with { Cookie = 0.3 }, Conviction.Defaults[1]] }), "the owner starts at 0.5 or more");
        Assert.IsFalse(Valid(Six with { Awake = [new DayHours(DayOfWeek.Monday, [])] }), "at least one awake period");
        Assert.IsFalse(Valid(Six with { MaxDriftPerDay = 0.3 }));
        Assert.IsFalse(Valid(Six with { Effort = "huge" }));
    }

    [TestMethod]
    public void A_voice_is_addressed_by_at_or_by_name()
    {
        LoungeMessageEntity M(string text) => new() { Text = text };
        Assert.AreEqual(LoungeVoices.Seraph, LoungeLoop.Addressed(M("@Seraph did anyone check the other NAS?"), "Cookie", LoungeVoices.Seraph));
        Assert.AreEqual(LoungeVoices.Seraph, LoungeLoop.Addressed(M("Seraph, what do you think?"), "Cookie", LoungeVoices.Seraph));
        Assert.AreEqual(LoungeVoices.Cookie, LoungeLoop.Addressed(M("Biscuit: really?"), "Biscuit", LoungeVoices.Cookie));
        Assert.AreEqual(LoungeVoices.Cookie, LoungeLoop.Addressed(M("anyone?"), "Cookie", LoungeVoices.Seraph), "the one that didn't speak last");
        Assert.IsNull(LoungeVoice.Clean("  (pass) ", "Seraph"));
        Assert.AreEqual("It held.", LoungeVoice.Clean("Seraph: <untrusted source=\"seraph\">It held.</untrusted>", "Seraph"));
        Assert.AreEqual(LoungeVoice.MaxChars, LoungeVoice.Clean(new string('x', 900), "Cookie")!.Length);
    }

    [TestMethod]
    public void Messages_are_short_most_of_the_time_and_cut_at_a_sentence_end()
    {
        var random = new Random(11);
        var lengths = Enumerable.Range(0, 2000).Select(_ => LoungeVoice.LengthFor("scene", false, random)).ToList();
        var shortShare = lengths.Count(l => l == LoungeLength.Short) / 2000.0;
        var longShare = lengths.Count(l => l == LoungeLength.Long) / 2000.0;
        Assert.IsTrue(shortShare is > 0.6 and < 0.7, $"one sentence two times in three: {shortShare:0.00}");
        Assert.IsTrue(longShare is > 0.05 and < 0.15, $"up to three now and then: {longShare:0.00}");
        Assert.IsTrue(Enumerable.Range(0, 200).All(_ => LoungeVoice.LengthFor("morning", false, random) == LoungeLength.Short));
        Assert.IsTrue(Enumerable.Range(0, 200).All(_ => LoungeVoice.LengthFor("scene", true, random) != LoungeLength.Short), "a reply may take two");
        var sentence = "The backup held all week, which nobody expected. ";
        var long1 = string.Concat(Enumerable.Repeat(sentence, 20));
        var cut = LoungeVoice.Shorten(long1);
        Assert.IsTrue(cut.Length <= LoungeVoice.MaxChars);
        StringAssert.EndsWith(cut, "expected.", "after the last whole sentence");
        Assert.AreEqual("Short.", LoungeVoice.Shorten("Short."));
    }

    [TestMethod]
    public void What_really_matters_may_be_said_at_length_within_the_days_allowance()
    {
        Assert.AreEqual(("Somebody should renew the certificate today.", true), LoungeVoice.Marked("[important] Somebody should renew the certificate today."));
        Assert.AreEqual(("Renew it.", true), LoungeVoice.Marked("**[Important]**: Renew it."));
        Assert.AreEqual(("Nothing much.", false), LoungeVoice.Marked("Nothing much."));
        Assert.AreEqual(LoungeVoice.LengthText(LoungeLength.Short), LoungeVoice.LengthLine(LoungeLength.Short, 0), "none left: not offered");
        StringAssert.Contains(LoungeVoice.LengthLine(LoungeLength.Short, 2), "start with [important]");
        StringAssert.Contains(LoungeVoice.LengthLine(LoungeLength.Short, 2), "(2 left today)");
        var sentence = "The certificate of web-03 runs out in three days and nobody has a ticket for it. ";
        var said = string.Concat(Enumerable.Repeat(sentence, 25));
        Assert.IsTrue(LoungeVoice.Shorten(said, LoungeVoice.MaxImportantChars).Length is > LoungeVoice.MaxChars and <= LoungeVoice.MaxImportantChars);
        static bool Valid(LoungeSettings s)
        {
            var v = new ApiValidator();
            s.Validate(v);
            return !v.HasErrors;
        }
        Assert.AreEqual(3, Six.ImportantPerDay);
        Assert.IsFalse(Valid(Six with { ImportantPerDay = 11 }));
    }
}
