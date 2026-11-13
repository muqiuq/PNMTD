using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.Events;
using PNMTD.UplinkCheck;

namespace PNMTD.Tests.Api.V1;

/// <summary>
/// The uplink page and its system sensor (docs/ai-operations-plan.md §4, §7), against fake endpoints that answer the
/// challenge as a real one does, or fail as told. Every test ends with the uplink up: other classes read UPLINK_OK.
/// </summary>
[TestClass]
public sealed class UplinkTests
{
    private const string Key = "uplink-test-key";
    private static readonly FakeEndpoints endpoints = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["UplinkCheck:SharedKey"] = Key,
            ["UplinkCheck:Url1"] = "https://up1.example.test/check",
            ["UplinkCheck:Url2"] = "https://up2.example.test/check?site=a",
        }, services: s => s.AddHttpClient(UplinkMonitor.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeHandler(endpoints)));
        admin = app.Client(Roles.Admin);
        endpoints.Next.Clear();
        await CheckAsync();
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<UplinkCheckItem> CheckAsync() =>
        await (await admin.PostAsync("/api/v1/uplink/check", null)).ReadAsync<UplinkCheckItem>();

    [TestMethod]
    public async Task Three_failures_bring_it_down_and_a_success_up_again_with_events()
    {
        await CheckAsync();
        endpoints.Next.Enqueue(Answer.Status500);
        endpoints.Next.Enqueue(Answer.WrongSignature);
        endpoints.Next.Enqueue(Answer.Dns);
        var a = await CheckAsync();
        Assert.AreEqual(UplinkOutcome.HttpStatus, a.Outcome);
        Assert.AreEqual(500, a.HttpStatus);
        Assert.IsTrue((await (await admin.GetAsync("/api/v1/uplink")).ReadAsync<UplinkOverview>()).Up, "two failures are not down yet");
        Assert.AreEqual(UplinkOutcome.BadSignature, (await CheckAsync()).Outcome);
        Assert.AreEqual(UplinkOutcome.Dns, (await CheckAsync()).Outcome);

        var down = await (await admin.GetAsync("/api/v1/uplink")).ReadAsync<UplinkOverview>();
        Assert.IsFalse(down.Up);
        Assert.AreEqual(3, down.FailuresInARow);
        Assert.IsNotNull(down.DownSince);
        Assert.IsNotNull(down.SensorId);
        Assert.IsFalse((await (await admin.GetAsync("/api/v1/system/uplink")).ReadAsync<UplinkStatus>()).IsUp, "the monitoring tasks see it too");

        var up = await CheckAsync();
        Assert.AreEqual(UplinkOutcome.Ok, up.Outcome);
        Assert.IsTrue((await (await admin.GetAsync("/api/v1/uplink")).ReadAsync<UplinkOverview>()).Up);

        await using var db = app.NewDb();
        var events = await db.Events.Where(e => e.SensorId == down.SensorId).OrderByDescending(e => e.Created).Take(2).ToListAsync();
        Assert.AreEqual(PNMTStatusCodes.SYSTEM_OK, events[0].Code);
        StringAssert.StartsWith(events[0].Message, "Uplink up again after ");
        Assert.AreEqual(PNMTStatusCodes.UPLINK_DOWN, events[1].Code);
        Assert.AreEqual("Uplink down: 3 checks failed in a row (HTTP error, wrong signature, DNS).", events[1].Message);

        var outages = await (await admin.GetAsync("/api/v1/uplink/outages")).ReadAsync<List<UplinkOutage>>();
        Assert.IsTrue(outages.Any(o => o.End is not null && o.FirstError == "HTTP 500 Internal Server Error"));
        var failures = await (await admin.GetAsync("/api/v1/uplink/checks?failuresOnly=true&pageSize=3")).ReadAsync<Paged<UplinkCheckItem>>();
        CollectionAssert.AreEqual(new[] { UplinkOutcome.Dns, UplinkOutcome.BadSignature, UplinkOutcome.HttpStatus }, failures.Items.Select(c => c.Outcome).ToArray());
    }

    [TestMethod]
    public async Task Viewers_see_the_endpoints_host_only()
    {
        var viewer = await (await app.Client(Roles.Viewer).GetAsync("/api/v1/uplink")).ReadAsync<UplinkOverview>();
        var full = await (await admin.GetAsync("/api/v1/uplink")).ReadAsync<UplinkOverview>();
        Assert.IsTrue(viewer.Configured);
        CollectionAssert.AreEqual(new[] { "up1.example.test", "up2.example.test" }, viewer.Endpoints.Select(e => e.Address).ToArray());
        Assert.AreEqual("https://up2.example.test/check?site=a", full.Endpoints[1].Address);
        Assert.AreEqual(48, full.Day.Count);
        Assert.AreEqual(168, full.Week.Count);
        Assert.IsTrue(full.Day.Sum(b => b.Ok) > 0);
    }

    [TestMethod]
    public async Task System_sensors_are_PNMTs_own()
    {
        var id = await app.Services.GetRequiredService<SystemSensors>().IdAsync(SystemSensorKeys.Uplink, CancellationToken.None);
        var sensor = await (await admin.GetAsync($"/api/v1/sensors/{id}")).ReadAsync<SensorDetail>();
        Assert.AreEqual(SensorType.SYSTEM, sensor.Type);
        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/sensors/{id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/hosts/{sensor.Host.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = sensor.Host.Id, Type = SensorType.SYSTEM, Name = "another",
        })).StatusCode);
        var renamed = await (await admin.PutJsonAsync($"/api/v1/sensors/{id}", new SensorRequest
        {
            HostId = sensor.Host.Id, Type = SensorType.SYSTEM, Name = "Internet uplink", Enabled = true,
        })).ReadAsync<SensorDetail>();
        Assert.AreEqual("Internet uplink", renamed.Name);
        Assert.AreEqual(SystemSensorKeys.Uplink, renamed.Parameters);
        await admin.PutJsonAsync($"/api/v1/sensors/{id}", new SensorRequest { HostId = sensor.Host.Id, Type = SensorType.SYSTEM, Name = "Uplink", Enabled = true });
    }

    internal enum Answer { Ok, Status500, WrongSignature, Dns }

    internal sealed class FakeEndpoints
    {
        public Queue<Answer> Next { get; } = new();
    }

    /// <summary>An uplink endpoint as v0.12's documentation describes it: <c>HMAC(key, challenge-random),random</c>.</summary>
    private sealed class FakeHandler(FakeEndpoints owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var answer = owner.Next.TryDequeue(out var next) ? next : Answer.Ok;
            if (answer == Answer.Dns)
            {
                throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (up1.example.test:443)");
            }
            if (answer == Answer.Status500)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = "Internal Server Error" });
            }
            var challenge = QueryHelpers.ParseQuery(request.RequestUri!.Query)["challenge"].ToString();
            const string random = "R4ND0M";
            var hash = answer == Answer.WrongSignature ? new string('0', 64) : UplinkMonitor.Hmac(challenge, Key, random);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash},{random}", Encoding.UTF8) });
        }
    }
}
