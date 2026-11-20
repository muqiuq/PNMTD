using System.Text.RegularExpressions;
using PNMTD.Api;

namespace PNMTD.Cookie.Lounge;

/// <summary>
/// A conviction (docs/lounge-plan.md §6.1): a question with two poles, an owner whose very strong opinion it is, and where each voice
/// starts on it, from −1 (<see cref="Minus"/>) to +1 (<see cref="Plus"/>).
/// </summary>
public sealed record Conviction(string Key, string Question, string Minus, string Plus, string Owner, double Cookie, double Seraph)
{
    /// <summary>The defaults (§6.1): Cookie's on reboots, Seraph's on Fridays, each on the other side of the other's.</summary>
    public static readonly IReadOnlyList<Conviction> Defaults =
    [
        new("reboot", "Is turning it off and on again a fix?",
            "No. A reboot buries the evidence; find the cause or it comes back.",
            "Yes. A reboot that works is a fix, and uptime is a vanity metric.", LoungeVoices.Cookie, 0.85, -0.70),
        new("friday", "Should anything be changed on a Friday?",
            "Of course. A good change is good on any day; waiting only piles them up.",
            "Never. Friday changes are how weekends die.", LoungeVoices.Seraph, -0.50, 0.90),
    ];

    public double StartOf(string voice) => voice == LoungeVoices.Seraph ? Seraph : Cookie;
}

/// <summary>The two voices' names as code and the tables write them.</summary>
public static class LoungeVoices
{
    public const string Cookie = "cookie";
    public const string Seraph = "seraph";

    public static readonly IReadOnlyList<string> Both = [Cookie, Seraph];

    public static string Other(string voice) => voice == Cookie ? Seraph : Cookie;

    public static bool Known(string? voice) => voice is Cookie or Seraph;
}

/// <summary>
/// Admin → Cookie → Lounge (docs/lounge-plan.md §12): the rhythm, what the voices read with, their powers, memory and convictions, and
/// the budget. No On switch: Start and Stop are the runtime's (§3.3), as Cookie's are.
/// </summary>
public sealed partial record LoungeSettings : ICookieSection
{
    public static string Section => "lounge";

    /// <summary>The ceiling of <see cref="MessagesPerHour"/> (D19): a message about every 15 seconds.</summary>
    public const int MaxMessagesPerHour = 240;

    /// <summary>When the voices are awake, per weekday (§5.1).</summary>
    public IReadOnlyList<DayHours> Awake { get; init; } = WorkingCalendar.DefaultWeek;

    /// <summary>The availability plan's holidays are closed days for the lounge too.</summary>
    public bool AsleepOnHolidays { get; init; } = true;

    /// <summary>The voices' own messages an hour (§5.2), 0 to 240; from about 45 they run into one continuous conversation.</summary>
    public int MessagesPerHour { get; init; } = 6;

    /// <summary>The most messages a scene is planned with, 2 to 10.</summary>
    public int LongestScene { get; init; } = 6;

    /// <summary>The share of scenes set up as a disagreement (§6.3).</summary>
    public double DisagreeShare { get; init; } = 0.3;

    /// <summary>
    /// Messages a voice may mark important a day (§4.3): something that really matters, said fully, up to six sentences. A message that raises
    /// a case may be long without it.
    /// </summary>
    public int ImportantPerDay { get; init; } = 3;

    /// <summary>Replies to people an hour (§5.2), which don't count against <see cref="MessagesPerHour"/>.</summary>
    public int RepliesPerHour { get; init; } = 20;

    /// <summary>The group the lounge reads with, lowered by Cookie's (§3.2): required to start, no fallback to Cookie's group.</summary>
    public Guid? GroupId { get; init; }

    public string Effort { get; init; } = "low";

    public int ToolCallsPerMessage { get; init; } = 3;

    /// <summary>Raised cases a day (§8.1).</summary>
    public int CasesPerDay { get; init; } = 2;

    /// <summary>E-mail's level (§8.2): <i>Propose</i> until an admin trusts it.</summary>
    public ActionLevel EmailLevel { get; init; } = ActionLevel.Propose;

    public int EmailsPerDay { get; init; } = 2;

    public int EmailsPerPersonWeek { get; init; } = 1;

    /// <summary>A memory is at half strength after this many days untouched (§7.1).</summary>
    public double HalfLifeDays { get; init; } = 7;

