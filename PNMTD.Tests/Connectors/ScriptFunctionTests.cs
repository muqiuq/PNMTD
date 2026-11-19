using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;
using static PNMTD.Tests.Connectors.ConnectorHarness;

namespace PNMTD.Tests.Connectors;

/// <summary>
/// Functions in scripts (docs/connectors-plan.md §8, §16): bash's header declares them, with findings; the arguments are checked against a
/// function's parameters; PNMTD sends a function only to an agent that can call one; a connector and the run dialog call one.
/// </summary>
[TestClass]
public sealed class ScriptFunctionTests
{
    public const string BackupTools = """
        #!/usr/bin/env bash
        # pnmt:
        #   synopsis: Tools for the backup server
        #   functions:
        #     start_job:
        #       synopsis: Starts a backup job now
        #       parameters:
        #         job: { type: string, mandatory: true, set: [nightly, weekly] }
        #     job_status:
        #       synopsis: Prints a job's last result as JSON
        #       parameters:
        #         job: { type: string, mandatory: true }
        set -euo pipefail

        start_job()  { systemctl start "backup-${PNMT_JOB}.service"; echo "started ${PNMT_JOB}"; }
        job_status() { systemctl show "backup-${PNMT_JOB}.service" -p Result --value; }

        if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
          echo "Call a function: start_job or job_status" >&2
          exit 2
        fi
        """;

