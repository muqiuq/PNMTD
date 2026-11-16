using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Cookie;
using PNMTD.Dev;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// The code's duties (docs/cookie-plan.md §3.4 A, §6.1, §19.7): off until switched on; once each; reminders as a private
/// note and to the assignee; a missed deadline to the escalation group; the limits hold back the rest, and a person can send
/// it; the ticket after 2 days, and its closing note. E-mails are simulated in the tests.
/// </summary>
[TestClass]
public sealed class DutiesTests
{
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid anna;
    private static Guid bob;
    private static Guid team;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: FakeRedmine.WritableSettings, services: redmine.Install);
        admin = app.Client(Roles.Admin);
        await using var db = app.NewDb();
        UserEntity User(string name) => new()
        {
            Id = Guid.NewGuid(), Username = V1.Unique(name), NormalizedUsername = V1.Unique(name).ToLowerInvariant(), DisplayName = name,
            PasswordHash = "x", SecurityStamp = Guid.NewGuid().ToString(),
        };
        // The contact details are on the user (docs/cookie-plan.md §8.1); Anna is the assignee by her Redmine user.
        var a = User("Anna");
        a.Email = "anna@example.com";
        a.PushoverKey = "uQiRzpo4DXghDmr9QzzfQu27cmVRsG";
        a.RedmineName = "Anna Muster";
        var b = User("Bob");
        b.Email = "bob@example.com";
        db.Users.AddRange(a, b);
        var group = new GroupEntity { Id = Guid.NewGuid(), Name = V1.Unique("Service desk"), NormalizedName = V1.Unique("service desk") };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        db.UserGroups.Add(new UserGroupEntity { UserId = b.Id, GroupId = group.Id });
        await db.SaveChangesAsync();
        (anna, bob, team) = (a.Id, b.Id, group.Id);
        // Always working hours: the tests run at any hour, on any day.
        await V1.AvailabilityHoursAsync(admin, []);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await V1.AvailabilityHoursAsync(admin, null);
        await SaveAsync("redmine", new CookieRedmineSettings());
        await SaveAsync("actions", new ActionSettings());
        app.Dispose();
    }

    private static CaseBook Book => app.Services.GetRequiredService<CaseBook>();

    private static DutyRunner Runner => app.Services.GetRequiredService<DutyRunner>();

    /// <summary>A ticket of IT Support, assigned to Anna (or someone else), written by a requester this long ago.</summary>
    private static int Ticket(TimeSpan age, string assignee = "Anna Muster")
    {
        var id = redmine.Issues.Keys.Max() + 1;
        var at = DateTime.UtcNow.Subtract(age).ToString("yyyy-MM-ddTHH:mm:ssZ");
        redmine.Issues[id] = JsonNode.Parse($$"""
            {"id":{{id}},"project":{"id":1,"name":"IT Support"},"tracker":{"id":1,"name":"Bug"},"status":{"id":1,"name":"New","is_closed":false},
             "priority":{"id":2,"name":"Normal"},"author":{"id":7,"name":"Requester"},"assigned_to":{"id":5,"name":"{{assignee}}"},
             "subject":"The printer in 204","description":"It doesn't print.","is_private":false,"created_on":"{{at}}","updated_on":"{{at}}"}
            """)!.AsObject();
        return id;
    }

    private static async Task<List<CookieDutyEntity>> DutiesAsync(int issueId)
    {
        await using var db = app.NewDb();
        var c = await db.CookieCases.SingleAsync(x => x.Key == $"ticket:{issueId}" && x.Open);
        return await db.CookieDuties.AsNoTracking().Where(d => d.CaseId == c.Id).OrderBy(d => d.DueAt).ToListAsync();
    }

    private static async Task<List<CookieOutboxEntity>> OutboxAsync(Guid caseId)
    {
        await using var db = app.NewDb();
        return await db.CookieOutbox.AsNoTracking().Where(o => o.CaseId == caseId).OrderBy(o => o.Id).ToListAsync();
    }

    [TestMethod]
    public async Task A_duty_that_is_off_sends_nothing_and_stays_off()
    {
        await SaveAsync("redmine", new CookieRedmineSettings { Projects = [WatchedProject.Default(1) with { EscalationGroupId = team }] });
        await SaveAsync("actions", new ActionSettings());
        var issue = Ticket(TimeSpan.FromHours(3.5));
        await Book.RunAsync(true, CancellationToken.None);
        await Runner.RunAsync(CancellationToken.None);
        var reminder = (await DutiesAsync(issue)).Single(d => d.Kind == CookieDutyKind.FirstAnswerReminder);
        Assert.AreEqual(CookieDutyState.Off, reminder.State);
        Assert.AreEqual(0, (await OutboxAsync(reminder.CaseId)).Count);
        await SaveAsync("actions", new ActionSettings { Duties = new DutySwitches(FirstAnswerReminder: true) });
        await Runner.RunAsync(CancellationToken.None);
        Assert.AreEqual(0, (await OutboxAsync(reminder.CaseId)).Count, "switching it on later sends nothing old");
    }

    [TestMethod]
    public async Task A_reminder_is_a_private_note_and_an_email_to_the_assignee_once()
    {
        await SaveAsync("redmine", new CookieRedmineSettings { Projects = [WatchedProject.Default(1) with { EscalationGroupId = team }] });
        await SaveAsync("actions", new ActionSettings { Duties = new DutySwitches(FirstAnswerReminder: true, FirstAnswerMissed: true) });
        var issue = Ticket(TimeSpan.FromHours(3.5));
        await Book.RunAsync(true, CancellationToken.None);
        await Runner.RunAsync(CancellationToken.None);
        await Runner.RunAsync(CancellationToken.None);

        var duties = await DutiesAsync(issue);
        var reminder = duties.Single(d => d.Kind == CookieDutyKind.FirstAnswerReminder);
        Assert.AreEqual(CookieDutyState.Done, reminder.State, reminder.Outcome);
        Assert.AreEqual(CookieDutyState.Due, duties.Single(d => d.Kind == CookieDutyKind.FirstAnswerMissed).State, "the deadline is in half an hour");
        var sent = await OutboxAsync(reminder.CaseId);
        CollectionAssert.AreEqual(new[] { CookieChannel.RedminePrivateNote, CookieChannel.Email }, sent.Select(o => o.Channel).ToArray(),
            "once, though it ran twice; no Pushover for a reminder");
        Assert.AreEqual("Anna", sent[1].Recipient);
        Assert.AreEqual(CookieOutboxState.Simulated, sent[1].State);
        var note = redmine.Writes.Single(w => w.Path == $"/issues/{issue}.json");
        Assert.AreEqual("PUT", note.Method);
        Assert.IsTrue(note.Body["issue"]!["private_notes"]!.GetValue<bool>());
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == "CookieNoteAdded" && l.Actor == PNMTD.Audit.AuditActor.Cookie));
    }

    [TestMethod]
    public async Task A_missed_deadline_tells_the_escalation_group_and_the_reminder_gives_way()
    {
        await SaveAsync("redmine", new CookieRedmineSettings { Projects = [WatchedProject.Default(1) with { EscalationGroupId = team }] });
        await SaveAsync("actions", new ActionSettings { Duties = new DutySwitches(FirstAnswerReminder: true, FirstAnswerMissed: true) });
        var issue = Ticket(TimeSpan.FromHours(10));
        await Book.RunAsync(true, CancellationToken.None);
        await Runner.RunAsync(CancellationToken.None);
        var duties = await DutiesAsync(issue);
        Assert.AreEqual(CookieDutyState.Off, duties.Single(d => d.Kind == CookieDutyKind.FirstAnswerReminder).State);
        var missed = duties.Single(d => d.Kind == CookieDutyKind.FirstAnswerMissed);
        Assert.AreEqual(CookieDutyState.Done, missed.State, missed.Outcome);
        var sent = await OutboxAsync(missed.CaseId);
        CollectionAssert.AreEquivalent(new[] { "Anna:Email", "Anna:Pushover", "Bob:Email" }, sent.Select(o => $"{o.Recipient}:{o.Channel}").ToArray(),
            "urgent: Pushover too, to whoever has a key; the group's members by e-mail");
    }

    [TestMethod]
    public async Task The_limits_hold_back_what_is_over_and_a_person_can_send_it()
    {
        await SaveAsync("redmine", new CookieRedmineSettings { Projects = [WatchedProject.Default(1) with { EscalationGroupId = team }] });
        await SaveAsync("actions", new ActionSettings
        {
            Duties = new DutySwitches(FirstAnswerMissed: true), Limits = new OutputLimits(EmailPerPersonDay: 0, PushoverPerPersonDay: 0),
        });
        var issue = Ticket(TimeSpan.FromHours(10));
        await Book.RunAsync(true, CancellationToken.None);
        await Runner.RunAsync(CancellationToken.None);
        var missed = (await DutiesAsync(issue)).Single(d => d.Kind == CookieDutyKind.FirstAnswerMissed);
        Assert.AreEqual(CookieDutyState.HeldBack, missed.State, missed.Outcome);
        var held = (await OutboxAsync(missed.CaseId)).First(o => o.Channel == CookieChannel.Email);
        Assert.AreEqual(CookieOutboxState.HeldBack, held.State);
        StringAssert.Contains(held.Detail, "e-mail per person: 0 a day");
        var overview = await (await admin.GetAsync("/api/v1/cookie/admin/overview")).ReadAsync<CookieAdminOverview>();
        Assert.IsGreaterThanOrEqualTo(3, overview.HeldBackToday);

        using var send = await admin.PostAsync($"/api/v1/cookie/admin/outbox/{held.Id}/send", null);
        Assert.AreEqual(HttpStatusCode.OK, send.StatusCode, await send.Content.ReadAsStringAsync());
        var item = await send.ReadAsync<CookieOutboxItem>();
        Assert.AreEqual(CookieOutboxState.Simulated, item.State);
        Assert.IsNotNull(item.SentByName);
        using var again = await admin.PostAsync($"/api/v1/cookie/admin/outbox/{held.Id}/send", null);
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode, "only what is held back");
        await SaveAsync("actions", new ActionSettings());
    }

    [TestMethod]
    public async Task A_problem_without_progress_gets_a_ticket_and_a_closing_note_when_resolved()
    {
        await SaveAsync("redmine", new CookieRedmineSettings { TicketProjectId = 3 });
        await SaveAsync("actions", new ActionSettings { Duties = new DutySwitches(ProblemTicket: true, ClosingNote: true) });
        var host = await V1.CreateHostAsync(admin, V1.Unique("ups"));
        var sensor = await V1.CreateSensorAsync(admin, host.Id, name: "battery");
        var back = Clock.UtcNow.AddDays(-3);
        V1.AddEvents(app, sensor.Id, (back, 200, "ok"), (back.AddMinutes(1), 500, "battery failed"));
        await Book.RunAsync(false, CancellationToken.None);
        await Runner.RunAsync(CancellationToken.None);

        await using (var db = app.NewDb())
        {
            var c = await db.CookieCases.AsNoTracking().SingleAsync(x => x.Key == $"host:{host.Id}" && x.Open);
            Assert.IsNotNull(c.OwnIssueId, string.Join(" | ", await db.CookieDuties.Where(d => d.CaseId == c.Id).Select(d => d.Outcome).ToListAsync()));
            var created = redmine.Writes.Last(w => w.Method == "POST");
            Assert.AreEqual(3, created.Body["issue"]!["project_id"]!.GetValue<int>());
            StringAssert.Contains(created.Body["issue"]!["description"]!.GetValue<string>(), "battery failed");
            Assert.IsTrue(await db.RedmineLinks.AnyAsync(l => l.HostId == host.Id && l.IssueId == c.OwnIssueId), "linked to the host");
        }

        V1.AddEvents(app, sensor.Id, (back.AddMinutes(5), 200, "battery replaced"));
        await Book.RunAsync(false, CancellationToken.None);
        await Runner.RunAsync(CancellationToken.None);
        var note = redmine.Writes.Last();
        Assert.AreEqual("PUT", note.Method);
        StringAssert.Contains(note.Body["issue"]!["notes"]!.GetValue<string>(), "resolved");
        Assert.IsFalse(note.Body["issue"]!["private_notes"]!.GetValue<bool>());
        await SaveAsync("actions", new ActionSettings());
    }


    [TestMethod]
    public async Task Without_a_ticket_system_the_problem_goes_by_email_to_whoever_is_responsible_and_so_does_its_end()
    {
        await SaveAsync("redmine", new CookieRedmineSettings { TicketProjectId = 3 });
        await SaveAsync("actions", new ActionSettings { Duties = new DutySwitches(ProblemTicket: true, ClosingNote: true) });
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var writes = redmine.Writes.Count;
        await using (var db = app.NewDb())
        {
            // Anna is responsible today (§8.2), and Redmine has been unreachable for longer than the grace period (docs/cookie-fallback-plan.md §3.3).
            if (await db.AvailabilityDays.FindAsync(today) is { } row)
            {
                row.ResponsibleId = anna;
            }
            else
            {
                db.AvailabilityDays.Add(new AvailabilityDayEntity { Date = today, ResponsibleId = anna, ChangedAt = Clock.UtcNow, ChangedBy = "test" });
            }
            var runtime = await CaseBook.RuntimeAsync(db, CancellationToken.None);
            runtime.CaseBookError = "Redmine: 502 Bad Gateway";
            runtime.TicketsAt = Clock.UtcNow - CookieIntegrations.Grace - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync();
        }
        try
        {
            var host = await V1.CreateHostAsync(admin, V1.Unique("nas"));
            var sensor = await V1.CreateSensorAsync(admin, host.Id, name: "raid");
            var back = Clock.UtcNow.AddDays(-3);
            V1.AddEvents(app, sensor.Id, (back, 200, "ok"), (back.AddMinutes(1), 500, "disk 2 failed"));
            await Book.RunAsync(false, CancellationToken.None);
            await Runner.RunAsync(CancellationToken.None);

            Guid caseId;
            await using (var db = app.NewDb())
            {
                var c = await db.CookieCases.AsNoTracking().SingleAsync(x => x.Key == $"host:{host.Id}" && x.Open);
                caseId = c.Id;
                Assert.IsNull(c.OwnIssueId, "no ticket");
                var duty = await db.CookieDuties.AsNoTracking().SingleAsync(d => d.CaseId == c.Id && d.Kind == CookieDutyKind.ProblemTicket);
                Assert.AreEqual(CookieDutyState.Done, duty.State, duty.Outcome);
                StringAssert.Contains(duty.Outcome, "No ticket (Redmine is unreachable since");
                StringAssert.Contains(duty.Outcome, "Anna");
            }
            Assert.AreEqual(writes, redmine.Writes.Count, "nothing written to Redmine");
            var told = (await OutboxAsync(caseId)).Single();
            Assert.AreEqual(CookieChannel.Email, told.Channel, "importance Normal: e-mail only, no Pushover");
            Assert.AreEqual("Anna", told.Recipient);
            Assert.AreEqual(CookieOutboxState.Simulated, told.State, told.Detail);
            StringAssert.Contains(told.Subject, "nobody took care");
            StringAssert.Contains(told.Body, "disk 2 failed");
            StringAssert.Contains(told.Body, "There is no ticket to open for it");
            StringAssert.Contains(told.Body, "PNMT's assistant");

            V1.AddEvents(app, sensor.Id, (back.AddMinutes(5), 200, "disk replaced"));
            await Book.RunAsync(false, CancellationToken.None);
            await Runner.RunAsync(CancellationToken.None);
            await using (var db = app.NewDb())
            {
                var closing = await db.CookieDuties.AsNoTracking().SingleAsync(d => d.CaseId == caseId && d.Kind == CookieDutyKind.ClosingNote);
                Assert.AreEqual(CookieDutyState.Done, closing.State, closing.Outcome);
                StringAssert.StartsWith(closing.Outcome, "Resolved, told by e-mail");
            }
            var rows = await OutboxAsync(caseId);
            Assert.AreEqual(2, rows.Count, string.Join(" | ", rows.Select(r => $"{r.Channel} {r.Subject}")));
            var resolved = rows.Last();
            Assert.AreEqual("Anna", resolved.Recipient);
            StringAssert.EndsWith(resolved.Subject, ": resolved");
            StringAssert.Contains(resolved.Body, "fine again since");
            Assert.AreEqual(writes, redmine.Writes.Count, "still nothing written to Redmine");
        }
        finally
        {
            await using var db = app.NewDb();
            var runtime = await CaseBook.RuntimeAsync(db, CancellationToken.None);
            runtime.CaseBookError = null;
            runtime.TicketsAt = Clock.UtcNow;
            if (await db.AvailabilityDays.FindAsync(today) is { } row)
            {
                row.ResponsibleId = null;
            }
            await db.SaveChangesAsync();
            await SaveAsync("actions", new ActionSettings());
        }
    }

    [TestMethod]
    public async Task An_assignee_who_doesnt_work_today_leaves_the_reminder_to_the_escalation_group()
    {
        await SaveAsync("redmine", new CookieRedmineSettings { Projects = [WatchedProject.Default(1) with { EscalationGroupId = team }] });
        await SaveAsync("actions", new ActionSettings { Duties = new DutySwitches(FirstAnswerReminder: true) });
        var clara = Guid.NewGuid();
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        await using (var db = app.NewDb())
        {
            var vacation = await db.AvailabilityTypes.SingleAsync(t => t.Code == "VACA");
            db.Users.Add(new UserEntity
            {
                Id = clara, Username = V1.Unique("clara"), NormalizedUsername = V1.Unique("clara"), DisplayName = "Clara", PasswordHash = "x",
                SecurityStamp = Guid.NewGuid().ToString(), Email = "clara@example.com", RedmineName = "Clara Away", InPlan = true, ShortCode = "CLA",
            });
            await db.SaveChangesAsync();
            db.AvailabilityEntries.Add(new AvailabilityEntryEntity { UserId = clara, Date = today, TypeId = vacation.Id, SetAt = Clock.UtcNow, SetBy = "test" });
            await db.SaveChangesAsync();
        }
        try
        {
            var issue = Ticket(TimeSpan.FromHours(3.5), "Clara Away");
            await Book.RunAsync(true, CancellationToken.None);
            await Runner.RunAsync(CancellationToken.None);
            var reminder = (await DutiesAsync(issue)).Single(d => d.Kind == CookieDutyKind.FirstAnswerReminder);
            Assert.AreEqual(CookieDutyState.Done, reminder.State, reminder.Outcome);
            StringAssert.Contains(reminder.Outcome, "Clara doesn't work today");
            CollectionAssert.AreEqual(new[] { "Bob" }, (await OutboxAsync(reminder.CaseId)).Where(o => o.Channel == CookieChannel.Email).Select(o => o.Recipient).ToArray(),
                "the escalation group stands in for her");
        }
        finally
        {
            await using var db = app.NewDb();
            await db.Users.Where(u => u.Id == clara).ExecuteUpdateAsync(u => u.SetProperty(x => x.InPlan, false).SetProperty(x => x.ShortCode, (string?)null));
            await SaveAsync("actions", new ActionSettings());
        }
    }

    private static async Task<int> SaveAsync<T>(string section, T value)
    {
        using var response = await admin.PutAsJsonAsync($"/api/v1/cookie/admin/settings/{section}", value, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.ReadAsync<CookieSaved>()).Version;
    }
}