    public int MemoriesInPrompt { get; init; } = 15;

    /// <summary>The most a night may move a position (§6.2).</summary>
    public double MaxDriftPerDay { get; init; } = 0.05;

    /// <summary>Exactly two, one owned by each voice (§6.1).</summary>
    public IReadOnlyList<Conviction> Convictions { get; init; } = Conviction.Defaults;

    /// <summary>The lounge's own budget a calendar month (§11); 0 for none.</summary>
    public decimal BudgetUsd { get; init; } = 10;

    public void Validate(ApiValidator v)
    {
        v.Check(Awake.Select(d => d.Day).Distinct().Count() == Awake.Count, "awake", "Each day once.");
        v.Check(Awake.All(d => d.Periods.All(p => p.TryParse(out _, out _))), "awake", "Each period from HH:mm to a later HH:mm.");
        v.Check(Awake.Any(d => d.Periods.Count > 0), "awake", "At least one awake period: to keep them quiet, stop the lounge.");
        v.Check(MessagesPerHour is >= 0 and <= MaxMessagesPerHour, "messagesPerHour", $"0 to {MaxMessagesPerHour}.");
        v.Check(LongestScene is >= 2 and <= 10, "longestScene", "2 to 10.");
        v.Check(DisagreeShare is >= 0 and <= 1, "disagreeShare", "0 to 1.");
        v.Check(RepliesPerHour is >= 0 and <= 60, "repliesPerHour", "0 to 60.");
        v.Check(ImportantPerDay is >= 0 and <= 10, "importantPerDay", "0 to 10.");
        v.Check(ModelSettings.Efforts.Contains(Effort), "effort", "minimal, low, medium or high.");
        v.Check(ToolCallsPerMessage is >= 0 and <= 5, "toolCallsPerMessage", "0 to 5.");
        v.Check(CasesPerDay is >= 0 and <= 5, "casesPerDay", "0 to 5.");
        v.Check(EmailsPerDay is >= 0 and <= 10, "emailsPerDay", "0 to 10.");
        v.Check(EmailsPerPersonWeek is >= 0 and <= 3, "emailsPerPersonWeek", "0 to 3.");
        v.Check(HalfLifeDays is >= 1 and <= 60, "halfLifeDays", "1 to 60 days.");
        v.Check(MemoriesInPrompt is >= 5 and <= 30, "memoriesInPrompt", "5 to 30.");
        v.Check(MaxDriftPerDay is >= 0 and <= 0.2, "maxDriftPerDay", "0 to 0.2.");
        v.Check(BudgetUsd is >= 0 and <= 1000, "budgetUsd", "0 to 1,000 USD.");
        v.Check(Convictions.Count == 2 && Convictions.Select(c => c.Owner).Distinct().Count() == 2 && Convictions.All(c => LoungeVoices.Known(c.Owner)),
            "convictions", "Two convictions, one for Cookie and one for Seraph.");
        v.Check(Convictions.Select(c => c.Key).Distinct().Count() == Convictions.Count, "convictions", "Each key once.");
        foreach (var (c, i) in Convictions.Select((c, i) => (c, i)))
        {
            var field = $"convictions.{i}";
            v.Check(KeyPattern().IsMatch(c.Key), field, "A key of 1 to 32 lower-case letters, digits and dashes.");
            v.Check(c.Question.Trim().Length is > 0 and <= 200, field, "A question of 1 to 200 characters.");
            v.Check(c.Minus.Trim().Length is > 0 and <= 200 && c.Plus.Trim().Length is > 0 and <= 200, field, "Both poles, 1 to 200 characters each.");
            v.Check(c.Cookie is >= -1 and <= 1 && c.Seraph is >= -1 and <= 1, field, "Positions from -1 to +1.");
            // The owner's is a very strong opinion (D9): at least 0.5 on its side, which it never leaves.
            v.Check(Math.Abs(c.StartOf(c.Owner)) >= LoungeOpinions.OwnerFloor, field, $"The owner starts at least {LoungeOpinions.OwnerFloor} on one side.");
        }
    }

    /// <summary>The lounge's calendar (§5.1): the awake hours, and the plan's holidays as closed days when asleep on holidays.</summary>
    public WorkingCalendar Calendar(IEnumerable<Holiday> holidays) =>
        new(Awake, AsleepOnHolidays ? holidays.Select(h => h with { Periods = [] }) : []);

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex KeyPattern();
}
