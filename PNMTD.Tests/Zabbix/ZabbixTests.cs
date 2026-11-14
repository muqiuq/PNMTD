using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;
using PNMTD.Zabbix;

namespace PNMTD.Tests.Zabbix;

/// <summary>
/// Zabbix in PNMT (docs/audit-redmine-zabbix-plan.md §7, §12), against a fake that answers as Zabbix 6.0, 7.0 and 8.0 did
/// in Z0, with Zabbix__HostGroups set to "PNMT Servers": the core switch (PNMT Network) and its problem are not PNMT's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ZabbixTests
{
    private static readonly FakeZabbix zabbix = new();
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient viewer = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: FakeZabbix.Settings, services: zabbix.Install);
        user = app.Client(Roles.User);
        viewer = app.Client(Roles.Viewer);
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public async Task Reset() => await UseVersion("70");

    /// <summary>A version's Zabbix, as after a restart: its fixtures, no version known, no snapshot, an empty mirror.</summary>
    private static async Task UseVersion(string version)
    {
        zabbix.Down = false;
        zabbix.RefuseToken = false;
        zabbix.Load(version);
        app.Services.GetRequiredService<ZabbixClient>().ForgetVersion();
        app.Services.GetRequiredService<ZabbixSnapshot>().Clear();
        await using var db = app.NewDb();
        await db.ZabbixHosts.ExecuteDeleteAsync();
        await db.KeyValues.Where(k => k.Key == "last_zabbix_host_sync").ExecuteDeleteAsync();
        await app.Services.GetRequiredService<ZabbixGroups>().ResolveAsync(true, CancellationToken.None);
    }

    private static List<FakeZabbix.Call> Calls()
    {
        lock (zabbix.Calls)
        {
            return [.. zabbix.Calls];
        }
    }

    [TestMethod]
    [DataRow("60")]
    [DataRow("70")]
    [DataRow("80")]
    public async Task TheTokenGoesWhereTheVersionTakesIt(string version)
    {
        await UseVersion(version);
        var status = await (await user.GetAsync("/api/v1/zabbix/status")).ReadAsync<ZabbixStatus>();
        Assert.IsTrue(status.Enabled);
        Assert.AreEqual(zabbix.VersionText, status.Version);
        Assert.AreEqual("https://zabbix.example.com", status.Url);
        Assert.IsNull(status.Error);
        var summary = await (await user.GetAsync("/api/v1/zabbix/summary")).ReadAsync<ZabbixSummary>();
        Assert.IsNull(summary.Error, summary.Error);
        Assert.IsNotNull(summary.AsOf);

        var calls = Calls();
        Assert.IsTrue(calls.Where(c => c.Method == "apiinfo.version").All(c => c.AuthField is null && c.Bearer is null), "the version without the token");
        var others = calls.Where(c => c.Method != "apiinfo.version").ToList();
        Assert.IsTrue(others.Count >= 3);
        if (version == "60")
        {
            Assert.IsTrue(others.All(c => c.AuthField == FakeZabbix.Token && c.Bearer is null), "6.0 takes only the auth field");
        }
        else
        {
            Assert.IsTrue(others.All(c => c.Bearer == FakeZabbix.Token && c.AuthField is null), "the header from 6.4; 8.0 takes nothing else");
        }
    }

    [TestMethod]
    [DataRow("60", "selectGroups")]
    [DataRow("70", "selectHostGroups")]
    [DataRow("80", "selectHostGroups")]
    public async Task HostGroupsNarrowTheMirrorAndTheProblems(string version, string groupsKey)
    {
        await UseVersion(version);
        var hosts = await (await user.GetAsync("/api/v1/zabbix/hosts")).ReadAsync<Paged<ZabbixHostItem>>();
        CollectionAssert.AreEquivalent(new[] { "web01.example.com", "db02", "nas01", "old01" }, hosts.Items.Select(h => h.TechnicalName).ToArray(),
            "not the core switch: PNMT Network isn't in Zabbix__HostGroups");
        Assert.IsTrue(hosts.Items.All(h => h.Groups.SequenceEqual(["PNMT Servers"])), $"the groups read under {groupsKey}'s key");
        var sync = Calls().Last(c => c.Method == "host.get");
        Assert.IsTrue(sync.Params.ContainsKey(groupsKey));
        Assert.AreEqual(zabbix.GroupId("PNMT Servers"), sync.Params["groupids"]![0]!.GetValue<string>());

        var web01 = hosts.Items.Single(h => h.TechnicalName == "web01.example.com");
        Assert.AreEqual("Web 01", web01.Name);
        Assert.AreEqual(2, web01.Problems, "the /var and the CPU load");
        Assert.AreEqual(ZabbixSeverity.High, web01.WorstSeverity);
        var nas = hosts.Items.Single(h => h.TechnicalName == "nas01");
        Assert.AreEqual(ZabbixAvailability.Unavailable, nas.Availability);
        StringAssert.Contains(nas.Interfaces.Single().Error, "Connection refused");
        Assert.IsFalse(hosts.Items.Single(h => h.TechnicalName == "old01").Monitored);
        Assert.IsTrue(hosts.Items.Single(h => h.TechnicalName == "db02").InMaintenance);

        var problems = await (await user.GetAsync("/api/v1/zabbix/problems?suppressed=true")).ReadAsync<Paged<ZabbixProblemItem>>();
        CollectionAssert.AreEquivalent(new[] { "/var is 95 % full", "High CPU load", "Replication lag above 60 s" }, problems.Items.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public async Task TheSummaryCountsBySeverityAndCategory()
    {
        var summary = await (await viewer.GetAsync("/api/v1/zabbix/summary")).ReadAsync<ZabbixSummary>();
        Assert.AreEqual(ZabbixSeverity.Disaster, summary.Severities[0].Severity, "Disaster first");
        var high = summary.Severities.Single(s => s.Severity == ZabbixSeverity.High);
        Assert.AreEqual((1, 0), (high.Count, high.Unacknowledged), "the /var problem, acknowledged");
        var average = summary.Severities.Single(s => s.Severity == ZabbixSeverity.Average);
        Assert.AreEqual((1, 1), (average.Count, average.Unacknowledged));
        Assert.AreEqual(0, summary.Severities.Single(s => s.Severity == ZabbixSeverity.Warning).Count, "db02's is suppressed: in maintenance");
        Assert.AreEqual(1, summary.Suppressed);
        Assert.AreEqual("group", summary.Category);
        var servers = summary.Categories.Single();
        Assert.AreEqual(("PNMT Servers", 2), (servers.Name, servers.Total));
        CollectionAssert.AreEqual(new[] { "class", "component", "scope" }, summary.TagNames.Take(3).ToArray());
        Assert.AreEqual(4, summary.Hosts.Total);
        Assert.AreEqual(3, summary.Hosts.Monitored);
        Assert.AreEqual(1, summary.Hosts.Unavailable);
        Assert.AreEqual(1, summary.Hosts.InMaintenance);
        Assert.AreEqual("https://zabbix.example.com/zabbix.php?action=dashboard.view", summary.DashboardUrl);

        var byComponent = await (await viewer.GetAsync("/api/v1/zabbix/summary?category=tag:component&suppressed=true")).ReadAsync<ZabbixSummary>();
        CollectionAssert.AreEquivalent(new[] { "storage", "cpu", ZabbixService.NoValue }, byComponent.Categories.Select(c => c.Name).ToArray());
        Assert.AreEqual(1, byComponent.Categories.Single(c => c.Name == "storage").Counts[(int)ZabbixSeverity.High]);
    }

    [TestMethod]
    public async Task ProblemsFilterAndOpenWithTheirUpdates()
    {
        async Task<List<ZabbixProblemItem>> Problems(string query) =>
            [.. (await (await user.GetAsync($"/api/v1/zabbix/problems?{query}")).ReadAsync<Paged<ZabbixProblemItem>>()).Items];

        var all = await Problems("");
        CollectionAssert.AreEqual(new[] { "/var is 95 % full", "High CPU load" }, all.Select(p => p.Name).ToArray(), "worst first; suppressed hidden");
        var full = all[0];
        Assert.AreEqual("Used: 95 %", full.OperationalData);
        Assert.AreEqual("Web 01", full.Hosts.Single().Name);
        Assert.IsTrue(full.Acknowledged);
        Assert.AreEqual(1, full.Updates);
        var trigger = zabbix.Problems.Single(p => p!["eventid"]!.GetValue<string>() == full.Id)!["objectid"]!.GetValue<string>();
        Assert.AreEqual($"https://zabbix.example.com/tr_events.php?triggerid={trigger}&eventid={full.Id}", full.Url);

        Assert.AreEqual("/var is 95 % full", (await Problems("minSeverity=High")).Single().Name);
        Assert.AreEqual("High CPU load", (await Problems("tag=component:cpu")).Single().Name);
        Assert.AreEqual(2, (await Problems("tag=class")).Count);
        Assert.AreEqual("High CPU load", (await Problems("acknowledged=false")).Single().Name);
        Assert.AreEqual(2, (await Problems("host=web01")).Count);
        Assert.AreEqual(0, (await Problems("host=db02")).Count, "suppressed");
        Assert.AreEqual(1, (await Problems("host=db02&suppressed=true")).Count);
        Assert.AreEqual(2, (await Problems("group=PNMT%20Servers")).Count);
        Assert.AreEqual(2, (await Problems($"hostId={zabbix.HostId("web01.example.com")}")).Count);

        var detail = await (await user.GetAsync($"/api/v1/zabbix/problems/{full.Id}")).ReadAsync<ZabbixProblemDetail>();
        Assert.AreEqual("The partition /var is almost full.", detail.Description);
        var update = detail.Updates!.Single();
        Assert.AreEqual("Looking into it, Anna", update.Message);
        CollectionAssert.AreEqual(new[] { "acknowledged", "message" }, update.Actions.ToArray());
        var forViewer = await (await viewer.GetAsync($"/api/v1/zabbix/problems/{full.Id}")).ReadAsync<ZabbixProblemDetail>();
        Assert.IsNull(forViewer.Updates, "messages name people: Zabbix's Details");
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync("/api/v1/zabbix/problems/999999")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync("/api/v1/zabbix/problems/x1")).StatusCode);
    }

    [TestMethod]
    [DataRow("60", "zabbix.php?action=host.edit&hostid=")]
    [DataRow("70", "zabbix.php?action=host.edit&hostid=")]
    [DataRow("80", "zabbix.php?action=popup&popup=host.edit&hostid=")]
    public async Task AHostsPageWithItsLinksInventoryAndLatestData(string version, string configuration)
    {
        await UseVersion(version);
        var id = zabbix.HostId("web01.example.com");
        var page = await (await user.GetAsync($"/api/v1/zabbix/hosts/{id}")).ReadAsync<ZabbixHostDetail>();
        Assert.IsFalse(page.FromMirror);
        Assert.AreEqual("Web 01", page.Host.Name);
        Assert.AreEqual("The public web server.", page.Description);
        Assert.AreEqual("env:prod", string.Join(",", page.Tags.Select(t => $"{t.Tag}:{t.Value}")));
        Assert.AreEqual("Zurich, rack 4", page.Inventory!.Single(f => f.Name == "Location").Value);
        Assert.AreEqual("Debian 12", page.Inventory!.Single(f => f.Name == "OS").Value);
        Assert.AreEqual(2, page.Problems.Count);
        Assert.AreEqual($"https://zabbix.example.com/{configuration}{id}", page.Links.Configuration);
        Assert.AreEqual("https://zabbix.example.com/zabbix.php?action=host.view&filter_set=1&filter_host=web01.example.com", page.Links.Open);
        Assert.AreEqual($"https://zabbix.example.com/zabbix.php?action=latest.view&filter_set=1&hostids%5B%5D={id}", page.Links.Latest);
        var forViewer = await (await viewer.GetAsync($"/api/v1/zabbix/hosts/{id}")).ReadAsync<ZabbixHostDetail>();
        Assert.IsNull(forViewer.Inventory, "the inventory names people: Zabbix's Details");
        Assert.IsFalse(Calls().Last(c => c.Method == "host.get").Params.ContainsKey("selectInventory"), "not even asked for");

        var items = await (await user.GetAsync($"/api/v1/zabbix/hosts/{id}/items")).ReadAsync<List<ZabbixItemItem>>();
        var disk = items.Single(i => i.Key == "vfs.var.pused");
        Assert.AreEqual(("95", "%"), (disk.Value, disk.Units));
        Assert.IsNotNull(disk.At);
        Assert.AreEqual($"https://zabbix.example.com/history.php?action=showgraph&itemids%5B%5D={disk.Id}", disk.GraphUrl);
        Assert.AreEqual(1, (await (await user.GetAsync($"/api/v1/zabbix/hosts/{id}/items?search=disk")).ReadAsync<List<ZabbixItemItem>>()).Count);

        var core = zabbix.HostId("sw-core");
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync($"/api/v1/zabbix/hosts/{core}")).StatusCode, "not in PNMT's host groups");
    }

    [TestMethod]
    public async Task TheMirrorMarksGoneHostsAndRemovesThemAWeekLater()
    {
        await (await user.GetAsync("/api/v1/zabbix/hosts")).ReadAsync<Paged<ZabbixHostItem>>();
        var old = zabbix.Hosts.Single(h => h!["host"]!.GetValue<string>() == "old01")!;
        zabbix.Hosts.Remove(old);
        await app.Services.GetRequiredService<ZabbixHostSync>().RunAsync(CancellationToken.None);
        var hosts = await (await user.GetAsync("/api/v1/zabbix/hosts")).ReadAsync<Paged<ZabbixHostItem>>();
        Assert.IsFalse(hosts.Items.Any(h => h.TechnicalName == "old01"));
        var oldId = long.Parse(old["hostid"]!.GetValue<string>());
        await using (var db = app.NewDb())
        {
            Assert.IsNotNull((await db.ZabbixHosts.SingleAsync(h => h.HostId == oldId)).GoneAt, "kept a week, for its name");
            await db.ZabbixHosts.Where(h => h.HostId == oldId).ExecuteUpdateAsync(u => u.SetProperty(h => h.GoneAt, DateTime.UtcNow.AddDays(-8)));
        }
        await app.Services.GetRequiredService<ZabbixHostSync>().RunAsync(CancellationToken.None);
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.ZabbixHosts.AnyAsync(h => h.HostId == oldId));
        }
        var search = await (await viewer.GetAsync("/api/v1/search?q=web01")).ReadAsync<SearchResult>();
        Assert.AreEqual("Web 01", search.ZabbixHosts.Single().Name);
        var byAddress = await (await viewer.GetAsync("/api/v1/search?q=10.0.4.18")).ReadAsync<SearchResult>();
        Assert.AreEqual("db02", byAddress.ZabbixHosts.Single().TechnicalName);
    }

    [TestMethod]
    public async Task AnOutageKeepsTheProblemsAndFailsTheSystemSensorOnce()
    {
        var poller = app.Services.GetRequiredService<ZabbixPoller>();
        var sensors = app.Services.GetRequiredService<SystemSensors>();
        await poller.RunAsync(CancellationToken.None);
        await (await user.GetAsync("/api/v1/zabbix/hosts")).ReadAsync<Paged<ZabbixHostItem>>();
        var before = await sensors.LastAsync(SystemSensorKeys.Zabbix, CancellationToken.None);

        zabbix.Down = true;
        await poller.RunAsync(CancellationToken.None);
        Assert.AreEqual(before?.Id, (await sensors.LastAsync(SystemSensorKeys.Zabbix, CancellationToken.None))?.Id, "once is no outage");
        await poller.RunAsync(CancellationToken.None);
        var failing = await sensors.LastAsync(SystemSensorKeys.Zabbix, CancellationToken.None);
        Assert.AreEqual(PNMTStatusCodes.ZABBIX_FAILING, failing!.Code);
        StringAssert.Contains(failing.Message, "doesn't answer");
        await poller.RunAsync(CancellationToken.None);
        Assert.AreEqual(failing.Id, (await sensors.LastAsync(SystemSensorKeys.Zabbix, CancellationToken.None))!.Id, "notified once");

        var summary = await (await user.GetAsync("/api/v1/zabbix/summary")).ReadAsync<ZabbixSummary>();
        Assert.AreEqual(1, summary.Severities.Single(s => s.Severity == ZabbixSeverity.High).Count, "the last problems, kept");
        StringAssert.Contains(summary.Error, "doesn't answer");
        var page = await (await user.GetAsync($"/api/v1/zabbix/hosts/{zabbix.HostId("web01.example.com")}")).ReadAsync<ZabbixHostDetail>();
        Assert.IsTrue(page.FromMirror, "the mirror's, with the error");
        Assert.IsNotNull(page.Error);
        Assert.AreEqual(HttpStatusCode.BadGateway, (await user.GetAsync($"/api/v1/zabbix/hosts/{zabbix.HostId("web01.example.com")}/items")).StatusCode);

        zabbix.Down = false;
        await poller.RunAsync(CancellationToken.None);
        Assert.AreEqual(PNMTStatusCodes.SYSTEM_OK, (await sensors.LastAsync(SystemSensorKeys.Zabbix, CancellationToken.None))!.Code);
    }

    [TestMethod]
    public async Task ARefusedTokenIsABadGatewayAndTheTestSaysWhatItSees()
    {
        zabbix.RefuseToken = true;
        var refused = await user.GetAsync($"/api/v1/zabbix/hosts/{zabbix.HostId("web01.example.com")}/items");
        Assert.AreEqual(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.AreEqual("zabbix_refused", (await refused.ProblemAsync()).GetProperty("code").GetString());
        zabbix.RefuseToken = false;

        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.PostAsync("/api/v1/zabbix/test", null)).StatusCode, "System's Manage");
        var test = await (await admin.PostAsync("/api/v1/zabbix/test", null)).ReadAsync<ZabbixTestResult>();
        Assert.AreEqual(zabbix.VersionText, test.Version);
        Assert.AreEqual(4, test.Hosts);
        Assert.AreEqual(3, test.Problems, "the suppressed one too");
        CollectionAssert.AreEqual(new[] { "PNMT Servers" }, test.HostGroups.ToArray());
        Assert.AreEqual(0, test.NotFound.Count);
        await using var db = app.NewDb();
        var entry = await db.AuditLog.OrderByDescending(e => e.CreatedAt).FirstAsync(e => e.EventType == "ZabbixTested");
        StringAssert.Contains(entry.Detail, "4 hosts");
    }
}
