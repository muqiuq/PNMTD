using PNMTD.Cookie;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Tests.Cookie;

/// <summary>The rules of docs/cookie-plan.md §7 as code: working hours, the ticket clock, the duties, the code's status (§19.7).</summary>
[TestClass]
public sealed class CookieRulesTests
{
    private static readonly WorkingCalendar Office = new(WorkingCalendar.DefaultWeek, []);

    private static DateTime Local(int year, int month, int day, int hour, int minute = 0) => Clock.ToUtc(new DateTime(year, month, day, hour, minute, 0));

    [TestMethod]
    public void Four_working_hours_from_Friday_afternoon_end_on_Monday_morning()
    {
        // 2026-11-13 is a Friday: 1 hour on Friday, 3 on Monday from 08:00.
        Assert.AreEqual(Local(2026, 11, 16, 11), Office.AddWorkingTime(Local(2026, 11, 13, 16), TimeSpan.FromHours(4)));
        Assert.AreEqual(Local(2026, 11, 13, 12), Office.AddWorkingTime(Local(2026, 11, 13, 8), TimeSpan.FromHours(4)));
        Assert.AreEqual(Local(2026, 11, 16, 12), Office.AddWorkingTime(Local(2026, 11, 14, 10), TimeSpan.FromHours(4)), "Saturday counts nothing");
        Assert.AreEqual(TimeSpan.FromHours(4), Office.WorkingTimeBetween(Local(2026, 11, 13, 16), Local(2026, 11, 16, 11)));
    }

    [TestMethod]
    public void A_holiday_replaces_the_weekday_and_outside_hours_waits_for_the_next_period()
    {
        var calendar = new WorkingCalendar(WorkingCalendar.DefaultWeek,
        [
            new Holiday(new DateOnly(2026, 12, 24), "Christmas Eve", [new HoursPeriod("08:00", "12:00")]),
            new Holiday(new DateOnly(2026, 12, 25), "Christmas", []),
        ]);
        Assert.IsFalse(calendar.IsWorking(Local(2026, 12, 24, 13)), "the holiday's hours, not Thursday's");
        Assert.IsTrue(calendar.IsWorking(Local(2026, 12, 24, 11)));
        // Thursday 24 Dec 11:00 + 2 hours: 1 hour on the 24th, the 25th is closed, then Saturday and Sunday, then Monday 28th.
        Assert.AreEqual(Local(2026, 12, 28, 9), calendar.AddWorkingTime(Local(2026, 12, 24, 11), TimeSpan.FromHours(2)));
        Assert.AreEqual(Local(2026, 12, 28, 8), calendar.NextWorkingStart(Local(2026, 12, 24, 18)));
        Assert.AreEqual(Local(2026, 12, 24, 9), calendar.NextWorkingStart(Local(2026, 12, 24, 9)), "in working hours: now");
        var always = new WorkingCalendar([], []);
        Assert.IsTrue(always.AlwaysOpen);
        Assert.AreEqual(Local(2026, 11, 14, 14), always.AddWorkingTime(Local(2026, 11, 14, 10), TimeSpan.FromHours(4)), "no hours set: every hour counts");
    }

