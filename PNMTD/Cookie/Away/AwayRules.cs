using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Cookie.Away;

/// <summary>
/// The away watch's rules as code (docs/cookie-redmine-tab-plan.md §6): which journals are new comments, who is away and until when,
/// and whom an important comment goes to. Pure, and tested.
/// </summary>
public static class AwayRules
{
    /// <summary>How long a comment waits for Cookie's verdict before code handles it as without one (§6.7).</summary>
    public static readonly TimeSpan CookieHas = TimeSpan.FromMinutes(30);

    /// <summary>One notice per ticket within this; a later comment joins the next one (§6.8).</summary>
    public static readonly TimeSpan AgainAfter = TimeSpan.FromHours(4);

    /// <summary>What wasn't told within three working days (of 8 hours) expires (§6.7).</summary>
    public static readonly TimeSpan ExpiresAfter = TimeSpan.FromHours(24);

    /// <summary>The comment's text is removed after this; the verdict stays (§6.9).</summary>
    public static readonly TimeSpan TextKept = TimeSpan.FromDays(30);

    public const int MaxText = 4000;

    /// <summary>The notice quotes this much of the comment, to a reader with <i>Redmine: View</i> (§6.6).</summary>
    public const int QuoteChars = 600;

    /// <summary>How far ahead <i>away until</i> looks for the day the person works again (§6.1).</summary>
    public const int LookAhead = 60;

    /// <summary>
    /// The journals that are new comments (§6.2): newer than the last one seen, with a note (a private one while private notes
    /// count), and by neither the assignee nor Cookie. A journal that only changes fields isn't a comment.
    /// </summary>
    public static IReadOnlyList<RmJournal> NewComments(IReadOnlyList<RmJournal>? journals, int lastJournalId, int? assigneeRedmineId,
        int? cookieRedmineId, bool privateNotes) =>
        [.. (journals ?? []).Where(j => j.Id > lastJournalId && !string.IsNullOrWhiteSpace(j.Notes) && (privateNotes || !j.PrivateNotes)
            && (j.User?.Id is not { } author || (author != assigneeRedmineId && author != cookieRedmineId))).OrderBy(j => j.Id)];

    /// <summary>
    /// Whether a person is away on a day (§6.1): in the plan, and their cell's type has one of the chosen checkboxes. A default day
    /// off and a closed day don't count; someone not in the plan is never away.
    /// </summary>
    public static bool Away(bool inPlan, Func<AvailabilityFlag, bool> cellHas, IEnumerable<AvailabilityFlag> flags) => inPlan && flags.Any(cellHas);

    /// <summary>
    /// The last day of the absence (§6.1): the last open day before the next day the person works, closed days (a weekend) skipped;
    /// null when they don't work again within <see cref="LookAhead"/> days.
    /// </summary>
    public static DateOnly? AwayUntil(DateOnly day, Func<DateOnly, bool> works, Func<DateOnly, bool> closed)
    {
        var last = day;
        for (var d = day.AddDays(1); d <= day.AddDays(LookAhead); d = d.AddDays(1))
        {
            if (works(d))
            {
                return last;
            }
            if (!closed(d))
            {
                last = d;
            }
        }
        return null;
    }

    /// <summary>
    /// Whom an important comment goes to (§6.5): the setting's choice, each standing in for the other when it is nobody; never the
    /// away assignee, nor the comment's author, who knows already. The model never names anyone.
    /// </summary>
    /// <param name="responsible">Whoever the plan makes responsible now: nobody outside working hours.</param>
    /// <param name="personWorks">Whether the chosen person works today.</param>
    /// <param name="notThese">The away assignee and the comments' authors.</param>
    public static IReadOnlyList<Guid> Recipients(AwayWatchSettings s, IReadOnlyList<Guid> responsible, bool personWorks, IReadOnlyCollection<Guid> notThese)
    {
        IReadOnlyList<Guid> person = s.PersonId is { } p && personWorks ? [p] : [];
        var (first, second) = s.Recipient == AwayRecipient.Person ? (person, responsible) : (responsible, person);
        List<Guid> Usable(IReadOnlyList<Guid> ids) => [.. ids.Where(id => !notThese.Contains(id)).Distinct()];
        var chosen = Usable(first);
        return chosen.Count > 0 ? chosen : Usable(second);
    }

    /// <summary>"until Fri 2026-12-05", or "for a while" beyond the look-ahead.</summary>
    public static string Until(DateOnly? until) =>
        until is { } d ? $"until {d.ToString("ddd yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}" : "for a while";

    public static string Cut(string? text, int max) => text is null ? "" : text.Length <= max ? text : text[..(max - 1)] + "…";
}
