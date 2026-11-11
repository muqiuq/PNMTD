using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;

namespace PNMTD.Tests.Api.V1;

[TestClass]
public sealed class SensorsApiTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient viewer = null!;
    private static HostDetail host = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
        viewer = app.Client(Roles.Viewer);
        host = await V1.CreateHostAsync(user);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
        app.Dispose();
    }

    [TestMethod]
    public async Task CreateReturnsTheSensorWithItsUrls()
    {
        var sensor = await V1.CreateSensorAsync(user, host.Id, SensorType.HEARTBEAT, interval: 300);

        Assert.AreEqual(SensorType.HEARTBEAT, sensor.Type);
        Assert.AreEqual(SensorState.NoData, sensor.State);
        Assert.AreEqual(host.Id, sensor.Host.Id);
        var integration = sensor.Integration!;
        Assert.AreEqual($"http://localhost/event/{integration.WriteToken}", integration.WriteUrl);
        Assert.AreEqual($"http://localhost/read/{integration.ReadToken}/json", integration.ReadUrl);
        StringAssert.Matches(integration.WriteToken, new System.Text.RegularExpressions.Regex("^[0-9a-f]{7}-[0-9a-f-]{36}$"));
    }

    /// <summary>The URLs the UI shows are the ones a sensor can report to.</summary>
    [TestMethod]
    public async Task TheWriteUrlTakesAReport()
    {
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        using var report = await app.Client().GetAsync(new Uri(sensor.Integration!.WriteUrl + "/200/hello").PathAndQuery);
        Assert.AreEqual(HttpStatusCode.OK, report.StatusCode);

        var detail = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        Assert.AreEqual(SensorState.Ok, detail.State);
        Assert.AreEqual(200, detail.LastCode);
        Assert.AreEqual("hello", detail.LastMessage);
    }

    [TestMethod]
    public async Task ViewersSeeNoTokensOrUrls()
    {
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        var body = await (await viewer.GetAsync($"/api/v1/sensors/{sensor.Id}")).Content.ReadAsStringAsync();

        Assert.AreEqual(JsonValueKind.Null, JsonDocument.Parse(body).RootElement.GetProperty("integration").ValueKind);
        Assert.IsFalse(body.Contains(sensor.Integration!.WriteToken, StringComparison.Ordinal));
        Assert.IsFalse(body.Contains(sensor.Integration.ReadToken!, StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("/event/", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("PING", "", "parameters")]
    [DataRow("PING", "two words", "parameters")]
    [DataRow("VALUECHECK", "([unclosed", "parameters")]
    [DataRow("ONE_WITHIN_TIMESPAN", "sometimes", "parameters")]
    [DataRow("HEARTBEAT", null, "interval")]
    [DataRow("DNS", null, "type")]
    public async Task ParametersAreCheckedByType(string type, string? parameters, string field)
    {
        using var response = await user.PostJsonAsync("/api/v1/sensors", new { hostId = host.Id, type, name = "invalid", parameters, interval = 0 });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsTrue((await V1.ErrorsOf(response)).TryGetProperty(field, out _), await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task ValidParametersAreTaken()
    {
        await V1.CreateSensorAsync(user, host.Id, SensorType.PING, parameters: "192.0.2.1", interval: 60);
        await V1.CreateSensorAsync(user, host.Id, SensorType.VALUECHECK, parameters: @"^\d+ ok$");
        await V1.CreateSensorAsync(user, host.Id, SensorType.ONE_WITHIN_TIMESPAN, parameters: "daily 8");
        using var unknownHost = await user.PostJsonAsync("/api/v1/sensors", new { hostId = Guid.NewGuid(), name = "orphan" });
        Assert.IsTrue((await V1.ErrorsOf(unknownHost)).TryGetProperty("hostId", out _));
    }

    /// <summary>Tokens cannot be written through PUT (issue 2); rotation is its own call.</summary>
    [TestMethod]
    public async Task PutKeepsTokensAndRotationReplacesThem()
    {
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        var old = sensor.Integration!;
        using var put = await user.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", new
        {
            hostId = host.Id, type = "SIMPLE", name = "renamed", enabled = true,
            secretWriteToken = "chosen-by-client", writeToken = "chosen-by-client", status = "hacked", created = "2001-01-01T00:00:00Z",
        });
        var updated = await put.ReadAsync<SensorDetail>();
        Assert.AreEqual("renamed", updated.Name);
        Assert.AreEqual(old.WriteToken, updated.Integration!.WriteToken);
        Assert.IsNull(updated.Status);
        Assert.AreEqual(sensor.Created, updated.Created);

        var rotated = await (await user.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/tokens/rotate", new { write = true, read = false }))
            .ReadAsync<SensorIntegration>();
        Assert.AreNotEqual(old.WriteToken, rotated.WriteToken);
        Assert.AreEqual(old.ReadToken, rotated.ReadToken);
        Assert.AreEqual(HttpStatusCode.NotFound, (await app.Client().GetAsync($"/event/{old.WriteToken}/200/x")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await app.Client().GetAsync($"/event/{rotated.WriteToken}/200/x")).StatusCode);
    }

    [TestMethod]
    public async Task LookupFindsTheSensorOfAWriteOrReadToken()
    {
        var sensor = await V1.CreateSensorAsync(user, host.Id);

        var write = await (await user.PostJsonAsync("/api/v1/sensors/lookup", new { token = sensor.Integration!.WriteToken })).ReadAsync<TokenLookupResult>();
        var read = await (await user.PostJsonAsync("/api/v1/sensors/lookup", new { token = sensor.Integration.ReadToken })).ReadAsync<TokenLookupResult>();
        using var none = await user.PostJsonAsync("/api/v1/sensors/lookup", new { token = "no-such-token" });

        Assert.AreEqual((sensor.Id, "write"), (write.SensorId, write.Matched));
        Assert.AreEqual((sensor.Id, "read"), (read.SensorId, read.Matched));
        Assert.AreEqual(host.Name, write.Host.Name);
        Assert.AreEqual(HttpStatusCode.NotFound, none.StatusCode);
    }

    /// <summary>The children an ENCAPSULADED sensor creates belong to it: listed on it, deleted with it, and its state.</summary>
    [TestMethod]
    public async Task EncapsulatedSensorsReportThroughTheirChildren()
    {
        var parent = await V1.CreateSensorAsync(user, host.Id, SensorType.ENCAPSULADED);
        var body = new StringContent("""[{"Name":"disk","Code":200,"Message":"ok"},{"Name":"raid","Code":500,"Message":"degraded"}]""", Encoding.UTF8);
        Assert.AreEqual(HttpStatusCode.OK, (await app.Client().PostAsync($"/event/{parent.Integration!.WriteToken}/200", body)).StatusCode);

        var detail = await (await user.GetAsync($"/api/v1/sensors/{parent.Id}")).ReadAsync<SensorDetail>();
        CollectionAssert.AreEqual(new[] { "disk", "raid" }, detail.Children.Select(c => c.Name).ToArray());
        Assert.AreEqual(SensorState.Failing, detail.State);
        Assert.AreEqual("degraded", detail.LastMessage);
        var child = await (await user.GetAsync($"/api/v1/sensors/{detail.Children[1].Id}")).ReadAsync<SensorDetail>();
        Assert.AreEqual(parent.Id, child.ParentSensorId);

        using var delete = await user.DeleteAsync($"/api/v1/sensors/{parent.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
        using var db = app.NewDb();
        Assert.IsFalse(await db.Sensors.AnyAsync(s => s.Id == parent.Id || s.OlderSiblingId == parent.Id));
    }

    [TestMethod]
    public async Task ListFiltersByHostTypeAndState()
    {
        var other = await V1.CreateHostAsync(user);
        var ping = await V1.CreateSensorAsync(user, other.Id, SensorType.PING, parameters: "192.0.2.7", interval: 60);
        var failing = await V1.CreateSensorAsync(user, other.Id);
        V1.AddEvents(app, failing.Id, (Clock.UtcNow, 500, "x"));

        var byHost = await (await user.GetAsync($"/api/v1/sensors?hostId={other.Id}")).ReadAsync<Paged<SensorListItem>>();
        var byType = await (await user.GetAsync($"/api/v1/sensors?hostId={other.Id}&type=PING")).ReadAsync<Paged<SensorListItem>>();
        var byState = await (await user.GetAsync($"/api/v1/sensors?hostId={other.Id}&state=Failing")).ReadAsync<Paged<SensorListItem>>();

        Assert.AreEqual(2, byHost.TotalCount);
        Assert.AreEqual(ping.Id, byType.Items.Single().Id);
        Assert.AreEqual(failing.Id, byState.Items.Single().Id);
        await user.DeleteAsync($"/api/v1/hosts/{other.Id}");
    }

    [TestMethod]
    public async Task EventsOfASensorArePagedNewestFirst()
    {
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        var now = Clock.UtcNow;
        V1.AddEvents(app, sensor.Id, Enumerable.Range(1, 30).Select(i => (now.AddMinutes(-i), i % 3 == 0 ? 500 : 200, (string?)$"e{i}")).ToArray());

        var first = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/events?pageSize=10")).ReadAsync<Paged<EventItem>>();
        var failed = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/events?status=failed&pageSize=100")).ReadAsync<Paged<EventItem>>();

        Assert.AreEqual(30, first.TotalCount);
        CollectionAssert.AreEqual(Enumerable.Range(1, 10).Select(i => $"e{i}").ToArray(), first.Items.Select(e => e.Message).ToArray());
        Assert.AreEqual(10, failed.TotalCount);
        Assert.IsTrue(failed.Items.All(e => !e.Success && e.Code == 500));
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync($"/api/v1/sensors/{Guid.NewGuid()}/events")).StatusCode);
    }

    [TestMethod]
    public async Task RulesLinkIdempotently()
    {
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        var rule = await (await user.PostJsonAsync("/api/v1/notification-rules", new { name = V1.Unique("rule"), recipient = "ops@example.com" }))
            .ReadAsync<NotificationRuleDetail>();

        Assert.AreEqual(HttpStatusCode.NoContent, (await user.PutAsync($"/api/v1/sensors/{sensor.Id}/notification-rules/{rule.Id}", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await user.PutAsync($"/api/v1/sensors/{sensor.Id}/notification-rules/{rule.Id}", null)).StatusCode);
        var linked = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/notification-rules")).ReadAsync<List<NotificationRuleItem>>();
        Assert.AreEqual(rule.Id, linked.Single().Id);
        Assert.AreEqual(1, linked[0].SensorCount);

        Assert.AreEqual(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/sensors/{sensor.Id}/notification-rules/{rule.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/sensors/{sensor.Id}/notification-rules/{rule.Id}")).StatusCode);
        Assert.AreEqual(0, (await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/notification-rules")).ReadAsync<List<NotificationRuleItem>>()).Count);
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.PutAsync($"/api/v1/sensors/{sensor.Id}/notification-rules/{Guid.NewGuid()}", null)).StatusCode);
        await user.DeleteAsync($"/api/v1/notification-rules/{rule.Id}");
    }

    /// <summary>DnsCheckTask owns DNS sensors: only Enabled and Ignore can change.</summary>
    [TestMethod]
    public async Task DnsSensorsTakeOnlyEnabledAndIgnore()
    {
        Guid id;
        using (var db = app.NewDb())
        {
            var dns = new Models.Db.SensorEntity
            {
                Id = Guid.NewGuid(), ParentId = host.Id, Type = SensorType.DNS, Name = "example.com", Enabled = true, Created = Clock.UtcNow,
                SecretWriteToken = Guid.NewGuid().ToString(), SecretReadToken = Guid.NewGuid().ToString(),
            };
            db.Sensors.Add(dns);
            await db.SaveChangesAsync();
            id = dns.Id;
        }
        using var retype = await user.PutJsonAsync($"/api/v1/sensors/{id}", new { hostId = host.Id, type = "SIMPLE", name = "x" });
        Assert.AreEqual(HttpStatusCode.BadRequest, retype.StatusCode);

        var updated = await (await user.PutJsonAsync($"/api/v1/sensors/{id}", new { hostId = host.Id, type = "DNS", name = "renamed", ignore = true, enabled = true }))
            .ReadAsync<SensorDetail>();
        Assert.AreEqual("example.com", updated.Name);
        Assert.IsTrue(updated.Ignore);
    }
}
