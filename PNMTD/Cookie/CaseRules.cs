using System.Globalization;
using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Cookie;

/// <summary>A problem case's member: a bad sensor, or a Zabbix problem of the host.</summary>
public sealed record CaseMember(
    string Type,
    string Id,
    string Name,
    DateTime Since,
    int? Code = null,
    string? Message = null,
    string? Severity = null,
    bool Acknowledged = false,
    string? Importance = null,
    string? NotesHash = null)
{
    public const string Sensor = "sensor";
    public const string Zabbix = "zabbix";

    public string Ref => $"{Type}:{Id}";
}

/// <summary>A ticket's clock as its journals tell it (docs/cookie-plan.md §7.2, §19.1).</summary>
public sealed record TicketClockState(bool AuthorIsStaff, DateTime? FirstAnswerAt, DateTime? LastStaffUpdateAt);

/// <summary>A duty a case should have now: its key, kind and when it is due.</summary>
public sealed record DutyPlan(string Key, CookieDutyKind Kind, DateTime DueAt);

/// <summary>
/// The rules of docs/cookie-plan.md §7 as functions: what a ticket's journals say about its first answer and its updates,
/// and which duties a case has.
/// </summary>
public static class CaseRules
{
    /// <summary>
    /// <paramref name="staff"/>: the ids of the project's members with a staff role; null when no roles are set, and then
    /// everyone but the author counts as staff. A first answer, and an update, is a public note or a status change by staff.
    /// </summary>
    public static TicketClockState Clock(RmIssue issue, IReadOnlySet<int>? staff)
    {
        var authorId = issue.Author?.Id;
        bool IsStaff(int? userId) => userId is { } id && (staff is null ? id != authorId : staff.Contains(id));
        var authorIsStaff = staff is not null && authorId is { } a && staff.Contains(a);
        DateTime? first = null, last = null;
        foreach (var j in (issue.Journals ?? []).OrderBy(j => j.CreatedOn))
        {
            var answers = (!string.IsNullOrWhiteSpace(j.Notes) && !j.PrivateNotes)
                || (j.Details ?? []).Any(d => d.Property == "attr" && d.Name == "status_id");
            if (answers && IsStaff(j.User?.Id))
            {
                var at = PNMTD.Lib.Helper.Clock.AsUtc(j.CreatedOn);
                first ??= at;
                last = at;
            }
        }
        return new TicketClockState(authorIsStaff, first, last);
    }

    /// <summary>
    /// A ticket case's duties (§7.2): before the first answer, the reminder and the deadline in working hours; after it, the
    /// update's reminder and deadline in calendar days from the last update by staff, moved to the next working period,
    /// unless its status stops the clock. A ticket written by staff has no first-answer deadline.
    /// </summary>
    public static IReadOnlyList<DutyPlan> TicketDuties(CookieCaseEntity c, WatchedProject project, WorkingCalendar calendar)
    {
        if (c.TicketCreatedAt is not { } created)
        {
            return [];
        }
        var duties = new List<DutyPlan>();
        if (!c.AuthorIsStaff && c.FirstAnswerAt is null)
        {
            duties.Add(new("first-answer:reminder", CookieDutyKind.FirstAnswerReminder,
                calendar.AddWorkingTime(created, TimeSpan.FromHours(project.FirstAnswerReminderHours))));
            duties.Add(new("first-answer:missed", CookieDutyKind.FirstAnswerMissed,
                calendar.AddWorkingTime(created, TimeSpan.FromHours(project.FirstAnswerHours))));
            return duties;
        }
        if (c.StatusId is { } status && project.StopStatusIds.Contains(status))
        {
            return duties;
        }
        var start = c.LastStaffUpdateAt ?? c.FirstAnswerAt ?? created;
        var stamp = start.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        duties.Add(new($"update:reminder@{stamp}", CookieDutyKind.UpdateReminder, calendar.NextWorkingStart(start.AddDays(project.UpdateReminderDays))));
        duties.Add(new($"update:missed@{stamp}", CookieDutyKind.UpdateMissed, calendar.NextWorkingStart(start.AddDays(project.UpdateDays))));
        return duties;
    }

    /// <summary>
    /// A problem case's duty (§7.1): the ticket once nothing has moved for <paramref name="unresolvedHours"/>, unless a ticket
    /// that is still open is linked to it, or it has one of its own.
    /// </summary>
    public static IReadOnlyList<DutyPlan> ProblemDuties(CookieCaseEntity c, bool openTicketLinked, double unresolvedHours) =>
        c.Open && !openTicketLinked && c.OwnIssueId is null
            ? [new("problem:ticket", CookieDutyKind.ProblemTicket, c.ProgressAt.AddHours(unresolvedHours))]
            : [];

    /// <summary>Whether a duty is a reminder (important) or a missed deadline (urgent), for its recipients (§19.1).</summary>
    public static Urgency UrgencyOf(CookieDutyKind kind) => kind switch
    {
        CookieDutyKind.FirstAnswerMissed or CookieDutyKind.UpdateMissed => Urgency.Urgent,
        CookieDutyKind.FirstAnswerReminder or CookieDutyKind.UpdateReminder => Urgency.Important,
        _ => Urgency.Info,
    };
}