    private static TestApp app = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: Settings(), services: Install);
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public void Bashs_header_declares_functions_with_their_parameters_and_the_findings()
    {
        var parsed = BashScriptHeader.Parse(BackupTools);
        Assert.AreEqual(0, parsed.Errors.Count, string.Join("; ", parsed.Errors.Select(e => e.Message)));
        CollectionAssert.AreEqual(new[] { "start_job", "job_status" }, parsed.Functions!.Select(f => f.Name).ToArray());
        Assert.AreEqual(0, parsed.Parameters.Count);
        var start = parsed.Function("start_job")!;
        Assert.AreEqual("Starts a backup job now", start.Help?.Synopsis);
        CollectionAssert.AreEqual(new[] { "nightly", "weekly" }, start.Parameters.Single().ValidateSet!.ToArray());
        Assert.IsFalse(parsed.Findings.Any(f => f.Rule.StartsWith("function-", StringComparison.Ordinal)), string.Join("; ", parsed.Findings.Select(f => f.Message)));
        Assert.IsFalse(parsed.Findings.Any(f => f.Rule == "Unused"), "a function's parameters count as used where the function uses them");

        var unguarded = BashScriptHeader.Parse(BackupTools.Replace("job_status() {", "other() {").Replace("""if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then""", "if true; then"));
        Assert.IsTrue(unguarded.Findings.Any(f => f.Rule == "function-not-defined" && f.Message.Contains("job_status")));
        Assert.IsTrue(unguarded.Findings.Any(f => f.Rule == "function-no-guard"));
        var bad = BashScriptHeader.Parse(BackupTools.Replace("#     job_status:", "#     Job-Status:"));
        Assert.IsTrue(bad.Errors.Any(e => e.Message.Contains("not a function name")));
        Assert.AreEqual(0, BashScriptHeader.Parse(BashScriptHeader.Template).Functions!.Count, "bash always lists its functions, none here");
    }

    [TestMethod]
    public void The_arguments_are_checked_against_the_functions_parameters()
    {
        var parsed = BashScriptHeader.Parse(BackupTools);
        var job = new Dictionary<string, JsonElement> { ["job"] = JsonSerializer.SerializeToElement("monthly") };
        Assert.AreEqual("job must be one of nightly, weekly.", ScriptArguments.Check(parsed, "start_job", job, []).Single().Message);
        Assert.AreEqual(0, ScriptArguments.Check(parsed, "job_status", job, []).Count());
        Assert.AreEqual("The script has no parameter job.", ScriptArguments.Check(parsed, job, []).Single().Message, "the whole script has no job");
        Assert.AreEqual("function", ScriptArguments.Check(parsed, "restore", job, []).Single().Field);
    }

    [TestMethod]
    public async Task A_connector_calls_a_function_and_an_agent_that_cant_is_never_sent_one()
    {
        await using var capable = await ServerAsync(app, admin, "cx-fn");
        await using var old = await ServerAsync(app, admin, "cx-fn-old", configure: s => s.NoFunctions = true);
        var request = Request($"Function {Guid.NewGuid().ToString("N")[..6]}", capable.Name, BackupTools, "backup-tools.sh", function: "start_job");
        var connector = await ConnectorAsync(admin, request, [new() { Name = "job", Mode = ConnectorArgumentMode.Exposed, Required = true }]);
        CollectionAssert.AreEqual(new[] { "job" }, connector.Arguments.Select(a => a.Name).ToArray(), "the function's parameters, not the script's");
        Assert.AreEqual($"Runs start_job in backup-tools.sh (revision 1) on {capable.Name} as root (the service)", connector.Target.Sentence);
        var caller = await CallerAsync(admin, "Function", [connector.Id], "10.20.0.0/24");
        var body = await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "weekly" } }));
        Assert.AreEqual("done", body.GetProperty("state").GetString(), body.ToString());
        await using (var db = app.NewDb())
        {
            var run = await db.AgentScriptRuns.AsNoTracking().SingleAsync(r => r.ConnectorId == connector.Id);
            Assert.AreEqual("start_job", run.Function);
            StringAssert.StartsWith(run.Output, "ran start_job");
        }
        Assert.AreEqual(422, (int)(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "monthly" } })).StatusCode);

        // The same connector on the older agent: refused before anything is sent, and the warning says so.
        var moved = await (await admin.PutJsonAsync($"/api/v1/connectors/{connector.Id}", Update(connector, server: old.Name))).ReadAsync<ConnectorDetail>();
        Assert.IsTrue(moved.Warnings.Any(w => w.Code == "agent_too_old"));
        var refused = await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "weekly" } }));
        Assert.AreEqual(("refused", "agent_too_old"), (refused.GetProperty("state").GetString(), refused.GetProperty("refusedBy").GetString()));
        Assert.IsFalse(old.Simulator.Requests.Contains(AgentMessageTypes.RunScript), "nothing was sent to the older agent");

        // The run dialog calls a function too, and refuses one for an older agent's server.
        var library = await (await admin.PostJsonAsync("/api/v1/agents/scripts", new AgentScriptRequest($"tools-{Guid.NewGuid():N}.sh", null, BackupTools, ScriptLanguage.Bash)))
            .ReadAsync<AgentScriptDetail>();
        var arguments = new Dictionary<string, JsonElement> { ["job"] = JsonSerializer.SerializeToElement("nightly") };
        var runs = await (await admin.PostJsonAsync($"/api/v1/agents/scripts/{library.Id}/run",
            new AgentScriptRunRequest([capable.SensorId, old.SensorId], null, arguments, null, null, 60, null, Function: "start_job"))).ReadAsync<List<AgentScriptRunItem>>();
        Assert.AreEqual("start_job", runs.Single(r => r.Server!.Id == capable.SensorId).Function);
        var oldRun = runs.Single(r => r.Server!.Id == old.SensorId);
        Assert.AreEqual(ScriptRunState.Refused, oldRun.State);
        StringAssert.Contains(oldRun.Message, "can't call functions");
        var unknown = await admin.PostJsonAsync($"/api/v1/agents/scripts/{library.Id}/run",
            new AgentScriptRunRequest([capable.SensorId], null, arguments, null, null, 60, null, Function: "restore"));
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [TestMethod]
    public void The_simulators_parser_lists_PowerShell_functions_at_the_top_level()
    {
        var parsed = PNMTD.Cli.SimulatedParser.Parse("""
            [CmdletBinding()]
            param()
            function Clear-ShopCache {
                [CmdletBinding()]
                param([Parameter(Mandatory = $true)] [string] $Site, [ValidateSet('Pages', 'All')] [string] $Scope = 'Pages')
            }
            function Restart-ShopPool { param([Parameter(Mandatory = $true)] [string] $Site) }
            """);
        Assert.AreEqual(0, parsed.Parameters.Count);
        CollectionAssert.AreEqual(new[] { "Clear-ShopCache", "Restart-ShopPool" }, parsed.Functions!.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "Site", "Scope" }, parsed.Function("clear-shopcache")!.Parameters.Select(p => p.Name).ToArray());
    }
}
