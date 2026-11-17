using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Advanced;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Advanced;

/// <summary>
/// The Advanced sensor through the API (docs/advanced-sensor-plan.md §8): creating it with and without the starter rule, the
/// validation and the limits, the order, copying, the type changed away, the sensor deleted, trying rules, data that names
/// people, and the audit of reports deleted and rules changed.
/// </summary>
[TestClass]
public sealed class AdvancedSensorTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?> { ["Advanced:MinSecondsBetweenReports"] = "0" });
        admin = app.Client(Roles.Admin);
        hostId = (await V1.CreateHostAsync(admin, V1.Unique("adv-host"), enabled: false)).Id;
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

    private static string Rules(Guid id) => $"/api/v1/sensors/{id}/advanced/rules";

    private static string Fields(Guid id) => $"/api/v1/sensors/{id}/advanced/fields";

    private static async Task<string> FieldOf(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        return string.Join(",", (await V1.ErrorsOf(response)).EnumerateObject().Select(p => p.Name));
    }

    private static async Task<AdvancedSensorDetail> ReportAsync(SensorDetail sensor, string body)
    {
        using var response = await AdvancedTesting.PostAsync(app, sensor.Integration!.WriteToken, body);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        await AdvancedTesting.WaitJudgedAsync(app, sensor.Id);
        return (await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>()).Advanced!;
    }

    [TestMethod]
    public async Task CreatedWithOrWithoutTheStarterRule()
    {
        var with = await AdvancedTesting.CreateAsync(admin, hostId);
        Assert.AreEqual(SensorType.ADVANCED, with.Type);
        Assert.IsNull(with.Parameters);
        var rule = with.Advanced!.Rules.Single();
        Assert.AreEqual((AdvancedSensorService.StatusRuleName, "status is healthy", AdvancedWhenMissing.Pass), (rule.Name, rule.Condition, rule.WhenMissing));
        Assert.IsNull(with.Advanced.Verdict, "no report, no verdict");
        Assert.IsNull(with.LastCode, "no event: no data");

        var without = await AdvancedTesting.CreateAsync(admin, hostId, new { addStatusRule = false, namesPeople = true });
        Assert.AreEqual(0, without.Advanced!.Rules.Count);
        Assert.IsTrue(without.Advanced.NamesPeople);

        var ok = await ReportAsync(without, """{"anything":[1,2,3]}""");
        Assert.AreEqual((PNMTStatusCodes.ADVANCED_OK, "Report received"), (ok.Verdict!.Code, ok.Verdict.Message));
    }

    [TestMethod]
    public async Task TheSensorsSettingsAreValidated()
    {
        async Task<string> Create(object request) => await FieldOf(await admin.PostJsonAsync("/api/v1/sensors", request));
        Assert.AreEqual("parameters", await Create(new { hostId, type = "ADVANCED", name = "x", parameters = "status" }));
        Assert.AreEqual("interval", await Create(new { hostId, type = "ADVANCED", name = "x", interval = 10 }));
    }

    [TestMethod]
    public async Task RulesAndFieldsAreValidatedAndLimited()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId, new { addStatusRule = false });
        async Task<string> Rule(object rule) => await FieldOf(await admin.PostJsonAsync(Rules(sensor.Id), rule));
        Assert.AreEqual("operand", await Rule(new { name = "n", path = "a", @operator = "Ge", operand = "five" }));
        Assert.AreEqual("operand2", await Rule(new { name = "n", path = "a", @operator = "Between", operand = "5", operand2 = "1" }));
        Assert.AreEqual("operand", await Rule(new { name = "n", path = "a", @operator = "Matches", operand = "(" }));
        Assert.AreEqual("operand", await Rule(new { name = "n", path = "a", @operator = "OlderThan", operand = "soon" }));
        Assert.AreEqual("path", await Rule(new { name = "n", path = "a..b", @operator = "Exists" }));
        Assert.AreEqual("name", await Rule(new { name = "", path = "a", @operator = "Exists" }));

        async Task<string> Field(object field) => await FieldOf(await admin.PostJsonAsync(Fields(sensor.Id), field));
        Assert.AreEqual("path", await Field(new { name = "Jobs", path = "jobs[*].state" }));
        Assert.AreEqual("decimals", await Field(new { name = "Free", path = "free", decimals = 7 }));

        await using (var db = app.NewDb())
        {
            for (var i = 0; i < AdvancedLimits.MaxRules; i++)
            {
                db.AdvancedRules.Add(new Models.Db.AdvancedRuleEntity
                {
                    Id = Guid.NewGuid(), SensorId = sensor.Id, Order = i, Name = $"r{i}", Path = "a", Operator = AdvancedOperator.Exists, Created = DateTime.UtcNow,
                });
            }
            for (var i = 0; i < AdvancedLimits.MaxFields; i++)
            {
                db.AdvancedFields.Add(new Models.Db.AdvancedFieldEntity
                {
                    Id = Guid.NewGuid(), SensorId = sensor.Id, Order = i, Name = $"f{i}", Path = $"f{i}", Chart = i < AdvancedLimits.MaxChartedFields,
                    Created = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }
        StringAssert.Contains((await V1.ErrorsOf(await admin.PostJsonAsync(Rules(sensor.Id), new { name = "51st", path = "a", @operator = "Exists" })))
            .GetProperty("name")[0].GetString(), "At most 50 rules");
        Assert.AreEqual("name", await Field(new { name = "31st", path = "x" }));
        var some = (await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/advanced")).ReadAsync<AdvancedSensorDetail>()).Fields.First(f => !f.Chart);
        Assert.AreEqual("chart", await FieldOf(await admin.PutJsonAsync($"{Fields(sensor.Id)}/{some.Id}", new { name = "eleventh", path = "x", chart = true })));
    }

    [TestMethod]
    public async Task RulesAreReorderedAndCopied()
    {
        var source = await AdvancedTesting.CreateAsync(admin, hostId);
        await (await admin.PostJsonAsync(Rules(source.Id), new { name = "Disk", path = "disk.free_gb", @operator = "Ge", operand = "5" })).ReadAsync<AdvancedSensorDetail>();
        var detail = await (await admin.PostJsonAsync(Fields(source.Id), new { name = "Free", path = "disk.free_gb", unit = "GB", pinned = true, chart = true }))
            .ReadAsync<AdvancedSensorDetail>();
        var ids = detail.Rules.Select(r => r.Id).Reverse().ToList();
        var ordered = await (await admin.PutJsonAsync($"{Rules(source.Id)}/order", new { ids })).ReadAsync<AdvancedSensorDetail>();
        CollectionAssert.AreEqual(new[] { "Disk", AdvancedSensorService.StatusRuleName }, ordered.Rules.Select(r => r.Name).ToArray());
        Assert.AreEqual("ids", await FieldOf(await admin.PutJsonAsync($"{Rules(source.Id)}/order", new { ids = ids.Take(1) })));

        var target = await AdvancedTesting.CreateAsync(admin, hostId);
        var appended = await (await admin.PostJsonAsync($"{Rules(target.Id)}/copy", new { fromSensorId = source.Id })).ReadAsync<AdvancedSensorDetail>();
        CollectionAssert.AreEqual(new[] { AdvancedSensorService.StatusRuleName, "Disk", AdvancedSensorService.StatusRuleName },
            appended.Rules.Select(r => r.Name).ToArray(), "appended");
        Assert.AreEqual(("Free", "GB", true), (appended.Fields.Single().Name, appended.Fields.Single().Unit, appended.Fields.Single().Chart));
        var replaced = await (await admin.PostJsonAsync($"{Rules(target.Id)}/copy", new { fromSensorId = source.Id, replace = true })).ReadAsync<AdvancedSensorDetail>();
        CollectionAssert.AreEqual(new[] { "Disk", AdvancedSensorService.StatusRuleName }, replaced.Rules.Select(r => r.Name).ToArray(), "replaced");
        Assert.AreEqual(1, replaced.Fields.Count);
        Assert.AreEqual("fromSensorId", await FieldOf(await admin.PostJsonAsync($"{Rules(target.Id)}/copy", new { fromSensorId = target.Id })));
    }

    [TestMethod]
    public async Task TheTypeChangedAwayAndTheSensorDeletedTakeEverything()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        await admin.PostJsonAsync(Fields(sensor.Id), new { name = "N", path = "n", chart = true });
        await ReportAsync(sensor, """{"n":1}""");
        using (var changed = await admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", new { hostId, type = "SIMPLE", name = sensor.Name, enabled = true }))
        {
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            Assert.IsNull((await changed.ReadAsync<SensorDetail>()).Advanced);
        }
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(0, await db.AdvancedSensors.CountAsync(a => a.SensorId == sensor.Id));
            Assert.AreEqual(0, await db.AdvancedRules.CountAsync(a => a.SensorId == sensor.Id));
            Assert.AreEqual(0, await db.AdvancedFields.CountAsync(a => a.SensorId == sensor.Id));
            Assert.AreEqual(0, await db.AdvancedReports.CountAsync(a => a.SensorId == sensor.Id));
            Assert.AreEqual(0, await db.AdvancedSamples.CountAsync(a => a.SensorId == sensor.Id));
        }

        var other = await AdvancedTesting.CreateAsync(admin, hostId);
        await ReportAsync(other, """{"n":1}""");
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/sensors/{other.Id}")).StatusCode);
        await using var check = app.NewDb();
        Assert.AreEqual(0, await check.AdvancedSensors.CountAsync(a => a.SensorId == other.Id));
        Assert.AreEqual(0, await check.AdvancedReports.CountAsync(a => a.SensorId == other.Id));
    }

    [TestMethod]
    public async Task TryingRulesStoresNothing()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        var empty = await (await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/advanced/test", new { })).ReadAsync<AdvancedTestResult>();
        StringAssert.StartsWith(empty.Error, "No report yet");

        var pasted = await (await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/advanced/test", new
        {
            report = """{"status":"ok","disk":{"free_gb":3.2}}""",
            rules = new object[] { new { id = Guid.Empty, name = "Disk", path = "disk.free_gb", @operator = "Ge", operand = "5" } },
        })).ReadAsync<AdvancedTestResult>();
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_FAILED, pasted.Code);
        Assert.AreEqual(("disk.free_gb", "3.2"), (pasted.Results.Single().Actual[0].Path, pasted.Results.Single().Actual[0].Value));

        var broken = await (await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/advanced/test", new { report = "{\"a\":1,\"a\":2}" })).ReadAsync<AdvancedTestResult>();
        Assert.AreEqual((PNMTStatusCodes.ADVANCED_INVALID, "The report has the key \"a\" twice"), (broken.Code, broken.Error));

        Assert.AreEqual("rules[0].operand", await FieldOf(await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/advanced/test",
            new { report = "{}", rules = new object[] { new { name = "x", path = "a", @operator = "Lt", operand = "" } } })));

        await ReportAsync(sensor, """{"status":"degraded"}""");
        var latest = await (await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/advanced/test", new { })).ReadAsync<AdvancedTestResult>();
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_WARNING, latest.Code, "the saved rules on the latest report");
        Assert.AreEqual(1, (await AdvancedTesting.EventsAsync(app, sensor.Id)).Count, "tests write no event");

        var paths = await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/advanced/paths")).ReadAsync<List<AdvancedPathItem>>();
        Assert.AreEqual(("status", "string", "\"degraded\""), (paths.Single().Path, paths.Single().Kind, paths.Single().Preview));
    }

    [TestMethod]
    public async Task DataThatNamesPeopleNeedsDetails()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId, new { namesPeople = true });
        await admin.PostJsonAsync(Rules(sensor.Id), new { name = "Owner", path = "owner", @operator = "Eq", operand = "nobody" });
        await admin.PostJsonAsync(Fields(sensor.Id), new { name = "Owner", path = "owner", pinned = true });
        await ReportAsync(sensor, """{"status":"ok","owner":"Jane Doe","message":"Exported for Jane Doe"}""");

        var viewer = app.Client(Roles.Viewer);
        foreach (var path in new[] { "reports", "reports/latest", "paths" })
        {
            using var response = await viewer.GetAsync($"/api/v1/sensors/{sensor.Id}/advanced/{path}");
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, path);
        }
        var seen = (await (await viewer.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>()).Advanced!;
        Assert.IsFalse(seen.CanSeeData);
        var owner = seen.Rules.Single(r => r.Name == "Owner");
        Assert.AreEqual(AdvancedOutcome.Fail, owner.Last?.Outcome, "the outcome");
        Assert.AreEqual(0, owner.Last!.Actual.Count, "without the values");
        Assert.IsNull(seen.Fields.Single().Value);
        Assert.IsNull(seen.Latest?.Message);

        var details = app.ClientWithRights("Monitoring:Details");
        Assert.AreEqual(HttpStatusCode.OK, (await details.GetAsync($"/api/v1/sensors/{sensor.Id}/advanced/reports/latest")).StatusCode);
        var full = (await (await details.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>()).Advanced!;
        Assert.AreEqual("\"Jane Doe\"", full.Rules.Single(r => r.Name == "Owner").Last!.Actual[0].Value);
        Assert.AreEqual("Exported for Jane Doe", full.Latest?.Message);
    }

    [TestMethod]
    public async Task ReportsAreReadAndDeletedWithTheAudit()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        await ReportAsync(sensor, """{"status":"ok","password":"oops"}""");
        await ReportAsync(sensor, """{"status":"ok"}""");
        var list = await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/advanced/reports")).ReadAsync<Paged<AdvancedReportItem>>();
        Assert.AreEqual(2, list.TotalCount);
        Assert.IsTrue(list.Items.All(r => r.Evaluated && r.Code == PNMTStatusCodes.ADVANCED_OK));
        var latest = await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/advanced/reports/latest")).ReadAsync<AdvancedReportDetail>();
        Assert.AreEqual("""{"status":"ok"}""", latest.Body, "exactly as received");
        Assert.AreEqual(list.Items[1].Id, latest.PreviousId);
        var older = await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/advanced/reports/{latest.PreviousId}")).ReadAsync<AdvancedReportDetail>();
        StringAssert.Contains(older.Body, "oops");

        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/sensors/{sensor.Id}/advanced/reports/{older.Report.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/sensors/{sensor.Id}/advanced/reports")).StatusCode);
        await using var db = app.NewDb();
        Assert.AreEqual(0, await db.AdvancedReports.CountAsync(r => r.SensorId == sensor.Id));
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.TargetId == sensor.Id.ToString()
            && (a.EventType == AuditEvents.AdvancedReportDeleted || a.EventType == AuditEvents.AdvancedReportsCleared)).ToListAsync();
        Assert.AreEqual(2, audit.Count);
        StringAssert.Contains(audit.Single(a => a.EventType == AuditEvents.AdvancedReportsCleared).Detail, "all 1 reports");
        var detail = (await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>()).Advanced!;
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_OK, detail.Verdict?.Code, "the verdict stays until the next report");
    }

    [TestMethod]
    public async Task TheHistoryListsRuleChanges()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        var created = await (await admin.PostJsonAsync(Rules(sensor.Id), new { name = "Disk space", path = "disk.free_gb", @operator = "Ge", operand = "5" }))
            .ReadAsync<AdvancedSensorDetail>();
        var rule = created.Rules.Single(r => r.Name == "Disk space");
        await (await admin.PutJsonAsync($"{Rules(sensor.Id)}/{rule.Id}", new { name = "Disk space", path = "disk.free_gb", @operator = "Ge", operand = "10" }))
            .ReadAsync<AdvancedSensorDetail>();
        await using var db = app.NewDb();
        var entries = await db.AuditLog.AsNoTracking().Where(a => a.Subjects.Contains(sensor.Id) && a.EventType == AuditEvents.AdvancedRulesChanged)
            .OrderBy(a => a.CreatedAt).ToListAsync();
        Assert.AreEqual(2, entries.Count);
        StringAssert.Contains(entries[0].Detail, "rule \"Disk space\" added: disk.free_gb ≥ 5");
        StringAssert.Contains(entries[1].Detail, "disk.free_gb ≥ 10");
        // The sensor's History tab (docs/audit-redmine-zabbix-plan.md §4.3), with the rule's field changes.
        var history = await (await admin.GetAsync($"/api/v1/audit-log/subjects/{sensor.Id}")).Content.ReadAsStringAsync();
        StringAssert.Contains(history, "AdvancedRulesChanged");
        StringAssert.Contains(history, "\"Operand\"");
    }

    [TestMethod]
    public async Task TheToolShowsTheVerdictAndWithholdsWhatNamesPeople()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId, new { namesPeople = true });
        await admin.PostJsonAsync(Rules(sensor.Id), new { name = "Owner", path = "owner", @operator = "Eq", operand = "nobody" });
        var detail = await ReportAsync(sensor, """{"status":"fail","owner":"Jane Doe"}""");
        Assert.AreEqual("2 rules fail: The app's status (status: not healthy); Owner (owner: must be \"nobody\")", detail.Verdict?.Message,
            "the event names the rules, not the values (§13)");
        var tool = global::PNMTD.Tools.ToolCatalog.All.Single(t => t.Name == "advanced_sensor_get");
        var filter = await Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::PNMTD.Cookie.PrivacyFilter>(app.Services)
            .OpenAsync(CancellationToken.None);
        async Task<string> RunAsync(global::PNMTD.Auth.Rights.Level level)
        {
            using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(app.Services);
            var ctx = new global::PNMTD.Tools.ToolContext(scope.ServiceProvider, new global::PNMTD.Auth.Rights.UserRights(false,
                new Dictionary<global::PNMTD.Auth.Rights.Area, global::PNMTD.Auth.Rights.Level> { [global::PNMTD.Auth.Rights.Area.Monitoring] = level }), filter);
            await using var db = app.NewDb();
            var result = await tool.Run(System.Text.Json.JsonDocument.Parse($$"""{"sensorId":"{{sensor.Id}}"}""").RootElement, ctx, db, CancellationToken.None);
            return System.Text.Json.JsonSerializer.Serialize(result);
        }
        var view = await RunAsync(global::PNMTD.Auth.Rights.Level.View);
        StringAssert.Contains(view, "\"Code\":495");
        Assert.IsFalse(view.Contains("Jane", StringComparison.Ordinal), view);
        var details = await RunAsync(global::PNMTD.Auth.Rights.Level.Details);
        StringAssert.Contains(details, "untrusted source=\\u0022advanced sensor report\\u0022");
        StringAssert.Contains(details, "Jane Doe", "with Details, the report");
    }
}
