using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Connectors;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;
using PNMTD.Tests.Agents;
using static PNMTD.Tests.Connectors.ConnectorHarness;

namespace PNMTD.Tests.Connectors;

/// <summary>
/// A connector's own text (docs/connectors-plan.md §3.3, §5.5, §16): a PowerShell parse that arrives later reaches the connector by its
/// hash and calls wait for it; a changed text reconciles the arguments and may need an admin; the revision follows the text; a library
/// script deleted after the connector took its text changes nothing.
/// </summary>
[TestClass]
public sealed class ConnectorTextTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Server linux = null!;

    private const string ShopTools = """
        <#
        .SYNOPSIS
            Tools for the shop's web servers.
        #>
        [CmdletBinding()]
        param(
            [string] $Site = 'shop-de',
            [ValidateSet('Pages', 'Images', 'All')] [string] $Scope = 'Pages'
        )
        Write-Output "Flushing $Site ($Scope)"
        """;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: Settings(), services: Install);
        admin = app.Client(Roles.Admin);
        linux = await ServerAsync(app, admin, "cx-text");
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await linux.DisposeAsync();
        app.Dispose();
    }

    [TestMethod]
    public async Task A_PowerShell_parse_that_arrives_later_reaches_the_connector_and_its_calls_wait_for_it()
    {
        // A Windows server that joined and went offline: nobody parses the new text until it is back.
        var windows = await ServerAsync(app, admin, "cx-ps", AgentPlatform.Windows);
        await windows.Stop.CancelAsync();
        try
        {
            await windows.Running;
        }
        catch (OperationCanceledException)
        {
        }
        var hub = app.Services.GetRequiredService<PNMTD.Agents.AgentHub>();
        await AgentHarness.EventuallyAsync(() => Task.FromResult(hub.Connection(windows.SensorId) is null));
        var text = ShopTools.Replace("Flushing", $"Flushing {Guid.NewGuid():N}");
        var connector = await ConnectorAsync(admin, Request($"PS later {Guid.NewGuid().ToString("N")[..6]}", windows.Name, text, "Shop-Tools.ps1",
            ConnectorType.WindowsScript, ConnectorMode.Ticket), enable: true);
        Assert.AreEqual(ScriptParseState.Pending, connector.Target.ParseState);
        Assert.IsTrue(connector.Warnings.Any(w => w.Code == "parameters_pending"));
        Assert.AreEqual(0, connector.Arguments.Count);
        var caller = await CallerAsync(admin, "PS later", [connector.Id], "10.20.0.0/24");
        var ticket = (await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { }))).GetProperty("ticket").GetGuid();
        var waiting = await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.ConnectorCalls.AsNoTracking().SingleAsync(c => c.Id == ticket);
        }, c => c.WaitingFor == "parameters_pending");
        Assert.AreEqual(ConnectorCallState.Queued, waiting.State);

        using var stop = new CancellationTokenSource();
        var running = windows.Simulator.RunAsync(windows.State, stop.Token, TimeSpan.FromSeconds(30));
        try
        {
            var parsed = await AgentHarness.EventuallyAsync(async () => await (await admin.GetAsync($"/api/v1/connectors/{connector.Id}")).ReadAsync<ConnectorDetail>(),
                c => c.Target.ParseState == ScriptParseState.Parsed);
            CollectionAssert.AreEqual(new[] { "Site", "Scope" }, parsed.Arguments.Select(a => a.Name).ToArray());
            Assert.IsTrue(parsed.Arguments.All(a => a.Mode == ConnectorArgumentMode.Omitted), "optional parameters start omitted");
            var done = await AgentHarness.EventuallyAsync(async () =>
            {
                await using var db = app.NewDb();
                return await db.ConnectorCalls.AsNoTracking().SingleAsync(c => c.Id == ticket);
            }, c => !ConnectorGateway.IsActive(c.State), 20);
            Assert.AreEqual(ConnectorCallState.Done, done.State, done.Message);
            Assert.AreEqual(windows.SensorId, done.ServerSensorId);
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [TestMethod]
    public async Task A_changed_text_reconciles_the_arguments_and_a_choice_that_no_longer_fits_waits_for_an_admin()
    {
        var connector = await ConnectorAsync(admin, Request($"Text change {Guid.NewGuid().ToString("N")[..6]}", linux.Name, scriptName: "text-change.sh"),
        [
            new() { Name = "job", Mode = ConnectorArgumentMode.Fixed, FixedValue = JsonSerializer.SerializeToElement("monthly") },
            new() { Name = "note", Mode = ConnectorArgumentMode.Exposed, Pattern = "[a-z ]+" },
            new() { Name = "keep", Mode = ConnectorArgumentMode.Omitted },
            new() { Name = "dry_run", Mode = ConnectorArgumentMode.Omitted },
            new() { Name = "token", Mode = ConnectorArgumentMode.Omitted },
        ]);
        Assert.AreEqual(1, connector.Target.ScriptRevision);
        var caller = await CallerAsync(admin, "Text change", [connector.Id], "10.20.0.0/24");
        // monthly leaves the set; keep goes; region comes, mandatory.
        var text = BackupScript
            .Replace("set: [nightly, weekly, monthly]", "set: [nightly, weekly]")
            .Replace("#     keep:    { type: int, default: 7, range: [1, 90] }\n", "#     region:  { type: string, mandatory: true }\n");
        var changed = await (await admin.PutJsonAsync($"/api/v1/connectors/{connector.Id}", Update(connector, text: text))).ReadAsync<ConnectorDetail>();
        Assert.AreEqual(2, changed.Target.ScriptRevision);
        Assert.IsTrue(changed.Target.NeedsReview);
        Assert.IsTrue(changed.Warnings.Any(w => w.Code == "arguments_need_review"));
        CollectionAssert.AreEquivalent(new[] { "job", "note", "region", "dry_run", "token" }, changed.Arguments.Select(a => a.Name).ToArray());
        Assert.AreEqual("[a-z ]+", changed.Arguments.Single(a => a.Name == "note").Pattern, "a kept choice stays");
        Assert.AreEqual(ConnectorArgumentMode.Exposed, changed.Arguments.Single(a => a.Name == "region").Mode);
        StringAssert.Contains(changed.Arguments.Single(a => a.Name == "job").Misfit, "must be one of nightly, weekly");
        var refused = await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { region = "eu" } });
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.AreEqual("arguments_need_review", (await JsonAsync(refused)).GetProperty("error").GetString());

        // Saving the arguments again clears it; the run carries the new revision and hash.
        var fixedUp = await (await admin.PutJsonAsync($"/api/v1/connectors/{connector.Id}", Update(changed, arguments:
        [
            new() { Name = "job", Mode = ConnectorArgumentMode.Fixed, FixedValue = JsonSerializer.SerializeToElement("weekly") },
            new() { Name = "note", Mode = ConnectorArgumentMode.Exposed, Pattern = "[a-z ]+" },
            new() { Name = "region", Mode = ConnectorArgumentMode.Exposed, Required = true },
            new() { Name = "dry_run", Mode = ConnectorArgumentMode.Omitted },
            new() { Name = "token", Mode = ConnectorArgumentMode.Omitted },
        ]))).ReadAsync<ConnectorDetail>();
        Assert.IsFalse(fixedUp.Target.NeedsReview);
        var done = await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { region = "eu" } }));
        Assert.AreEqual("done", done.GetProperty("state").GetString(), done.ToString());
        await using var db = app.NewDb();
        var run = await db.AgentScriptRuns.AsNoTracking().SingleAsync(r => r.ConnectorId == connector.Id);
        Assert.AreEqual((2, ScriptHashes.Of(text)), (run.VersionNumber, run.Hash));
        StringAssert.Contains(run.Output, "PNMT_JOB=weekly");
        // A fixed value outside the set is refused at the save already.
        var invalid = await admin.PutJsonAsync($"/api/v1/connectors/{connector.Id}", Update(fixedUp, arguments:
            [new() { Name = "job", Mode = ConnectorArgumentMode.Fixed, FixedValue = JsonSerializer.SerializeToElement("monthly") }]));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [TestMethod]
    public async Task A_text_taken_from_the_library_stays_the_connectors_when_the_library_script_goes()
    {
        var library = await (await admin.PostJsonAsync("/api/v1/agents/scripts",
            new AgentScriptRequest($"lib-{Guid.NewGuid():N}.sh", null, BackupScript.Replace("started", "library started"), ScriptLanguage.Bash))).ReadAsync<AgentScriptDetail>();
        var version = await (await admin.GetAsync($"/api/v1/connectors/lookups/library/{library.Id}/versions/1")).ReadAsync<ConnectorLibraryVersion>();
        Assert.AreEqual($"library: {library.Name} v1", version.TakenFrom);
        var request = Request($"From library {Guid.NewGuid().ToString("N")[..6]}", linux.Name, version.Text, library.Name);
        request.Target = new ConnectorTargetRequest
        {
            Server = linux.Name, ScriptName = library.Name, ScriptText = version.Text, TakenFrom = version.TakenFrom, RunAsKind = RunAsKind.Service, TimeoutSeconds = 60,
        };
        var connector = await ConnectorAsync(admin, request, BackupArguments);
        Assert.IsFalse(connector.Library!.Differs);
        var caller = await CallerAsync(admin, "From library", [connector.Id], "10.20.0.0/24");

        // A new library version: the connector says so, and changes nothing until someone takes it.
        await (await admin.PutJsonAsync($"/api/v1/agents/scripts/{library.Id}",
            new AgentScriptRequest(library.Name, null, BackupScript.Replace("started", "v2 started"), ScriptLanguage.Bash))).ReadAsync<AgentScriptDetail>();
        var hint = await (await admin.GetAsync($"/api/v1/connectors/{connector.Id}")).ReadAsync<ConnectorDetail>();
        Assert.AreEqual((2, true), (hint.Library!.Version, hint.Library.Differs));
        var compare = await (await admin.GetAsync($"/api/v1/connectors/{connector.Id}/compare")).ReadAsync<ConnectorCompare>();
        StringAssert.Contains(compare.LibraryText, "v2 started");
        StringAssert.Contains(compare.ConnectorText, "library started");

        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/agents/scripts/{library.Id}")).StatusCode);
        var body = await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "weekly" } }));
        Assert.AreEqual("done", body.GetProperty("state").GetString());
        await using var db = app.NewDb();
        StringAssert.Contains((await db.AgentScriptRuns.AsNoTracking().SingleAsync(r => r.ConnectorId == connector.Id)).Output, "PNMT_JOB=weekly");
    }
}
