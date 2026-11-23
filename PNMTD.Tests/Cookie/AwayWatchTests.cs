using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Cookie;
using PNMTD.Cookie.Away;
using PNMTD.Dev;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// The away watch in code (docs/cookie-redmine-tab-plan.md §6): a comment on a ticket whose assignee is on vacation becomes a row;
/// switched off it doesn't, and switching on judges nothing old; without Cookie the chosen person is told, marked not judged; an
/// assignee who is back gets nothing sent; and the rules on their own.
/// </summary>
[TestClass]
public sealed class AwayWatchTests
{
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid dora, bob;
    private static readonly string Tag = Guid.NewGuid().ToString("N")[..6];

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: FakeRedmine.WritableSettings, services: redmine.Install);
        admin = app.Client(Roles.Admin);
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        await using var db = app.NewDb();
        var vacation = await db.AvailabilityTypes.SingleAsync(t => t.Code == "VACA");
        UserEntity User(string name) => new()
        {
            Id = Guid.NewGuid(), Username = V1.Unique(name), NormalizedUsername = V1.Unique(name).ToLowerInvariant(), DisplayName = $"{name} {Tag}",
            PasswordHash = "x", SecurityStamp = Guid.NewGuid().ToString(), Email = $"{name.ToLowerInvariant()}@example.com",
        };
        var d = User("Dora");
        d.RedmineName = $"Dora Away {Tag}";
        d.InPlan = true;
        d.ShortCode = $"D{Tag[..2]}".ToUpperInvariant();
        var b = User("Bob");
        db.Users.AddRange(d, b);
        await db.SaveChangesAsync();
        db.AvailabilityEntries.Add(new AvailabilityEntryEntity { UserId = d.Id, Date = today, TypeId = vacation.Id, SetAt = Clock.UtcNow, SetBy = "test" });
        await db.SaveChangesAsync();
        (dora, bob) = (d.Id, b.Id);
        // Always working hours: the tests run at any hour, on any day.
        await V1.AvailabilityHoursAsync(admin, []);
        await SaveAsync("redmine", new CookieRedmineSettings
        {
            Projects = [WatchedProject.Default(1)],
            Away = new AwayWatchSettings { Recipient = AwayRecipient.Person, PersonId = bob },
        });
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await V1.AvailabilityHoursAsync(admin, null);
        await SaveAsync("redmine", new CookieRedmineSettings());
        await SaveAsync("actions", new ActionSettings());
        await using (var db = app.NewDb())
        {
            await db.AvailabilityEntries.Where(e => e.UserId == dora).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == dora).ExecuteUpdateAsync(u => u.SetProperty(x => x.InPlan, false).SetProperty(x => x.ShortCode, (string?)null));
        }
        app.Dispose();
    }

    private static async Task SaveAsync<T>(string section, T value)
    {
        using var response = await admin.PutAsJsonAsync(V1.CookieSettingsPath(section), value, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static CaseBook Book => app.Services.GetRequiredService<CaseBook>();

    private static AwayWatchTask Task_ => app.Services.GetRequiredService<AwayWatchTask>();

    /// <summary>A ticket of IT Support assigned to Dora, with one journal so far.</summary>
    private static int Ticket()
    {
        var id = redmine.Issues.Keys.Max() + 1;
        var at = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        redmine.Issues[id] = JsonNode.Parse($$"""
            {"id":{{id}},"project":{"id":1,"name":"IT Support"},"tracker":{"id":1,"name":"Bug"},"status":{"id":1,"name":"New","is_closed":false},
             "priority":{"id":2,"name":"Normal"},"author":{"id":7,"name":"Requester"},"assigned_to":{"id":5,"name":"Dora Away {{Tag}}"},
             "subject":"The server room is too warm","description":"38 degrees.","is_private":false,"created_on":"{{at}}","updated_on":"{{at}}",
             "journals":[{"id":{{id * 100}},"user":{"id":5,"name":"Dora Away {{Tag}}"},"notes":"Looking at it.","private_notes":false,"created_on":"{{at}}","details":[]}]}
            """)!.AsObject();
        return id;
    }

    /// <summary>A comment on the ticket, a minute later each time, and the ticket's updated_on with it.</summary>
    private static void Comment(int issue, string text, int authorId = 7, string author = "Requester", bool isPrivate = false)
    {
        var i = redmine.Issues[issue];
        var journals = i["journals"]!.AsArray();
        var at = DateTime.UtcNow.AddSeconds(journals.Count).ToString("yyyy-MM-ddTHH:mm:ssZ");
        journals.Add(new JsonObject
        {
            ["id"] = issue * 100 + journals.Count, ["user"] = new JsonObject { ["id"] = authorId, ["name"] = author }, ["notes"] = text,
            ["private_notes"] = isPrivate, ["created_on"] = at, ["details"] = new JsonArray(),
        });
        i["updated_on"] = at;
    }

    private static async Task<List<CookieAwayCommentEntity>> RowsAsync(int issue)
    {
        await using var db = app.NewDb();
        return await db.CookieAwayComments.AsNoTracking().Where(a => a.IssueId == issue).OrderBy(a => a.JournalId).ToListAsync();
    }

    [TestMethod]
    public async Task Switched_off_finds_nothing_and_switching_on_judges_nothing_old()
    {
        await SaveAsync("actions", new ActionSettings());
        var issue = Ticket();
        await Book.RunAsync(true, CancellationToken.None);
        Comment(issue, "The cooling failed, we need someone today!");
        await Book.RunAsync(true, CancellationToken.None);
        Assert.AreEqual(0, (await RowsAsync(issue)).Count, "off: no row");

        await SaveAsync("actions", new ActionSettings { Duties = new DutySwitches(AwayComments: true) });
        await Book.RunAsync(true, CancellationToken.None);
        Assert.AreEqual(0, (await RowsAsync(issue)).Count, "the comment came while it was off: nothing old");
        Comment(issue, "Still 38 degrees.");
        await Book.RunAsync(true, CancellationToken.None);
        Assert.AreEqual(1, (await RowsAsync(issue)).Count, "a comment after switching on");
        await SaveAsync("actions", new ActionSettings());
        await Task_.StepAsync(CancellationToken.None);
        Assert.AreEqual(AwayCommentState.Off, (await RowsAsync(issue)).Single().State, "switched off: what waits ends as off");
    }

    [TestMethod]
    public async Task Without_Cookie_the_chosen_person_is_told_marked_not_judged_and_once_per_ticket()
    {
        await SaveAsync("actions", new ActionSettings { Duties = new DutySwitches(AwayComments: true) });
        var issue = Ticket();
        await Book.RunAsync(true, CancellationToken.None);
        Comment(issue, "Assigned to me? I'm only the requester.", authorId: 5, author: $"Dora Away {Tag}");
        Comment(issue, "The cooling failed, we need someone today!");
        Comment(issue, "A note for the team.", authorId: 8, author: "Staff Member", isPrivate: true);
        Comment(issue, "Thanks!", authorId: FakeRedmine.CookieUserId, author: "PNMT Cookie");
        await Book.RunAsync(true, CancellationToken.None);
        var rows = await RowsAsync(issue);
        Assert.AreEqual(2, rows.Count, "not Dora's own, nor Cookie's");
        Assert.IsTrue(rows.All(r => r.State == AwayCommentState.Pending && r.AssigneeUserId == dora));
        Assert.IsTrue(rows[1].IsPrivate, "private notes count by default");
        Assert.AreEqual(DateOnly.FromDateTime(Clock.LocalNow), rows[0].AwayUntil, "back tomorrow, with the hours always open");

        // Cookie is stopped: code handles it at once, as set (tell, marked not judged).
        var step = await Task_.StepAsync(CancellationToken.None);
        Assert.AreEqual(2, step.WithoutVerdict);
        Assert.AreEqual(1, step.Told, "one notice for the ticket");
        rows = await RowsAsync(issue);
        CollectionAssert.AreEqual(new[] { AwayCommentState.Notified, AwayCommentState.Joined }, rows.Select(r => r.State).ToArray());
        Assert.IsTrue(rows.All(r => r.JudgedBy == "code" && r.Importance is null));
        await using (var db = app.NewDb())
        {
            var sent = await db.CookieOutbox.AsNoTracking().Where(o => o.IssueId == issue && o.Channel == CookieChannel.AwayEmail).ToListAsync();
            Assert.AreEqual(bob, sent.Single().UserId, "the chosen person, never Dora");
            StringAssert.Contains(sent[0].Subject, $"#{issue} while Dora {Tag} is away");
            StringAssert.Contains(sent[0].Body, "Not judged: Cookie is stopped or paused.");
            Assert.IsFalse(sent[0].Body.Contains("cooling failed"), "Bob has no Redmine: View: the links only");
        }

        // A later comment within 4 hours waits for the next notice.
        Comment(issue, "Any news?");
        await Book.RunAsync(true, CancellationToken.None);
        await Task_.StepAsync(CancellationToken.None);
        Assert.AreEqual(AwayCommentState.Waiting, (await RowsAsync(issue)).Last().State);
        await SaveAsync("actions", new ActionSettings());
        await Task_.StepAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task An_assignee_who_is_back_gets_nothing_sent_and_record_only_tells_nobody()
    {
        await SaveAsync("actions", new ActionSettings { Duties = new DutySwitches(AwayComments: true) });
        var issue = Ticket();
        await Book.RunAsync(true, CancellationToken.None);
        Comment(issue, "Please call me back.");
        await Book.RunAsync(true, CancellationToken.None);
        Assert.AreEqual(1, (await RowsAsync(issue)).Count);
        await using (var db = app.NewDb())
        {
            await db.AvailabilityEntries.Where(e => e.UserId == dora).ExecuteDeleteAsync();
        }
        try
        {
            await Task_.StepAsync(CancellationToken.None);
            Assert.AreEqual(AwayCommentState.Back, (await RowsAsync(issue)).Single().State);
        }
        finally
        {
            await using var db = app.NewDb();
            var vacation = await db.AvailabilityTypes.SingleAsync(t => t.Code == "VACA");
            db.AvailabilityEntries.Add(new AvailabilityEntryEntity
            {
                UserId = dora, Date = DateOnly.FromDateTime(Clock.LocalNow), TypeId = vacation.Id, SetAt = Clock.UtcNow, SetBy = "test",
            });
            await db.SaveChangesAsync();
        }

        await SaveAsync("redmine", new CookieRedmineSettings
        {
            Projects = [WatchedProject.Default(1)],
            Away = new AwayWatchSettings { Recipient = AwayRecipient.Person, PersonId = bob, WithoutVerdict = AwayWithoutVerdict.Record, PrivateNotes = false },
        });
        try
        {
            Comment(issue, "Private: the vendor called.", authorId: 8, author: "Staff Member", isPrivate: true);
            Comment(issue, "Second try: please call me back.");
            await Book.RunAsync(true, CancellationToken.None);
            await Task_.StepAsync(CancellationToken.None);
            var last = (await RowsAsync(issue)).Last();
            Assert.AreEqual("Second try: please call me back.", last.Text, "the private note doesn't count while private notes are off");
            Assert.AreEqual(AwayCommentState.Recorded, last.State);
        }
        finally
        {
            await SaveAsync("redmine", new CookieRedmineSettings
            {
                Projects = [WatchedProject.Default(1)],
                Away = new AwayWatchSettings { Recipient = AwayRecipient.Person, PersonId = bob },
            });
            await SaveAsync("actions", new ActionSettings());
        }

        var list = await (await app.ClientWithRights("Redmine:Manage").GetAsync("/api/v1/cookie/admin/redmine/away")).ReadAsync<Paged<CookieAwayCommentItem>>();
        Assert.IsTrue(list.Items.Any(i => i.IssueId == issue && i.State == AwayCommentState.Recorded && i.Assignee == $"Dora {Tag}"));
    }

    [TestMethod]
    public async Task A_chosen_person_must_be_an_active_user_and_one_absence_must_count()
    {
        using var nobody = await admin.PutAsJsonAsync(V1.CookieSettingsPath("redmine"),
            new CookieRedmineSettings { Away = new AwayWatchSettings { Recipient = AwayRecipient.Person } }, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.BadRequest, nobody.StatusCode);
        StringAssert.Contains(await nobody.Content.ReadAsStringAsync(), "away.personId");
        using var unknown = await admin.PutAsJsonAsync(V1.CookieSettingsPath("redmine"),
            new CookieRedmineSettings { Away = new AwayWatchSettings { PersonId = Guid.NewGuid() } }, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var none = await admin.PutAsJsonAsync(V1.CookieSettingsPath("redmine"),
            new CookieRedmineSettings { Away = new AwayWatchSettings { AwayFlags = [] } }, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.BadRequest, none.StatusCode);
        using var onSite = await admin.PutAsJsonAsync(V1.CookieSettingsPath("redmine"),
            new CookieRedmineSettings { Away = new AwayWatchSettings { AwayFlags = [AvailabilityFlag.OnSite] } }, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.BadRequest, onSite.StatusCode, "only absences");
        await SaveAsync("redmine", new CookieRedmineSettings { Projects = [WatchedProject.Default(1)], Away = new AwayWatchSettings { Recipient = AwayRecipient.Person, PersonId = bob } });
    }

    [TestMethod]
    public void The_rules_pick_new_comments_absences_and_whom_to_tell()
    {
        RmJournal J(int id, int? user, string? notes, bool isPrivate = false) => new(id, user is { } u ? new RmRef(u, "x") : null, notes, DateTime.UtcNow, isPrivate, []);
        var journals = new[] { J(1, 7, "old"), J(2, 7, "new"), J(3, 5, "the assignee's"), J(4, 9, "Cookie's"), J(5, 7, null), J(6, 8, "private", true), J(7, null, "anonymous") };
        CollectionAssert.AreEqual(new[] { 2, 6, 7 }, AwayRules.NewComments(journals, 1, 5, 9, true).Select(j => j.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 7 }, AwayRules.NewComments(journals, 1, 5, 9, false).Select(j => j.Id).ToArray());

        Assert.IsTrue(AwayRules.Away(true, f => f == AvailabilityFlag.Vacation, AwayWatchSettings.Allowed));
        Assert.IsFalse(AwayRules.Away(true, f => f == AvailabilityFlag.Compensation, [AvailabilityFlag.Vacation]), "compensation not chosen");
        Assert.IsFalse(AwayRules.Away(false, _ => true, AwayWatchSettings.Allowed), "not in the plan: never away");
        Assert.IsFalse(AwayRules.Away(true, f => f == AvailabilityFlag.HomeOffice, AwayWatchSettings.Allowed), "working from home");

        // Away Thursday and Friday, the weekend closed, back on Monday: away until Friday.
        var thursday = new DateOnly(2026, 11, 26);
        bool Weekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        Assert.AreEqual(new DateOnly(2026, 11, 27), AwayRules.AwayUntil(thursday, d => d >= new DateOnly(2026, 11, 30) && !Weekend(d), Weekend));
        Assert.IsNull(AwayRules.AwayUntil(thursday, _ => false, Weekend), "not back within 60 days");
        Assert.AreEqual("for a while", AwayRules.Until(null));

        var (anna, ben, carl) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var responsible = new AwayWatchSettings();
        var person = new AwayWatchSettings { Recipient = AwayRecipient.Person, PersonId = ben };
        CollectionAssert.AreEqual(new[] { anna }, AwayRules.Recipients(responsible, [anna], true, [carl]).ToArray());
        CollectionAssert.AreEqual(new[] { ben }, AwayRules.Recipients(responsible with { PersonId = ben }, [], true, [carl]).ToArray(),
            "nobody responsible: the chosen person stands in");
        CollectionAssert.AreEqual(new[] { ben }, AwayRules.Recipients(person, [anna], true, [carl]).ToArray());
        CollectionAssert.AreEqual(new[] { anna }, AwayRules.Recipients(person, [anna], false, [carl]).ToArray(), "the person doesn't work: the responsible");
        CollectionAssert.AreEqual(new[] { anna }, AwayRules.Recipients(person, [anna], true, [ben]).ToArray(), "the person wrote the comment");
        Assert.AreEqual(0, AwayRules.Recipients(responsible, [carl], false, [carl]).Count, "never the away assignee");
    }
}
