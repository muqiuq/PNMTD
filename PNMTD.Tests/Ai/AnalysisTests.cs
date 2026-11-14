using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Ai;
using PNMTD.Ai.Analysis;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Ai;

/// <summary>The situation analysis (docs/ai-operations-plan.md §9): anonymised, verified, reported once, muted on request.</summary>
[TestClass]
public sealed class AnalysisTests
{
    private static readonly FakeOpenRouter router = new();
    private static TestApp app = null!;
    private static Func<JsonNode, JsonArray>? findings;
    private static string verdict = "agree";

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: FakeOpenRouter.Settings, services: s => router.Install(s));
        router.Answer = (schema, request) =>
        {
            if (schema == nameof(Verdict))
            {
                return new JsonObject { ["answer"] = verdict, ["issues"] = new JsonArray() }.ToJsonString();
            }
            var data = JsonNode.Parse(request["messages"]![1]!["content"]!.GetValue<string>())!["data"]!;
            var list = findings?.Invoke(data) ?? [];
            return new JsonObject
            {
                ["status"] = list.Count > 0 ? "incident" : "normal", ["summary"] = list.Count > 0 ? "Something is broken." : "All quiet.", ["findings"] = list,
            }.ToJsonString();
        };
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestCleanup]
    public void Reset()
    {
        findings = null;
        verdict = "agree";
        router.Injection = 0.01;
    }

    [TestMethod]
    public void The_scrubber_replaces_values_consistently_and_keeps_numbers()
    {
        var scrubber = new Scrubber();
        scrubber.Name("nas-zurich-01", "H1");
        var text = scrubber.Scrub("Backup to 10.1.2.3 failed on NAS-Zurich-01 (fe80::1, 00:11:22:33:44:55) for admin@example.com via " +
            "https://files.example.com/a?b=1 and nas.example.com, job 3f2a9c1e-1111-4222-8333-444455556666, key ABCD1234EFGH5678IJ, code 500 at 12:30:45, " +
            "retry 3 of 10.1.2.3");
        Assert.AreEqual("Backup to <ip:2> failed on H1 (<ip:1>, <mac:1>) for <mail:1> via <url:1> and <host:1>, job <id:1>, key <token:1>, " +
            "code 500 at 12:30:45, retry 3 of <ip:2>", text);
        Assert.AreEqual("10.1.2.3", scrubber.Values["<ip:2>"]);
        Assert.AreEqual("nas.example.com", scrubber.Values["<host:1>"]);
        Assert.IsTrue(SituationSnapshots.IsDistinctive("gw-zurich"));
        Assert.IsFalse(SituationSnapshots.IsDistinctive("Backup"), "a plain word would turn every \"backup\" into a pseudonym");
        var finding = new SituationTask().Schema["properties"]!["findings"]!["items"]!["properties"]!;
        Assert.AreEqual("number", finding["confidence"]!["type"]!.GetValue<string>(), "a plain number in the strict schema");
    }

    [TestMethod]
    public void The_scrubber_replaces_phone_numbers_when_asked_and_keeps_dates_and_versions()
    {
        const string text = "Call Anna on +41 44 123 45 67 or 044 123 45 67, the desk (044) 765-43-21, Berlin 030/1234567, Zurich 0041 44 222 33 44. " +
            "Not on 01/02/2026, 2026-11-14, 12:30:45, v0.12.1, port 8080, 10.1.2.3 or 3 of 10.";
        Assert.AreEqual("Call Anna on <phone:1> or <phone:2>, the desk <phone:3>, Berlin <phone:4>, Zurich <phone:5>. " +
            "Not on 01/02/2026, 2026-11-14, 12:30:45, v0.12.1, port 8080, <ip:1> or 3 of 10.", new Scrubber { PhoneNumbers = true }.Scrub(text));
        StringAssert.Contains(new Scrubber().Scrub(text), "+41 44 123 45 67", "the analysis keeps its numbers");
    }

    private static async Task<(SensorDetail Sensor, HostDetail Host)> FailingAsync(string message, DateTime? at = null)
    {
        var admin = app.Client(Roles.Admin);
        var host = await V1.CreateHostAsync(admin, V1.Unique("acme-host"));
        var sensor = await V1.CreateSensorAsync(admin, host.Id, name: V1.Unique("acme-sensor"));
        var when = at ?? Clock.UtcNow;
        V1.AddEvents(app, sensor.Id, (when.AddMinutes(-3), 200, "fine"), (when.AddMinutes(-2), 500, message), (when.AddMinutes(-1), 500, message));
        return (sensor, host);
    }

    /// <summary>The sensor whose events hold <paramref name="marker"/>: its reference, its host's, and the message as the AI saw it.</summary>
    private static (string Sensor, string Host, string Message) Find(JsonNode data, string marker)
    {
        foreach (var s in data["sensors"]!.AsArray())
        {
            var message = s!["recentEvents"]?.AsArray().Select(e => e!["untrustedMessage"]!.GetValue<string>()).FirstOrDefault(m => m.Contains(marker));
            if (message is not null)
            {
                return (s["ref"]!.GetValue<string>(), s["host"]!.GetValue<string>(), message);
            }
        }
        throw new AssertFailedException($"No sensor with {marker} in the snapshot.");
    }

    private static JsonObject Finding(string key, string severity, string sensor, string host, string explanation) => new()
    {
        ["kind"] = "incident", ["severity"] = severity, ["title"] = $"{sensor} refuses connections", ["explanation"] = explanation,
        ["sensors"] = new JsonArray(sensor), ["hosts"] = new JsonArray(host), ["evidence"] = new JsonArray($"{sensor}: 500, 1 minute ago"),
        ["recommendation"] = $"Check {host}.", ["confidence"] = 0.9, ["key"] = key,
    };

    private static async Task<AiAnalysisRunDetail> RunAsync()
    {
        using var response = await app.Client(Roles.Admin).PostAsync("/api/v1/ai/analysis/run", null);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        var id = (await response.ReadAsync<AiAnalysisQueued>()).Id;
        await app.Services.GetRequiredService<AiWorker>().DrainAsync(CancellationToken.None);
        return await (await app.Client(Roles.Viewer).GetAsync($"/api/v1/ai/analysis/{id}")).ReadAsync<AiAnalysisRunDetail>();
    }

    private static async Task<EventEntity?> LastEventAsync(string key)
    {
        var id = await app.Services.GetRequiredService<SystemSensors>().IdAsync(key, CancellationToken.None);
        await using var db = app.NewDb();
        return await db.Events.Where(e => e.SensorId == id).OrderByDescending(e => e.Created).FirstOrDefaultAsync();
    }

    [TestMethod]
    public async Task The_snapshot_holds_no_names_addresses_or_notes()
    {
        var (sensor, host) = await FailingAsync("replication to 10.9.8.7 stalled");
        var snapshot = await app.Services.GetRequiredService<SituationSnapshots>().BuildAsync(CancellationToken.None);
        var json = SituationSnapshots.Serialize(snapshot.Input);
        StringAssert.DoesNotMatch(json, new Regex(Regex.Escape(sensor.Name)));
        StringAssert.DoesNotMatch(json, new Regex(Regex.Escape(host.Name)));
        StringAssert.DoesNotMatch(json, new Regex(@"10\.9\.8\.7"));
        var (sensorRef, hostRef, message) = Find(JsonNode.Parse(json)!, "replication to");
        Assert.AreEqual(sensor.Id.ToString(), snapshot.Mapping[sensorRef]);
        Assert.AreEqual(host.Id.ToString(), snapshot.Mapping[hostRef]);
        var ip = Regex.Match(message, @"<ip:\d+>").Value;
        Assert.AreEqual("10.9.8.7", snapshot.Mapping[ip]);
        Assert.IsTrue(snapshot.Messages.Contains(message));
        Assert.AreEqual(snapshot.Hash, (await app.Services.GetRequiredService<SituationSnapshots>().BuildAsync(CancellationToken.None)).Hash,
            "nothing changed, so the hash is the same");
        V1.AddEvents(app, sensor.Id, (Clock.UtcNow, 200, "recovered"));
        Assert.AreNotEqual(snapshot.Hash, (await app.Services.GetRequiredService<SituationSnapshots>().BuildAsync(CancellationToken.None)).Hash);
    }

    [TestMethod]
    public async Task A_verified_critical_finding_is_reported_once_raises_a_decision_and_can_be_muted()
    {
        var (sensor, host) = await FailingAsync("connections from 10.7.7.7 refused");
        var key = $"db-refusing-{Guid.NewGuid():N}"[..20];
        findings = data =>
        {
            var (s, h, message) = Find(data, "connections from");
            var ip = Regex.Match(message, @"<ip:\d+>").Value;
            return [Finding(key, "critical", s, h, $"{s} on {h} refuses connections from {ip}.")];
        };

        var run = await RunAsync();
        Assert.AreEqual(AnalysisRunState.Done, run.Run.State);
        Assert.AreEqual(SituationStatus.Incident, run.Run.Status);
        var f = run.Findings.Single(x => x.Key == key);
        Assert.AreEqual(FindingSeverity.Critical, f.Severity);
        Assert.IsTrue(f.Verified && f.New && !f.Muted);
        Assert.AreEqual($"{host.Name} · {sensor.Name} on {host.Name} refuses connections from 10.7.7.7.", f.Explanation, "the names are mapped back");
        Assert.AreEqual(sensor.Id, f.Sensors.Single().Id);
        Assert.IsTrue(run.RequestIds.Count >= 2, "the main model and the verification");
        var alert = await LastEventAsync(SystemSensorKeys.AnalysisCritical);
        Assert.AreEqual(PNMTStatusCodes.AI_FINDING_CRITICAL, alert!.Code);
        StringAssert.Contains(alert.Message, $"Critical: {host.Name} · {sensor.Name} refuses connections");
        Assert.IsNotNull(f.DecisionId);

        var again = await RunAsync();
        var repeated = again.Findings.Single(x => x.Key == key);
        Assert.IsFalse(repeated.New, "reported in the last 24 hours");
        Assert.AreEqual(f.DecisionId, repeated.DecisionId, "attached to the open decision");
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(2, (await db.Decisions.SingleAsync(d => d.Id == f.DecisionId)).Occurrences);
        }

        using (var mute = await app.Client(Roles.User).PostJsonAsync($"/api/v1/decisions/{f.DecisionId}/decide", new { choice = "mute" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, mute.StatusCode, await mute.Content.ReadAsStringAsync());
        }
        var muted = (await RunAsync()).Findings.Single(x => x.Key == key);
        Assert.IsTrue(muted.Muted && !muted.New);
        Assert.IsNull(muted.DecisionId);
        Assert.AreEqual(PNMTStatusCodes.SYSTEM_OK, (await LastEventAsync(SystemSensorKeys.AnalysisCritical))!.Code, "a muted finding counts as none");
    }

    [TestMethod]
    public async Task A_critical_finding_the_verifier_disputes_is_a_warning()
    {
        await FailingAsync("disk array degraded beyond repair");
        var key = $"array-{Guid.NewGuid():N}"[..16];
        findings = data =>
        {
            var (s, h, _) = Find(data, "disk array degraded");
            return [Finding(key, "critical", s, h, $"{s} reports a degraded array.")];
        };
        verdict = "disagree";
        var f = (await RunAsync()).Findings.Single(x => x.Key == key);
        Assert.AreEqual(FindingSeverity.Warning, f.Severity);
        Assert.IsTrue(f.Disputed && !f.Verified);
        Assert.AreEqual(PNMTStatusCodes.AI_FINDING_WARNING, (await LastEventAsync(SystemSensorKeys.AnalysisWarning))!.Code);
        await using var db = app.NewDb();
        Assert.IsTrue((await db.Decisions.SingleAsync(d => d.Id == f.DecisionId)).Disputed);
    }

    [TestMethod]
    public async Task When_the_guard_flags_the_messages_every_message_is_withheld()
    {
        await FailingAsync("certificate store unreadable xyzzy");
        router.Injection = 0.99;
        var before = router.Requests.Count;
        var run = await RunAsync();
        Assert.AreEqual(AnalysisRunState.Done, run.Run.State);
        Assert.IsTrue(run.Run.GuardFlagged);
        Assert.IsTrue(run.Run.Withheld > 0);
        var chats = router.Requests.Skip(before).Where(r => r.Url.EndsWith("/chat/completions", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1, chats.Count, "only the run with the messages withheld reached a model");
        StringAssert.DoesNotMatch(chats[0].Body, new Regex("xyzzy"));
        var sent = JsonNode.Parse(chats[0].Body)!["messages"]![1]!["content"]!.GetValue<string>();
        StringAssert.Contains(sent, SituationSnapshot.Withheld);
    }

    [TestMethod]
    public async Task A_storm_queues_an_early_run_and_only_one_run_is_open_at_a_time()
    {
        await using (var db = app.NewDb())
        {
            // The last run long ago, so that an early run is allowed.
            await db.AiAnalysisRuns.ExecuteUpdateAsync(u => u.SetProperty(r => r.At, r => r.At.AddHours(-3)));
        }
        for (var i = 0; i < SituationScheduler.StormSensors; i++)
        {
            await FailingAsync($"storm {i}");
        }
        var scheduler = app.Services.GetRequiredService<SituationScheduler>();
        var id = await scheduler.TickAsync(CancellationToken.None);
        Assert.IsNotNull(id);
        await using (var db = app.NewDb())
        {
            Assert.AreEqual("storm", (await db.AiAnalysisRuns.SingleAsync(r => r.Id == id)).Trigger);
        }
        Assert.IsNull(await scheduler.TickAsync(CancellationToken.None), "one run at a time");
        Assert.AreEqual(id, await scheduler.QueueAsync("manual", null, CancellationToken.None));
        await app.Services.GetRequiredService<AiWorker>().DrainAsync(CancellationToken.None);
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(AnalysisRunState.Done, (await db.AiAnalysisRuns.SingleAsync(r => r.Id == id)).State);
        }
        var list = await (await app.Client(Roles.Viewer).GetAsync("/api/v1/ai/analysis?state=Done&pageSize=1")).ReadAsync<Paged<AiAnalysisRunItem>>();
        Assert.AreEqual(id, list.Items.Single().Id);
    }
}
