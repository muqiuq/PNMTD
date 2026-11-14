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
/// The case book (docs/cookie-plan.md §4.3, §7, §19.7): a host's problems are one case, which closes when all are fine;
/// a sensor note and a moving linked ticket are progress; watched tickets get their clock from the fake Redmine. Shares the
/// tests' database, so it only looks at its own hosts and tickets.
/// </summary>
[TestClass]
public sealed class CaseBookTests
{
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: FakeRedmine.Settings, services: redmine.Install);
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static CaseBook Book => app.Services.GetRequiredService<CaseBook>();

    /// <summary>The tests' events are hours old, so that they don't crowd the dashboard's recent changes of the other tests.</summary>
    private static DateTime Back => Clock.UtcNow.AddHours(-6);

    private static async Task<CookieCaseEntity?> OpenCaseAsync(string key)
    {
        await using var db = app.NewDb();
        return await db.CookieCases.AsNoTracking().SingleOrDefaultAsync(c => c.Key == key && c.Open);
    }

    [TestMethod]
    public async Task A_hosts_bad_sensors_are_one_case_that_closes_when_all_are_fine()
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique("nas"));
        var a = await V1.CreateSensorAsync(admin, host.Id, name: "backup");
        var b = await V1.CreateSensorAsync(admin, host.Id, name: "disk");
        var now = Back;
        V1.AddEvents(app, a.Id, (now.AddMinutes(-10), 200, "fine"), (now.AddMinutes(-5), 500, "backup failed"));
        V1.AddEvents(app, b.Id, (now.AddMinutes(-4), 500, "disk full"));
        await Book.RunAsync(false, CancellationToken.None);

        var c = await OpenCaseAsync($"host:{host.Id}");
        Assert.IsNotNull(c);
        Assert.AreEqual(CookieCaseKind.Problem, c.Kind);
        Assert.AreEqual($"{host.Name}: 2 problems", c.Title);
        CollectionAssert.AreEqual(new[] { "Monitoring" }, c.Areas);

        V1.AddEvents(app, a.Id, (Back.AddMinutes(1), 200, "backup ok"));
        await Book.RunAsync(false, CancellationToken.None);
        c = await OpenCaseAsync($"host:{host.Id}");
        Assert.AreEqual($"{host.Name}: disk", c!.Title, "one member left");

        V1.AddEvents(app, b.Id, (Back.AddMinutes(2), 200, "disk ok"));
        await Book.RunAsync(false, CancellationToken.None);
        Assert.IsNull(await OpenCaseAsync($"host:{host.Id}"));
        await using var db = app.NewDb();
        var kinds = await db.CookieCaseEntries.Where(e => e.CaseId == c.Id).OrderBy(e => e.Id).Select(e => e.Kind).ToListAsync();
        CollectionAssert.AreEqual(new[] { "opened", "ok", "closed" }, kinds);
    }

    [TestMethod]
    public async Task A_sensor_note_and_a_moving_linked_ticket_are_progress_and_an_open_ticket_takes_the_ticket_duty()
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique("router"));
        var sensor = await V1.CreateSensorAsync(admin, host.Id, name: "uplink");
        V1.AddEvents(app, sensor.Id, (Back.AddMinutes(-30), 500, "down"));
        await Book.RunAsync(false, CancellationToken.None);
        var c = (await OpenCaseAsync($"host:{host.Id}"))!;
        await using (var db = app.NewDb())
        {
            var duty = await db.CookieDuties.SingleAsync(d => d.CaseId == c.Id);
            Assert.AreEqual("problem:ticket", duty.Key);
            Assert.AreEqual(c.ProgressAt.AddHours(48), duty.DueAt, "2 days without progress");
            var entity = await db.Sensors.SingleAsync(s => s.Id == sensor.Id);
            entity.Notes = "ISP informed, ticket 4711";
            await db.SaveChangesAsync();
        }
        await Book.RunAsync(false, CancellationToken.None);
        var after = (await OpenCaseAsync($"host:{host.Id}"))!;
        Assert.IsTrue(after.ProgressAt > c.ProgressAt, "the note is progress");

        await using (var db = app.NewDb())
        {
            db.RedmineIssues.Add(new RedmineIssueEntity { IssueId = 777_001, ProjectId = 3, ProjectName = "Network", StatusName = "New", Subject = "uplink",
                UpdatedOn = Clock.UtcNow, FetchedAt = Clock.UtcNow });
            db.RedmineLinks.Add(new RedmineLinkEntity { Id = Guid.NewGuid(), IssueId = 777_001, HostId = host.Id, CreatedAt = Clock.UtcNow });
            await db.SaveChangesAsync();
        }
        await Book.RunAsync(false, CancellationToken.None);
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.CookieDuties.AnyAsync(d => d.CaseId == c.Id && d.State == CookieDutyState.Due), "an open linked ticket: no ticket of Cookie's");
            var entries = await db.CookieCaseEntries.Where(e => e.CaseId == c.Id && e.Kind == "progress").Select(e => e.Text).ToListAsync();
            Assert.IsTrue(entries.Any(t => t.Contains("#777001")), string.Join(" | ", entries));
        }
        V1.AddEvents(app, sensor.Id, (Back.AddMinutes(3), 200, "up"));
        await Book.RunAsync(false, CancellationToken.None);
    }

    [TestMethod]
    public async Task Watched_tickets_get_a_case_and_a_first_answer_resets_their_clock()
    {
        await SaveAsync("redmine", new CookieRedmineSettings { Projects = [WatchedProject.Default(1)] });
        await Book.RunAsync(true, CancellationToken.None);
        var c = (await OpenCaseAsync("ticket:10"))!;
        Assert.AreEqual(CookieCaseKind.Ticket, c.Kind);
        Assert.IsNull(c.FirstAnswerAt);
        Assert.IsNotNull(await OpenCaseAsync("ticket:9"), "In Progress is open");
        Assert.IsNull(await OpenCaseAsync("ticket:15"), "closed");
        Assert.IsNull(await OpenCaseAsync("ticket:11"), "Network isn't watched");
        await using (var db = app.NewDb())
        {
            var keys = await db.CookieDuties.Where(d => d.CaseId == c.Id).Select(d => d.Key).OrderBy(k => k).ToListAsync();
            CollectionAssert.AreEqual(new[] { "first-answer:missed", "first-answer:reminder" }, keys);
        }

        var issue = redmine.Issues[10];
        issue["journals"] = new JsonArray(new JsonObject
        {
            ["id"] = 501, ["user"] = new JsonObject { ["id"] = 5, ["name"] = "Anna Muster" }, ["notes"] = "Which printer is it?",
            ["created_on"] = "2026-11-14T15:00:00Z", ["private_notes"] = false, ["details"] = new JsonArray(),
        });
        issue["updated_on"] = "2026-11-14T15:00:00Z";
        try
        {
            await Book.RunAsync(true, CancellationToken.None);
            c = (await OpenCaseAsync("ticket:10"))!;
            Assert.AreEqual(new DateTime(2026, 11, 14, 15, 0, 0, DateTimeKind.Utc), c.FirstAnswerAt);
            await using (var db = app.NewDb())
            {
                var keys = await db.CookieDuties.Where(d => d.CaseId == c.Id).Select(d => d.Key).OrderBy(k => k).ToListAsync();
                CollectionAssert.AreEqual(new[] { "update:missed@202611141500", "update:reminder@202611141500" }, keys);
            }

            issue["status"] = new JsonObject { ["id"] = 5, ["name"] = "Closed", ["is_closed"] = true };
            await Book.RunAsync(true, CancellationToken.None);
            Assert.IsNull(await OpenCaseAsync("ticket:10"), "closed in Redmine");
        }
        finally
        {
            redmine.Issues.Clear();
            foreach (var (id, fresh) in new FakeRedmine().Issues)
            {
                redmine.Issues[id] = fresh;
            }
            await SaveAsync("redmine", new CookieRedmineSettings());
        }
    }

    [TestMethod]
    public async Task A_stopped_case_has_no_duties_raises_Info_at_most_and_goes_on_when_it_grows()
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique("copier"));
        var first = await V1.CreateSensorAsync(admin, host.Id, name: "toner");
        V1.AddEvents(app, first.Id, (Back.AddDays(-3), 500, "toner empty"));
        await Book.RunAsync(false, CancellationToken.None);
        var c = (await OpenCaseAsync($"host:{host.Id}"))!;

        using (var comment = await admin.PostAsJsonAsync($"/api/v1/cookie/cases/{c.Id}/comments", new CookieCaseCommentRequest("Ordered, arrives Monday.")))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, comment.StatusCode, await comment.Content.ReadAsStringAsync());
        }
        using (var noReason = await admin.PostAsJsonAsync($"/api/v1/cookie/cases/{c.Id}/stop", new CookieCaseCommentRequest(" ")))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, noReason.StatusCode, "a stop says why");
        }
        using (var use = await app.ClientWithRights("Cookie:Use,Monitoring:View").PostAsJsonAsync($"/api/v1/cookie/cases/{c.Id}/stop",
            new CookieCaseCommentRequest("no")))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, use.StatusCode, "stopping needs Instruct");
        }
        using (var stop = await admin.PostAsJsonAsync($"/api/v1/cookie/cases/{c.Id}/stop", new CookieCaseCommentRequest("Known: the vendor replaces it.")))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, stop.StatusCode, await stop.Content.ReadAsStringAsync());
        }
        await Book.RunAsync(false, CancellationToken.None);
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.CookieDuties.AnyAsync(d => d.CaseId == c.Id && d.State == CookieDutyState.Due), "nothing falls due");
            Assert.IsTrue(await db.CookieDuties.AnyAsync(d => d.CaseId == c.Id && d.State == CookieDutyState.Stopped), "the 2-day ticket closed as stopped");
        }
        var stopped = (await OpenCaseAsync($"host:{host.Id}"))!;
        var status = CookieStatusRules.Compute([stopped], [], new Dictionary<Guid, string>());
        Assert.AreEqual(OverallState.Info, status.State, status.Reason);
        var detail = await (await admin.GetAsync($"/api/v1/cookie/cases/{c.Id}")).ReadAsync<CookieCaseDetail>();
        Assert.AreEqual("Known: the vendor replaces it.", detail.Case.Stopped?.Comment);
        Assert.IsTrue(detail.Entries.Any(e => e.Kind == "comment" && e.Text == "Ordered, arrives Monday."));

        var second = await V1.CreateSensorAsync(admin, host.Id, name: "drum");
        V1.AddEvents(app, second.Id, (Back, 500, "drum worn"));
        await Book.RunAsync(false, CancellationToken.None);
        var grown = (await OpenCaseAsync($"host:{host.Id}"))!;
        Assert.IsNull(grown.StoppedAt, "a new member: Cookie goes on by itself");
        await using (var db = app.NewDb())
        {
            Assert.IsTrue(await db.CookieCaseEntries.AnyAsync(e => e.CaseId == c.Id && e.Kind == "go-on"));
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == "CookieCaseStopped" && l.Detail!.Contains("vendor")));
        }
    }

    [TestMethod]
    public async Task An_unreachable_Redmine_closes_no_ticket_case()
    {
        await SaveAsync("redmine", new CookieRedmineSettings { Projects = [WatchedProject.Default(1)] });
        try
        {
            await Book.RunAsync(true, CancellationToken.None);
            Assert.IsNotNull(await OpenCaseAsync("ticket:9"));
            redmine.Down = true;
            var result = await Book.RunAsync(true, CancellationToken.None);
            StringAssert.StartsWith(result.Error, "Redmine:");
            Assert.IsNotNull(await OpenCaseAsync("ticket:9"));
        }
        finally
        {
            redmine.Down = false;
            await SaveAsync("redmine", new CookieRedmineSettings());
        }
    }

    [TestMethod]
    public async Task Settings_are_versioned_checked_and_rolled_back()
    {
        using (var bad = await admin.PutAsJsonAsync("/api/v1/cookie/admin/settings/models", new ModelSettings { SleepMinutes = 0 }, CookieSettings.Json))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode, "0 minutes of sleep");
        }
        var first = await SaveAsync("models", new ModelSettings { SleepMinutes = 7 });
        var second = await SaveAsync("models", new ModelSettings());
        Assert.AreEqual(first + 1, second);
        using var rollback = await admin.PostAsync($"/api/v1/cookie/admin/settings/models/rollback/{first}", null);
        Assert.AreEqual(HttpStatusCode.OK, rollback.StatusCode, await rollback.Content.ReadAsStringAsync());
        var all = await (await admin.GetAsync("/api/v1/cookie/admin/settings")).ReadAsync<CookieAllSettings>();
        Assert.AreEqual(7, all.Models.SleepMinutes, "the values of the first version again");
        var versions = await (await admin.GetAsync("/api/v1/cookie/admin/settings/models/versions")).ReadAsync<List<CookieSettingsVersion>>();
        Assert.AreEqual($"Rolled back to version {first}", versions[0].Note);
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == "CookieSettingsRolledBack" && l.Detail!.Contains("Models")));
        await SaveAsync("models", new ModelSettings());
    }

    [TestMethod]
    public async Task The_status_hides_its_reason_from_those_who_cant_see_its_cases()
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique("core"));
        var sensor = await V1.CreateSensorAsync(admin, host.Id, name: "ping");
        V1.AddEvents(app, sensor.Id, (Back.AddMinutes(-1), 500, "down"));
        await Book.RunAsync(false, CancellationToken.None);
        var viewer = await (await app.Client(Roles.Viewer).GetAsync("/api/v1/cookie/status")).ReadAsync<CookieStatusItem>();
        Assert.AreEqual("code", viewer.Overall.Source);
        Assert.AreNotEqual(OverallState.AllFine, viewer.Overall.State);
        var nobody = await (await app.ClientWithRights("Mail:Read").GetAsync("/api/v1/cookie/status")).ReadAsync<CookieStatusItem>();
        Assert.AreEqual(viewer.Overall.State, nobody.Overall.State);
        Assert.AreEqual(0, nobody.Overall.Cases.Count);
        StringAssert.Contains(nobody.Overall.Reason, "outside your rights");
        V1.AddEvents(app, sensor.Id, (Back.AddMinutes(3), 200, "up"));
        await Book.RunAsync(false, CancellationToken.None);
    }

    private static async Task<int> SaveAsync<T>(string section, T value)
    {
        using var response = await admin.PutAsJsonAsync($"/api/v1/cookie/admin/settings/{section}", value, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.ReadAsync<CookieSaved>()).Version;
    }
}
