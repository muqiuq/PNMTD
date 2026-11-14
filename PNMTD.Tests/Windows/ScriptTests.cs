using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Cli;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;
using PNMTD.Tests.Agents;

namespace PNMTD.Tests.Windows;

/// <summary>
/// PowerShell scripts (docs/windows-sensor-plan.md §7, W6), PNMTD's side: a version parsed by a connected agent, the arguments
/// checked against its parameters, a refusal where scripts aren't allowed locally, a run as an account whose password is typed
/// per run (encrypted to the agent, never stored), and redacting.
/// </summary>
[TestClass]
public sealed class ScriptTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;

    private const string Script = """
        <#
        .SYNOPSIS
            Greets.
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory = $true)] [string] $Name,
            [ValidateSet('low', 'high')] [string] $Level = 'low',
            [securestring] $Secret,
            [switch] $Force
        )
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        Write-Output "Hello, $Name"
        """;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Agents:JoinRateLimitPerMinute"] = "1000",
            ["Agents:AgentDownloadBaseUrl"] = "http://127.0.0.1:9/releases/download",
        });
        admin = app.Client(Roles.Admin);
        hostId = await AgentHarness.CreateHostAsync(admin, "script-tests");
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

    [TestMethod]
    public void ASecretOpensOnlyWithTheAgentsKeyAndItsRun()
    {
        using var agent = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var run = Guid.NewGuid();
        var secret = AgentSecrets.Encrypt(Convert.ToBase64String(agent.ExportSubjectPublicKeyInfo()), "s3cret!", run);
        Assert.AreEqual("s3cret!", AgentSecrets.Decrypt(agent, secret, run));
        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => AgentSecrets.Decrypt(agent, secret, Guid.NewGuid()));
        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => AgentSecrets.Decrypt(other, secret, run));
    }

    private static async Task<(AgentSimulator Simulator, SimulatedAgentState State, CancellationTokenSource Stop, Task Running)> ConnectAsync(bool allowScripts,
        SimulatedAgentState? state = null)
    {
        var simulator = new AgentSimulator(new Uri("http://localhost/"), app.Client(), ct => AgentHarness.ConnectAsync(app, ct), TextWriter.Null)
        {
            AllowScripts = allowScripts,
        };
        state ??= await simulator.JoinAsync((await AgentHarness.TokenAsync(admin, hostId, "scripted")).Token, null, "SIM-SCRIPTS", CancellationToken.None);
        var stop = new CancellationTokenSource();
        var running = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        var hub = app.Services.GetRequiredService<AgentHub>();
        await AgentHarness.EventuallyAsync(() => Task.FromResult(hub.Connection(state.SensorId)?.Capabilities.Contains(AgentCapabilities.Parse) == true
            && hub.Connection(state.SensorId)!.Capabilities.Contains(AgentCapabilities.Scripts) == allowScripts));
        return (simulator, state, stop, running);
    }

    [TestMethod]
    public async Task AScriptIsParsedOnAnAgentAndRunsOnlyWhereAllowed()
    {
        var (_, state, stop, running) = await ConnectAsync(allowScripts: false);
        var created = await (await admin.PostJsonAsync("/api/v1/agents/scripts", new AgentScriptRequest($"greet {Guid.NewGuid():N}", "test", Script)))
            .ReadAsync<AgentScriptDetail>();
        var parsed = await AgentHarness.EventuallyAsync(async () => await (await admin.GetAsync($"/api/v1/agents/scripts/{created.Id}")).ReadAsync<AgentScriptDetail>(),
            d => d.ParseState == ScriptParseState.Parsed);
        CollectionAssert.AreEqual(new[] { "Name", "Level", "Secret", "Force" }, parsed.Parsed!.Parameters.Select(p => p.Name).ToList());
        Assert.IsTrue(parsed.Parsed.Parameters.Single(p => p.Name == "Name").Mandatory);
        Assert.IsTrue(parsed.Parsed.Parameters.Single(p => p.Name == "Secret").IsSecure);
        CollectionAssert.AreEqual(new[] { "low", "high" }, parsed.Parsed.Parameters.Single(p => p.Name == "Level").ValidateSet!.ToList());

        var service = await (await admin.PostJsonAsync("/api/v1/windows/run-as", new WindowsRunAsRequest($"service {Guid.NewGuid():N}", RunAsKind.Service, null, null)))
            .ReadAsync<WindowsRunAsItem>();
        var deploy = await (await admin.PostJsonAsync("/api/v1/windows/run-as", new WindowsRunAsRequest($"deploy {Guid.NewGuid():N}", RunAsKind.TypedPerRun, @"CORP\deploy", null)))
            .ReadAsync<WindowsRunAsItem>();
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/windows/run-as",
            new WindowsRunAsRequest($"gmsa {Guid.NewGuid():N}", RunAsKind.Gmsa, @"CORP\web", null))).StatusCode, "a gMSA ends with $");

        // The arguments are checked against the parsed parameters.
        Dictionary<string, JsonElement> Args(params (string Key, object Value)[] values) => values.ToDictionary(v => v.Key, v => JsonSerializer.SerializeToElement(v.Value));
        async Task<HttpResponseMessage> Run(Guid runAs, Dictionary<string, JsonElement> arguments, Dictionary<string, string>? secure = null, string? password = null) =>
            await admin.PostJsonAsync($"/api/v1/agents/scripts/{created.Id}/run", new AgentScriptRunRequest([state.SensorId], runAs, arguments, secure, password, 60, null));
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(service.Id, Args(("Level", "low")))).StatusCode, "Name is mandatory");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(service.Id, Args(("Name", "x"), ("Nope", 1)))).StatusCode, "no such parameter");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(service.Id, Args(("Name", "x"), ("Level", "max")))).StatusCode, "not in the set");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(deploy.Id, Args(("Name", "x")))).StatusCode, "the password is typed per run");

        // Not allowed on the server: refused, nothing runs.
        var refused = (await (await Run(service.Id, Args(("Name", "x")))).ReadAsync<List<AgentScriptRunItem>>()).Single();
        Assert.AreEqual(ScriptRunState.Refused, refused.State);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        // Allowed: the password and the secure argument reach the agent encrypted, and are never stored.
        (_, state, stop, running) = await ConnectAsync(allowScripts: true, state);
        var sent = (await (await Run(deploy.Id, Args(("Name", "x"), ("Force", true)), new Dictionary<string, string> { ["Secret"] = "123456" }, "p4ssw0rd"))
            .ReadAsync<List<AgentScriptRunItem>>()).Single();
        var done = await AgentHarness.EventuallyAsync(async () => await (await admin.GetAsync($"/api/v1/agents/script-runs/{sent.Id}")).ReadAsync<AgentScriptRunDetail>(),
            d => d.Run.State == ScriptRunState.Done);
        StringAssert.Contains(done.Output, @"as CORP\deploy");
        StringAssert.Contains(done.Output, "Secret has 6 characters");
        StringAssert.Contains(done.Output, "the password has 8 characters");
        Assert.IsFalse(done.Arguments!.Contains("123456"), "secure arguments are not stored");
        await using (var db = app.NewDb())
        {
            var row = await db.AgentScriptRuns.AsNoTracking().SingleAsync(r => r.Id == sent.Id);
            Assert.IsFalse((row.Arguments ?? "").Contains("p4ssw0rd"));
        }
        var redacted = await (await admin.PostJsonAsync($"/api/v1/agents/script-runs/{sent.Id}/redact", new { })).ReadAsync<AgentScriptRunDetail>();
        Assert.IsTrue(redacted.Run.Redacted);
        Assert.IsNull(redacted.Arguments);

        // A new version is a new hash, parsed again.
        var v2 = await (await admin.PutJsonAsync($"/api/v1/agents/scripts/{created.Id}", new AgentScriptRequest(created.Name, null, Script + "\nWrite-Output 'v2'")))
            .ReadAsync<AgentScriptDetail>();
        Assert.AreEqual(2, v2.CurrentVersion);
        Assert.AreNotEqual(created.Hash, v2.Hash);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task AScriptRegisteredOnTheServerIsListedAndRunsAsItsAccount()
    {
        var (_, state, stop, running) = await ConnectAsync(allowScripts: true);
        var local = await AgentHarness.EventuallyAsync(
            async () => await (await admin.GetAsync($"/api/v1/windows/servers/{state.SensorId}/local-scripts")).ReadAsync<WindowsLocalScripts>(),
            l => l.Scripts.Count == 1);
        var script = local.Scripts.Single().Script;
        Assert.AreEqual("Get-DiskReport", script.Name);
        Assert.AreEqual(ScriptHashes.Of(SimulatedSections.RegisteredScriptText), script.Sha256);
        Assert.AreEqual("MinFreePercent", script.Parsed!.Parameters.Single().Name);
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/windows/servers/{state.SensorId}/sections/scripts")).StatusCode,
            "the texts only through local-scripts, Admin");

        async Task<HttpResponseMessage> Run(string name, params (string Key, object Value)[] values) =>
            await admin.PostJsonAsync($"/api/v1/windows/servers/{state.SensorId}/local-scripts/run", new WindowsLocalScriptRunRequest(name,
                values.ToDictionary(v => v.Key, v => JsonSerializer.SerializeToElement(v.Value)), null, 60, null));
        Assert.AreEqual(HttpStatusCode.NotFound, (await Run("Nope")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run("Get-DiskReport", ("Nope", 1))).StatusCode, "no such parameter");
        var sent = await (await Run("get-diskreport", ("MinFreePercent", 15))).ReadAsync<AgentScriptRunItem>();
        Assert.AreEqual(RunAsKind.Service, sent.RunAsKind);
        Assert.IsNull(sent.ScriptId);
        var done = await AgentHarness.EventuallyAsync(async () => await (await admin.GetAsync($"/api/v1/agents/script-runs/{sent.Id}")).ReadAsync<AgentScriptRunDetail>(),
            d => d.Run.State == ScriptRunState.Done);
        StringAssert.Contains(done.Output, "as SYSTEM");
        StringAssert.Contains(done.Output, "MinFreePercent=15");
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
