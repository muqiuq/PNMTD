using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Advanced;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Advanced;

/// <summary>Shared by the advanced sensors' test classes: a sensor, a report, the events, waiting for the evaluator.</summary>
internal static class AdvancedTesting
{
    public static async Task<SensorDetail> CreateAsync(HttpClient admin, Guid hostId, object? advanced = null, int interval = 0, int grace = 0,
        bool enabled = true)
    {
        using var response = await admin.PostJsonAsync("/api/v1/sensors", new
        {
            hostId, type = SensorType.ADVANCED, name = V1.Unique("app"), interval, gracePeriod = grace, enabled, advanced = advanced ?? new { },
        });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<SensorDetail>();
    }

    /// <summary>A report with the token in the header (or in the path), as an app sends it: curl's form content type included.</summary>
    public static async Task<HttpResponseMessage> PostAsync(TestApp app, string token, string body, bool inPath = false, string query = "",
        Action<HttpRequestMessage>? change = null)
    {
        app.Services.GetRequiredService<ReportGate>().Reset();
        var request = new HttpRequestMessage(HttpMethod.Post, (inPath ? $"/report/{token}" : "/report") + query)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        if (!inPath)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        change?.Invoke(request);
        return await app.Client().SendAsync(request);
    }

    public static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task<List<(int Code, string? Message, string? Source)>> EventsAsync(TestApp app, Guid sensorId)
    {
        await using var db = app.NewDb();
        return (await db.Events.AsNoTracking().Where(e => e.SensorId == sensorId).OrderBy(e => e.Created)
                .Select(e => new { e.Code, e.Message, e.Source }).ToListAsync())
            .Select(e => (e.Code, e.Message, e.Source)).ToList();
    }

    public static async Task<List<int>> CodesAsync(TestApp app, Guid sensorId) => (await EventsAsync(app, sensorId)).Select(e => e.Code).ToList();

