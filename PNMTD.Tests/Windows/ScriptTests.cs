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
            ["Windows:JoinRateLimitPerMinute"] = "1000",
            ["Windows:AgentDownloadBaseUrl"] = "http://127.0.0.1:9/releases/download",
        });
        admin = app.Client(Roles.Admin);
        hostId = await WindowsHarness.CreateHostAsync(admin, "script-tests");
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

    private static async Task<(WindowsSimulator Simulator, SimulatedAgentState State, CancellationTokenSource Stop, Task Running)> ConnectAsync(bool allowScripts,
        SimulatedAgentState? state = null)
    {
        var simulator = new WindowsSimulator(new Uri("http://localhost/"), app.Client(), ct => WindowsHarness.ConnectAsync(app, ct), TextWriter.Null)
        {
            AllowScripts = allowScripts,
        };
        state ??= await simulator.JoinAsync((await WindowsHarness.TokenAsync(admin, hostId, "scripted")).Token, null, "SIM-SCRIPTS", CancellationToken.None);
        var stop = new CancellationTokenSource();
        var running = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        var hub = app.Services.GetRequiredService<AgentHub>();
        await WindowsHarness.EventuallyAsync(() => Task.FromResult(hub.Connection(state.SensorId)?.Capabilities.Contains(AgentCapabilities.Parse) == true
            && hub.Connection(state.SensorId)!.Capabilities.Contains(AgentCapabilities.Scripts) == allowScripts));
        return (simulator, state, stop, running);
    }

    [TestMethod]
    public async Task AScriptIsParsedOnAnAgentAndRunsOnlyWhereAllowed()
    {
        var (_, state, stop, running) = await ConnectAsync(allowScripts: false);
        var created = await (await admin.PostJsonAsync("/api/v1/windows/scripts", new WindowsScriptRequest($"greet {Guid.NewGuid():N}", "test", Script)))
            .ReadAsync<WindowsScriptDetail>();
        var parsed = await WindowsHarness.EventuallyAsync(async () => await (await admin.GetAsync($"/api/v1/windows/scripts/{created.Id}")).ReadAsync<WindowsScriptDetail>(),
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
            await admin.PostJsonAsync($"/api/v1/windows/scripts/{created.Id}/run", new WindowsScriptRunRequest([state.SensorId], runAs, arguments, secure, password, 60, null));
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(service.Id, Args(("Level", "low")))).StatusCode, "Name is mandatory");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(service.Id, Args(("Name", "x"), ("Nope", 1)))).StatusCode, "no such parameter");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(service.Id, Args(("Name", "x"), ("Level", "max")))).StatusCode, "not in the set");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Run(deploy.Id, Args(("Name", "x")))).StatusCode, "the password is typed per run");

        // Not allowed on the server: refused, nothing runs.
        var refused = (await (await Run(service.Id, Args(("Name", "x")))).ReadAsync<List<WindowsScriptRunItem>>()).Single();
        Assert.AreEqual(ScriptRunState.Refused, refused.State);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        // Allowed: the password and the secure argument reach the agent encrypted, and are never stored.
        (_, state, stop, running) = await ConnectAsync(allowScripts: true, state);
        var sent = (await (await Run(deploy.Id, Args(("Name", "x"), ("Force", true)), new Dictionary<string, string> { ["Secret"] = "123456" }, "p4ssw0rd"))
            .ReadAsync<List<WindowsScriptRunItem>>()).Single();
        var done = await WindowsHarness.EventuallyAsync(async () => await (await admin.GetAsync($"/api/v1/windows/script-runs/{sent.Id}")).ReadAsync<WindowsScriptRunDetail>(),
            d => d.Run.State == ScriptRunState.Done);
        StringAssert.Contains(done.Output, @"as CORP\deploy");
        StringAssert.Contains(done.Output, "Secret has 6 characters");
        StringAssert.Contains(done.Output, "the password has 8 characters");
        Assert.IsFalse(done.Arguments!.Contains("123456"), "secure arguments are not stored");
        await using (var db = app.NewDb())
        {
            var row = await db.WindowsScriptRuns.AsNoTracking().SingleAsync(r => r.Id == sent.Id);
            Assert.IsFalse((row.Arguments ?? "").Contains("p4ssw0rd"));
        }
        var redacted = await (await admin.PostJsonAsync($"/api/v1/windows/script-runs/{sent.Id}/redact", new { })).ReadAsync<WindowsScriptRunDetail>();
        Assert.IsTrue(redacted.Run.Redacted);
        Assert.IsNull(redacted.Arguments);

        // A new version is a new hash, parsed again.
        var v2 = await (await admin.PutJsonAsync($"/api/v1/windows/scripts/{created.Id}", new WindowsScriptRequest(created.Name, null, Script + "\nWrite-Output 'v2'")))
            .ReadAsync<WindowsScriptDetail>();
        Assert.AreEqual(2, v2.CurrentVersion);
        Assert.AreNotEqual(created.Hash, v2.Hash);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
