using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Connectors;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tests.Agents;
using static PNMTD.Tests.Connectors.ConnectorHarness;

namespace PNMTD.Tests.Connectors;

/// <summary>
/// The worker driven by hand (docs/connectors-plan.md §9, §16), as the advanced sensors' tests drive their evaluator: one run per script and
/// server, oldest first and across connectors; a run from the UI refused while the script runs, and waited for; the limit over all
/// connectors; an offline server; expiry; cancelling; a restart that loses the secrets; runs that never report.
/// </summary>
[TestClass]
public sealed class ConnectorWorkerTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Server server = null!;
    private static ConnectorWorker worker = null!;

    /// <summary>The held runs, by id: a run's result waits until its gate opens.</summary>
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> Gates = new();

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: Settings(new Dictionary<string, string?>
        {
            ["Connectors:Nudge"] = "false",
            ["Connectors:MaxRunningCalls"] = "2",
        }), services: Install);
        admin = app.Client(Roles.Admin);
        server = await ServerAsync(app, admin, "cx-worker",
            configure: s => s.HoldRuns = run => Gates.GetOrAdd(run.RunId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
        worker = app.Services.GetRequiredService<ConnectorWorker>();
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        ReleaseAll();
        await server.DisposeAsync();
        app.Dispose();
    }

    /// <summary>This class's connectors: the shared database has other classes' calls too, whose apps may still be alive.</summary>
    private static readonly ConcurrentBag<Guid> Mine = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        // Nothing of this class waits any more, and every held run ends, so the next test starts with free places.
        var mine = Mine.ToList();
        await using (var cleanup = app.NewDb())
        {
            await cleanup.ConnectorCalls.Where(c => c.State == ConnectorCallState.Queued && c.ConnectorId != null && mine.Contains(c.ConnectorId.Value))
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, ConnectorCallState.Cancelled));
        }
        List<ConnectorCallEntity> running = [];
        for (var i = 0; i < 300; i++)
        {
            ReleaseAll();
            await worker.RunOnceAsync(CancellationToken.None, maintenance: false);
            await using var db = app.NewDb();
            running = await db.ConnectorCalls.AsNoTracking()
                .Where(c => c.State == ConnectorCallState.Running && c.ConnectorId != null && mine.Contains(c.ConnectorId.Value)).ToListAsync();
            if (running.Count == 0)
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"Still running after the cleanup: {string.Join("; ", running.Select(c => $"{c.ConnectorName} {c.Id}, run {c.RunId}"))}");
    }

    private static void ReleaseAll()
    {
        foreach (var gate in Gates.Values)
        {
            gate.TrySetResult();
        }
    }

    private static async Task ReleaseAsync(Guid callId)
    {
        await using var db = app.NewDb();
        var runId = await db.ConnectorCalls.AsNoTracking().Where(c => c.Id == callId).Select(c => c.RunId).SingleAsync();
        Gates.GetOrAdd(runId!.Value, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var check = app.NewDb();
            return await check.AgentScriptRuns.AnyAsync(r => r.Id == runId && r.State == ScriptRunState.Done);
        });
    }

    private static async Task<(ConnectorDetail Connector, ConnectorCallerCreated Caller)> SetupAsync(string name, string scriptName)
    {
        var connector = await ConnectorAsync(admin, Request($"{name} {Guid.NewGuid().ToString("N")[..8]}", server.Name, scriptName: scriptName, mode: ConnectorMode.Ticket),
            BackupArguments);
        Mine.Add(connector.Id);
        var caller = await CallerAsync(admin, name, [connector.Id], "10.20.0.0/24");
        return (connector, caller);
    }

    private static async Task<Guid> CallAsync((ConnectorDetail Connector, ConnectorCallerCreated Caller) c, object? arguments = null)
    {
        var response = await ConnectorHarness.CallAsync(Client(app, c.Caller.Key), c.Connector.Slug, new { arguments = arguments ?? new { job = "nightly" } });
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await JsonAsync(response)).GetProperty("ticket").GetGuid();
    }

    private static async Task<ConnectorCallEntity> CallOf(Guid id)
    {
        await using var db = app.NewDb();
        return await db.ConnectorCalls.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private static Task PassAsync() => worker.RunOnceAsync(CancellationToken.None, maintenance: false);

    [TestMethod]
    public async Task Calls_for_one_script_on_one_server_run_one_after_the_other_oldest_first_across_connectors()
    {
        var a = await SetupAsync("Shared A", "shared.sh");
        var b = await SetupAsync("Shared B", "SHARED.sh");
        var other = await SetupAsync("Other", "other.sh");
        var a1 = await CallAsync(a);
        var b1 = await CallAsync(b);
        var a2 = await CallAsync(a);
        var c1 = await CallAsync(other);
        await PassAsync();
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(a1)).State);
        Assert.AreEqual((ConnectorCallState.Queued, "turn"), ((await CallOf(b1)).State, (await CallOf(b1)).WaitingFor), "the script is its name, ignoring case");
        Assert.AreEqual((ConnectorCallState.Queued, "turn"), ((await CallOf(a2)).State, (await CallOf(a2)).WaitingFor));
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(c1)).State, "another script doesn't wait");

        // Calls shows the place in the line.
        var calls = await (await admin.GetAsync($"/api/v1/connector-calls?active=true&pageSize=500")).ReadAsync<Paged<ConnectorCallItem>>();
        Assert.AreEqual(1, calls.Items.Single(c => c.Id == b1).Place);
        Assert.AreEqual(2, calls.Items.Single(c => c.Id == a2).Place);

        await ReleaseAsync(a1);
        await PassAsync();
        Assert.AreEqual(ConnectorCallState.Done, (await CallOf(a1)).State);
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(b1)).State, "the oldest waiting call goes next, whatever its connector");
        Assert.AreEqual(ConnectorCallState.Queued, (await CallOf(a2)).State);
    }

    [TestMethod]
    public async Task A_run_from_the_UI_is_refused_while_a_connector_runs_the_script_and_a_call_waits_for_a_UI_run()
    {
        var library = await (await admin.PostJsonAsync("/api/v1/agents/scripts", new AgentScriptRequest("ui-shared.sh", null, BackupScript, ScriptLanguage.Bash)))
            .ReadAsync<AgentScriptDetail>();
        var c = await SetupAsync("UI shared", "ui-shared.sh");
        var first = await CallAsync(c);
        await PassAsync();
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(first)).State);
        var run = new AgentScriptRunRequest([server.SensorId], null, new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["job"] = System.Text.Json.JsonSerializer.SerializeToElement("nightly"),
        }, null, null, 60, null);
        var refused = await admin.PostJsonAsync($"/api/v1/agents/scripts/{library.Id}/run", run);
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.ProblemAsync();
        Assert.AreEqual("script_running", problem.GetProperty("code").GetString());
        StringAssert.Contains(problem.GetProperty("detail").GetString(), $"via {c.Connector.Name}");
        await ReleaseAsync(first);
        await PassAsync();

        // Now a run from the UI holds the script, and a call waits for it.
        var ui = (await (await admin.PostJsonAsync($"/api/v1/agents/scripts/{library.Id}/run", run)).ReadAsync<List<AgentScriptRunItem>>()).Single();
        var second = await CallAsync(c);
        await PassAsync();
        Assert.AreEqual((ConnectorCallState.Queued, "turn"), ((await CallOf(second)).State, (await CallOf(second)).WaitingFor));
        Gates.GetOrAdd(ui.Id, _ => new TaskCompletionSource()).TrySetResult();
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.AgentScriptRuns.AnyAsync(r => r.Id == ui.Id && r.State == ScriptRunState.Done);
        });
        await PassAsync();
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(second)).State);
    }

    [TestMethod]
    public async Task At_most_MaxRunningCalls_run_at_once_the_places_go_oldest_first_and_UI_runs_dont_count()
    {
        var d = await SetupAsync("Capacity D", "d.sh");
        var e = await SetupAsync("Capacity E", "e.sh");
        var f = await SetupAsync("Capacity F", "f.sh");
        var d1 = await CallAsync(d);
        var e1 = await CallAsync(e);
        var f1 = await CallAsync(f);
        await PassAsync();
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(d1)).State);
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(e1)).State);
        Assert.AreEqual((ConnectorCallState.Queued, "capacity"), ((await CallOf(f1)).State, (await CallOf(f1)).WaitingFor));
        var figures = await (await admin.GetAsync("/api/v1/connector-calls/figures")).ReadAsync<ConnectorCallFigures>();
        Assert.AreEqual((2, 2), (figures.Running, figures.MaxRunning));

        // A run from the UI neither counts nor waits.
        var library = await (await admin.PostJsonAsync("/api/v1/agents/scripts", new AgentScriptRequest("g.sh", null, BackupScript, ScriptLanguage.Bash)))
            .ReadAsync<AgentScriptDetail>();
        var ui = await admin.PostJsonAsync($"/api/v1/agents/scripts/{library.Id}/run", new AgentScriptRunRequest([server.SensorId], null,
            new Dictionary<string, System.Text.Json.JsonElement> { ["job"] = System.Text.Json.JsonSerializer.SerializeToElement("weekly") }, null, null, 60, null));
        Assert.AreEqual(HttpStatusCode.OK, ui.StatusCode);

        await ReleaseAsync(d1);
        await PassAsync();
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(f1)).State);
    }

    [TestMethod]
    public async Task An_offline_server_keeps_the_call_queued_until_its_agent_connects()
    {
        await using var lonely = await ServerAsync(app, admin, "cx-offline");
        var connector = await ConnectorAsync(admin, Request($"Offline {Guid.NewGuid().ToString("N")[..8]}", lonely.Name, mode: ConnectorMode.Ticket), BackupArguments);
        Mine.Add(connector.Id);
        var caller = await CallerAsync(admin, "Offline", [connector.Id], "10.20.0.0/24");
        await lonely.Stop.CancelAsync();
        try
        {
            await lonely.Running;
        }
        catch (OperationCanceledException)
        {
        }
        var hub = app.Services.GetRequiredService<PNMTD.Agents.AgentHub>();
        await AgentHarness.EventuallyAsync(() => Task.FromResult(hub.Connection(lonely.SensorId) is null));
        var call = await CallAsync((connector, caller));
        await PassAsync();
        Assert.AreEqual((ConnectorCallState.Queued, "server_offline"), ((await CallOf(call)).State, (await CallOf(call)).WaitingFor));

        using var again = new CancellationTokenSource();
        var running = lonely.Simulator.RunAsync(lonely.State, again.Token, TimeSpan.FromSeconds(30));
        await AgentHarness.EventuallyAsync(() => Task.FromResult(hub.Connection(lonely.SensorId) is not null));
        await PassAsync();
        Assert.AreEqual(ConnectorCallState.Done, (await AgentHarness.EventuallyAsync(async () =>
        {
            await PassAsync();
            return await CallOf(call);
        }, c => c.State != ConnectorCallState.Queued && c.State != ConnectorCallState.Running)).State);
        await again.CancelAsync();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public async Task A_call_past_its_waiting_time_expires_with_what_it_waited_for()
    {
        var c = await SetupAsync("Expiry", "expiry.sh");
        var first = await CallAsync(c);
        var second = await CallAsync(c);
        await PassAsync();
        await using (var db = app.NewDb())
        {
            await db.ConnectorCalls.Where(x => x.Id == second).ExecuteUpdateAsync(u => u.SetProperty(x => x.ExpiresAt, Clock.UtcNow.AddSeconds(-1)));
        }
        await PassAsync();
        var expired = await CallOf(second);
        Assert.AreEqual(ConnectorCallState.Expired, expired.State);
        StringAssert.Contains(expired.Message, "waiting for its turn");
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(first)).State);
    }

    [TestMethod]
    public async Task A_queued_call_can_be_cancelled_a_running_one_cant_and_disabling_cancels_the_queue()
    {
        var c = await SetupAsync("Cancel", "cancel.sh");
        var first = await CallAsync(c);
        var second = await CallAsync(c);
        var third = await CallAsync(c);
        await PassAsync();
        var running = await admin.PostAsync($"/api/v1/connector-calls/{first}/cancel", null);
        Assert.AreEqual(HttpStatusCode.Conflict, running.StatusCode);
        Assert.AreEqual("call_running", (await running.ProblemAsync()).GetProperty("code").GetString());
        var cancelled = await (await admin.PostAsync($"/api/v1/connector-calls/{second}/cancel", null)).ReadAsync<ConnectorCallDetail>();
        Assert.AreEqual(ConnectorCallState.Cancelled, cancelled.Call.State);
        await (await admin.PutJsonAsync($"/api/v1/connectors/{c.Connector.Id}", Update(c.Connector, enabled: false))).ReadAsync<ConnectorDetail>();
        Assert.AreEqual(ConnectorCallState.Cancelled, (await CallOf(third)).State);
        StringAssert.Contains((await CallOf(third)).Message, "disabled");
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(first)).State, "a running call goes on");
    }

    [TestMethod]
    public async Task After_a_restart_queued_calls_go_on_and_one_whose_secrets_are_gone_expires()
    {
        var c = await SetupAsync("Restart", "restart.sh");
        var first = await CallAsync(c);
        var plain = await CallAsync(c);
        var secret = await CallAsync(c, new { job = "weekly", token = "abc-123-xyz" });
        await PassAsync();
        // A restart: the memory is empty, the table is not.
        app.Services.GetRequiredService<ConnectorSecretVault>().Clear();
        await PassAsync();
        var expired = await CallOf(secret);
        Assert.AreEqual((ConnectorCallState.Expired, ConnectorWorker.RestartMessage), (expired.State, expired.Message));
        Assert.AreEqual(ConnectorCallState.Queued, (await CallOf(plain)).State);
        await ReleaseAsync(first);
        await PassAsync();
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(plain)).State);
    }

    [TestMethod]
    public async Task A_run_past_its_timeout_and_five_minutes_is_closed_as_lost_and_a_late_result_still_wins()
    {
        var c = await SetupAsync("Lost", "lost.sh");
        var call = await CallAsync(c);
        await PassAsync();
        var runId = (await CallOf(call)).RunId!.Value;
        await using (var db = app.NewDb())
        {
            await db.AgentScriptRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u.SetProperty(r => r.StartedAt, Clock.UtcNow.AddHours(-1)));
        }
        await worker.RunOnceAsync(CancellationToken.None, maintenance: true);
        var lost = await CallOf(call);
        Assert.AreEqual((ConnectorCallState.Lost, ConnectorWorker.LostMessage), (lost.State, lost.Message));
        await using (var db = app.NewDb())
        {
            var run = await db.AgentScriptRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
            Assert.AreEqual((ScriptRunState.Failed, ConnectorWorker.LostMessage), (run.State, run.Message));
        }
        // The script's turn is free again.
        var next = await CallAsync(c);
        await PassAsync();
        Assert.AreEqual(ConnectorCallState.Running, (await CallOf(next)).State);
        // The late result overwrites the run, as any result does.
        await ReleaseAsync(call);
    }
}
