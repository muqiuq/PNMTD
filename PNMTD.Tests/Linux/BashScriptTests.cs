using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Cli;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;
using PNMTD.Tests.Agents;

namespace PNMTD.Tests.Linux;

/// <summary>
/// Bash scripts on PNMTD (docs/linux-sensor-plan.md §8, §14, L7): the language in the library and its template, the header read at the
/// save so the parameters are there at once, a Linux agent's syntax check merged in, arguments refused against the header, runs only on
/// servers of the script's platform, Linux's run-as (root or an account, refused with config.yaml's line where the rights refuse it),
/// and a secret arriving decrypted at the agent and stored nowhere.
/// </summary>
[TestClass]
public sealed class BashScriptTests
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
            ["Agents:HelloTimeoutSeconds"] = "2",
        });
        admin = app.Client(Roles.Admin);
        hostId = await AgentHarness.CreateHostAsync(admin, "bash-tests");
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

    private static Task<HttpResponseMessage> CreateAsync(string name, string text, ScriptLanguage? language = ScriptLanguage.Bash) =>
        admin.PostJsonAsync("/api/v1/agents/scripts", new AgentScriptRequest($"{name} {Guid.NewGuid():N}", null, text, language));

    [TestMethod]
    public async Task ABashScriptsHeaderGivesItsParametersAtTheSave()
    {
        var template = await (await admin.GetAsync("/api/v1/agents/scripts/template?language=Bash")).ReadAsync<AgentScriptTemplate>();
        Assert.AreEqual(BashScriptHeader.Template, template.Text);
        StringAssert.Contains((await (await admin.GetAsync("/api/v1/agents/scripts/template")).ReadAsync<AgentScriptTemplate>()).Text, "[CmdletBinding()]");

        // Without a text, a new bash script starts from the template; its parameters are there, never pending for an agent.
        var fromTemplate = await (await CreateAsync("new", "")).ReadAsync<AgentScriptDetail>();
        Assert.AreEqual(BashScriptHeader.Template, fromTemplate.Text);
        CollectionAssert.AreEqual(new[] { "target", "days", "dry_run" }, fromTemplate.Parsed!.Parameters.Select(p => p.Name).ToList());

        var script = await (await CreateAsync("rotate-logs", DevSeedAgents.RotateLogsScript)).ReadAsync<AgentScriptDetail>();
        Assert.AreEqual(ScriptLanguage.Bash, script.Language);
        var parameters = script.Parsed!.Parameters.ToDictionary(p => p.Name);
        CollectionAssert.AreEqual(new[] { "site", "keep_days", "dry_run", "api_token" }, script.Parsed.Parameters.Select(p => p.Name).ToList());
        Assert.IsTrue(parameters["site"].Mandatory);
        CollectionAssert.AreEqual(new[] { "www", "api", "shop" }, parameters["site"].ValidateSet!.ToList());
        Assert.AreEqual(("14", 1d, 365d), (parameters["keep_days"].Default, parameters["keep_days"].RangeMin, parameters["keep_days"].RangeMax));
        Assert.IsTrue(parameters["dry_run"].IsSwitch);
        Assert.IsTrue(parameters["api_token"].IsSecure);
        Assert.AreEqual("Rotate a site's logs and reload nginx", script.Parsed.Help?.Synopsis);

        // The language in the list; it can't change; header errors block runs.
        var bash = await (await admin.GetAsync("/api/v1/agents/scripts?language=Bash&pageSize=500")).ReadAsync<Paged<AgentScriptItem>>();
        Assert.IsTrue(bash.Items.Any(i => i.Id == script.Id && i.Parameters == 4));
        Assert.IsFalse((await (await admin.GetAsync("/api/v1/agents/scripts?language=PowerShell&pageSize=500")).ReadAsync<Paged<AgentScriptItem>>()).Items.Any(i => i.Id == script.Id));
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PutJsonAsync($"/api/v1/agents/scripts/{script.Id}",
            new AgentScriptRequest(script.Name, null, script.Text, ScriptLanguage.PowerShell))).StatusCode);
        var broken = await (await CreateAsync("broken", "#!/usr/bin/env bash\n# pnmt:\n#   parameters:\n#     n: { type: integer }\nset -euo pipefail\necho \"$PNMT_N\"\n"))
            .ReadAsync<AgentScriptDetail>();
        StringAssert.Contains(broken.Parsed!.Errors.Single().Message, "the type integer is not one of");
        var linux = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null, AgentPlatform.Linux)).Token, "bash-header", AgentPlatform.Linux);
        var refused = await admin.PostJsonAsync($"/api/v1/agents/scripts/{broken.Id}/run",
            new AgentScriptRunRequest([linux.Grant.SensorId], null, null, null, null, 60, null));
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.AreEqual("syntax_errors", (await refused.ProblemAsync()).GetProperty("code").GetString());
    }

    [TestMethod]
    public void ALinuxServersRightsRefuseRunsWithTheLineThatWouldAllowThem()
    {
        var rights = new AgentRightsSummary("config.yaml", null, false, [], 0, [], true, ["root", "deploy"], 0, 30, false, false);
        string[] scripts = [AgentCapabilities.Scripts];
        Assert.IsNull(AgentScriptService.LinuxRefusal(rights, scripts, "web-01", "deploy", 600));
        StringAssert.Contains(AgentScriptService.LinuxRefusal(rights, scripts, "web-01", "www", 600), "\"run_as: [root, deploy, www]\" in /etc/pnmt-agent/config.yaml");
        StringAssert.Contains(AgentScriptService.LinuxRefusal(rights, scripts, "web-01", "root", 3600), "\"max_timeout_minutes: 60\"");
        StringAssert.Contains(AgentScriptService.LinuxRefusal(rights with { Scripts = false }, [], "web-01", "root", 60), "\"scripts: enabled: true\"");
        StringAssert.Contains(AgentScriptService.LinuxRefusal(rights with { Source = "refused", Problem = "writable by others" }, scripts, "web-01", "root", 60), "refused");
        StringAssert.Contains(AgentScriptService.LinuxRefusal(rights, [], "web-01", "root", 60), "doesn't announce scripts.v1");
    }

    [TestMethod]
    public async Task BashRunsOnLinuxServersOnlyWithItsArgumentsCheckedAndItsSecretDecryptedAtTheAgentAndStoredNowhere()
    {
        var simulator = new AgentSimulator(new Uri("http://localhost/"), app.Client(), ct => AgentHarness.ConnectAsync(app, ct), TextWriter.Null, AgentPlatform.Linux)
        {
            AllowScripts = true,
        };
        var state = await simulator.JoinAsync((await AgentHarness.TokenAsync(admin, hostId, "bash-runs", AgentPlatform.Linux)).Token, null, "sim-bash", CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var running = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        var hub = app.Services.GetRequiredService<AgentHub>();
        await AgentHarness.EventuallyAsync(() => Task.FromResult(hub.Connection(state.SensorId)?.Capabilities.Contains(AgentCapabilities.Scripts) == true));

        // Saved while a Linux agent is connected: it checks the syntax, and its result joins PNMTD's header parse.
        var script = await (await CreateAsync("rotate", DevSeedAgents.RotateLogsScript)).ReadAsync<AgentScriptDetail>();
        var parsed = await AgentHarness.EventuallyAsync(async () => await (await admin.GetAsync($"/api/v1/agents/scripts/{script.Id}")).ReadAsync<AgentScriptDetail>(),
            d => d.ParseState == ScriptParseState.Parsed);
        StringAssert.Contains(parsed.Versions.Single().ParsedOn, "bash-runs");
        Assert.AreEqual(4, parsed.Parsed!.Parameters.Count);
        Assert.IsTrue(parsed.ParserAvailable);

        // The run dialog's accounts: root and deploy are in run_as, philipp isn't.
        var accounts = await (await admin.GetAsync($"/api/v1/agents/servers/{state.SensorId}/run-as-accounts")).ReadAsync<LinuxRunAsAccounts>();
        Assert.IsTrue(accounts.ScriptsAllowed);
        Assert.AreEqual((RunAsKind.Service, true), (accounts.Accounts[0].Kind, accounts.Accounts[0].Allowed));
        Assert.IsTrue(accounts.Accounts.Single(a => a.Name == "deploy").Allowed);
        StringAssert.Contains(accounts.Accounts.Single(a => a.Name == "philipp").Refused, "\"run_as: [root, deploy, philipp]\"");

        Dictionary<string, JsonElement> Args(params (string Key, object Value)[] values) => values.ToDictionary(v => v.Key, v => JsonSerializer.SerializeToElement(v.Value));
        Task<HttpResponseMessage> Run(Guid scriptId, IReadOnlyList<Guid> servers, Dictionary<string, JsonElement> arguments, Dictionary<string, string>? secure = null,
            RunAsKind? runAs = null, string? account = null) =>
            admin.PostJsonAsync($"/api/v1/agents/scripts/{scriptId}/run", new AgentScriptRunRequest(servers, null, arguments, secure, null, 120, null, runAs, account));

        // The arguments against the header: mandatory, the set, the range, unknown names, a secret sent plainly.
        var linux = new[] { state.SensorId };
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(script.Id, linux, Args(("keep_days", 3)))).StatusCode, "site is mandatory");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(script.Id, linux, Args(("site", "blog")))).StatusCode, "not in the set");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(script.Id, linux, Args(("site", "www"), ("keep_days", 900)))).StatusCode, "out of range");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(script.Id, linux, Args(("site", "www"), ("nope", 1)))).StatusCode, "no such parameter");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(script.Id, linux, Args(("site", "www"), ("api_token", "x")))).StatusCode, "a secret is sent encrypted");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(script.Id, linux, Args(("site", "www")), runAs: RunAsKind.Stored, account: "deploy")).StatusCode, "root or an account");

        // Only on servers of its platform: bash not on Windows, PowerShell not on Linux.
        var windows = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null)).Token, "SRV-NOT-BASH");
        var wrong = await Run(script.Id, [state.SensorId, windows.Grant.SensorId], Args(("site", "www")));
        Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
        StringAssert.Contains((await wrong.ProblemAsync()).GetProperty("errors").GetProperty("sensorIds")[0].GetString(), "is a Windows server");
        var powershell = await (await CreateAsync("ps", "param([string] $Name)\nWrite-Output $Name", ScriptLanguage.PowerShell)).ReadAsync<AgentScriptDetail>();
        await using (var db = app.NewDb())
        {
            // As a Windows agent would have parsed it.
            await db.AgentScriptVersions.Where(v => v.ScriptId == powershell.Id).ExecuteUpdateAsync(u => u
                .SetProperty(v => v.ParseState, ScriptParseState.Parsed).SetProperty(v => v.Parsed, AgentJson.Serialize(SimulatedParser.Parse(powershell.Text))));
        }
        var service = await (await admin.PostJsonAsync("/api/v1/windows/run-as", new WindowsRunAsRequest($"svc {Guid.NewGuid():N}", RunAsKind.Service, null, null)))
            .ReadAsync<WindowsRunAsItem>();
        var onLinux = await admin.PostJsonAsync($"/api/v1/agents/scripts/{powershell.Id}/run",
            new AgentScriptRunRequest(linux, service.Id, Args(("Name", "x")), null, null, 60, null));
        Assert.AreEqual(HttpStatusCode.BadRequest, onLinux.StatusCode);
        StringAssert.Contains((await onLinux.ProblemAsync()).GetProperty("errors").GetProperty("sensorIds")[0].GetString(), "is a Linux server");

        // An account outside run_as: refused here, with the line, and nothing sent.
        var outside = (await (await Run(script.Id, linux, Args(("site", "www")), runAs: RunAsKind.User, account: "www")).ReadAsync<List<AgentScriptRunItem>>()).Single();
        Assert.AreEqual(ScriptRunState.Refused, outside.State);
        StringAssert.Contains(outside.Message, "\"run_as: [root, deploy, www]\"");
        CollectionAssert.DoesNotContain(simulator.Requests, AgentMessageTypes.RunScript);

        // As deploy with a secret: it reaches the agent decrypted, and is stored nowhere.
        const string token = "cdn-s3cret-42";
        var sent = (await (await Run(script.Id, linux, Args(("site", "www"), ("dry_run", true)), new Dictionary<string, string> { ["api_token"] = token },
            RunAsKind.User, "deploy")).ReadAsync<List<AgentScriptRunItem>>()).Single();
        Assert.AreEqual(("deploy", RunAsKind.User), (sent.Account, sent.RunAsKind));
        var done = await AgentHarness.EventuallyAsync(async () => await (await admin.GetAsync($"/api/v1/agents/script-runs/{sent.Id}")).ReadAsync<AgentScriptRunDetail>(),
            d => d.Run.State == ScriptRunState.Done);
        Assert.AreEqual(token, simulator.LastSecrets["api_token"]);
        StringAssert.Contains(done.Output, "Hello from sim-bash as deploy");
        StringAssert.Contains(done.Output, "PNMT_SITE=www");
        StringAssert.Contains(done.Output, "PNMT_DRY_RUN=true");
        StringAssert.Contains(done.Output, $"PNMT_API_TOKEN=({token.Length} characters, secret)");
        await using (var db = app.NewDb())
        {
            var row = await db.AgentScriptRuns.AsNoTracking().SingleAsync(r => r.Id == sent.Id);
            Assert.IsFalse((row.Arguments ?? "").Contains(token) || (row.Output ?? "").Contains(token));
            Assert.IsFalse(await db.AuthLog.AnyAsync(l => l.Detail != null && l.Detail.Contains(token)));
            var log = await db.AgentLog.AsNoTracking().Where(l => l.SensorId == state.SensorId).Select(l => new { l.Message, l.Details }).ToListAsync();
            Assert.IsFalse(log.Any(l => l.Message.Contains(token) || (l.Details ?? "").Contains(token)));
        }

        // As root, the default: the service.
        var asRoot = (await (await Run(script.Id, linux, Args(("site", "api")))).ReadAsync<List<AgentScriptRunItem>>()).Single();
        Assert.AreEqual(("root (the service)", RunAsKind.Service, (string?)null), (asRoot.RunAsName, asRoot.RunAsKind, asRoot.Account));
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
