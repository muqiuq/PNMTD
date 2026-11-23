using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PNMTD.Api.V1.Contracts;
using PNMTD.Dev;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Redmine;

/// <summary>
/// Tickets received and solved today and yesterday, in PNMT's time zone, and the newest tickets (docs/boards-plan.md §7.4): the
/// overview's tiles, the search's filters behind them, and a project's view that keeps only its own linked problems (§2.6). A fake
/// Redmine of its own, since the tickets added here would change the other classes' counts.
/// </summary>
[TestClass]
public sealed class RedmineDaysTests
{
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient user = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: FakeRedmine.Settings, services: redmine.Install);
        user = app.Client(Roles.User);
        // Times relative to the start of today where PNMT is: the middle of today so far, yesterday, the day before.
        var today = Clock.ToUtc(DateOnly.FromDateTime(Clock.LocalNow).ToDateTime(TimeOnly.MinValue));
        var now = Clock.UtcNow;
        var midToday = today + (now - today) / 2;
        var yesterday = today.AddHours(-12);
        var before = today.AddHours(-36);
        Add(101, "Received today", midToday, null, false);
        Add(102, "Received yesterday, solved today", yesterday, midToday, true);
        Add(103, "Solved yesterday", before.AddDays(-2), yesterday, true);
        Add(104, "Reopened: closed today, open again", before.AddDays(-3), midToday, false);
        Add(105, "Received today, private", midToday.AddSeconds(1), null, false, isPrivate: true);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static void Add(int id, string subject, DateTime created, DateTime? closed, bool isClosed, bool isPrivate = false)
    {
        var issue = JsonNode.Parse(redmine.Issues[10].ToJsonString())!.AsObject();
        issue["id"] = id;
        issue["subject"] = subject;
        issue["created_on"] = Stamp(created);
        issue["updated_on"] = Stamp(closed ?? created);
        issue["closed_on"] = closed is { } c ? Stamp(c) : null;
        issue["is_private"] = isPrivate;
        issue["status"] = isClosed ? new JsonObject { ["id"] = 5, ["name"] = "Closed", ["is_closed"] = true }
            : new JsonObject { ["id"] = 1, ["name"] = "New", ["is_closed"] = false };
        redmine.Issues[id] = issue;
    }

    private static string Stamp(DateTime utc) => Clock.AsUtc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task TheOverviewCountsTicketsReceivedAndSolvedTodayAndYesterday()
    {
        var refresh = await user.PostAsync("/api/v1/redmine/refresh?view=overview", null);
        Assert.AreEqual(HttpStatusCode.OK, refresh.StatusCode, await refresh.Content.ReadAsStringAsync());
        var overview = await (await user.GetAsync("/api/v1/redmine/overview")).ReadAsync<RedmineOverview>();
        Assert.AreEqual(2, overview.Tiles.ReceivedToday, "#101 and #105, whatever their status");
        Assert.AreEqual(1, overview.Tiles.SolvedToday, "#102; the reopened #104 keeps closed_on, but its status is open");
        Assert.AreEqual(1, overview.Tiles.ReceivedYesterday, "#102");
        Assert.AreEqual(1, overview.Tiles.SolvedYesterday, "#103");

        Assert.AreEqual(105, overview.Newest[0].Id, "the newest first");
        Assert.IsTrue(overview.Newest[0].IsPrivate);
        Assert.IsNotNull(overview.Newest[0].CreatedOn);
        Assert.IsTrue(overview.Newest.Select(i => i.Id).Contains(103), "closed tickets are among the newest too");

        // Each tile opens the tickets it counts.
        var received = await (await user.GetAsync("/api/v1/redmine/issues?received=today&status=all&sort=-created")).ReadAsync<Paged<RedmineIssueItem>>();
        CollectionAssert.AreEqual(new[] { 105, 101 }, received.Items.Select(i => i.Id).ToArray());
        var solved = await (await user.GetAsync("/api/v1/redmine/issues?solved=today&status=closed")).ReadAsync<Paged<RedmineIssueItem>>();
        CollectionAssert.AreEqual(new[] { 102 }, solved.Items.Select(i => i.Id).ToArray());
        var solvedYesterday = await (await user.GetAsync("/api/v1/redmine/issues?solved=yesterday&status=closed")).ReadAsync<Paged<RedmineIssueItem>>();
        CollectionAssert.AreEqual(new[] { 103 }, solvedYesterday.Items.Select(i => i.Id).ToArray());

        // Sent as UTC timestamps from PNMT's midnight, never as a date that Redmine would read in its API user's zone.
        lock (redmine.Requests)
        {
            var created = redmine.Requests.Select(r => Uri.UnescapeDataString(r.RequestUri!.Query))
                .Where(q => q.Contains("v[created_on][]", StringComparison.Ordinal)).ToList();
            Assert.IsTrue(created.Count > 0);
            Assert.IsTrue(created.All(q => System.Text.RegularExpressions.Regex.IsMatch(q, @"v\[created_on\]\[\]=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ")), created[0]);
        }
    }

    [TestMethod]
    public async Task AProjectsOverviewListsOnlyItsOwnTicketsLinkedToAProblem()
    {
        var host = await V1.CreateHostAsync(user, V1.Unique("rmdays"));
        var sensor = await V1.CreateSensorAsync(user, host.Id, SensorType.SIMPLE);
        V1.AddEvents(app, sensor.Id, (Clock.UtcNow.AddMinutes(-5), 500, "down"));
        app.ClearDashboardCache();
        var linked = await user.PostAsJsonAsync("/api/v1/redmine/links", new { issueId = 9, sensorId = sensor.Id });
        Assert.AreEqual(HttpStatusCode.Created, linked.StatusCode, await linked.Content.ReadAsStringAsync());
        try
        {
            var all = await (await user.GetAsync("/api/v1/redmine/overview")).ReadAsync<RedmineOverview>();
            CollectionAssert.Contains(all.Problems.Select(t => t.Id).ToList(), 9);
            var itSupport = await (await user.GetAsync("/api/v1/redmine/overview?project=1")).ReadAsync<RedmineOverview>();
            CollectionAssert.Contains(itSupport.Problems.Select(t => t.Id).ToList(), 9, "#9 is IT Support's");
            var network = await (await user.GetAsync("/api/v1/redmine/overview?project=3")).ReadAsync<RedmineOverview>();
            Assert.AreEqual(0, network.Problems.Count, "Network's view doesn't list IT Support's ticket");
        }
        finally
        {
            await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
        }
    }
}