    private static RmIssue Issue(int authorId, params RmJournal[] journals) => new(1, new RmRef(1, "IT Support"), null, new RmStatus(1, "New", false),
        null, new RmRef(authorId, "Author"), null, null, "Printer", null, null, null, null, false, null, new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc), null, null, journals, null, null, null);

    private static RmJournal Note(int user, int minutes, string? notes, bool isPrivate = false, bool status = false) => new(minutes, new RmRef(user, $"U{user}"),
        notes, new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc).AddMinutes(minutes), isPrivate,
        status ? [new RmDetail("attr", "status_id", "1", "2")] : []);

    [TestMethod]
    public void A_staff_note_answers_and_the_authors_or_a_private_one_does_not()
    {
        var issue = Issue(7, Note(7, 10, "any news?"), Note(5, 20, "internal", isPrivate: true), Note(5, 30, "Which printer?"), Note(5, 90, null, status: true));
        var noRoles = CaseRules.Clock(issue, null);
        Assert.AreEqual(new DateTime(2026, 11, 13, 9, 30, 0, DateTimeKind.Utc), noRoles.FirstAnswerAt, "the author's note and the private one don't count");
        Assert.AreEqual(new DateTime(2026, 11, 13, 10, 30, 0, DateTimeKind.Utc), noRoles.LastStaffUpdateAt, "a status change is an update");
        Assert.IsFalse(noRoles.AuthorIsStaff);
        var roles = CaseRules.Clock(issue, new HashSet<int> { 9 });
        Assert.IsNull(roles.FirstAnswerAt, "with roles set, only their members are staff");
        Assert.IsTrue(CaseRules.Clock(Issue(9), new HashSet<int> { 9 }).AuthorIsStaff);
    }

    [TestMethod]
    public void A_ticket_has_first_answer_duties_then_update_duties_unless_its_status_stops_the_clock()
    {
        var project = WatchedProject.Default(1);
        var c = new CookieCaseEntity { TicketCreatedAt = Local(2026, 11, 13, 16), StatusId = 1 };
        var first = CaseRules.TicketDuties(c, project, Office);
        CollectionAssert.AreEqual(new[] { "first-answer:reminder", "first-answer:missed" }, first.Select(d => d.Key).ToArray());
        Assert.AreEqual(Local(2026, 11, 16, 10), first[0].DueAt, "3 working hours");
        Assert.AreEqual(Local(2026, 11, 16, 11), first[1].DueAt, "4 working hours");

        c.FirstAnswerAt = c.LastStaffUpdateAt = Local(2026, 11, 16, 9);
        var update = CaseRules.TicketDuties(c, project, Office);
        Assert.AreEqual("update:reminder@" + Local(2026, 11, 16, 9).ToString("yyyyMMddHHmm"), update[0].Key);
        Assert.AreEqual(Local(2026, 11, 30, 8), update[0].DueAt, "12 days on a Saturday: the next working period, Monday 08:00");
        Assert.AreEqual(Local(2026, 11, 30, 9), update[1].DueAt, "14 days: Monday 09:00, in working hours");

        c.LastStaffUpdateAt = Local(2026, 11, 20, 9);
        Assert.AreNotEqual(update[0].Key, CaseRules.TicketDuties(c, project, Office)[0].Key, "a new update starts a new clock");
        c.StatusId = 4;
        Assert.AreEqual(0, CaseRules.TicketDuties(c, project with { StopStatusIds = [4] }, Office).Count, "Feedback stops the clock");
        c.AuthorIsStaff = true;
        c.FirstAnswerAt = c.LastStaffUpdateAt = null;
        c.StatusId = 1;
        Assert.IsTrue(CaseRules.TicketDuties(c, project, Office).All(d => d.Kind == CookieDutyKind.UpdateReminder || d.Kind == CookieDutyKind.UpdateMissed),
            "a ticket written by staff has no first-answer deadline");
    }

    [TestMethod]
    public void A_problem_gets_its_ticket_duty_only_without_an_open_linked_ticket()
    {
        var c = new CookieCaseEntity { Open = true, ProgressAt = Local(2026, 11, 13, 9) };
        Assert.AreEqual(Local(2026, 11, 15, 9), CaseRules.ProblemDuties(c, false, 48).Single().DueAt);
        Assert.AreEqual(0, CaseRules.ProblemDuties(c, true, 48).Count);
        c.OwnIssueId = 5;
        Assert.AreEqual(0, CaseRules.ProblemDuties(c, false, 48).Count, "it has its own ticket already");
    }

    [TestMethod]
    public void The_codes_status_puts_high_importance_and_crowded_locations_first()
    {
        var location = Guid.NewGuid();
        CookieCaseEntity Problem(string title, Importance importance, Guid? locationId = null) => new()
        {
            Id = Guid.NewGuid(), Kind = CookieCaseKind.Problem, Title = title, Importance = importance, LocationId = locationId, HostId = Guid.NewGuid(),
            OpenedAt = Clock.UtcNow,
        };
        var names = new Dictionary<Guid, string> { [location] = "Zurich" };
        Assert.AreEqual(OverallState.AllFine, CookieStatusRules.Compute([], [], names).State);
        Assert.AreEqual(OverallState.Info, CookieStatusRules.Compute([Problem("printer: toner", Importance.Low)], [], names).State);
        var normal = CookieStatusRules.Compute([Problem("nas: backup", Importance.Normal), Problem("printer: toner", Importance.Low)], [], names);
        Assert.AreEqual(OverallState.Warning, normal.State);
        Assert.AreEqual("nas: backup", normal.Reason);
        var high = CookieStatusRules.Compute([Problem("nas: backup", Importance.Normal), Problem("core-sw: down", Importance.High)], [], names);
        Assert.AreEqual(OverallState.Alert, high.State);
        Assert.AreEqual("core-sw: down; 1 more problem", high.Reason);
        var crowded = CookieStatusRules.Compute(
            [Problem("a: x", Importance.Normal, location), Problem("b: x", Importance.Normal, location), Problem("c: x", Importance.Low, location)], [], names);
        Assert.AreEqual(OverallState.Alert, crowded.State);
        Assert.AreEqual("3 hosts at Zurich have problems", crowded.Reason);
        var ticket = new CookieCaseEntity { Id = Guid.NewGuid(), Kind = CookieCaseKind.Ticket, Title = "#5 printer" };
        var missed = CookieStatusRules.Compute([ticket], [new CookieDutyEntity { CaseId = ticket.Id, Kind = CookieDutyKind.FirstAnswerMissed }], names);
        Assert.AreEqual(OverallState.Warning, missed.State);
        Assert.AreEqual("a ticket's deadline missed", missed.Reason);
    }

    [TestMethod]
    public void The_limits_stop_at_the_ceilings_in_code()
    {
        var v = new PNMTD.Api.ApiValidator();
        new ActionSettings { Limits = new OutputLimits(PushoverPerPersonDay: 11) }.Validate(v);
        Assert.IsTrue(v.HasErrors, "11 Pushover messages a day is above the ceiling of 10");
        var ok = new PNMTD.Api.ApiValidator();
        new ActionSettings { Limits = OutputLimits.Ceilings }.Validate(ok);
        Assert.IsFalse(ok.HasErrors);
        Assert.IsTrue(new DutySwitches().GetType().GetProperties().All(p => p.GetValue(new DutySwitches()) is false), "every duty starts off");
    }

    [TestMethod]
    public void Every_default_prompt_keeps_the_parts_code_fills_in()
    {
        foreach (var (key, prompt) in CookiePrompts.Defaults)
        {
            foreach (var required in CookiePrompts.Required(key))
            {
                StringAssert.Contains(prompt.Text, required, key);
            }
        }
        Assert.AreEqual(9, CookiePrompts.CaseKinds.Count);
        Assert.AreEqual("Hi Cookie, {unknown}", CookiePrompts.Fill("Hi {name}, {unknown}", new Dictionary<string, string> { ["name"] = "Cookie" }));
    }
}
