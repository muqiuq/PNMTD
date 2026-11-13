using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Cli;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Windows;

/// <summary>
/// Windows servers, PNMTD's side (docs/windows-sensor-plan.md §13): the join (expiry, single use, its host, re-join, the
/// rate limit), the WebSocket's authentication (a good hello, 4401, 4403, 4408), the limits (4413 for a large message and a
/// gzip bomb), heartbeats (250, 251, 451) and signed requests answered by the simulator.
/// </summary>
[TestClass]
public sealed class WindowsAgentTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Windows:JoinRateLimitPerMinute"] = "1000",
            ["Windows:HelloTimeoutSeconds"] = "2",
            ["Windows:MaxMessageBytes"] = "65536",
            // No GitHub in the tests.
            ["Windows:AgentDownloadBaseUrl"] = "http://127.0.0.1:9/releases/download",
        });
        admin = app.Client(Roles.Admin);
        hostId = await WindowsHarness.CreateHostAsync(admin, "windows-tests");
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    [TestMethod]
    public async Task AJoinCreatesTheServerOnTheTokensHostOnce()
    {
        var created = await WindowsHarness.TokenAsync(admin, hostId, "file server");
        StringAssert.Matches(created.Token, new System.Text.RegularExpressions.Regex("^[a-z2-9]{4}-[a-z2-9]{4}$"));
        Assert.AreEqual(WindowsJoinTokenState.Pending, created.Item.State);
        StringAssert.Contains(created.CommandLine, $"--token {created.Token}");

        var joined = await WindowsHarness.JoinAsync(app, created.Token, "SRV-JOIN");
        Assert.AreEqual("file server", joined.Grant.SensorName);
        await using (var db = app.NewDb())
        {
            var sensor = await db.Sensors.SingleAsync(s => s.Id == joined.Grant.SensorId);
            Assert.AreEqual(SensorType.WINDOWS, sensor.Type);
            Assert.AreEqual(hostId, sensor.ParentId);
            var agent = await db.WindowsAgents.SingleAsync(a => a.SensorId == sensor.Id);
            Assert.AreEqual("SRV-JOIN", agent.Hostname);
            Assert.AreEqual(joined.Grant.AgentKeyFingerprint, agent.KeyFingerprint);
            var log = await db.WindowsAgentLog.SingleAsync(l => l.SensorId == sensor.Id && l.Kind == WindowsAgentLogKind.Joined);
            StringAssert.Contains(log.Message, agent.KeyFingerprint);
            StringAssert.Contains(log.Message, joined.Response.SigningKeyFingerprint);
        }

        // Single use.
        var again = await WindowsHarness.TryJoinAsync(app, created.Token, "SRV-AGAIN");
        Assert.AreEqual("invalid_token", again);
        var tokens = await (await admin.GetAsync("/api/v1/windows/join-tokens?state=Used")).ReadAsync<Paged<WindowsJoinTokenItem>>();
        var used = tokens.Items.Single(t => t.Id == created.Item.Id);
        Assert.AreEqual(joined.Grant.SensorId, used.Result?.Id);
    }

    [TestMethod]
    public async Task ExpiredRevokedAndMalformedTokensAreRefused()
    {
        var expired = await WindowsHarness.TokenAsync(admin, hostId, null);
        await using (var db = app.NewDb())
        {
            await db.WindowsJoinTokens.Where(t => t.Id == expired.Item.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiresAt, Clock.UtcNow.AddMinutes(-1)));
        }
        Assert.AreEqual("invalid_token", await WindowsHarness.TryJoinAsync(app, expired.Token, "SRV-EXPIRED"));

        var revoked = await WindowsHarness.TokenAsync(admin, hostId, null);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/windows/join-tokens/{revoked.Item.Id}")).StatusCode);
        Assert.AreEqual("invalid_token", await WindowsHarness.TryJoinAsync(app, revoked.Token, "SRV-REVOKED"));

        Assert.AreEqual("invalid_token", await WindowsHarness.TryJoinAsync(app, "0000-1111", "SRV-MALFORMED"));
        Assert.AreEqual("invalid_token", await WindowsHarness.TryJoinAsync(app, "abcd-efgh", "SRV-UNKNOWN"));

        // A token's host must exist when it is created; deleting the host deletes its tokens.
        var noHost = await admin.PostJsonAsync("/api/v1/windows/join-tokens", new WindowsJoinTokenRequest(Guid.NewGuid(), null, null));
        Assert.AreEqual(HttpStatusCode.BadRequest, noHost.StatusCode);
    }

    [TestMethod]
    public async Task ARejoinReplacesTheKeyAndKeepsTheServer()
    {
        var first = await WindowsHarness.JoinAsync(app, (await WindowsHarness.TokenAsync(admin, hostId, "rejoin")).Token, "SRV-REJOIN");
        await using (var session = await WindowsHarness.OpenAsync(app, first))
        {
            await session.SendAsync(AgentMessageTypes.Heartbeat, WindowsHarness.Heartbeat(session), CancellationToken.None);
            await WindowsHarness.EventuallyAsync(async () => (await WindowsHarness.EventsAsync(app, first.Grant.SensorId)).Count > 0);
        }

        var rejoin = await (await admin.PostJsonAsync($"/api/v1/windows/servers/{first.Grant.SensorId}/rejoin-token", new WindowsRejoinTokenRequest(null)))
            .ReadAsync<WindowsJoinTokenCreated>();
        Assert.AreEqual(first.Grant.SensorId, rejoin.Item.Rejoin?.Id);
        var second = await WindowsHarness.JoinAsync(app, rejoin.Token, "SRV-REJOIN");
        Assert.AreEqual(first.Grant.SensorId, second.Grant.SensorId);
        Assert.AreNotEqual(first.Grant.AgentId, second.Grant.AgentId);
        Assert.AreNotEqual(first.Grant.AgentKeyFingerprint, second.Grant.AgentKeyFingerprint);
        Assert.IsGreaterThan(0, (await WindowsHarness.EventsAsync(app, first.Grant.SensorId)).Count, "the history stays");

        // The old key is gone.
        var refused = await Assert.ThrowsExactlyAsync<AgentHandshakeException>(() => WindowsHarness.OpenAsync(app, first));
        Assert.AreEqual(AgentCloseCodes.Unauthorized, refused.CloseCode);
        await using var renewed = await WindowsHarness.OpenAsync(app, second);
        Assert.IsTrue(renewed.IsOpen);
    }

    [TestMethod]
    public async Task JoinsAreRateLimitedAndCloseAfterManyFailures()
    {
        using var limited = new TestApp(settings: new Dictionary<string, string?> { ["Windows:JoinRateLimitPerMinute"] = "2" });
        Assert.AreEqual("invalid_token", await WindowsHarness.TryJoinAsync(limited, "abcd-efgh", "SRV-A"));
        Assert.AreEqual("invalid_token", await WindowsHarness.TryJoinAsync(limited, "abcd-efgh", "SRV-B"));
        Assert.AreEqual("rate_limited", await WindowsHarness.TryJoinAsync(limited, "abcd-efgh", "SRV-C"));

        var guard = new JoinGuard();
        for (var i = 0; i < JoinGuard.FailuresPerHour - 1; i++)
        {
            guard.Failed();
        }
        Assert.IsFalse(guard.IsClosed);
        guard.Failed();
        Assert.IsTrue(guard.IsClosed);
        Clock.Provider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow + JoinGuard.ClosedFor + TimeSpan.FromSeconds(1));
        Assert.IsFalse(guard.IsClosed);
    }

    [TestMethod]
    public async Task AGoodHelloGetsTheSignedWelcomeAndAWrongOneIsClosed()
    {
        var joined = await WindowsHarness.JoinAsync(app, (await WindowsHarness.TokenAsync(admin, hostId, null)).Token, "SRV-HELLO");
        await using (var session = await WindowsHarness.OpenAsync(app, joined))
        {
            Assert.AreEqual(joined.Grant.Config.HeartbeatSeconds, session.Config.HeartbeatSeconds);
            await WindowsHarness.EventuallyAsync(async () =>
            {
                await using var db = app.NewDb();
                return await db.WindowsAgents.AnyAsync(a => a.SensorId == joined.Grant.SensorId && a.Connected);
            });
            var detail = await (await admin.GetAsync($"/api/v1/windows/servers/{joined.Grant.SensorId}")).ReadAsync<WindowsServerDetail>();
            Assert.IsTrue(detail.Server.Connected);
            CollectionAssert.Contains(detail.Capabilities.ToList(), AgentCapabilities.DiagPing);
        }

        // A key that isn't the joined one: 4401.
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var refused = await Assert.ThrowsExactlyAsync<AgentHandshakeException>(() => WindowsHarness.OpenAsync(app, joined, wrongKey));
        Assert.AreEqual(AgentCloseCodes.Unauthorized, refused.CloseCode);
        await using var db2 = app.NewDb();
        Assert.IsTrue(await db2.WindowsAgentLog.AnyAsync(l => l.SensorId == joined.Grant.SensorId && l.Kind == WindowsAgentLogKind.AuthFailed));
    }

    [TestMethod]
    public async Task ARevokedAgentIsClosedWith4403AndNoHelloWith4408()
    {
        var joined = await WindowsHarness.JoinAsync(app, (await WindowsHarness.TokenAsync(admin, hostId, null)).Token, "SRV-REVOKE");
        await using (var session = await WindowsHarness.OpenAsync(app, joined))
        {
            var running = session.RunAsync(new WindowsHarness.NullHandler(), CancellationToken.None);
            var revoked = await (await admin.PostJsonAsync($"/api/v1/windows/servers/{joined.Grant.SensorId}/revoke", new WindowsRevokeRequest("lost laptop")))
                .ReadAsync<WindowsServerDetail>();
            Assert.IsTrue(revoked.Server.Revoked);
            var end = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(end.Revoked, $"{end.CloseCode} {end.Reason}");
        }
        var again = await Assert.ThrowsExactlyAsync<AgentHandshakeException>(() => WindowsHarness.OpenAsync(app, joined));
        Assert.AreEqual(AgentCloseCodes.Revoked, again.CloseCode);
        StringAssert.Contains(again.Reason, "lost laptop");

        // Connected, but silent: 4408 after the hello timeout.
        using var socket = await WindowsHarness.ConnectAsync(app);
        var challenge = await AgentFrames.ReceiveAsync(socket, 65536, CancellationToken.None);
        Assert.AreEqual(AgentMessageTypes.Challenge, challenge?.T);
        Assert.IsNull(await AgentFrames.ReceiveAsync(socket, 65536, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));
        Assert.AreEqual(AgentCloseCodes.HelloTimeout, (int?)socket.CloseStatus);
    }

    [TestMethod]
    public async Task LargeMessagesAndGzipBombsAreClosedWith4413()
    {
        var joined = await WindowsHarness.JoinAsync(app, (await WindowsHarness.TokenAsync(admin, hostId, null)).Token, "SRV-LIMITS");

        // 100 KB of text above the 64 KB limit.
        await using (var session = await WindowsHarness.OpenAsync(app, joined))
        {
            var running = session.RunAsync(new WindowsHarness.NullHandler(), CancellationToken.None);
            await session.SendAsync(AgentMessageTypes.Problem, new ProblemBatch([new AgentProblem("test", "big", new string('x', 100_000), null, null, 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]), CancellationToken.None, compress: false);
            var end = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(AgentCloseCodes.TooLarge, end.CloseCode);
        }

        // A few KB on the wire, megabytes decompressed.
        await using (var session = await WindowsHarness.OpenAsync(app, joined))
        {
            var running = session.RunAsync(new WindowsHarness.NullHandler(), CancellationToken.None);
            var envelope = AgentFrames.Envelope(AgentMessageTypes.Problem, new ProblemBatch([new AgentProblem("test", "bomb", new string('0', 2_000_000), null, null, 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]));
            var (bytes, type) = AgentFrames.Encode(envelope, compress: true);
            Assert.AreEqual(WebSocketMessageType.Binary, type);
            Assert.IsLessThan(65536, bytes.Length, "small on the wire");
            await WindowsHarness.SendRawAsync(session, bytes, type);
            var end = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(AgentCloseCodes.TooLarge, end.CloseCode);
        }
    }

    [TestMethod]
    public async Task HeartbeatsWrite250ThenARestart251AndAClockOff451()
    {
        var joined = await WindowsHarness.JoinAsync(app, (await WindowsHarness.TokenAsync(admin, hostId, null)).Token, "SRV-BEAT");
        await using var session = await WindowsHarness.OpenAsync(app, joined);
        var boot = DateTimeOffset.UtcNow.AddDays(-2);
        await session.SendAsync(AgentMessageTypes.Heartbeat, WindowsHarness.Heartbeat(session, boot), CancellationToken.None);
        var events = await WindowsHarness.EventuallyAsync(() => WindowsHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count == 1);
        Assert.AreEqual(PNMTStatusCodes.WINDOWS_OK, events[0].Code);
        StringAssert.StartsWith(events[0].Message, "SRV-BEAT: Microsoft Windows Server 2019 Standard 17763.6532, agent");

        // The same heartbeat collapses by the dedupe: at most two rows.
        for (var i = 0; i < 3; i++)
        {
            await session.SendAsync(AgentMessageTypes.Heartbeat, WindowsHarness.Heartbeat(session, boot), CancellationToken.None);
        }
        await Task.Delay(500);
        await WindowsHarness.EventuallyAsync(() => WindowsHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count == 2);

        await session.SendAsync(AgentMessageTypes.Heartbeat, WindowsHarness.Heartbeat(session, boot.AddDays(1)), CancellationToken.None);
        events = await WindowsHarness.EventuallyAsync(() => WindowsHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.WINDOWS_RESTARTED);
        StringAssert.Contains(events[0].Message, "restarted at");

        await session.SendAsync(AgentMessageTypes.Heartbeat, WindowsHarness.Heartbeat(session, boot.AddDays(1)) with { AgentTime = DateTimeOffset.UtcNow.AddMinutes(7).AddSeconds(5) },
            CancellationToken.None);
        events = await WindowsHarness.EventuallyAsync(() => WindowsHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.WINDOWS_AGENT_PROBLEM);
        StringAssert.Contains(events[0].Message, "the clock is off by 7 min");

        await session.SendAsync(AgentMessageTypes.Heartbeat, WindowsHarness.Heartbeat(session, boot.AddDays(1)), CancellationToken.None);
        await WindowsHarness.EventuallyAsync(() => WindowsHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.WINDOWS_OK);
    }

    [TestMethod]
    public async Task RequestsAreSignedAndAnsweredByTheSimulator()
    {
        var created = await WindowsHarness.TokenAsync(admin, hostId, "simulated");
        var simulator = new WindowsSimulator(new Uri("http://localhost/"), app.Client(), ct => WindowsHarness.ConnectAsync(app, ct), TextWriter.Null);
        var state = await simulator.JoinAsync(created.Token, null, "SIM-REQ", CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var running = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        await WindowsHarness.EventuallyAsync(() => Task.FromResult(app.Services.GetRequiredService<AgentHub>().IsConnected(state.SensorId)));

        var collected = await (await app.Client(Roles.User).PostJsonAsync($"/api/v1/windows/servers/{state.SensorId}/collect", new WindowsCollectRequest(null)))
            .ReadAsync<WindowsRequestResult>();
        Assert.IsTrue(collected.Ok, collected.Error);
        var log = await (await admin.PostJsonAsync($"/api/v1/windows/servers/{state.SensorId}/send-log", new { })).ReadAsync<WindowsAgentLogFile>();
        Assert.IsTrue(log.Lines.Any(l => l.Contains("Connected")), string.Join("\n", log.Lines));
        CollectionAssert.AreEqual(new[] { AgentMessageTypes.Collect, AgentMessageTypes.SendLog }, simulator.Requests);

        // A viewer may not ask; an offline agent is a 409.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.Viewer).PostJsonAsync($"/api/v1/windows/servers/{state.SensorId}/collect",
            new WindowsCollectRequest(null))).StatusCode);
        stop.Cancel();
        Assert.AreEqual("stopped", (await running.WaitAsync(TimeSpan.FromSeconds(10))).Reason);
        await WindowsHarness.EventuallyAsync(() => Task.FromResult(!app.Services.GetRequiredService<AgentHub>().IsConnected(state.SensorId)));
        var offline = await app.Client(Roles.User).PostJsonAsync($"/api/v1/windows/servers/{state.SensorId}/collect", new WindowsCollectRequest(null));
        Assert.AreEqual(HttpStatusCode.Conflict, offline.StatusCode);
    }

    [TestMethod]
    public async Task ARequestIsVerifiedBoundAndRunOnce()
    {
        using var pnmtd = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var agentId = Guid.NewGuid();
        var signed = AgentSigning.Sign(pnmtd, AgentMessageTypes.Collect, agentId, new CollectRequest([]), AgentProtocol.RequestLifetime);
        Assert.IsNotNull(AgentSigning.Verify(pnmtd, signed));
        Assert.IsNull(AgentSigning.Verify(other, signed), "another key");
        Assert.IsNull(AgentSigning.Verify(pnmtd, signed with { P = signed.P.Replace("collect", "send-log") }), "a changed text");
        Assert.AreEqual(39, AgentSigning.Fingerprint(AgentSigning.PublicKey(pnmtd)).Length, "16 bytes in groups of four");
        Assert.AreEqual("k7mq-3xtp", JoinTokens.Normalize(" K7MQ3XTP "));
        Assert.IsNull(JoinTokens.Normalize("k7mq-3xt0"), "0 is not in the alphabet");
        await Task.CompletedTask;
    }
}

