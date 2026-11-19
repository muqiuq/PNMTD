using System.Net;
using System.Text.Json;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Services.Domain;

namespace PNMTD.Tests.Api.V1;

/// <summary>Events, state changes, the dashboard, search and system (§3.7, §5.5, §8.3).</summary>
[TestClass]
public sealed class MonitoringApiTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient viewer = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
        viewer = app.Client(Roles.Viewer);
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public async Task EventsFilterByHostStatusTimeAndText()
    {
        var host = await V1.CreateHostAsync(user);
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        var now = Clock.UtcNow;
        V1.AddEvents(app, sensor.Id, (now.AddHours(-30), 200, "old ok"), (now.AddHours(-2), 500, "disk full"), (now.AddHours(-1), 200, "disk ok"));

        async Task<Paged<EventItem>> Get(string query) =>
            await (await viewer.GetAsync($"/api/v1/events?hostId={host.Id}&{query}")).ReadAsync<Paged<EventItem>>();

        Assert.AreEqual(3, (await Get("")).TotalCount);
        Assert.AreEqual("disk full", (await Get("status=failed")).Items.Single().Message);
        Assert.AreEqual(2, (await Get($"from={now.AddHours(-24):O}")).TotalCount);
        Assert.AreEqual(2, (await Get("search=DISK")).TotalCount);
        var oldestFirst = await Get("sort=created");
        Assert.AreEqual("old ok", oldestFirst.Items[0].Message);
        var item = oldestFirst.Items[0];
        Assert.AreEqual((host.Name, sensor.Name, true), (item.HostName, item.SensorName, item.Success));

        var single = await (await viewer.GetAsync($"/api/v1/events/{item.Id}")).ReadAsync<EventItem>();
        Assert.AreEqual(item, single);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/events?status=maybe")).StatusCode);
        await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
    }

    [TestMethod]
    public async Task StateChangesCarryTheOutage()
    {
        var host = await V1.CreateHostAsync(user);
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        var now = Clock.UtcNow;
        V1.AddEvents(app, sensor.Id, (now.AddHours(-5), 200, "ok"), (now.AddHours(-4), 470, "ping failed"), (now.AddHours(-3), 470, "ping failed"),
            (now.AddHours(-1), 270, "ping ok"));

        var changes = await (await viewer.GetAsync($"/api/v1/events/state-changes?sensorId={sensor.Id}")).ReadAsync<List<StateChange>>();

        Assert.AreEqual(2, changes.Count);
        var recovery = changes[0];
        Assert.AreEqual((470, 270, true), (recovery.PreviousCode, recovery.Code, recovery.Success));
        Assert.AreEqual(3 * 3600, recovery.DownSeconds!.Value, 1);
        Assert.AreEqual((200, 470, false), (changes[1].PreviousCode, changes[1].Code, changes[1].Success));
        Assert.IsNull(changes[1].DownSeconds);
        await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
    }

    [TestMethod]
    public async Task DashboardShowsProblemsWithTheErrorAndSince()
    {
        var host = await V1.CreateHostAsync(user, V1.Unique("dash"));
        var failing = await V1.CreateSensorAsync(user, host.Id, name: "backup");
        var ok = await V1.CreateSensorAsync(user, host.Id, name: "web");
        var ignored = await V1.CreateSensorAsync(user, host.Id, name: "flappy", ignore: true);
        var now = Clock.UtcNow;
        V1.AddEvents(app, failing.Id, (now.AddHours(-26), 200, "ok"), (now.AddHours(-2), 475, "93 % used"), (now.AddMinutes(-1), 475, "94 % used"));
        V1.AddEvents(app, ok.Id, (now.AddHours(-1), 200, "ok"));
        V1.AddEvents(app, ignored.Id, (now.AddHours(-1), 500, "ignored failure"));
        app.ClearDashboardCache();

        var dashboard = await (await viewer.GetAsync("/api/v1/dashboard")).ReadAsync<Dashboard>();

        var problem = dashboard.Problems.Single(p => p.Id == host.Id);
        var sensor = problem.Sensors.Single();
        Assert.AreEqual((failing.Id, 475, "94 % used"), (sensor.Id, sensor.Code, sensor.Message));
        Assert.IsTrue((sensor.FailingSince!.Value - now.AddHours(-2)).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(24, sensor.Last24Hours.Count);
        Assert.AreEqual(0.0, sensor.Last24Hours[^2]);
        var row = dashboard.Hosts.Single(h => h.Id == host.Id);
        Assert.AreEqual((HostState.Failing, 1, 90), (row.State, row.FailingCount, row.Days.Values.Count));
        Assert.AreEqual(HostState.Failing, dashboard.Hosts[0].State, "failing hosts first");
        Assert.IsTrue(dashboard.RecentChanges.Any(c => c.SensorId == failing.Id && c.Code == 475 && !c.Success));
        Assert.IsFalse(dashboard.RecentChanges.Any(c => c.SensorId == ignored.Id));
        Assert.AreEqual(Clock.TimeZone.Id, dashboard.TimeZone);

        // The summary counts what the sensor list says (ENCAPSULADED parents excepted).
        var all = await (await viewer.GetAsync("/api/v1/sensors?pageSize=500")).ReadAsync<Paged<SensorListItem>>();
        var counted = all.Items.Where(s => s.Type != SensorType.ENCAPSULADED).ToList();
        Assert.AreEqual(counted.Count(s => s.State == SensorState.Failing), dashboard.Summary.Sensors.Failing);
        Assert.AreEqual(counted.Count(s => s.State == SensorState.Ignored), dashboard.Summary.Sensors.Ignored);
        Assert.AreEqual(counted.Count, dashboard.Summary.Sensors.Total);
        Assert.AreEqual(dashboard.Hosts.Count(h => h.State == HostState.Failing), dashboard.Summary.Hosts.Failing);
        await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
    }

    /// <summary>Every browser polls the dashboard: within ~10 s it answers from the cache.</summary>
    [TestMethod]
    public async Task DashboardIsCachedBriefly()
    {
        app.ClearDashboardCache();
        var first = await (await viewer.GetAsync("/api/v1/dashboard")).ReadAsync<Dashboard>();
        var host = await V1.CreateHostAsync(user);
        var second = await (await viewer.GetAsync("/api/v1/dashboard")).ReadAsync<Dashboard>();

        Assert.AreEqual(first.GeneratedAt, second.GeneratedAt);
        Assert.IsFalse(second.Hosts.Any(h => h.Id == host.Id));
        app.ClearDashboardCache();
        Assert.IsTrue((await (await viewer.GetAsync("/api/v1/dashboard")).ReadAsync<Dashboard>()).Hosts.Any(h => h.Id == host.Id));
        await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
    }

    /// <summary>The dashboard's <i>Refresh</i>: <c>fresh=true</c> waits for a new build once the last is 2 seconds old.</summary>
    [TestMethod]
    public async Task DashboardRefreshWaitsForAFreshBuild()
    {
        app.ClearDashboardCache();
        await (await viewer.GetAsync("/api/v1/dashboard?fresh=true")).ReadAsync<Dashboard>();
        var host = await V1.CreateHostAsync(user);
        Assert.IsFalse((await (await viewer.GetAsync("/api/v1/dashboard?fresh=true")).ReadAsync<Dashboard>()).Hosts.Any(h => h.Id == host.Id),
            "a build younger than 2 seconds is fresh enough");
        await Task.Delay(DashboardCache.FreshAge + TimeSpan.FromMilliseconds(200));
        Assert.IsFalse((await (await viewer.GetAsync("/api/v1/dashboard")).ReadAsync<Dashboard>()).Hosts.Any(h => h.Id == host.Id),
            "a poll gets the cached build at once");
        Assert.IsTrue((await (await viewer.GetAsync("/api/v1/dashboard?fresh=true")).ReadAsync<Dashboard>()).Hosts.Any(h => h.Id == host.Id),
            "Refresh waits for a new one");
        await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
    }

    [TestMethod]
    public async Task SearchFindsHostsAndSensorsByName()
    {
        var tag = V1.Unique("needle");
        var host = await V1.CreateHostAsync(user, $"{tag}-host");
        await V1.CreateSensorAsync(user, host.Id, name: $"{tag}-sensor");

        var result = await (await viewer.GetAsync($"/api/v1/search?q={tag.ToUpperInvariant()}")).ReadAsync<SearchResult>();

        Assert.AreEqual(host.Id, result.Hosts.Single().Id);
        Assert.AreEqual(host.Name, result.Sensors.Single().HostName);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/search")).StatusCode);
        // % and _ are literal, not wildcards.
        Assert.AreEqual(0, (await (await viewer.GetAsync("/api/v1/search?q=%25")).ReadAsync<SearchResult>()).Hosts.Count(h => h.Id == host.Id));
        await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
    }

    [TestMethod]
    public async Task SystemInfoSignedInHasVersionZoneAndRole()
    {
        var info = await (await viewer.GetAsync("/api/v1/system/info")).ReadAsync<SystemInfo>();

        Assert.AreEqual(Clock.TimeZone.Id, info.TimeZone);
        Assert.AreEqual("Optional", info.TotpPolicy);
        Assert.IsFalse(string.IsNullOrEmpty(info.Version));
    }

    [TestMethod]
    public async Task TasksAreOffWhenBackgroundTasksAreDisabled()
    {
        var tasks = await (await admin.GetAsync("/api/v1/system/tasks")).ReadAsync<List<PNMTD.Api.V1.Contracts.TaskStatus>>();

        // 8 monitoring tasks, the MikroTik poller and version check (docs/mikrotik-plan.md §5.2, §5.4), the audit log's
        // retention (docs/audit-redmine-zabbix-plan.md §3.8), Redmine's sync (§6.7), Zabbix's problems and hosts (§7.3), Cookie's
        // case book (docs/cookie-plan.md §19.3), the MCP tokens' expiry (docs/mcp-plan.md §4.3), the certificate checks
        // (docs/ssl-certificate-plan.md §5.2), the advanced sensors' evaluator (docs/advanced-sensor-plan.md §7.3), and the
        // connectors' worker (docs/connectors-plan.md §9.1).
        Assert.AreEqual(19, tasks.Count);
        Assert.IsTrue(tasks.All(t => t.State == "off"), string.Join(",", tasks.Select(t => t.State)));
        Assert.IsTrue(tasks.Any(t => t.Key == "last_heartbeat_task_run" && t.IntervalSeconds == 10));
    }

    [TestMethod]
    public async Task ReferenceDataForTheDocs()
    {
        var codes = await (await viewer.GetAsync("/api/v1/system/status-codes")).ReadAsync<List<StatusCodeInfo>>();
        var types = JsonDocument.Parse(await (await viewer.GetAsync("/api/v1/system/sensor-types")).Content.ReadAsStringAsync()).RootElement;
        var ruleTypes = await (await viewer.GetAsync("/api/v1/system/notification-rule-types")).ReadAsync<List<NotificationRuleTypeInfo>>();

        Assert.IsTrue(codes.Any(c => c is { Code: 460, Name: "HEARTBEAT_MISSING", Success: false }));
        Assert.IsTrue(codes.Where(c => c.Success).All(c => c.Code <= 399));
        // SSL certificates (docs/ssl-certificate-plan.md §3): the seven codes, 285 and 286 the successes.
        CollectionAssert.IsSubsetOf(new[] { 285, 286, 485, 486, 487, 488, 489 }, codes.Select(c => c.Code).ToArray());
        // Advanced sensors (docs/advanced-sensor-plan.md §3): the four codes, 295 and 296 the successes.
        CollectionAssert.IsSubsetOf(new[] { 295, 296, 495, 496 }, codes.Select(c => c.Code).ToArray());
        CollectionAssert.AreEquivalent(new[] { 295, 296 }, codes.Where(c => c.Name.StartsWith("ADVANCED_") && c.Success).Select(c => c.Code).ToArray());
        CollectionAssert.AreEquivalent(new[] { 285, 286 }, codes.Where(c => c.Code is >= 285 and <= 489 && c.Name.StartsWith("SSL_") && c.Success).Select(c => c.Code).ToArray());
        Assert.AreEqual(Enum.GetValues<SensorType>().Length, types.GetArrayLength());
        Assert.IsTrue(types.EnumerateArray().Any(t => t.GetProperty("type").GetString() == "DNS" && !t.GetProperty("canCreate").GetBoolean()));
        Assert.AreEqual(Enum.GetValues<NotificationRuleType>().Length, ruleTypes.Count);
    }

    [TestMethod]
    public async Task UplinkWithoutSettingsCountsAsUp()
    {
        var uplink = await (await viewer.GetAsync("/api/v1/system/uplink")).ReadAsync<UplinkStatus>();
        Assert.IsFalse(uplink.Configured);
        Assert.IsTrue(uplink.IsUp);
    }
}