    /// <summary>Until the sensor has no report waiting for the evaluator, at most 5 seconds.</summary>
    public static async Task WaitJudgedAsync(TestApp app, Guid sensorId)
    {
        for (var i = 0; i < 100; i++)
        {
            await using var db = app.NewDb();
            if (await db.AdvancedReports.AnyAsync(r => r.SensorId == sensorId) && !await db.AdvancedReports.AnyAsync(r => r.SensorId == sensorId && r.EvaluatedAt == null))
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail("The evaluator didn't judge the report within 5 seconds.");
    }
}

/// <summary>
/// <c>POST /report</c> (docs/advanced-sensor-plan.md §4, §7.1): the token, the sensor, the answers, invalid and large reports,
/// gzip, the rate limit, repeats, the nudge, dry runs, no session, and /event untouched.
/// </summary>
[TestClass]
public sealed class ReportEndpointTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?> { ["Advanced:MaxReportBytes"] = "4096" });
        admin = app.Client(Roles.Admin);
        hostId = (await V1.CreateHostAsync(admin, V1.Unique("app-host"), enabled: false)).Id;
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            await db.Sensors.Where(s => s.ParentId == hostId).ExecuteDeleteAsync();
        }
        app.Dispose();
    }

    private static Task<HttpResponseMessage> PostAsync(string token, string body, bool inPath = false, string query = "",
        Action<HttpRequestMessage>? change = null) => AdvancedTesting.PostAsync(app, token, body, inPath, query, change);

    [TestMethod]
    public async Task TheTokenInTheHeaderOrInThePath()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        var token = sensor.Integration!.WriteToken;
        StringAssert.EndsWith(sensor.Integration.ReportUrl, "/report");

        using var header = await PostAsync(token, """{"status":"ok","queue":{"length":12}}""");
        Assert.AreEqual(HttpStatusCode.Accepted, header.StatusCode, await header.Content.ReadAsStringAsync());
        var answer = await AdvancedTesting.JsonOf(header);
        Assert.IsTrue(answer.GetProperty("ok").GetBoolean());
        Assert.IsTrue(Guid.TryParse(answer.GetProperty("reportId").GetString(), out _));
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_OK, answer.GetProperty("preview").GetProperty("code").GetInt32());
        Assert.AreEqual("ok", answer.GetProperty("preview").GetProperty("state").GetString());
        Assert.IsFalse(header.Headers.Contains("Set-Cookie"));

        using var path = await PostAsync(token, """{"status":"error"}""", inPath: true);
        Assert.AreEqual(HttpStatusCode.Accepted, path.StatusCode);
        var preview = (await AdvancedTesting.JsonOf(path)).GetProperty("preview");
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_FAILED, preview.GetProperty("code").GetInt32(), "the starter rule");
        Assert.AreEqual("The app's status", preview.GetProperty("failed")[0].GetString());

        using var none = await app.Client().PostAsync("/report", new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.AreEqual("no_token", (await AdvancedTesting.JsonOf(none)).GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task OtherTypesUnknownTokensAndDisabledSensors()
    {
        var heartbeat = await V1.CreateSensorAsync(admin, hostId, SensorType.HEARTBEAT, interval: 60);
        using var wrong = await PostAsync(heartbeat.Integration!.WriteToken, "{}");
        Assert.AreEqual(HttpStatusCode.Conflict, wrong.StatusCode);
        var body = await AdvancedTesting.JsonOf(wrong);
        Assert.AreEqual("wrong_type", body.GetProperty("error").GetString());
        StringAssert.Contains(body.GetProperty("message").GetString(), "HEARTBEAT");

        using var unknown = await PostAsync("no-such-token", "{}");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual("", await unknown.Content.ReadAsStringAsync(), "an empty 404, as on /event");

        var disabled = await AdvancedTesting.CreateAsync(admin, hostId, enabled: false);
        using var off = await PostAsync(disabled.Integration!.WriteToken, "{}");
        Assert.AreEqual(HttpStatusCode.NotFound, off.StatusCode);
        Assert.AreEqual("", await off.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task InvalidReportsAreStoredAndFailTheSensor()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        var token = sensor.Integration!.WriteToken;
        foreach (var (json, error) in new[]
        {
            ("{\"a\":1,", "invalid_json"), ("[1,2]", "not_an_object"), ("{\"disk\":1,\"disk\":2}", "duplicate_key"),
            (new string('[', 40) + new string(']', 40), "too_deep"),
        })
        {
            using var response = await PostAsync(token, json);
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode, json);
            var answer = await AdvancedTesting.JsonOf(response);
            Assert.AreEqual((false, error), (answer.GetProperty("ok").GetBoolean(), answer.GetProperty("error").GetString()));
            Assert.IsTrue(answer.TryGetProperty("reportId", out _));
        }
        await AdvancedTesting.WaitJudgedAsync(app, sensor.Id);
        await using var db = app.NewDb();
        var rows = await db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensor.Id).ToListAsync();
        Assert.AreEqual(4, rows.Count);
        Assert.IsTrue(rows.All(r => !r.Valid && r.Body != null && r.Code == PNMTStatusCodes.ADVANCED_INVALID));
        var events = await AdvancedTesting.EventsAsync(app, sensor.Id);
        Assert.IsTrue(events.All(e => e.Code == PNMTStatusCodes.ADVANCED_INVALID));
        Assert.AreEqual("The report is nested more than 32 levels deep", events[^1].Message);
    }

    [TestMethod]
    public async Task TooLargeByContentLengthByChunksAndAfterGzip()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        var token = sensor.Integration!.WriteToken;
        var large = "{\"x\":\"" + new string('a', 5000) + "\"}";

        using var announced = await PostAsync(token, large);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, announced.StatusCode);
        Assert.AreEqual("too_large", (await AdvancedTesting.JsonOf(announced)).GetProperty("error").GetString());

        using var chunked = await PostAsync(token, "", change: r => r.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(large)))
        {
            Headers = { ContentLength = null },
        });
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);

        // 5 KB of a's compress to a few bytes: the limit counts what decompresses.
        using var bomb = await PostAsync(token, "", change: r => r.Content = Gzip(large));
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, bomb.StatusCode);

        using var small = await PostAsync(token, "", change: r => r.Content = Gzip("""{"status":"ok","n":1}"""));
        Assert.AreEqual(HttpStatusCode.Accepted, small.StatusCode, await small.Content.ReadAsStringAsync());

        await AdvancedTesting.WaitJudgedAsync(app, sensor.Id);
        await using var db = app.NewDb();
        var tooLarge = await db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensor.Id && r.ErrorKind == AdvancedReportError.TooLarge).ToListAsync();
        Assert.AreEqual(3, tooLarge.Count);
        Assert.IsTrue(tooLarge.All(r => r.Body == null), "recorded without its body");
        Assert.AreEqual("The report is 4.9 KiB, more than 4 KiB", tooLarge.OrderBy(r => r.ReceivedAt).First().Error);
    }

    private static ByteArrayContent Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }
        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentEncoding.Add("gzip");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    [TestMethod]
    public async Task OneReportEveryFiveSeconds()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        var token = sensor.Integration!.WriteToken;
        using var first = await PostAsync(token, """{"n":1}""");
        Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode);
        // PostAsync resets the gate; this one goes straight after.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/report/{token}") { Content = new StringContent("""{"n":2}""") };
        using var second = await app.Client().SendAsync(request);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.AreEqual("too_soon", (await AdvancedTesting.JsonOf(second)).GetProperty("error").GetString());
        Assert.IsTrue(second.Headers.RetryAfter?.Delta is { TotalSeconds: >= 1 and <= 5 });
        await using var db = app.NewDb();
        Assert.AreEqual(1, await db.AdvancedReports.CountAsync(r => r.SensorId == sensor.Id), "neither counted nor stored");
    }

    [TestMethod]
    public async Task RepeatsFoldIntoOneRowAndAreJudgedAgain()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        var token = sensor.Integration!.WriteToken;
        const string body = """{"status":"ok","queue":{"length":3}}""";
        for (var i = 0; i < 3; i++)
        {
            using var response = await PostAsync(token, body);
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
            await AdvancedTesting.WaitJudgedAsync(app, sensor.Id);
        }
        await using var db = app.NewDb();
        var row = await db.AdvancedReports.AsNoTracking().SingleAsync(r => r.SensorId == sensor.Id);
        Assert.AreEqual(2, row.Repeats);
        Assert.IsTrue(row.LastReceivedAt > row.ReceivedAt);
        Assert.IsNotNull(row.EvaluatedAt);
        CollectionAssert.AreEqual(new[] { PNMTStatusCodes.ADVANCED_OK }, await AdvancedTesting.CodesAsync(app, sensor.Id), "one event for all three");
    }

    [TestMethod]
    public async Task ANudgeHasTheReportJudgedWithinASecond()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        var started = DateTime.UtcNow;
        using var response = await PostAsync(sensor.Integration!.WriteToken, """{"status":"fail"}""");
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        await AdvancedTesting.WaitJudgedAsync(app, sensor.Id);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "judged without waiting for the timer");
        var events = await AdvancedTesting.EventsAsync(app, sensor.Id);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_FAILED, events[0].Code);
        Assert.AreEqual("1 rule fails: The app's status (status = \"fail\", not healthy)", events[0].Message);
    }

    [TestMethod]
    public async Task ADryRunStoresAndNudgesNothing()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        using var response = await PostAsync(sensor.Integration!.WriteToken, """{"status":"warn"}""", query: "?dryRun=true");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var answer = await AdvancedTesting.JsonOf(response);
        Assert.IsTrue(answer.GetProperty("dryRun").GetBoolean());
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_WARNING, answer.GetProperty("preview").GetProperty("code").GetInt32());
        Assert.IsFalse(answer.TryGetProperty("reportId", out _));
        await Task.Delay(300);
        await using var db = app.NewDb();
        Assert.AreEqual(0, await db.AdvancedReports.CountAsync(r => r.SensorId == sensor.Id));
        Assert.AreEqual(0, (await AdvancedTesting.EventsAsync(app, sensor.Id)).Count);
    }

    [TestMethod]
    public async Task NoSessionEvenWithACookie()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        using var response = await PostAsync(sensor.Integration!.WriteToken, "{}",
            change: r => r.Headers.Add("Cookie", "pnmt_session=whatever; XSRF-TOKEN=x"));
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("Set-Cookie"));
        Assert.IsFalse(response.Headers.Contains("Content-Security-Policy"), "the UI's headers stay on /pnmt-admin and /api");
    }

    [TestMethod]
    public async Task TheFrozenEventApiStillWorksAndTheVerdictComesBack()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        var token = sensor.Integration!.WriteToken;
        using (var report = await PostAsync(token, """{"status":"ok"}"""))
        {
            Assert.AreEqual(HttpStatusCode.Accepted, report.StatusCode);
        }
        await AdvancedTesting.WaitJudgedAsync(app, sensor.Id);
        using (var legacy = await app.Client().GetAsync($"/event/{token}/499/by-hand"))
        {
            Assert.AreEqual(HttpStatusCode.OK, legacy.StatusCode);
        }
        using (var again = await PostAsync(token, """{"status":"ok","n":2}"""))
        {
            Assert.AreEqual(HttpStatusCode.Accepted, again.StatusCode);
        }
        await AdvancedTesting.WaitJudgedAsync(app, sensor.Id);
        CollectionAssert.AreEqual(new[] { 295, 499, 295 }, await AdvancedTesting.CodesAsync(app, sensor.Id), "the verdict again after /event's");
    }
}
