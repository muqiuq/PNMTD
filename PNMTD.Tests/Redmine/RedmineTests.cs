using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Dev;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Models;
using PNMTD.Redmine;
using PNMTD.Services.Events;

namespace PNMTD.Tests.Redmine;

/// <summary>
/// Redmine in PNMT (docs/audit-redmine-zabbix-plan.md §6, §12), against a fake that answers as Redmine 6.0 did in R0.
/// </summary>
[TestClass]
public sealed class RedmineTests
{
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient viewer = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: FakeRedmine.Settings, services: redmine.Install);
        user = app.Client(Roles.User);
        viewer = app.Client(Roles.Viewer);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public void Reset()
    {
        redmine.Down = false;
        redmine.RefuseKey = false;
    }

    [TestMethod]
    public async Task TheKeyIsAHeaderAndTheProjectsIncludeTheirSubprojects()
    {
        var status = await (await user.GetAsync("/api/v1/redmine/status")).ReadAsync<RedmineStatus>();
        Assert.IsTrue(status.Enabled);
        Assert.AreEqual("https://redmine.example.com", status.Url);
        CollectionAssert.AreEquivalent(new[] { "it-support", "it-printers", "network" }, status.Projects.Select(p => p.Identifier).ToArray(),
            "Printers below IT Support; Other is not PNMT's");
        Assert.IsNull(status.Error);
        lock (redmine.Requests)
        {
            Assert.IsTrue(redmine.Requests.All(r => !r.RequestUri!.ToString().Contains(FakeRedmine.Key, StringComparison.Ordinal)), "never in a URL");
            Assert.IsTrue(redmine.Requests.All(r => r.Headers.GetValues("X-Redmine-API-Key").Single() == FakeRedmine.Key));
        }
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/redmine/issues")).StatusCode, "Viewers read no tickets");
    }

    [TestMethod]
    public async Task TheSearchStaysWithinTheConfiguredProjects()
    {
        var open = await (await user.GetAsync("/api/v1/redmine/issues")).ReadAsync<Paged<RedmineIssueItem>>();
        CollectionAssert.AreEquivalent(new[] { 9, 10, 11, 12, 13 }, open.Items.Select(i => i.Id).ToArray(), "open, not #14 of Other, not the closed #15");
        var all = await (await user.GetAsync("/api/v1/redmine/issues?status=all")).ReadAsync<Paged<RedmineIssueItem>>();
        CollectionAssert.Contains(all.Items.Select(i => i.Id).ToList(), 15);
        var other = await (await user.GetAsync("/api/v1/redmine/issues?search=%2314")).ReadAsync<Paged<RedmineIssueItem>>();
        Assert.AreEqual(0, other.TotalCount, "#14 is another project's: not found, though the key could read it");
        var byNumber = await (await user.GetAsync("/api/v1/redmine/issues?search=9")).ReadAsync<Paged<RedmineIssueItem>>();
        Assert.AreEqual("https://redmine.example.com/issues/9", byNumber.Items.Single().Url);
        var text = await (await user.GetAsync("/api/v1/redmine/issues?search=printer")).ReadAsync<Paged<RedmineIssueItem>>();
        CollectionAssert.AreEquivalent(new[] { 9, 13 }, text.Items.Select(i => i.Id).ToArray());
        var unassignedNetwork = await (await user.GetAsync("/api/v1/redmine/issues?assignee=none&project=3")).ReadAsync<Paged<RedmineIssueItem>>();
        Assert.AreEqual(0, unassignedNetwork.TotalCount);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.GetAsync("/api/v1/redmine/issues?project=4")).StatusCode, "Other is not PNMT's");
    }

    [TestMethod]
    public async Task ATicketInFullWithoutPrivateNotesAndWithItsReferencesAsLinks()
    {
        var issue = await (await user.GetAsync("/api/v1/redmine/issues/9")).ReadAsync<RedmineIssueDetail>();
        Assert.AreEqual("Printer on floor 2 does not print", issue.Subject);
        Assert.AreEqual(("In Progress", "Urgent"), (issue.Status, issue.Priority));
        Assert.AreEqual("markdown", issue.Description!.Format);
        StringAssert.Contains(issue.Description.Text, "[#2](/pnmt-admin/redmine/issues/2)");
        Assert.IsFalse(issue.Journals.Any(j => j.Notes?.Text.Contains("4711", StringComparison.Ordinal) == true), "private notes stay out");
        var status = issue.Journals.SelectMany(j => j.Changes).Single(c => c.Field == "Status");
        Assert.AreEqual(("New", "In Progress"), (status.Old, status.New));
        Assert.AreEqual("related to", issue.Related.Single().Relation);
        Assert.AreEqual("https://redmine.example.com/issues/9", issue.Url);

        var other = await user.GetAsync("/api/v1/redmine/issues/14");
        Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
        Assert.AreEqual("redmine_other_project", (await other.ProblemAsync()).GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync("/api/v1/redmine/issues/999")).StatusCode);
    }

    [TestMethod]
    public async Task ALinkIsUniqueAuditedShownOnThePanelsAndGoesWithItsHost()
    {
        var host = await (await user.PostAsJsonAsync("/api/v1/hosts", new { name = $"rm-{Guid.NewGuid():N}"[..20] })).ReadAsync<HostDetail>();
        var sensor = await (await user.PostAsJsonAsync("/api/v1/sensors", new { hostId = host.Id, name = "ping", type = "PING", parameters = "10.0.0.1", interval = 60 }))
            .ReadAsync<SensorDetail>();
        var created = await user.PostAsJsonAsync("/api/v1/redmine/links", new { issueId = 9, hostId = host.Id, note = "the printer" });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var link = await created.ReadAsync<RedmineLinkItem>();
        Assert.AreEqual((RedmineLinkKind.Host, host.Name, "the printer"), (link.Kind, link.TargetName, link.Note));

        Assert.AreEqual(HttpStatusCode.Conflict, (await user.PostAsJsonAsync("/api/v1/redmine/links", new { issueId = 9, hostId = host.Id })).StatusCode);
        var other = await user.PostAsJsonAsync("/api/v1/redmine/links", new { issueId = 14, hostId = host.Id });
        Assert.AreEqual(HttpStatusCode.BadRequest, other.StatusCode, "another project's ticket can't be linked");

        // The sensor's panel shows its host's ticket, from PNMT's copy.
        var related = await (await user.GetAsync($"/api/v1/redmine/related?sensorId={sensor.Id}")).ReadAsync<RedmineRelated>();
        Assert.AreEqual(9, related.Via.Single(g => g.Kind == RedmineLinkKind.Host).Tickets.Single().Ticket.Id);
        var ticket = await (await user.GetAsync("/api/v1/redmine/issues/9")).ReadAsync<RedmineIssueDetail>();
        Assert.AreEqual(host.Name, ticket.Links.Single(l => l.TargetId == host.Id).TargetName);
        var search = await (await user.GetAsync("/api/v1/search?q=%239")).ReadAsync<SearchResult>();
        Assert.AreEqual("Printer on floor 2 does not print", search.Tickets.Single().Subject);

        await using (var db = app.NewDb())
        {
            var entry = await db.AuditLog.AsNoTracking().SingleAsync(l => l.EventType == AuditEvents.RedmineLinkAdded && l.Subjects.Contains(host.Id)
                && l.Outcome == AuditOutcome.Succeeded);
            Assert.AreEqual(1, await db.AuditLog.CountAsync(l => l.EventType == AuditEvents.RedmineLinkAdded && l.Subjects.Contains(host.Id)
                && l.Outcome == AuditOutcome.Failed), "the duplicate is in the log too, as failed");
            Assert.AreEqual(("RedmineTicket", "9"), (entry.TargetType, entry.TargetId));
            StringAssert.Contains(entry.Detail, $"#9 Printer on floor 2 does not print → host {host.Name}");
        }
        Assert.AreEqual(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/hosts/{host.Id}")).StatusCode);
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.RedmineLinks.AnyAsync(l => l.Id == link.Id), "the link goes with its host");
        }
    }

    [TestMethod]
    public async Task TheOverviewPicksTheImportantTickets()
    {
        var overview = await (await user.GetAsync("/api/v1/redmine/overview")).ReadAsync<RedmineOverview>();
        CollectionAssert.AreEquivalent(new[] { 9, 11 }, overview.Urgent.Select(i => i.Id).ToArray(), "above Normal, Redmine's default; not #14 of Other");
        CollectionAssert.AreEquivalent(new[] { 9, 10, 13 }, overview.Unassigned.Select(i => i.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 11 }, overview.Overdue.Select(i => i.Id).ToArray());
        Assert.AreEqual(5, overview.Tiles.Open);
        Assert.AreEqual(2, overview.Tiles.Urgent);
        Assert.AreEqual(14, overview.StaleDays);
    }

    [TestMethod]
    public async Task TheSyncMarksMissingTicketsAndTheSensorFailsOnTheSecondFailure()
    {
        var host = await (await user.PostAsJsonAsync("/api/v1/hosts", new { name = $"rm-sync-{Guid.NewGuid():N}"[..22] })).ReadAsync<HostDetail>();
        Assert.AreEqual(HttpStatusCode.Created, (await user.PostAsJsonAsync("/api/v1/redmine/links", new { issueId = 12, hostId = host.Id })).StatusCode);
        var worker = app.Services.GetRequiredService<RedmineWorker>();
        var sensors = app.Services.GetRequiredService<SystemSensors>();
        var removed = redmine.Issues[12];
        redmine.Issues.Remove(12);
        try
        {
            await worker.SyncAsync(CancellationToken.None);
            await using (var db = app.NewDb())
            {
                Assert.IsTrue(await db.RedmineIssues.Where(i => i.IssueId == 12).Select(i => i.Missing).SingleAsync());
            }
        }
        finally
        {
            redmine.Issues[12] = removed;
        }
        redmine.Down = true;
        await worker.SyncAsync(CancellationToken.None);
        Assert.AreNotEqual(PNMTStatusCodes.REDMINE_FAILING, (await sensors.LastAsync(SystemSensorKeys.Redmine, CancellationToken.None))?.Code, "once is no outage");
        await worker.SyncAsync(CancellationToken.None);
        var failing = await sensors.LastAsync(SystemSensorKeys.Redmine, CancellationToken.None);
        Assert.AreEqual(PNMTStatusCodes.REDMINE_FAILING, failing!.Code);
        StringAssert.Contains(failing.Message, "doesn't answer");
        // A ticket nobody read yet can't be shown; one PNMT keeps a snapshot of is shown as it was (docs/redmine-queue-outbound-plan.md §4.2).
        var down = await user.GetAsync("/api/v1/redmine/issues/11");
        Assert.AreEqual(HttpStatusCode.BadGateway, down.StatusCode);
        var kept = await (await user.GetAsync("/api/v1/redmine/issues/12")).ReadAsync<RedmineIssueDetail>();
        Assert.AreEqual(12, kept.Id, "read when it was linked");
        Assert.IsNotNull(kept.Freshness.RefreshedAt);

        redmine.Down = false;
        await worker.SyncAsync(CancellationToken.None);
        Assert.AreEqual(PNMTStatusCodes.SYSTEM_OK, (await sensors.LastAsync(SystemSensorKeys.Redmine, CancellationToken.None))!.Code);
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.RedmineIssues.Where(i => i.IssueId == 12).Select(i => i.Missing).SingleAsync(), "back again");
        }
    }

    [TestMethod]
    public void RedminesReferencesBecomeLinksOutsideCode()
    {
        var text = RedmineMarkdown.Prepare("See #12 and `#13`, attachment:\"photo 1.png\" and ![](scan.pdf)\n```\n#14 stays\n```\n# Heading",
            "https://redmine.example.com", [("photo 1.png", 5), ("scan.pdf", 6)]);
        StringAssert.Contains(text, "[#12](/pnmt-admin/redmine/issues/12)");
        StringAssert.Contains(text, "`#13`");
        StringAssert.Contains(text, "[photo 1.png](https://redmine.example.com/attachments/5)");
        StringAssert.Contains(text, "[scan.pdf](https://redmine.example.com/attachments/6)");
        StringAssert.Contains(text, "#14 stays");
        StringAssert.Contains(text, "# Heading");
    }
}
