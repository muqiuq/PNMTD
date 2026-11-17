using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Advanced;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Advanced;

/// <summary>
/// The evaluator (docs/advanced-sensor-plan.md §7.3), its run called directly, with nudges off and a fake clock: the queue in
/// order, quiet repeats, judging as of receipt, staleness and its hold-backs, ages between reports, re-evaluations, /event,
/// errors, the work set, the retention and the samples.
/// </summary>
[TestClass]
public sealed class AdvancedEvaluatorTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;
    private static FakeTimeProvider time = null!;
    /// <summary>
    /// The first test's start, in whole seconds from one reading of the clock: PostgreSQL keeps microseconds, and Linux's clock
    /// has 100-ns ticks, so a start between two microseconds made a report stored at it look a fraction late at exactly
    /// interval plus grace (CI, 1.14.0).
    /// </summary>
    private static DateTimeOffset next = WholeSeconds(DateTimeOffset.UtcNow);

    private static DateTimeOffset WholeSeconds(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Advanced:Nudge"] = "false",
            ["Advanced:MinSecondsBetweenReports"] = "0",
            ["Advanced:KeepReports"] = "3",
            ["Heartbeat:StartupGraceMinutes"] = "0",
        });
        admin = app.Client(Roles.Admin);
        hostId = (await V1.CreateHostAsync(admin, V1.Unique("eval-host"), enabled: false)).Id;
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        Clock.Provider = TimeProvider.System;
        await using (var db = app.NewDb())
        {
            await db.Sensors.Where(s => s.ParentId == hostId).ExecuteDeleteAsync();
        }
        app.Dispose();
    }

    [TestInitialize]
    public void Freeze()
    {
        // Each test 100 days after the last, so time only moves on; whole seconds, as PostgreSQL keeps microseconds.
        next = next.AddDays(100);
        time = new FakeTimeProvider(next);
        Clock.Provider = time;
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    private static AdvancedEvaluator Evaluator => app.Services.GetRequiredService<AdvancedEvaluator>();

    private static Task<AdvancedRunResult> RunAsync(bool timerPass = true) => Evaluator.RunOnceAsync(CancellationToken.None, timerPass);

    private static async Task PostAsync(SensorDetail sensor, string body)
    {
        using var response = await AdvancedTesting.PostAsync(app, sensor.Integration!.WriteToken, body);
        Assert.IsTrue(response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync());
    }

    private static async Task<AdvancedSensorDetail> RuleAsync(SensorDetail sensor, object rule)
    {
        using var response = await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/advanced/rules", rule);
        return await response.ReadAsync<AdvancedSensorDetail>();
    }

    private static object Disk(double min = 5) => new { name = "Disk space", path = "disk.free_gb", @operator = "Ge", operand = $"{min}" };

    [TestMethod]
    public async Task TheEndpointQueuesAndTheQueueIsJudgedInOrder()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        await RuleAsync(sensor, Disk());
        await PostAsync(sensor, """{"disk":{"free_gb":10}}""");
        await using (var db = app.NewDb())
        {
            var row = await db.AdvancedReports.AsNoTracking().SingleAsync(r => r.SensorId == sensor.Id);
            Assert.IsNull(row.EvaluatedAt, "queued");
            Assert.IsNull(row.Code);
            var side = await db.AdvancedSensors.AsNoTracking().SingleAsync(a => a.SensorId == sensor.Id);
            Assert.AreEqual((row.Id, (int?)null), (side.LastReportId, side.LastCode), "the receipt, but no verdict");
            Assert.AreEqual(0, await db.Events.CountAsync(e => e.SensorId == sensor.Id), "the endpoint writes no event");
        }
        await RunAsync();
        CollectionAssert.AreEqual(new[] { 295 }, await AdvancedTesting.CodesAsync(app, sensor.Id));

        // A failure between two passes, all three between two runs: the failure and the recovery.
        foreach (var free in new[] { 11, 3, 12 })
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await PostAsync(sensor, $$$"""{"disk":{"free_gb":{{{free}}}}}""");
        }
        var run = await RunAsync();
        Assert.AreEqual(3, run.Reports);
        var events = await AdvancedTesting.EventsAsync(app, sensor.Id);
        CollectionAssert.AreEqual(new[] { 295, 495, 295 }, events.Select(e => e.Code).ToArray());
        Assert.AreEqual("1 rule fails: Disk space (disk.free_gb = 3, must be at least 5)", events[1].Message);
        Assert.IsTrue(events.All(e => e.Source != AdvancedEvaluator.TimerSource), "the report's address");
        await using var check = app.NewDb();
        var codes = await check.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensor.Id).OrderBy(r => r.ReceivedAt).Select(r => r.Code).ToListAsync();
        CollectionAssert.AreEqual(new int?[] { 295, 495, 295 }, codes, "each report its own verdict; the first went with KeepReports = 3");
    }

    [TestMethod]
    public async Task AFailureThatLastsWritesOneEvent()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        await RuleAsync(sensor, Disk());
        foreach (var free in new[] { 3.2, 3.1, 3.0 })
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await PostAsync(sensor, $$$"""{"disk":{"free_gb":{{{free}}}}}""");
            await RunAsync();
        }
        var events = await AdvancedTesting.EventsAsync(app, sensor.Id);
        Assert.AreEqual(1, events.Count, "the same problem");
        StringAssert.Contains(events[0].Message, "3.2", "the value when the verdict changed");
    }

    [TestMethod]
    public async Task ABacklogIsJudgedAsOfEachReceipt()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        await RuleAsync(sensor, new { name = "Backup", path = "backup.finished", @operator = "NewerThan", operand = "26h" });
        using (var field = await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/advanced/fields",
                   new { name = "Size", path = "backup.size", chart = true, pinned = true }))
        {
            Assert.AreEqual(HttpStatusCode.OK, field.StatusCode, await field.Content.ReadAsStringAsync());
        }
        var finished = Clock.UtcNow.AddHours(-1);
        var receipt = Clock.UtcNow;
        await PostAsync(sensor, $$$"""{"backup":{"finished":"{{{finished:O}}}","size":42}}""");
        // PNMTD was down for 30 hours: the report is judged now, as of its receipt, then the timer's pass as of now.
        time.Advance(TimeSpan.FromHours(30));
        await RunAsync();
        var events = await AdvancedTesting.EventsAsync(app, sensor.Id);
        CollectionAssert.AreEqual(new[] { 295, 495 }, events.Select(e => e.Code).ToArray());
        Assert.AreEqual(AdvancedEvaluator.TimerSource, events[1].Source);
        await using var db = app.NewDb();
        var sample = await db.AdvancedSamples.AsNoTracking().SingleAsync(s => s.SensorId == sensor.Id);
        Assert.AreEqual(new DateTime(receipt.Ticks - receipt.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc), sample.Minute, "the receipt's minute");
    }

    [TestMethod]
    public async Task StaleAfterIntervalAndGraceAndBackWithTheNextReport()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId, interval: 300, grace: 60);
        await PostAsync(sensor, """{"status":"ok"}""");
        await RunAsync();
        time.Advance(TimeSpan.FromMinutes(6));
        await RunAsync();
        CollectionAssert.AreEqual(new[] { 295 }, await AdvancedTesting.CodesAsync(app, sensor.Id), "exactly interval plus grace");
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue((await RunAsync()).Timed.Contains(sensor.Id));
        var events = await AdvancedTesting.EventsAsync(app, sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.HEARTBEAT_MISSING, events[^1].Code);
        StringAssert.EndsWith(events[^1].Message, "(expected every 5 min, 1 min grace)");
        await RunAsync();
        Assert.AreEqual(2, (await AdvancedTesting.EventsAsync(app, sensor.Id)).Count, "once");

        // A report stored but not yet judged is a sign of life: the queue comes before the timer's pass.
        time.Advance(TimeSpan.FromMinutes(10));
        await PostAsync(sensor, """{"status":"ok","n":2}""");
        await RunAsync();
        CollectionAssert.AreEqual(new[] { 295, 460, 295 }, await AdvancedTesting.CodesAsync(app, sensor.Id));
        var detail = (await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>()).Advanced!;
        Assert.IsFalse(detail.Stale);
        Assert.AreEqual(Clock.UtcNow.AddMinutes(6), detail.StaleAt);
    }

    [TestMethod]
    public async Task NoMissingReportWhileTheUplinkIsDownButReportsAreJudged()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId, interval: 60);
        await PostAsync(sensor, """{"status":"ok"}""");
        await RunAsync();
        await using (var db = app.NewDb())
        {
            db.SetKeyValueEntryByEnum(KeyValueKeyEnums.UPLINK_OK, false);
        }
        try
        {
            time.Advance(TimeSpan.FromMinutes(10));
            await RunAsync();
            CollectionAssert.AreEqual(new[] { 295 }, await AdvancedTesting.CodesAsync(app, sensor.Id), "460 held back");
            await PostAsync(sensor, """{"status":"down"}""");
            await RunAsync();
            CollectionAssert.AreEqual(new[] { 295, 495 }, await AdvancedTesting.CodesAsync(app, sensor.Id), "a report that arrives is judged");
        }
        finally
        {
            await using var db = app.NewDb();
            // Gone rather than true again: true after false would hold everything back for 5 more minutes.
            var key = KeyValueDbExtensions.KeyOf(KeyValueKeyEnums.UPLINK_OK);
            await db.KeyValues.Where(k => k.Key == key).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    public async Task AnAgeRuleFailsBetweenReports()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        await RuleAsync(sensor, new { name = "Backup", path = "finished", @operator = "NewerThan", operand = "2h" });
        await PostAsync(sensor, $$"""{"finished":"{{Clock.UtcNow:O}}"}""");
        await RunAsync();
        time.Advance(TimeSpan.FromMinutes(119));
        await RunAsync();
        CollectionAssert.AreEqual(new[] { 295 }, await AdvancedTesting.CodesAsync(app, sensor.Id));
        time.Advance(TimeSpan.FromMinutes(2));
        await RunAsync();
        CollectionAssert.AreEqual(new[] { 295, 495 }, await AdvancedTesting.CodesAsync(app, sensor.Id), "on time, without a report");
        await PostAsync(sensor, $$"""{"finished":"{{Clock.UtcNow:O}}"}""");
        await RunAsync();
        CollectionAssert.AreEqual(new[] { 295, 495, 295 }, await AdvancedTesting.CodesAsync(app, sensor.Id));
    }

    [TestMethod]
    public async Task ARuleChangeIsJudgedBeforeTheAnswerAndQuietWhenTheKeyStays()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        await PostAsync(sensor, """{"disk":{"free_gb":3}}""");
        await RunAsync();
        var detail = await RuleAsync(sensor, Disk());
        var rule = detail.Rules.Single(r => r.Name == "Disk space");
        Assert.AreEqual(AdvancedOutcome.Fail, rule.Last?.Outcome, "the answer carries the new outcome");
        Assert.AreEqual("disk.free_gb ≥ 5", rule.Condition);
        Assert.IsNotNull(rule.FailingSince);
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_FAILED, detail.Verdict?.Code);
        CollectionAssert.AreEqual(new[] { 295, 495 }, await AdvancedTesting.CodesAsync(app, sensor.Id));

        using (var renamed = await admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}/advanced/rules/{rule.Id}",
                   new { name = "Free space", path = "disk.free_gb", @operator = "Ge", operand = "5" }))
        {
            Assert.AreEqual("Free space", (await renamed.ReadAsync<AdvancedSensorDetail>()).Rules.Single(r => r.Id == rule.Id).Last?.Name);
        }
        CollectionAssert.AreEqual(new[] { 295, 495 }, await AdvancedTesting.CodesAsync(app, sensor.Id), "renaming notifies nobody");

        using (var relaxed = await admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}/advanced/rules/{rule.Id}",
                   new { name = "Free space", path = "disk.free_gb", @operator = "Ge", operand = "2" }))
        {
            Assert.AreEqual(PNMTStatusCodes.ADVANCED_OK, (await relaxed.ReadAsync<AdvancedSensorDetail>()).Verdict?.Code);
        }
        CollectionAssert.AreEqual(new[] { 295, 495, 295 }, await AdvancedTesting.CodesAsync(app, sensor.Id));

        // Another interval is a settings change too.
        using var update = await admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}",
            new { hostId, type = "ADVANCED", name = sensor.Name, enabled = true, interval = 60, gracePeriod = 0 });
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        await using var db = app.NewDb();
        Assert.IsNull((await db.AdvancedSensors.AsNoTracking().SingleAsync(a => a.SensorId == sensor.Id)).ReevaluateRequestedAt);
    }

    [TestMethod]
    public async Task AFailingReportIsMarkedAfterThreeAttemptsAndTheOthersGoOn()
    {
        var broken = await AdvancedTesting.CreateAsync(admin, hostId);
        var healthy = await AdvancedTesting.CreateAsync(admin, hostId);
        Guid ruleId;
        await using (var db = app.NewDb())
        {
            // An operand the API would refuse makes the evaluation throw.
            ruleId = Guid.NewGuid();
            db.AdvancedRules.Add(new AdvancedRuleEntity
            {
                Id = ruleId, SensorId = broken.Id, Name = "Broken", Path = "jobs", Operator = AdvancedOperator.CountGe, Operand = "many", Created = Clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await PostAsync(broken, """{"jobs":[1]}""");
        time.Advance(TimeSpan.FromSeconds(1));
        await PostAsync(broken, """{"jobs":[1,2]}""");
        await PostAsync(healthy, """{"status":"ok"}""");
        for (var i = 0; i < AdvancedEvaluator.MaxAttempts; i++)
        {
            await RunAsync();
        }
        CollectionAssert.AreEqual(new[] { 295 }, await AdvancedTesting.CodesAsync(app, healthy.Id), "the other sensor goes on");
        Assert.AreEqual(0, (await AdvancedTesting.EventsAsync(app, broken.Id)).Count);
        await using (var db = app.NewDb())
        {
            var first = await db.AdvancedReports.Where(r => r.SensorId == broken.Id).OrderBy(r => r.ReceivedAt).FirstAsync();
            Assert.AreEqual((short)3, first.EvaluationAttempts);
            await db.AdvancedRules.Where(r => r.Id == ruleId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Operand, "1"));
        }
        await RunAsync();
        var events = await AdvancedTesting.EventsAsync(app, broken.Id);
        CollectionAssert.AreEqual(new[] { 496, 295 }, events.Select(e => e.Code).ToArray(), "the bad one as internal, the next one judged");
        Assert.AreEqual("PNMT couldn't evaluate this report", events[0].Message);
    }

    [TestMethod]
    public async Task TheTimersPassReadsOnlySensorsThatTimeChanges()
    {
        var plain = await AdvancedTesting.CreateAsync(admin, hostId);
        var timed = await AdvancedTesting.CreateAsync(admin, hostId, interval: 600);
        var aged = await AdvancedTesting.CreateAsync(admin, hostId);
        await RuleAsync(aged, new { name = "Fresh", path = "t", @operator = "NewerThan", operand = "1d" });
        foreach (var s in new[] { plain, timed, aged })
        {
            await PostAsync(s, $$"""{"t":"{{Clock.UtcNow:O}}"}""");
        }
        var run = await RunAsync();
        Assert.IsFalse(run.Timed.Contains(plain.Id));
        Assert.IsTrue(run.Timed.Contains(timed.Id));
        Assert.IsTrue(run.Timed.Contains(aged.Id));
    }

    [TestMethod]
    public async Task RetentionKeepsTheNewestAndTheWaitingOnes()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await PostAsync(sensor, $$"""{"n":{{i}}}""");
            await RunAsync(timerPass: false);
        }
        await using var db = app.NewDb();
        Assert.AreEqual(3, await db.AdvancedReports.CountAsync(r => r.SensorId == sensor.Id), "KeepReports");

        // 40 days later, the hourly retention: the newest two stay, whatever their age.
        time.Advance(TimeSpan.FromDays(40));
        await Evaluator.MaintainAsync(CancellationToken.None);
        var left = await db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensor.Id).OrderBy(r => r.ReceivedAt).ToListAsync();
        Assert.AreEqual(2, left.Count);
        StringAssert.Contains(left[^1].Body, "4");
    }

    [TestMethod]
    public async Task SamplesPerMinuteAndPerHour()
    {
        var sensor = await AdvancedTesting.CreateAsync(admin, hostId);
        await RuleAsync(sensor, new { name = "Queue", path = "queue.length", @operator = "Le", operand = "500", severity = "Warn" });
        AdvancedSensorDetail detail;
        using (var field = await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/advanced/fields", new { name = "Queue", path = "queue.length", chart = true }))
        {
            detail = await field.ReadAsync<AdvancedSensorDetail>();
        }
        var fieldId = detail.Fields.Single().Id;
        // Three reports in one minute.
        time.SetUtcNow(new DateTimeOffset(time.GetUtcNow().Year, time.GetUtcNow().Month, time.GetUtcNow().Day, 10, 0, 5, TimeSpan.Zero).AddDays(1));
        foreach (var n in new[] { 1, 5, 3 })
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await PostAsync(sensor, $$$"""{"queue":{"length":"{{{n}}}"}}""");
        }
        time.Advance(TimeSpan.FromMinutes(5));
        await PostAsync(sensor, """{"queue":{"length":812}}""");
        await RunAsync();
        var day = await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/advanced/samples?fieldId={fieldId}&range=24h")).ReadAsync<AdvancedSamples>();
        Assert.AreEqual(2, day.Points.Count);
        Assert.AreEqual((1d, 5d, 3d, 3), (day.Points[0].Min, day.Points[0].Max, day.Points[0].Last, day.Points[0].Count));
        Assert.AreEqual(60, day.StepSeconds);
        Assert.AreEqual(500d, day.Thresholds.Single().Value);
        var week = await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/advanced/samples?fieldId={fieldId}&range=7d")).ReadAsync<AdvancedSamples>();
        Assert.AreEqual(1, week.Points.Count, "one hour");
        Assert.AreEqual((1d, 812d, 812d, 4), (week.Points[0].Min, week.Points[0].Max, week.Points[0].Last, week.Points[0].Count));

        var current = (await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>()).Advanced!;
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_WARNING, current.Verdict?.Code);
        Assert.AreEqual("812", current.Fields.Single().Value?.GetRawText());
        Assert.AreEqual(AdvancedOutcome.Warn, current.Fields.Single().Outcome, "the rule on its path");
    }
}
