using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Connectors;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.Events;
using static PNMTD.Tests.Connectors.ConnectorHarness;

namespace PNMTD.Tests.Connectors;

/// <summary>
/// <c>/connector/v1</c> end to end (docs/connectors-plan.md §4, §6, §16): the key, the networks, the grants, the body, the arguments, wait
/// and ticket mode, idempotency, the limits, dry runs, the target as text, the description, the key shown again and rotated, the
/// failed-key guard, the run in the history with the connector as actor, and the retention.
/// </summary>
[TestClass]
public sealed class ConnectorEndpointTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Server server = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: Settings(new Dictionary<string, string?> { ["Connectors:FailedKeysPerAddress"] = "5" }), services: Install);
        admin = app.Client(Roles.Admin);
        server = await ServerAsync(app, admin, "cx-endpoint");
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
        app.Services.GetRequiredService<ConnectorGuard>().Reset();
        app.Services.GetRequiredService<ConnectorPollGate>().Reset();
    }

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..8]}";

    private static async Task<(ConnectorDetail Connector, ConnectorCallerCreated Caller)> SetupAsync(string name, ConnectorMode mode = ConnectorMode.Wait,
        Action<ConnectorRequest>? change = null, string scriptName = "backup.sh", params string[] networks)
    {
        var connector = await ConnectorAsync(admin, Request(Unique(name), server.Name, scriptName: scriptName, mode: mode), BackupArguments, change: change);
        var caller = await CallerAsync(admin, name, [connector.Id], networks.Length > 0 ? networks : ["10.20.0.0/24"]);
        return (connector, caller);
    }

    [TestMethod]
    public async Task Without_a_valid_key_every_slug_answers_the_same_and_a_key_in_the_query_counts_as_none()
    {
        var (connector, caller) = await SetupAsync("Anonymous");
        foreach (var slug in new[] { connector.Slug, "no-such-connector" })
        {
            var none = await CallAsync(Client(app, null), slug, new { arguments = new { job = "nightly" } });
            Assert.AreEqual(HttpStatusCode.Unauthorized, none.StatusCode);
            Assert.AreEqual("unauthorized", (await JsonAsync(none)).GetProperty("error").GetString());
            var wrong = await CallAsync(Client(app, "pnmt_cx_" + new string('A', 43)), slug, new { arguments = new { job = "nightly" } });
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        var query = await Client(app, null).PostAsync($"/connector/v1/{connector.Slug}?key={caller.Key}", new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, query.StatusCode);
        await using var db = app.NewDb();
        Assert.IsFalse(await db.ConnectorCalls.AnyAsync(c => c.ConnectorId == connector.Id), "calls with a wrong key aren't stored");
    }

    [TestMethod]
    public async Task A_valid_key_from_outside_its_networks_is_refused_stored_audited_and_433()
    {
        var (connector, caller) = await SetupAsync("Outside");
        var response = await CallAsync(Client(app, caller.Key, "192.0.2.77"), connector.Slug, new { arguments = new { job = "nightly" } });
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual("address_refused", (await JsonAsync(response)).GetProperty("error").GetString());
        await using var db = app.NewDb();
        var call = await db.ConnectorCalls.AsNoTracking().SingleAsync(c => c.CallerId == caller.Caller.Id);
        Assert.AreEqual((ConnectorCallState.Refused, "address_refused", "192.0.2.77", connector.Id), (call.State, call.RefusedBy, call.From, call.ConnectorId));
        Assert.IsTrue(await db.AuditLog.AnyAsync(a => a.EventType == AuditEvents.ConnectorRefused && a.Detail!.Contains("192.0.2.77") && a.Actor == AuditActor.Connector));
        var sensor = await db.Sensors.AsNoTracking().SingleAsync(s => s.Type == SensorType.SYSTEM && s.Parameters == SystemSensorKeys.Connectors);
        var last = await db.Events.AsNoTracking().Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).FirstAsync();
        Assert.AreEqual(PNMTStatusCodes.CONNECTORS_REFUSED, last.Code);
        StringAssert.Contains(last.Message, "outside its networks");
        // IPv6 and a single address: /128 lets exactly that one in.
        var v6 = await CallerAsync(admin, "Outside v6", [connector.Id], "2001:db8:20::17");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await CallAsync(Client(app, v6.Key, "2001:db8:20::18"), connector.Slug, new { arguments = new { job = "nightly" } })).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await CallAsync(Client(app, v6.Key, "2001:db8:20::17"), connector.Slug, new { arguments = new { job = "nightly" } })).StatusCode);
    }

    [TestMethod]
    public async Task Wait_mode_answers_the_outcome_and_the_run_is_in_the_history_with_the_connector_as_actor()
    {
        var (connector, caller) = await SetupAsync("Wait mode");
        var response = await CallAsync(Client(app, caller.Key), connector.Slug,
            new { arguments = new { job = "nightly", token = "s3cret-value-1" }, reference = "IMPORT-8812", note = "after the import" });
        var body = await JsonAsync(response);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body.ToString());
        Assert.AreEqual("done", body.GetProperty("state").GetString());
        Assert.AreEqual(0, body.GetProperty("exitCode").GetInt32());
        Assert.IsFalse(body.TryGetProperty("output", out _), "the output stays in PNMT unless the connector returns it (D10)");
        Assert.IsFalse(body.TryGetProperty("reviewed", out _));
        var ticket = body.GetProperty("ticket").GetGuid();

        await using var db = app.NewDb();
        var call = await db.ConnectorCalls.AsNoTracking().SingleAsync(c => c.Id == ticket);
        var run = await db.AgentScriptRuns.AsNoTracking().SingleAsync(r => r.Id == call.RunId);
        Assert.AreEqual(connector.Id, run.ConnectorId);
        Assert.AreEqual($"{caller.Caller.Name} via {connector.Name}", run.RequestedBy);
        Assert.IsNull(run.ScriptId);
        Assert.AreEqual(connector.Target.ScriptRevision, run.VersionNumber);
        StringAssert.Contains(run.Output, "PNMT_JOB=nightly");
        StringAssert.Contains(run.Output, "PNMT_KEEP=3", "the fixed value");
        StringAssert.Contains(run.Output, "PNMT_TOKEN=(14 characters, secret)");
        Assert.AreEqual("s3cret-value-1", server.Simulator.LastSecrets["token"]);
        Assert.IsFalse(call.Arguments!.Contains("s3cret"), "a secret is never stored");
        Assert.IsFalse(run.Arguments?.Contains("s3cret") ?? false);
        CollectionAssert.AreEqual(new[] { "token" }, call.SecureNames);
        Assert.AreEqual("IMPORT-8812", call.Reference);
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EventType == AuditEvents.AgentScriptRun && a.Detail!.Contains(ticket.ToString())).SingleAsync();
        Assert.AreEqual(AuditActor.Connector, audit.Actor);
        Assert.AreEqual($"caller:{caller.Caller.Id}/connector:{connector.Id}", audit.ActorRef);
        Assert.AreEqual(Office, audit.IpAddress);

        // With ReturnOutput on, the output goes back.
        await (await admin.PutJsonAsync($"/api/v1/connectors/{connector.Id}", Update(connector, returnOutput: true))).ReadAsync<ConnectorDetail>();
        var again = await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "weekly" } }));
        StringAssert.Contains(again.GetProperty("output").GetString(), "PNMT_JOB=weekly");
        Assert.IsFalse(again.GetProperty("outputTruncated").GetBoolean());

        // The run's history shows it under the connector.
        var runs = await (await admin.GetAsync($"/api/v1/agents/script-runs?connectorId={connector.Id}")).ReadAsync<Paged<AgentScriptRunItem>>();
        Assert.AreEqual(2, runs.TotalCount);
        Assert.AreEqual(connector.Name, runs.Items[0].Connector?.Name);
    }

    [TestMethod]
    public async Task Every_bad_argument_is_listed_with_the_pattern_and_no_value()
    {
        var (connector, caller) = await SetupAsync("Arguments");
        var response = await CallAsync(Client(app, caller.Key), connector.Slug,
            new { arguments = new { job = "nightly; rm -rf /", keep = 9, Force = true, note = "two\nlines" } });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.AreEqual("arguments_invalid", body.GetProperty("error").GetString());
        Assert.AreEqual("3 arguments are not allowed.", body.GetProperty("message").GetString());
        var fields = body.GetProperty("fields");
        Assert.AreEqual("doesn't match the pattern [a-z]{5,10}", fields.GetProperty("job").GetString());
        Assert.AreEqual("set by PNMT: it can't be passed", fields.GetProperty("keep").GetString());
        Assert.AreEqual("not an argument of this connector", fields.GetProperty("Force").GetString());
        Assert.IsFalse(body.ToString().Contains("rm -rf"));
        // The script's own set, once the pattern passes.
        var set = await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "hourly" } }));
        Assert.AreEqual("job must be one of nightly, weekly, monthly", set.GetProperty("fields").GetProperty("job").GetString());
        // A body that isn't one object, an unknown property, and one too large.
        Assert.AreEqual("invalid_body", (await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, "[1]"))).GetProperty("error").GetString());
        Assert.AreEqual("invalid_body", (await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { argumnts = new { } }))).GetProperty("error").GetString());
        Assert.AreEqual("invalid_body", (await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, """{"note":"a","note":"b"}"""))).GetProperty("error").GetString());
        var large = await CallAsync(Client(app, caller.Key), connector.Slug, new { note = new string('x', 70_000) });
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        await using var db = app.NewDb();
        var refused = await db.ConnectorCalls.AsNoTracking().Where(c => c.ConnectorId == connector.Id).ToListAsync();
        Assert.IsTrue(refused.All(c => c.State == ConnectorCallState.Refused && !c.Accepted));
        Assert.IsTrue(refused.Any(c => c.RefusedBy == "arguments_invalid") && refused.Any(c => c.RefusedBy == "too_large"));
    }

    [TestMethod]
    public async Task Ticket_mode_answers_at_once_and_the_ticket_gives_the_outcome_to_its_caller_only()
    {
        var (connector, caller) = await SetupAsync("Tickets", ConnectorMode.Ticket);
        var response = await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "monthly" } });
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.AreEqual("queued", body.GetProperty("state").GetString());
        var poll = body.GetProperty("poll").GetString()!;
        Assert.AreEqual(poll, response.Headers.Location?.OriginalString);
        var outcome = await Agents.AgentHarness.EventuallyAsync(async () =>
        {
            app.Services.GetRequiredService<ConnectorPollGate>().Reset();
            return await JsonAsync(await Client(app, caller.Key).GetAsync(poll));
        }, b => b.GetProperty("state").GetString() == "done");
        Assert.AreEqual(0, outcome.GetProperty("exitCode").GetInt32());
        await using (var db = app.NewDb())
        {
            Assert.IsNotNull((await db.ConnectorCalls.AsNoTracking().SingleAsync(c => c.Id == body.GetProperty("ticket").GetGuid())).CollectedAt);
        }
        // At most once a second.
        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await Client(app, caller.Key).GetAsync(poll)).StatusCode);
        // Another caller's key: as if the ticket didn't exist.
        var other = await CallerAsync(admin, "Tickets other", [connector.Id], "10.20.0.0/24");
        var foreign = await Client(app, other.Key).GetAsync(poll);
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.AreEqual("unknown_ticket", (await JsonAsync(foreign)).GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task An_idempotency_key_answers_the_first_call_and_runs_once()
    {
        var (connector, caller) = await SetupAsync("Idempotent");
        var first = await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } }, "import-0815"));
        var second = await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } }, "import-0815");
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual(first.GetProperty("ticket").GetGuid(), (await JsonAsync(second)).GetProperty("ticket").GetGuid());
        var mismatch = await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "weekly" } }, "import-0815");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.AreEqual("idempotency_mismatch", (await JsonAsync(mismatch)).GetProperty("error").GetString());
        await using var db = app.NewDb();
        Assert.AreEqual(1, await db.AgentScriptRuns.CountAsync(r => r.ConnectorId == connector.Id));
    }

    [TestMethod]
    public async Task The_limits_answer_429_with_Retry_After()
    {
        var (connector, caller) = await SetupAsync("Limits", change: r => r.MaxCallsPerHour = 2);
        Assert.AreEqual(HttpStatusCode.OK, (await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } })).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" }, dryRun = true })).StatusCode);
        var limited = await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual("rate_limited", (await JsonAsync(limited)).GetProperty("error").GetString());
        Assert.IsTrue(int.Parse(limited.Headers.RetryAfter!.ToString()) is > 3000 and <= 3600);

        // Calls waiting: the queue holds one, and the next is refused.
        var full = await ConnectorAsync(admin, Request(Unique("Queue full"), server.Name, scriptName: "queue-full.sh", mode: ConnectorMode.Ticket), BackupArguments,
            change: r => r.MaxQueued = 1);
        var fullCaller = await CallerAsync(admin, "Queue full", [full.Id], "10.20.0.0/24");
        await using (var db = app.NewDb())
        {
            db.ConnectorCalls.Add(new ConnectorCallEntity
            {
                Id = Guid.NewGuid(), ConnectorId = full.Id, ConnectorName = full.Name, Slug = full.Slug, CallerId = fullCaller.Caller.Id, CallerName = "x",
                ReceivedAt = Lib.Helper.Clock.UtcNow, State = ConnectorCallState.Queued, Accepted = true, WaitingFor = "server_offline",
                ExpiresAt = Lib.Helper.Clock.UtcNow.AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }
        var queueFull = await CallAsync(Client(app, fullCaller.Key), full.Slug, new { arguments = new { job = "nightly" } });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, queueFull.StatusCode);
        Assert.AreEqual("queue_full", (await JsonAsync(queueFull)).GetProperty("error").GetString());
        Assert.AreEqual("10", queueFull.Headers.RetryAfter!.ToString());
    }

    [TestMethod]
    public async Task A_dry_run_checks_and_runs_nothing()
    {
        var (connector, caller) = await SetupAsync("Dry run");
        var response = await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" }, dryRun = true });
        var body = await JsonAsync(response);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body.ToString());
        Assert.AreEqual("checked", body.GetProperty("state").GetString());
        Assert.IsTrue(body.GetProperty("dryRun").GetBoolean());
        StringAssert.Contains(body.GetProperty("message").GetString(), server.Name);
        await using var db = app.NewDb();
        Assert.AreEqual(0, await db.AgentScriptRuns.CountAsync(r => r.ConnectorId == connector.Id));
    }

    [TestMethod]
    public async Task The_target_is_text_resolved_at_each_call()
    {
        var (connector, caller) = await SetupAsync("Target text");
        // The host's name alone finds the server too.
        await (await admin.PutJsonAsync($"/api/v1/connectors/{connector.Id}", Update(connector, server: server.HostName.ToUpperInvariant()))).ReadAsync<ConnectorDetail>();
        Assert.AreEqual("done", (await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } })))
            .GetProperty("state").GetString());
        // A name that finds nothing: refused in the queue, and the caller in wait mode hears it.
        var renamed = await (await admin.PutJsonAsync($"/api/v1/connectors/{connector.Id}", Update(connector, server: "no-such-host / Linux"))).ReadAsync<ConnectorDetail>();
        Assert.IsTrue(renamed.Warnings.Any(w => w.Code == "server_not_found"));
        var body = await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } }));
        Assert.AreEqual(("refused", "server_not_found"), (body.GetProperty("state").GetString(), body.GetProperty("refusedBy").GetString()));
    }

    [TestMethod]
    public async Task A_connector_not_granted_answers_as_one_that_doesnt_exist_and_disabled_ones_say_so()
    {
        var (connector, caller) = await SetupAsync("Granted");
        var (other, _) = await SetupAsync("Not granted");
        var notGranted = await JsonAsync(await CallAsync(Client(app, caller.Key), other.Slug, new { }));
        var missing = await JsonAsync(await CallAsync(Client(app, caller.Key), "no-such-connector", new { }));
        Assert.AreEqual(notGranted.ToString(), missing.ToString());
        Assert.AreEqual("unknown_connector", missing.GetProperty("error").GetString());

        // The list and the description: exactly the caller's connectors, and only the exposed arguments.
        var list = await JsonAsync(await Client(app, caller.Key).GetAsync("/connector/v1"));
        CollectionAssert.AreEqual(new[] { connector.Slug }, list.GetProperty("connectors").EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ToArray());
        var description = await JsonAsync(await Client(app, caller.Key).GetAsync($"/connector/v1/{connector.Slug}"));
        CollectionAssert.AreEqual(new[] { "job", "note", "token" },
            description.GetProperty("arguments").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToArray());
        var job = description.GetProperty("arguments")[0];
        Assert.AreEqual(("[a-z]{5,10}", true, "The job to start."), (job.GetProperty("pattern").GetString(), job.GetProperty("required").GetBoolean(),
            job.GetProperty("help").GetString()));
        Assert.IsFalse(description.ToString().Contains(server.Name), "nothing about the server");

        await (await admin.PutJsonAsync($"/api/v1/connectors/{connector.Id}", Update(connector, enabled: false))).ReadAsync<ConnectorDetail>();
        var disabled = await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } });
        Assert.AreEqual((HttpStatusCode.Forbidden, "connector_disabled"), (disabled.StatusCode, (await JsonAsync(disabled)).GetProperty("error").GetString()));
        var detail = await (await admin.GetAsync($"/api/v1/connector-callers/{caller.Caller.Id}")).ReadAsync<ConnectorCallerDetail>();
        await admin.PutJsonAsync($"/api/v1/connector-callers/{caller.Caller.Id}", new ConnectorCallerRequest
        {
            Name = detail.Name, Enabled = false, Networks = detail.Networks.Select(n => new ConnectorNetworkRequest { Cidr = n.Cidr }).ToList(),
        });
        var callerOff = await CallAsync(Client(app, caller.Key), connector.Slug, new { });
        Assert.AreEqual((HttpStatusCode.Forbidden, "caller_disabled"), (callerOff.StatusCode, (await JsonAsync(callerOff)).GetProperty("error").GetString()));
        // An enabled caller needs a network.
        var noNetwork = await admin.PutJsonAsync($"/api/v1/connector-callers/{caller.Caller.Id}", new ConnectorCallerRequest { Name = detail.Name, Enabled = true });
        Assert.AreEqual(HttpStatusCode.BadRequest, noNetwork.StatusCode);
    }

    [TestMethod]
    public async Task The_key_is_shown_again_audited_and_rotating_it_stops_the_old_one_at_once()
    {
        var (connector, caller) = await SetupAsync("Keys", ConnectorMode.Ticket);
        var shown = await (await admin.GetAsync($"/api/v1/connector-callers/{caller.Caller.Id}/key")).ReadAsync<ConnectorCallerKey>();
        Assert.AreEqual(caller.Key, shown.Key);
        await using (var db = app.NewDb())
        {
            Assert.IsTrue(await db.AuditLog.AnyAsync(a => a.EventType == AuditEvents.ConnectorCallerKeyViewed && a.TargetId == caller.Caller.Id.ToString()));
            var row = await db.ConnectorCallers.AsNoTracking().SingleAsync(c => c.Id == caller.Caller.Id);
            Assert.AreEqual(ConnectorKeys.Hash(caller.Key), row.KeyHash);
            Assert.IsFalse(row.KeyProtected.Contains(caller.Key));
        }
        var poll = (await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } }))).GetProperty("poll").GetString()!;
        var rotated = await (await admin.PostAsync($"/api/v1/connector-callers/{caller.Caller.Id}/key/rotate", null)).ReadAsync<ConnectorCallerKey>();
        Assert.AreNotEqual(caller.Key, rotated.Key);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } })).StatusCode);
        // A ticket from before is fetched with the new key.
        Assert.AreEqual(HttpStatusCode.OK, (await Client(app, rotated.Key).GetAsync(poll)).StatusCode);

        // A lost key ring: the key works by its hash, but can't be shown.
        await using (var db = app.NewDb())
        {
            await db.ConnectorCallers.Where(c => c.Id == caller.Caller.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.KeyProtected, "CfDJ8-gone"));
        }
        var lost = await admin.GetAsync($"/api/v1/connector-callers/{caller.Caller.Id}/key");
        Assert.AreEqual(HttpStatusCode.Conflict, lost.StatusCode);
        Assert.AreEqual("key_lost", (await lost.ProblemAsync()).GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.Accepted, (await CallAsync(Client(app, rotated.Key), connector.Slug, new { arguments = new { job = "nightly" } })).StatusCode);
    }

    [TestMethod]
    public async Task Failed_keys_block_the_address_and_write_433()
    {
        var (connector, caller) = await SetupAsync("Guard");
        var bad = Client(app, "pnmt_cx_" + new string('B', 43), "198.51.100.9");
        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await CallAsync(bad, connector.Slug, new { })).StatusCode);
        }
        var blocked = await CallAsync(bad, connector.Slug, new { });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.AreEqual("blocked", (await JsonAsync(blocked)).GetProperty("error").GetString());
        Assert.IsNotNull(blocked.Headers.RetryAfter);
        // The right key from the blocked address is blocked too; from elsewhere it works.
        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await CallAsync(Client(app, caller.Key, "198.51.100.9"), connector.Slug, new { })).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } })).StatusCode);
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(a => a.EventType == AuditEvents.ConnectorRefused && a.Detail!.Contains("198.51.100.9 is blocked")));
    }

    [TestMethod]
    public async Task The_retention_keeps_the_newest_calls_with_their_runs_and_never_an_active_one()
    {
        var (connector, caller) = await SetupAsync("Retention");
        for (var i = 0; i < 3; i++)
        {
            Assert.AreEqual(HttpStatusCode.OK, (await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } })).StatusCode);
        }
        await using var db = app.NewDb();
        var old = Lib.Helper.Clock.UtcNow.AddYears(-1);
        var active = new ConnectorCallEntity
        {
            Id = Guid.NewGuid(), ConnectorId = connector.Id, ConnectorName = connector.Name, Slug = connector.Slug, CallerId = caller.Caller.Id, CallerName = "x",
            ReceivedAt = old, State = ConnectorCallState.Queued, Accepted = true, ExpiresAt = Lib.Helper.Clock.UtcNow.AddMinutes(10),
        };
        db.ConnectorCalls.Add(active);
        await db.SaveChangesAsync();
        var calls = await db.ConnectorCalls.AsNoTracking().Where(c => c.ConnectorId == connector.Id && c.RunId != null).OrderBy(c => c.ReceivedAt).ToListAsync();
        // The two oldest finished calls become the oldest of all.
        foreach (var (call, i) in calls.Take(2).Select((c, i) => (c, i)))
        {
            await db.ConnectorCalls.Where(c => c.Id == call.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.ReceivedAt, old.AddMinutes(-10 + i)));
        }
        var total = await db.ConnectorCalls.CountAsync();
        var deleted = await ConnectorWorker.RetainAsync(db, total - 2, CancellationToken.None);
        Assert.AreEqual(2, deleted);
        Assert.IsTrue(await db.ConnectorCalls.AnyAsync(c => c.Id == active.Id), "an active call stays, however old");
        Assert.IsFalse(await db.AgentScriptRuns.AnyAsync(r => r.Id == calls[0].RunId || r.Id == calls[1].RunId), "their runs went with them");
        Assert.IsTrue(await db.AgentScriptRuns.AnyAsync(r => r.Id == calls[2].RunId));
        await db.ConnectorCalls.Where(c => c.Id == active.Id).ExecuteDeleteAsync();
    }
}