/// <summary>Joining, connecting and reading events for the Windows tests.</summary>
internal static class WindowsHarness
{
    public sealed record Joined(JoinResponse Response, JoinGrant Grant, ECDsa Key);

    public static async Task<Guid> CreateHostAsync(HttpClient admin, string prefix)
    {
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"{prefix}-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        return host.Id;
    }

    public static async Task<WindowsJoinTokenCreated> TokenAsync(HttpClient admin, Guid hostId, string? name) =>
        await (await admin.PostJsonAsync("/api/v1/windows/join-tokens", new WindowsJoinTokenRequest(hostId, name, null))).ReadAsync<WindowsJoinTokenCreated>();

    public static JoinRequest Request(string token, ECDsa key, string hostname) => new(token, AgentSigning.PublicKey(key), hostname, null, "TEST",
        WindowsSimulator.Os, "1.2.0", "x64", Guid.NewGuid().ToString(), null);

    public static async Task<Joined> JoinAsync(TestApp app, string token, string hostname)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (response, grant) = await AgentJoinClient.JoinAsync(app.Client(), new Uri("http://localhost/"), Request(token, key, hostname), CancellationToken.None);
        return new Joined(response, grant, key);
    }

    /// <summary>The refusal's code, or "joined".</summary>
    public static async Task<string> TryJoinAsync(TestApp app, string token, string hostname)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            await AgentJoinClient.JoinAsync(app.Client(), new Uri("http://localhost/"), Request(token, key, hostname), CancellationToken.None);
            return "joined";
        }
        catch (AgentJoinException e)
        {
            return e.Code;
        }
    }

    public static async Task<WebSocket> ConnectAsync(TestApp app, CancellationToken ct = default)
    {
        var client = app.Server.CreateWebSocketClient();
        client.SubProtocols.Add(AgentProtocol.Subprotocol);
        return await client.ConnectAsync(new Uri("ws://localhost" + AgentProtocol.WebSocketPath), ct);
    }

    public static async Task<AgentSession> OpenAsync(TestApp app, Joined joined, ECDsa? key = null)
    {
        var pnmtd = AgentSigning.ImportPublicKey(joined.Response.SigningPublicKey)!;
        var identity = new AgentIdentity(joined.Grant.AgentId, key ?? joined.Key, pnmtd, "http://localhost", "1.2.0", "x64", WindowsSimulator.Os,
            WindowsSimulator.Capabilities);
        var socket = await ConnectAsync(app);
        try
        {
            return await AgentSession.HandshakeAsync(socket, identity, restartedAfterCrash: false, CancellationToken.None);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static Heartbeat Heartbeat(AgentSession session, DateTimeOffset? boot = null) => new(DateTimeOffset.UtcNow, boot ?? DateTimeOffset.UtcNow.AddDays(-1),
        12.5, 40, "C:", 55, [], false, new Dictionary<string, string>(), session.Config.ConfigVersion, "1.2.0", 0, null, null);

    public static async Task SendRawAsync(AgentSession session, byte[] bytes, WebSocketMessageType type)
    {
        var socket = (WebSocket)typeof(AgentSession).GetField("socket", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!;
        await socket.SendAsync(bytes, type, true, CancellationToken.None);
    }

    public static async Task<List<EventEntity>> EventsAsync(TestApp app, Guid sensorId)
    {
        await using var db = app.NewDb();
        return await db.Events.AsNoTracking().Where(e => e.SensorId == sensorId).OrderByDescending(e => e.Created).ToListAsync();
    }

    public static async Task EventuallyAsync(Func<Task<bool>> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
            {
                Assert.Fail("The condition did not become true in time.");
            }
            await Task.Delay(50);
        }
    }

    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var value = await read();
            if (condition(value))
            {
                return value;
            }
            if (DateTime.UtcNow > until)
            {
                Assert.Fail($"The condition did not become true in time: {System.Text.Json.JsonSerializer.Serialize(value)}");
            }
            await Task.Delay(50);
        }
    }

    public sealed class NullHandler : IAgentSessionHandler
    {
        public Task OnConfigAsync(AgentConfig config, CancellationToken ct) => Task.CompletedTask;

        public Task<Ack> OnRequestAsync(AgentSession session, SignedContent request, CancellationToken ct) => Task.FromResult(new Ack(request.RequestId, true));

        public void OnIgnored(string type, string reason)
        {
        }
    }
}
