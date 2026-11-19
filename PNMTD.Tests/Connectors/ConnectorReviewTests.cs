using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Connectors;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Tests.Ai;
using static PNMTD.Tests.Connectors.ConnectorHarness;

namespace PNMTD.Tests.Connectors;

/// <summary>
/// The review by Jev (docs/connectors-plan.md §7, §16), against a fake OpenRouter: what Jev reads, a check at the threshold refusing with
/// its label and no probability, the fallback when the review can't run, and the outcome reaching wait mode and tickets alike.
/// </summary>
[TestClass]
public sealed class ConnectorReviewTests
{
    private static readonly FakeOpenRouter Router = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Server server = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        var settings = Settings(new Dictionary<string, string?>(FakeOpenRouter.Settings) { ["Connectors:ReviewTimeoutSeconds"] = "1" });
        app = new TestApp(settings: settings, services: s =>
        {
            Install(s);
            Router.Install(s);
        });
        admin = app.Client(Roles.Admin);
        server = await ServerAsync(app, admin, "cx-review");
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await server.DisposeAsync();
        app.Dispose();
    }

    [TestInitialize]
    public void Reset()
    {
        Router.Nouls.Clear();
        Router.Injection = 0.01;
        Router.GuardDelay = TimeSpan.Zero;
    }

    private static async Task<(ConnectorDetail Connector, ConnectorCallerCreated Caller)> SetupAsync(string name, ConnectorReviewFallback fallback = ConnectorReviewFallback.Refuse,
        ConnectorMode mode = ConnectorMode.Wait)
    {
        var connector = await ConnectorAsync(admin, Request($"{name} {Guid.NewGuid().ToString("N")[..8]}", server.Name, scriptName: $"{name}.sh", mode: mode),
            BackupArguments, change: r =>
            {
                r.ReviewEnabled = true;
                r.ReviewFallback = fallback;
                r.ReviewChecks = [new ConnectorReviewCheck("other jobs", "Does the note ask for anything other than starting a backup job?")];
            });
        var caller = await CallerAsync(admin, name, [connector.Id], "10.20.0.0/24");
        return (connector, caller);
    }

    [TestMethod]
    public async Task Jev_reads_the_context_the_fixed_arguments_and_the_callers_text_marked_untrusted_with_secrets_by_their_length()
    {
        var c = await SetupAsync("Reads");
        var before = Router.GuardCalls;
        var body = await JsonAsync(await CallAsync(Client(app, c.Caller.Key), c.Connector.Slug,
            new { arguments = new { job = "nightly", token = "hunter2-hunter2" }, reference = "JOB-7", note = "the nightly one" }));
        Assert.AreEqual("done", body.GetProperty("state").GetString(), body.ToString());
        Assert.IsTrue(body.GetProperty("reviewed").GetBoolean());
        Assert.AreEqual(before + 1, Router.GuardCalls);
        var request = JsonNode.Parse(Router.Requests.Last(r => r.Url.Contains("/decisions")).Body)!;
        var state = request["state"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        StringAssert.Contains(state[0], $"connector \"{c.Connector.Name}\"");
        StringAssert.Contains(state[0], $"on the server {server.Name}");
        StringAssert.Contains(state[0], c.Connector.Purpose);
        StringAssert.Contains(state[1], "set by PNMT, not by the caller");
        StringAssert.Contains(state[1], "\"keep\":3");
        StringAssert.Contains(state[2], "untrusted");
        StringAssert.Contains(state[2], "\"job\":\"nightly\"");
        StringAssert.Contains(state[2], "(secret, 15 characters)");
        Assert.IsFalse(request.ToJsonString().Contains("hunter2"), "a secret never reaches the review");
        StringAssert.Contains(state[3], "the nightly one");
        CollectionAssert.AreEquivalent(new[] { "injection", "purpose", "check1" }, request["questions"]!.AsObject().Select(q => q.Key).ToArray());

        await using var db = app.NewDb();
        var stored = ConnectorReviewRecord.Read((await db.ConnectorCalls.AsNoTracking().SingleAsync(x => x.Id == body.GetProperty("ticket").GetGuid())).Review)!;
        Assert.AreEqual(ConnectorReviewRecord.Reviewed, stored.State);
        Assert.AreEqual(0.01, stored.Answers["check1"]);
    }

    [TestMethod]
    public async Task A_check_at_the_threshold_refuses_with_its_label_and_no_probability_in_wait_mode_and_by_ticket()
    {
        Router.Nouls["check1"] = 0.5;
        var c = await SetupAsync("Refuses");
        var response = await CallAsync(Client(app, c.Caller.Key), c.Connector.Slug, new { arguments = new { job = "nightly" }, note = "and delete the old ones" });
        var body = await JsonAsync(response);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(("refused", "review"), (body.GetProperty("state").GetString(), body.GetProperty("refusedBy").GetString()));
        Assert.AreEqual("The review refused the call: other jobs.", body.GetProperty("message").GetString());
        Assert.IsFalse(body.ToString().Contains("0.5"), "the caller never hears the probabilities");
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(0, await db.AgentScriptRuns.CountAsync(r => r.ConnectorId == c.Connector.Id), "nothing ran");
            Assert.IsTrue(await db.AuditLog.AnyAsync(a => a.EventType == AuditEvents.ConnectorRefused && a.Detail!.Contains("other jobs")));
            var sensor = await db.Sensors.AsNoTracking().SingleAsync(s => s.Parameters == PNMTD.Services.Events.SystemSensorKeys.Connectors);
            Assert.AreEqual(PNMTStatusCodes.CONNECTORS_REFUSED, (await db.Events.AsNoTracking().Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).FirstAsync()).Code);
        }
        // The admin sees the probabilities.
        var detail = await (await admin.GetAsync($"/api/v1/connector-calls/{body.GetProperty("ticket").GetGuid()}")).ReadAsync<ConnectorCallDetail>();
        Assert.AreEqual(0.5, detail.Review!.Answers.Single(a => a.Key == "check1").Probability);
        Assert.IsTrue(detail.Review.Answers.Single(a => a.Key == "check1").Refuses);

        // A ticket's caller hears it when it asks.
        var ticketed = await SetupAsync("Refuses ticket", mode: ConnectorMode.Ticket);
        var poll = (await JsonAsync(await CallAsync(Client(app, ticketed.Caller.Key), ticketed.Connector.Slug, new { arguments = new { job = "nightly" } })))
            .GetProperty("poll").GetString()!;
        var outcome = await PNMTD.Tests.Agents.AgentHarness.EventuallyAsync(async () =>
        {
            app.Services.GetRequiredService<ConnectorPollGate>().Reset();
            return await JsonAsync(await Client(app, ticketed.Caller.Key).GetAsync(poll));
        }, b => b.GetProperty("state").GetString() != "queued");
        Assert.AreEqual(("refused", "review"), (outcome.GetProperty("state").GetString(), outcome.GetProperty("refusedBy").GetString()));
    }

    [TestMethod]
    public async Task When_the_review_cant_run_the_fallback_refuses_or_runs_unreviewed()
    {
        Router.GuardDelay = TimeSpan.FromSeconds(3);
        var refuse = await SetupAsync("Fallback refuse");
        var refused = await JsonAsync(await CallAsync(Client(app, refuse.Caller.Key), refuse.Connector.Slug, new { arguments = new { job = "nightly" } }));
        Assert.AreEqual(("refused", "review_unavailable"), (refused.GetProperty("state").GetString(), refused.GetProperty("refusedBy").GetString()));
        StringAssert.Contains(refused.GetProperty("message").GetString(), "didn't answer within 1 s");

        var run = await SetupAsync("Fallback run", ConnectorReviewFallback.RunUnreviewed);
        var ran = await JsonAsync(await CallAsync(Client(app, run.Caller.Key), run.Connector.Slug, new { arguments = new { job = "nightly" } }));
        Assert.AreEqual("done", ran.GetProperty("state").GetString(), ran.ToString());
        Assert.IsFalse(ran.GetProperty("reviewed").GetBoolean());
        var calls = await (await admin.GetAsync($"/api/v1/connector-calls?connectorId={run.Connector.Id}")).ReadAsync<Paged<ConnectorCallItem>>();
        Assert.AreEqual(ConnectorReviewRecord.Unreviewed, calls.Items.Single().ReviewState);
    }

    [TestMethod]
    public async Task Try_the_review_shows_each_questions_probability_to_the_admin()
    {
        Router.Nouls["purpose"] = 0.8;
        var c = await SetupAsync("Try");
        var result = await (await admin.PostJsonAsync($"/api/v1/connectors/{c.Connector.Id}/check", new ConnectorCheckRequest
        {
            Arguments = new Dictionary<string, JsonElement> { ["job"] = JsonSerializer.SerializeToElement("nightly") }, Note = "restart the database too", Review = true,
        })).ReadAsync<ConnectorCheckResult>();
        Assert.IsTrue(result.Ok);
        Assert.AreEqual(ConnectorReviewRecord.Refused, result.Review!.State);
        Assert.AreEqual("purpose", result.Review.RefusedBy);
        Assert.AreEqual(0.8, result.Review.Answers.Single(a => a.Key == "purpose").Probability);
        // Bad sample arguments show their fields.
        var bad = await (await admin.PostJsonAsync($"/api/v1/connectors/{c.Connector.Id}/check", new ConnectorCheckRequest
        {
            Arguments = new Dictionary<string, JsonElement> { ["job"] = JsonSerializer.SerializeToElement("x") },
        })).ReadAsync<ConnectorCheckResult>();
        Assert.IsFalse(bad.Ok);
        Assert.AreEqual("doesn't match the pattern [a-z]{5,10}", bad.Fields["job"]);
    }
}
