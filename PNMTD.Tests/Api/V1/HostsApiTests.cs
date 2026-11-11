using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Api.V1;

[TestClass]
public sealed class HostsApiTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public async Task CreateAnswers201WithLocationAndTheHost()
    {
        var name = V1.Unique("web");
        using var response = await user.PostJsonAsync("/api/v1/hosts", new { name = $"  {name}  ", location = "rack 4", notes = "n", enabled = true });

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var host = await response.ReadAsync<HostDetail>();
        Assert.AreEqual($"/api/v1/hosts/{host.Id}", response.Headers.Location?.AbsolutePath);
        Assert.AreEqual(name, host.Name);
        Assert.AreEqual("rack 4", host.Location);
        Assert.AreEqual(HostState.NoData, host.State);
        Assert.AreEqual(90, host.Days.Values.Count);
        Assert.AreEqual(DateOnly.FromDateTime(Clock.LocalNow), host.Days.Start.AddDays(89));
        Assert.AreEqual(0, host.SensorCounts.Total);
    }

    [TestMethod]
    public async Task ValidationErrorsNameTheField()
    {
        using var response = await user.PostJsonAsync("/api/v1/hosts", new { name = "x", location = new string('l', 200) });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("validation_failed", await V1.CodeOf(response));
        var errors = await V1.ErrorsOf(response);
        Assert.IsTrue(errors.TryGetProperty("name", out _));
        Assert.IsTrue(errors.TryGetProperty("location", out _));
    }

    /// <summary>PUT replaces the writable fields; Created and the id come from the server, whatever the body says.</summary>
    [TestMethod]
    public async Task PutIgnoresFieldsThatAreNotWritable()
    {
        var host = await V1.CreateHostAsync(user);
        using var response = await user.PutJsonAsync($"/api/v1/hosts/{host.Id}", new
        {
            id = Guid.NewGuid(), created = "2001-01-01T00:00:00Z", state = "Failing",
            name = "renamed-host", location = (string?)null, notes = "now with notes", enabled = false,
        });

        var updated = await response.ReadAsync<HostDetail>();
        Assert.AreEqual(host.Id, updated.Id);
        Assert.AreEqual(host.Created, updated.Created);
        Assert.AreEqual("renamed-host", updated.Name);
        Assert.IsNull(updated.Location);
        Assert.AreEqual("now with notes", updated.Notes);
        Assert.AreEqual(HostState.Disabled, updated.State);
    }

    [TestMethod]
    public async Task ListFiltersByStateAndSearchAndPages()
    {
        var tag = V1.Unique("filter");
        var failing = await V1.CreateHostAsync(user, $"{tag}-a");
        var ok = await V1.CreateHostAsync(user, $"{tag}-b");
        await V1.CreateHostAsync(user, $"{tag}-c", enabled: false);
        var f = await V1.CreateSensorAsync(user, failing.Id);
        var o = await V1.CreateSensorAsync(user, ok.Id);
        V1.AddEvents(app, f.Id, (Clock.UtcNow.AddMinutes(-5), 500, "down"));
        V1.AddEvents(app, o.Id, (Clock.UtcNow.AddMinutes(-5), 200, "up"));

        var all = await (await user.GetAsync($"/api/v1/hosts?search={tag}")).ReadAsync<Paged<HostListItem>>();
        Assert.AreEqual(3, all.TotalCount);
        CollectionAssert.AreEqual(new[] { $"{tag}-a", $"{tag}-b", $"{tag}-c" }, all.Items.Select(h => h.Name).ToArray());

        var failingOnly = await (await user.GetAsync($"/api/v1/hosts?search={tag}&state=Failing")).ReadAsync<Paged<HostListItem>>();
        Assert.AreEqual(failing.Id, failingOnly.Items.Single().Id);
        Assert.AreEqual(1, failingOnly.Items[0].Sensors.Failing);
        Assert.AreEqual(30, failingOnly.Items[0].Days.Values.Count);

        var byState = await (await user.GetAsync($"/api/v1/hosts?search={tag}&sort=state")).ReadAsync<Paged<HostListItem>>();
        CollectionAssert.AreEqual(new[] { HostState.Failing, HostState.Ok, HostState.Disabled }, byState.Items.Select(h => h.State).ToArray());

        var page = await (await user.GetAsync($"/api/v1/hosts?search={tag}&page=2&pageSize=2")).ReadAsync<Paged<HostListItem>>();
        Assert.AreEqual(2, page.Page);
        Assert.AreEqual(2, page.PageSize);
        Assert.AreEqual(3, page.TotalCount);
        Assert.AreEqual($"{tag}-c", page.Items.Single().Name);
    }

    [TestMethod]
    public async Task PageSizeIsCappedAndUnknownSortIsRejected()
    {
        var page = await (await user.GetAsync("/api/v1/hosts?pageSize=100000")).ReadAsync<Paged<HostListItem>>();
        Assert.AreEqual(500, page.PageSize);

        using var badSort = await user.GetAsync("/api/v1/hosts?sort=password");
        Assert.AreEqual(HttpStatusCode.BadRequest, badSort.StatusCode);
        Assert.IsTrue((await V1.ErrorsOf(badSort)).TryGetProperty("sort", out _));
    }

    [TestMethod]
    public async Task DetailHasTopLevelSensorsStatesAndCounts()
    {
        var host = await V1.CreateHostAsync(user);
        var ok = await V1.CreateSensorAsync(user, host.Id, name: "a-ok");
        var failing = await V1.CreateSensorAsync(user, host.Id, name: "b-failing");
        await V1.CreateSensorAsync(user, host.Id, name: "c-nodata");
        await V1.CreateSensorAsync(user, host.Id, name: "d-ignored", ignore: true);
        await V1.CreateSensorAsync(user, host.Id, name: "e-off", enabled: false);
        var now = Clock.UtcNow;
        V1.AddEvents(app, ok.Id, (now.AddHours(-2), 200, "fine"));
        V1.AddEvents(app, failing.Id, (now.AddHours(-3), 200, "fine"), (now.AddHours(-2), 500, "broken"), (now.AddHours(-1), 503, "still"));

        var detail = await (await user.GetAsync($"/api/v1/hosts/{host.Id}")).ReadAsync<HostDetail>();

        Assert.AreEqual(HostState.Failing, detail.State);
        Assert.AreEqual(new SensorCounts(1, 1, 1, 1, 1), detail.SensorCounts);
        Assert.AreEqual(4, detail.EventCount);
        CollectionAssert.AreEqual(new[] { SensorState.Ok, SensorState.Failing, SensorState.NoData, SensorState.Ignored, SensorState.Disabled },
            detail.Sensors.Select(s => s.State).ToArray());
        var f = detail.Sensors[1];
        Assert.AreEqual(503, f.LastCode);
        Assert.AreEqual("still", f.LastMessage);
        Assert.IsTrue((f.FailingSince!.Value - now.AddHours(-2)).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.IsNotNull(detail.Uptime24Hours);
    }

    [TestMethod]
    public async Task DeleteTakesSensorsAndEventsAndReleasesZonesAndMailRules()
    {
        var host = await V1.CreateHostAsync(user);
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        V1.AddEvents(app, sensor.Id, (Clock.UtcNow, 200, null));
        var zone = await (await user.PostJsonAsync("/api/v1/dns-zones", new { zoneFileContent = DbTestHelper.DnsExampleZoneFile, hostId = host.Id }))
            .ReadAsync<DnsZoneDetail>();
        var rule = await (await user.PostJsonAsync("/api/v1/mail-input-rules", new { name = "to host", fromTest = "backup@", sensorId = sensor.Id }))
            .ReadAsync<MailInputRule>();

        using var response = await user.DeleteAsync($"/api/v1/hosts/{host.Id}");

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync($"/api/v1/hosts/{host.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).StatusCode);
        using var db = app.NewDb();
        Assert.IsFalse(await db.Events.AnyAsync(e => e.SensorId == sensor.Id));
        Assert.IsNull((await (await user.GetAsync($"/api/v1/dns-zones/{zone.Id}")).ReadAsync<DnsZoneDetail>()).Host);
        Assert.IsNull((await (await user.GetAsync($"/api/v1/mail-input-rules/{rule.Id}")).ReadAsync<MailInputRule>()).Sensor);
        await user.DeleteAsync($"/api/v1/dns-zones/{zone.Id}");
        await user.DeleteAsync($"/api/v1/mail-input-rules/{rule.Id}");
    }

    [TestMethod]
    public async Task UnknownIdIsNotFound()
    {
        using var response = await user.GetAsync($"/api/v1/hosts/{Guid.NewGuid()}");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("not_found", await V1.CodeOf(response));
        using var delete = await user.DeleteAsync($"/api/v1/hosts/{Guid.NewGuid()}");
        Assert.AreEqual(HttpStatusCode.NotFound, delete.StatusCode);
    }

    /// <summary>Timestamps are UTC with a Z, enums strings (§3.7).</summary>
    [TestMethod]
    public async Task TimestampsAreUtcAndEnumsAreStrings()
    {
        var host = await V1.CreateHostAsync(user);
        await V1.CreateSensorAsync(user, host.Id, SensorType.HEARTBEAT, interval: 60);
        var body = JsonDocument.Parse(await (await user.GetAsync($"/api/v1/hosts/{host.Id}")).Content.ReadAsStringAsync()).RootElement;

        StringAssert.EndsWith(body.GetProperty("created").GetString(), "Z");
        Assert.AreEqual("NoData", body.GetProperty("state").GetString());
        Assert.AreEqual("HEARTBEAT", body.GetProperty("sensors")[0].GetProperty("type").GetString());
        Assert.AreEqual(DateOnly.FromDateTime(Clock.LocalNow).AddDays(-89).ToString("yyyy-MM-dd"), body.GetProperty("days").GetProperty("start").GetString());
    }
}
