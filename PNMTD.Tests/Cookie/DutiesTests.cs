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
        var a = User("Anna");
        var b = User("Bob");
        db.Users.AddRange(a, b);
        var group = new GroupEntity { Id = Guid.NewGuid(), Name = V1.Unique("Service desk"), NormalizedName = V1.Unique("service desk") };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        db.UserGroups.Add(new UserGroupEntity { UserId = b.Id, GroupId = group.Id });
        await db.SaveChangesAsync();
        (anna, bob, team) = (a.Id, b.Id, group.Id);
        await SaveAsync("hours", new HoursSettings { Week = [] });
        await SaveAsync("notifications", new NotificationSettings
        {
            Contacts =
            [
                new CookieContact(anna, "anna@example.com", "uQiRzpo4DXghDmr9QzzfQu27cmVRsG", "Anna Muster", null, null),
                new CookieContact(bob, "bob@example.com", null, null, null, null),
            ],
        });
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await SaveAsync("hours", new HoursSettings());
        await SaveAsync("notifications", new NotificationSettings());
        await SaveAsync("redmine", new CookieRedmineSettings());
        await SaveAsync("actions", new ActionSettings());
        app.Dispose();
    }

    private static CaseBook Book => app.Services.GetRequiredService<CaseBook>();

    private static DutyRunner Runner => app.Services.GetRequiredService<DutyRunner>();

    /// <summary>A ticket of IT Support, assigned to Anna, written by a requester this long ago.</summary>
    private static int Ticket(TimeSpan age)
    {
        var id = redmine.Issues.Keys.Max() + 1;
        var at = DateTime.UtcNow.Subtract(age).ToString("yyyy-MM-ddTHH:mm:ssZ");
        redmine.Issues[id] = JsonNode.Parse($$"""
            {"id":{{id}},"project":{"id":1,"name":"IT Support"},"tracker":{"id":1,"name":"Bug"},"status":{"id":1,"name":"New","is_closed":false},
             "priority":{"id":2,"name":"Normal"},"author":{"id":7,"name":"Requester"},"assigned_to":{"id":5,"name":"Anna Muster"},
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
    public void Recipient_rules_match_scope_urgency_and_time()
    {
        var rule = new RecipientRule(Guid.NewGuid(), "Network at night", RecipientScope.Project, "3", Urgency.Urgent, RecipientWhen.OutsideWorkingHours,
            [anna], [], [RecipientChannel.Pushover]);
        Assert.IsTrue(Recipients.Matches(rule, new Recipients.Subject(CookieCaseKind.Ticket, null, null, 3, Urgency.Urgent, false)));
        Assert.IsFalse(Recipients.Matches(rule, new Recipients.Subject(CookieCaseKind.Ticket, null, null, 3, Urgency.Important, false)), "too little urgent");
        Assert.IsFalse(Recipients.Matches(rule, new Recipients.Subject(CookieCaseKind.Ticket, null, null, 3, Urgency.Urgent, true)), "in working hours");
        Assert.IsFalse(Recipients.Matches(rule, new Recipients.Subject(CookieCaseKind.Ticket, null, null, 1, Urgency.Urgent, false)), "another project");
        var everyone = rule with { Scope = RecipientScope.Everything, Users = [], Groups = [team], When = RecipientWhen.Always };
        var found = Recipients.FromRules(new NotificationSettings { Rules = [everyone] },
            new Recipients.Subject(CookieCaseKind.Problem, null, null, null, Urgency.Urgent, true), new Dictionary<Guid, List<Guid>> { [team] = [bob] });
        CollectionAssert.AreEqual(new[] { bob }, found.Keys.ToArray());
    }

    private static async Task<int> SaveAsync<T>(string section, T value)
    {
        using var response = await admin.PutAsJsonAsync($"/api/v1/cookie/admin/settings/{section}", value, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.ReadAsync<CookieSaved>()).Version;
    }
}
