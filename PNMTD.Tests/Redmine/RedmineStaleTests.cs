using System.Globalization;
using System.Text.Json.Nodes;
using PNMTD.Api.V1.Contracts;
using PNMTD.Dev;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Redmine;

namespace PNMTD.Tests.Redmine;

/// <summary>
/// *No update for 14 days* (docs/audit-redmine-zabbix-plan.md §6.3): an open ticket nobody changed for <c>Redmine__StaleDays</c>.
/// Any change counts, except Cookie's own notes, which Redmine's <c>updated_on</c> counts too.
/// </summary>
[TestClass]
public sealed class RedmineStaleTests
{
    private static string Ago(double days) =>
        Clock.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>A ticket created <paramref name="createdDaysAgo"/> days ago, with journals by these users these many days ago.</summary>
    private static void Ticket(FakeRedmine redmine, int id, double createdDaysAgo, params (int User, double DaysAgo)[] journals)
    {
        var issue = redmine.Issues[id];
        issue["created_on"] = Ago(createdDaysAgo);
        var list = new JsonArray();
        foreach (var (user, daysAgo) in journals)
        {
            var cookie = user == FakeRedmine.CookieUserId;
            list.Add(new JsonObject
            {
                ["id"] = 7000 + list.Count, ["user"] = new JsonObject { ["id"] = user, ["name"] = cookie ? "PNMT Cookie" : "Anna" },
                ["notes"] = cookie ? "A reminder: this ticket needs an update." : "Looked at it.", ["created_on"] = Ago(daysAgo),
                ["private_notes"] = cookie, ["details"] = new JsonArray(),
            });
        }
        issue["journals"] = list;
        issue["updated_on"] = journals.Length == 0 ? Ago(createdDaysAgo) : Ago(journals.Min(j => j.DaysAgo));
    }

    [TestMethod]
    public async Task AnyChangeCountsExceptCookiesNotes()
    {
        var redmine = new FakeRedmine();
        Ticket(redmine, 9, 31);                                                             // nothing for 31 days: stale
        Ticket(redmine, 10, 40, (7, 30), (FakeRedmine.CookieUserId, 1));                    // Anna 30 days ago, then only Cookie's reminder: stale
        Ticket(redmine, 11, 40, (7, 30), (FakeRedmine.CookieUserId, 5), (7, 2));            // Anna answered after Cookie's note: not stale
        Ticket(redmine, 12, 3);                                                             // new: not stale
        Ticket(redmine, 13, 40, (7, 3));                                                    // Anna 3 days ago: not stale
        using var app = new TestApp(settings: FakeRedmine.WritableSettings, services: redmine.Install);
        var user = app.Client(Roles.User);

        var overview = await (await user.GetAsync("/api/v1/redmine/overview")).ReadAsync<RedmineOverview>();
        Assert.AreEqual(2, overview.Tiles.Stale, "#9 and #10; Cookie's note doesn't make #10 fresh");
        CollectionAssert.AreEqual(new[] { 9, 10 }, overview.Stale.Select(i => i.Id).ToArray(), "the oldest change first");
        Assert.IsTrue(overview.Stale.Single(i => i.Id == 10).UpdatedOn < Clock.UtcNow.AddDays(-29), "#10's last change is Anna's, not Cookie's note");

        var search = await (await user.GetAsync("/api/v1/redmine/issues?stale=true&sort=updated")).ReadAsync<Paged<RedmineIssueItem>>();
        Assert.AreEqual(2, search.TotalCount, "the tile's search finds what the tile counts");
        CollectionAssert.AreEqual(new[] { 9, 10 }, search.Items.Select(i => i.Id).ToArray());
        var newestFirst = await (await user.GetAsync("/api/v1/redmine/issues?stale=true")).ReadAsync<Paged<RedmineIssueItem>>();
        CollectionAssert.AreEqual(new[] { 10, 9 }, newestFirst.Items.Select(i => i.Id).ToArray(), "sorted by the change that counts, not by Cookie's note");
        var network = await (await user.GetAsync("/api/v1/redmine/issues?stale=true&project=3")).ReadAsync<Paged<RedmineIssueItem>>();
        Assert.AreEqual(0, network.TotalCount, "the other filters apply too");
        lock (redmine.Requests)
        {
            Assert.IsTrue(redmine.Requests.Where(r => r.RequestUri!.AbsolutePath != "/users/current.json")
                .All(r => r.Headers.GetValues("X-Redmine-API-Key").Single() == FakeRedmine.Key), "the write key only asks whose it is");
        }
    }

    [TestMethod]
    public async Task WithoutCookiesKeyEveryChangeCounts()
    {
        var redmine = new FakeRedmine();
        Ticket(redmine, 9, 31);
        Ticket(redmine, 10, 40, (7, 30), (FakeRedmine.CookieUserId, 1));
        using var app = new TestApp(settings: FakeRedmine.Settings, services: redmine.Install);
        var overview = await (await app.Client(Roles.User).GetAsync("/api/v1/redmine/overview")).ReadAsync<RedmineOverview>();
        CollectionAssert.AreEqual(new[] { 9 }, overview.Stale.Select(i => i.Id).ToArray(), "no write key: Redmine's own date decides");
    }

    [TestMethod]
    public void AChangeWithoutAJournalCounts()
    {
        var created = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        RmJournal Journal(int user, DateTime at) => new(1, new RmRef(user, null), "note", at, false, []);
        RmIssue Issue(DateTime updated, params RmJournal[] journals) => new(1, new RmRef(1, "IT Support"), null, new RmStatus(1, "New", false), null, null,
            null, null, "Subject", null, null, null, null, false, null, created, updated, null, null, journals, null, null, null);

        var cookieNote = created.AddDays(20);
        Assert.AreEqual(created.AddDays(2), RedmineService.LastChangeWithoutCookie(
            Issue(cookieNote, Journal(7, created.AddDays(2)), Journal(FakeRedmine.CookieUserId, cookieNote)), FakeRedmine.CookieUserId));
        Assert.AreEqual(created, RedmineService.LastChangeWithoutCookie(Issue(cookieNote, Journal(FakeRedmine.CookieUserId, cookieNote)),
            FakeRedmine.CookieUserId), "only Cookie wrote: the creation is the last change");
        var later = cookieNote.AddHours(3);
        Assert.AreEqual(later, RedmineService.LastChangeWithoutCookie(Issue(later, Journal(FakeRedmine.CookieUserId, cookieNote)),
            FakeRedmine.CookieUserId), "Redmine changed it after Cookie's note without a journal: someone did");
    }
}
