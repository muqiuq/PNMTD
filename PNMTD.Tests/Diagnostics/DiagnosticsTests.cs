using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Cli;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Tests.Agents;

namespace PNMTD.Tests.Diagnostics;

/// <summary>
/// Diagnostics (docs/windows-sensor-plan.md §5, §13): the caps, an assignment with its sensor and the agent's configuration,
/// Run now through the simulator with 252 and 452, and deleting (a set refuses while assigned).
/// </summary>
[TestClass]
public sealed class DiagnosticsTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Agents:JoinRateLimitPerMinute"] = "1000",
            ["Agents:AgentDownloadBaseUrl"] = "http://127.0.0.1:9/releases/download",
        });
        admin = app.Client(Roles.Admin);
        hostId = await AgentHarness.CreateHostAsync(admin, "diagnostics-tests");
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            await db.Hosts.Where(h => h.Id == hostId).ExecuteUpdateAsync(u => u.SetProperty(h => h.Enabled, false));
        }
        app.Dispose();
    }

    private static DiagnosticSetRequest Set(string name, params DiagnosticCheckRequest[] checks) => new(name, "test", checks);

    private static DiagnosticCheckRequest Ping(string name, string target, int count = 2) =>
        new(name, DiagnosticCheckType.Ping, new PingParameters(target, count, 1000), null);

    private static DiagnosticCheckRequest Dns(string name, string query, string? server = null) =>
        new(name, DiagnosticCheckType.Dns, null, new DnsParameters(query, DnsRecordKind.A, server));

    [TestMethod]
    public async Task ChecksAreCappedSoASetCantFloodATarget()
    {
        async Task<HttpStatusCode> Create(DiagnosticSetRequest request) => (await admin.PostJsonAsync("/api/v1/diagnostics/sets", request)).StatusCode;
        Assert.AreEqual(HttpStatusCode.BadRequest, await Create(Set($"too-many-{Guid.NewGuid():N}", Ping("gw", "10.0.0.1", count: 11))));
        Assert.AreEqual(HttpStatusCode.BadRequest, await Create(Set($"bad-target-{Guid.NewGuid():N}", Ping("gw", "not a host!"))));
        Assert.AreEqual(HttpStatusCode.BadRequest, await Create(Set($"bad-server-{Guid.NewGuid():N}", Dns("intranet", "intranet.example", server: "dns.example"))));
        Assert.AreEqual(HttpStatusCode.BadRequest, await Create(Set($"empty-{Guid.NewGuid():N}")));
        Assert.AreEqual(HttpStatusCode.OK, await Create(Set($"fine-{Guid.NewGuid():N}", Ping("gw", "gw.example"), Dns("intranet", "intranet.example", "10.0.0.53"))));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.User).PostJsonAsync("/api/v1/diagnostics/sets", Set("user", Ping("gw", "10.0.0.1")))).StatusCode);
    }

    [TestMethod]
    public async Task AnAssignmentRunsOnTheAgentAndWrites252Or452()
    {
        var set = await (await admin.PostJsonAsync("/api/v1/diagnostics/sets", Set($"branch {Guid.NewGuid():N}", Ping("gw-zurich", "10.0.1.1"),
            Dns("intranet", "intranet.example")))).ReadAsync<DiagnosticSetDetail>();
        var created = await AgentHarness.TokenAsync(admin, hostId, "diag server");
        var simulator = new AgentSimulator(new Uri("http://localhost/"), app.Client(), ct => AgentHarness.ConnectAsync(app, ct), TextWriter.Null);
        var state = await simulator.JoinAsync(created.Token, null, "SIM-DIAG", CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var running = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        await AgentHarness.EventuallyAsync(() => Task.FromResult(app.Services.GetRequiredService<AgentHub>().IsConnected(state.SensorId)));

        // Assigning creates the sensor and sends the agent its new configuration.
        var assignments = await (await admin.PostJsonAsync("/api/v1/diagnostics/assignments",
            new DiagnosticAssignmentRequest(set.Id, [state.SensorId], DiagnosticMode.Scheduled, 15, true))).ReadAsync<List<DiagnosticAssignmentItem>>();
        var assignment = assignments.Single();
        await using (var db = app.NewDb())
        {
            var sensor = await db.Sensors.SingleAsync(s => s.Id == assignment.Sensor.Id);
            Assert.AreEqual(SensorType.DIAGNOSTICS, sensor.Type);
            Assert.AreEqual($"Diagnostics: {set.Name}", sensor.Name);
            Assert.AreEqual(hostId, sensor.ParentId);
            Assert.AreEqual(15 * 60, sensor.Interval);
        }
        await AgentHarness.EventuallyAsync(() => Task.FromResult(simulator.LastConfig?.Diagnostics.Any(a => a.AssignmentId == assignment.Id) == true));
        Assert.AreEqual(2, simulator.LastConfig!.Diagnostics.Single(a => a.AssignmentId == assignment.Id).Checks.Count);

        // Run now: all pass → 252.
        var run = await (await app.Client(Roles.User).PostJsonAsync($"/api/v1/diagnostics/assignments/{assignment.Id}/run", new { })).ReadAsync<DiagnosticRunItem>();
        Assert.AreEqual("manual", run.Trigger);
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, assignment.Sensor.Id), e => e.Count >= 1);
        Assert.AreEqual((PNMTStatusCodes.DIAGNOSTICS_OK, "All 2 checks passed"), (events[0].Code, events[0].Message));
        var detail = await (await admin.GetAsync($"/api/v1/diagnostics/runs/{run.Id}")).ReadAsync<DiagnosticRunDetail>();
        Assert.AreEqual(DiagnosticOutcome.Pass, detail.Run.Outcome);
        Assert.AreEqual(2, detail.Results.Count);

        // A failing check → 452 naming it.
        SimulatedSections.FailingChecks.Add("gw-zurich");
        try
        {
            await (await app.Client(Roles.User).PostJsonAsync($"/api/v1/diagnostics/assignments/{assignment.Id}/run", new { })).ReadAsync<DiagnosticRunItem>();
            events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, assignment.Sensor.Id), e => e[0].Code == PNMTStatusCodes.DIAGNOSTICS_FAILED);
            Assert.AreEqual("1 of 2 failed: ping gw-zurich (100 % loss)", events[0].Message);
        }
        finally
        {
            SimulatedSections.FailingChecks.Clear();
        }
        var servers = await (await admin.GetAsync("/api/v1/agents/servers?search=SIM-DIAG")).ReadAsync<Paged<AgentServerItem>>();
        Assert.AreEqual(1, servers.Items.Single().DiagnosticsFailing);

        // The set can't go while assigned; the assignment goes with its sensor.
        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/diagnostics/sets/{set.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/diagnostics/assignments/{assignment.Id}")).StatusCode);
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.Sensors.AnyAsync(s => s.Id == assignment.Sensor.Id));
            Assert.IsFalse(await db.DiagnosticRuns.AnyAsync(r => r.AssignmentId == assignment.Id));
        }
        await AgentHarness.EventuallyAsync(() => Task.FromResult(simulator.LastConfig?.Diagnostics.All(a => a.AssignmentId != assignment.Id) == true));
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/diagnostics/sets/{set.Id}")).StatusCode);

        // Offline: Run now is a 409.
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public void TheSummaryNamesTheFailedChecks()
    {
        var ok = new DiagnosticCheckResult(Guid.NewGuid(), "gw", DiagnosticCheckType.Ping, DiagnosticOutcome.Pass, "0 % loss");
        var bad = new DiagnosticCheckResult(Guid.NewGuid(), "intranet.example", DiagnosticCheckType.Dns, DiagnosticOutcome.Fail, "NXDOMAIN");
        Assert.AreEqual("1 of 2 failed: dns intranet.example (NXDOMAIN)", PNMTD.Diagnostics.DiagnosticsRunner.Summarize([ok, bad]).Summary);
        Assert.IsTrue(PNMTD.Diagnostics.DiagnosticsRunner.Summarize([ok]).Ok);
    }
}
