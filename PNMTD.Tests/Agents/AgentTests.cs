using System.Collections.Concurrent;
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

namespace PNMTD.Tests.Agents;

/// <summary>
/// Servers with an agent, PNMTD's side (docs/windows-sensor-plan.md §13; docs/linux-sensor-plan.md §14), for every platform:
/// the join (expiry, single use, its host, re-join, the rate limit, the token's platform and the sensor type it makes), the
/// WebSocket's authentication (a good hello, 4401, 4403, 4408), the limits (4413 for a large message and a gzip bomb),
/// heartbeats (250, 251, 451; Linux's 260), logins (254, 454) and signed requests answered by the simulator.
/// </summary>
[TestClass]
public sealed class AgentTests
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
            ["Agents:MaxMessageBytes"] = "65536",
            // No GitHub in the tests.
            ["Agents:AgentDownloadBaseUrl"] = "http://127.0.0.1:9/releases/download",
        });
        admin = app.Client(Roles.Admin);
        hostId = await AgentHarness.CreateHostAsync(admin, "agent-tests");
    }

    /// <summary>The servers' state changes would crowd other classes' dashboards (the newest 15 changes): their host goes off.</summary>
    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            await db.Hosts.Where(h => h.Id == hostId).ExecuteUpdateAsync(u => u.SetProperty(h => h.Enabled, false));
        }
        app.Dispose();
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    [TestMethod]
    public async Task AJoinCreatesTheServerOnTheTokensHostOnce()
    {
        var created = await AgentHarness.TokenAsync(admin, hostId, "file server");
        StringAssert.Matches(created.Token, new System.Text.RegularExpressions.Regex("^[a-z2-9]{4}-[a-z2-9]{4}$"));
        Assert.AreEqual(AgentJoinTokenState.Pending, created.Item.State);
        StringAssert.Contains(created.CommandLine, $"--token {created.Token}");
        // The test server is plain http://: the agent's command line must confirm it.
        StringAssert.EndsWith(created.CommandLine, " --allow-http");

        var joined = await AgentHarness.JoinAsync(app, created.Token, "SRV-JOIN");
        Assert.AreEqual("file server", joined.Grant.SensorName);
        await using (var db = app.NewDb())
        {
            var sensor = await db.Sensors.SingleAsync(s => s.Id == joined.Grant.SensorId);
            Assert.AreEqual(SensorType.WINDOWS, sensor.Type);
            Assert.AreEqual(hostId, sensor.ParentId);
            var agent = await db.Agents.SingleAsync(a => a.SensorId == sensor.Id);
            Assert.AreEqual("SRV-JOIN", agent.Hostname);
            Assert.AreEqual(joined.Grant.AgentKeyFingerprint, agent.KeyFingerprint);
            var log = await db.AgentLog.SingleAsync(l => l.SensorId == sensor.Id && l.Kind == AgentLogKind.Joined);
            StringAssert.Contains(log.Message, agent.KeyFingerprint);
            StringAssert.Contains(log.Message, joined.Response.SigningKeyFingerprint);
        }

        // Single use.
        var again = await AgentHarness.TryJoinAsync(app, created.Token, "SRV-AGAIN");
        Assert.AreEqual("invalid_token", again);
        var tokens = await (await admin.GetAsync("/api/v1/agents/join-tokens?state=Used")).ReadAsync<Paged<AgentJoinTokenItem>>();
        var used = tokens.Items.Single(t => t.Id == created.Item.Id);
        Assert.AreEqual(joined.Grant.SensorId, used.Result?.Id);
    }

    [TestMethod]
    public async Task ExpiredRevokedAndMalformedTokensAreRefused()
    {
        var expired = await AgentHarness.TokenAsync(admin, hostId, null);
        await using (var db = app.NewDb())
        {
            await db.AgentJoinTokens.Where(t => t.Id == expired.Item.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiresAt, Clock.UtcNow.AddMinutes(-1)));
        }
        Assert.AreEqual("invalid_token", await AgentHarness.TryJoinAsync(app, expired.Token, "SRV-EXPIRED"));

        var revoked = await AgentHarness.TokenAsync(admin, hostId, null);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/agents/join-tokens/{revoked.Item.Id}")).StatusCode);
        Assert.AreEqual("invalid_token", await AgentHarness.TryJoinAsync(app, revoked.Token, "SRV-REVOKED"));

        Assert.AreEqual("invalid_token", await AgentHarness.TryJoinAsync(app, "0000-1111", "SRV-MALFORMED"));
        Assert.AreEqual("invalid_token", await AgentHarness.TryJoinAsync(app, "abcd-efgh", "SRV-UNKNOWN"));

        // A token's host must exist when it is created; deleting the host deletes its tokens.
        var noHost = await admin.PostJsonAsync("/api/v1/agents/join-tokens", new AgentJoinTokenRequest(Guid.NewGuid(), null, null));
        Assert.AreEqual(HttpStatusCode.BadRequest, noHost.StatusCode);
    }

    [TestMethod]
    public async Task ARejoinReplacesTheKeyAndKeepsTheServer()
    {
        var first = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, "rejoin")).Token, "SRV-REJOIN");
        await using (var session = await AgentHarness.OpenAsync(app, first))
        {
            await session.SendAsync(AgentMessageTypes.Heartbeat, AgentHarness.Heartbeat(session), CancellationToken.None);
            await AgentHarness.EventuallyAsync(async () => (await AgentHarness.EventsAsync(app, first.Grant.SensorId)).Count > 0);
        }

        var rejoin = await (await admin.PostJsonAsync($"/api/v1/agents/servers/{first.Grant.SensorId}/rejoin-token", new AgentRejoinTokenRequest(null)))
            .ReadAsync<AgentJoinTokenCreated>();
        Assert.AreEqual(first.Grant.SensorId, rejoin.Item.Rejoin?.Id);
        var second = await AgentHarness.JoinAsync(app, rejoin.Token, "SRV-REJOIN");
        Assert.AreEqual(first.Grant.SensorId, second.Grant.SensorId);
        Assert.AreNotEqual(first.Grant.AgentId, second.Grant.AgentId);
        Assert.AreNotEqual(first.Grant.AgentKeyFingerprint, second.Grant.AgentKeyFingerprint);
        Assert.IsGreaterThan(0, (await AgentHarness.EventsAsync(app, first.Grant.SensorId)).Count, "the history stays");

        // The old key is gone.
        var refused = await Assert.ThrowsExactlyAsync<AgentHandshakeException>(() => AgentHarness.OpenAsync(app, first));
        Assert.AreEqual(AgentCloseCodes.Unauthorized, refused.CloseCode);
        await using var renewed = await AgentHarness.OpenAsync(app, second);
        Assert.IsTrue(renewed.IsOpen);
    }

    [TestMethod]
    public async Task JoinsAreRateLimitedAndCloseAfterManyFailures()
    {
        using var limited = new TestApp(settings: new Dictionary<string, string?> { ["Agents:JoinRateLimitPerMinute"] = "2" });
        Assert.AreEqual("invalid_token", await AgentHarness.TryJoinAsync(limited, "abcd-efgh", "SRV-A"));
        Assert.AreEqual("invalid_token", await AgentHarness.TryJoinAsync(limited, "abcd-efgh", "SRV-B"));
        Assert.AreEqual("rate_limited", await AgentHarness.TryJoinAsync(limited, "abcd-efgh", "SRV-C"));

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
        var joined = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null)).Token, "SRV-HELLO");
        await using (var session = await AgentHarness.OpenAsync(app, joined))
        {
            Assert.AreEqual(joined.Grant.Config.HeartbeatSeconds, session.Config.HeartbeatSeconds);
            await AgentHarness.EventuallyAsync(async () =>
            {
                await using var db = app.NewDb();
                return await db.Agents.AnyAsync(a => a.SensorId == joined.Grant.SensorId && a.Connected);
            });
            var detail = await (await admin.GetAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}")).ReadAsync<AgentServerDetail>();
            Assert.IsTrue(detail.Server.Connected);
            CollectionAssert.Contains(detail.Capabilities.ToList(), AgentCapabilities.DiagPing);
        }

        // A key that isn't the joined one: 4401.
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var refused = await Assert.ThrowsExactlyAsync<AgentHandshakeException>(() => AgentHarness.OpenAsync(app, joined, wrongKey));
        Assert.AreEqual(AgentCloseCodes.Unauthorized, refused.CloseCode);
        await using var db2 = app.NewDb();
        Assert.IsTrue(await db2.AgentLog.AnyAsync(l => l.SensorId == joined.Grant.SensorId && l.Kind == AgentLogKind.AuthFailed));
    }

    [TestMethod]
    public async Task ARevokedAgentIsClosedWith4403AndNoHelloWith4408()
    {
        var joined = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null)).Token, "SRV-REVOKE");
        await using (var session = await AgentHarness.OpenAsync(app, joined))
        {
            var running = session.RunAsync(new AgentHarness.NullHandler(), CancellationToken.None);
            var revoked = await (await admin.PostJsonAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/revoke", new AgentRevokeRequest("lost laptop")))
                .ReadAsync<AgentServerDetail>();
            Assert.IsTrue(revoked.Server.Revoked);
            var end = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(end.Revoked, $"{end.CloseCode} {end.Reason}");
        }
        var again = await Assert.ThrowsExactlyAsync<AgentHandshakeException>(() => AgentHarness.OpenAsync(app, joined));
        Assert.AreEqual(AgentCloseCodes.Revoked, again.CloseCode);
        StringAssert.Contains(again.Reason, "lost laptop");

        // Connected, but silent: 4408 after the hello timeout.
        using var socket = await AgentHarness.ConnectAsync(app);
        var challenge = await AgentFrames.ReceiveAsync(socket, 65536, CancellationToken.None);
        Assert.AreEqual(AgentMessageTypes.Challenge, challenge?.T);
        Assert.IsNull(await AgentFrames.ReceiveAsync(socket, 65536, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));
        Assert.AreEqual(AgentCloseCodes.HelloTimeout, (int?)socket.CloseStatus);
    }

    [TestMethod]
    public async Task ARequestSignedForAnotherConnectionIsRefused()
    {
        // Every signed message names its connection's nonce (docs/windows-sensor-plan.md §3.4): a request captured on one
        // connection, by a proxy that reads the TLS, can't be replayed on the agent's next connection within the five minutes.
        var joined = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null)).Token, "SRV-SESSION");
        var hub = app.Services.GetRequiredService<AgentHub>();
        var keys = app.Services.GetRequiredService<AgentKeyStore>();
        var none = CancellationToken.None;
        SignedEnvelope captured;
        string first;
        await using (var session = await AgentHarness.OpenAsync(app, joined))
        {
            var handler = new RecordingHandler();
            _ = session.RunAsync(handler, none);
            var connection = (await AgentHarness.EventuallyAsync(() => Task.FromResult(hub.Connection(joined.Grant.SensorId)), c => c is not null))!;
            first = connection.Session;

            // Signed for some other connection: refused, and the request never reaches the handler.
            var foreign = await keys.SignAsync(AgentMessageTypes.Collect, joined.Grant.AgentId, new { }, none, AgentProtocol.RequestLifetime, "req-foreign", "another");
            await connection.SendAsync(AgentMessageTypes.Collect, foreign, none, "req-foreign");
            // Signed for this one: runs.
            captured = await keys.SignAsync(AgentMessageTypes.Collect, joined.Grant.AgentId, new { }, none, AgentProtocol.RequestLifetime, "req-own", connection.Session);
            await connection.SendAsync(AgentMessageTypes.Collect, captured, none, "req-own");
            await AgentHarness.EventuallyAsync(() => Task.FromResult(handler.Requests.Count == 1));
            Assert.AreEqual("req-own", handler.Requests.Single().RequestId);
            Assert.IsTrue(handler.Ignored.Any(r => r.Contains("another connection")), string.Join("; ", handler.Ignored));

            // Once more on the same connection: seen before, as always.
            await connection.SendAsync(AgentMessageTypes.Collect, captured, none, "req-own");
            await AgentHarness.EventuallyAsync(() => Task.FromResult(handler.Ignored.Any(r => r.Contains("seen before"))));
            Assert.AreEqual(1, handler.Requests.Count);
        }

        // Replayed on the agent's next connection: refused.
        await using (var session = await AgentHarness.OpenAsync(app, joined))
        {
            var handler = new RecordingHandler();
            _ = session.RunAsync(handler, none);
            var connection = (await AgentHarness.EventuallyAsync(() => Task.FromResult(hub.Connection(joined.Grant.SensorId)), c => c is not null && c.Session != first))!;
            await connection.SendAsync(AgentMessageTypes.Collect, captured, none, "req-own");
            await AgentHarness.EventuallyAsync(() => Task.FromResult(handler.Ignored.Any(r => r.Contains("another connection"))));
            Assert.AreEqual(0, handler.Requests.Count);
        }
    }

    private sealed class RecordingHandler : IAgentSessionHandler
    {
        public ConcurrentQueue<SignedContent> Requests { get; } = [];

        public ConcurrentQueue<string> Ignored { get; } = [];

        public Task OnConfigAsync(AgentConfig config, CancellationToken ct) => Task.CompletedTask;

        public Task<Ack> OnRequestAsync(AgentSession session, SignedContent request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new Ack(request.RequestId, true));
        }

        public void OnIgnored(string type, string reason) => Ignored.Enqueue(reason);
    }

    [TestMethod]
    public async Task LargeMessagesAndGzipBombsAreClosedWith4413()
    {
        var joined = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null)).Token, "SRV-LIMITS");

        // 100 KB of text above the 64 KB limit.
        await using (var session = await AgentHarness.OpenAsync(app, joined))
        {
            var running = session.RunAsync(new AgentHarness.NullHandler(), CancellationToken.None);
            await session.SendAsync(AgentMessageTypes.Problem, new ProblemBatch([new AgentProblem("test", "big", new string('x', 100_000), null, null, 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]), CancellationToken.None, compress: false);
            var end = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(AgentCloseCodes.TooLarge, end.CloseCode);
        }

        // A few KB on the wire, megabytes decompressed.
        await using (var session = await AgentHarness.OpenAsync(app, joined))
        {
            var running = session.RunAsync(new AgentHarness.NullHandler(), CancellationToken.None);
            var envelope = AgentFrames.Envelope(AgentMessageTypes.Problem, new ProblemBatch([new AgentProblem("test", "bomb", new string('0', 2_000_000), null, null, 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]));
            var (bytes, type) = AgentFrames.Encode(envelope, compress: true);
            Assert.AreEqual(WebSocketMessageType.Binary, type);
            Assert.IsLessThan(65536, bytes.Length, "small on the wire");
            await AgentHarness.SendRawAsync(session, bytes, type);
            var end = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(AgentCloseCodes.TooLarge, end.CloseCode);
        }
    }

    [TestMethod]
    public async Task HeartbeatsWrite250ThenARestart251AndAClockOff451()
    {
        var joined = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null)).Token, "SRV-BEAT");
        await using var session = await AgentHarness.OpenAsync(app, joined);
        var boot = DateTimeOffset.UtcNow.AddDays(-2);
        await session.SendAsync(AgentMessageTypes.Heartbeat, AgentHarness.Heartbeat(session, boot), CancellationToken.None);
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count == 1);
        Assert.AreEqual(PNMTStatusCodes.WINDOWS_OK, events[0].Code);
        StringAssert.StartsWith(events[0].Message, "SRV-BEAT: Microsoft Windows Server 2019 Standard 17763.6532, agent");

        // The same heartbeat collapses by the dedupe: at most two rows.
        for (var i = 0; i < 3; i++)
        {
            await session.SendAsync(AgentMessageTypes.Heartbeat, AgentHarness.Heartbeat(session, boot), CancellationToken.None);
        }
        await Task.Delay(500);
        await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count == 2);

        await session.SendAsync(AgentMessageTypes.Heartbeat, AgentHarness.Heartbeat(session, boot.AddDays(1)), CancellationToken.None);
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.WINDOWS_RESTARTED);
        StringAssert.Contains(events[0].Message, "restarted at");

        await session.SendAsync(AgentMessageTypes.Heartbeat, AgentHarness.Heartbeat(session, boot.AddDays(1)) with { AgentTime = DateTimeOffset.UtcNow.AddMinutes(7).AddSeconds(5) },
            CancellationToken.None);
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.WINDOWS_AGENT_PROBLEM);
        StringAssert.Contains(events[0].Message, "the clock is off by 7 min");

        await session.SendAsync(AgentMessageTypes.Heartbeat, AgentHarness.Heartbeat(session, boot.AddDays(1)), CancellationToken.None);
        await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.WINDOWS_OK);
    }

    [TestMethod]
    public async Task RequestsAreSignedAndAnsweredByTheSimulator()
    {
        var created = await AgentHarness.TokenAsync(admin, hostId, "simulated");
        var simulator = new AgentSimulator(new Uri("http://localhost/"), app.Client(), ct => AgentHarness.ConnectAsync(app, ct), TextWriter.Null);
        var state = await simulator.JoinAsync(created.Token, null, "SIM-REQ", CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var running = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        await AgentHarness.EventuallyAsync(() => Task.FromResult(app.Services.GetRequiredService<AgentHub>().IsConnected(state.SensorId)));

        var collected = await (await app.Client(Roles.User).PostJsonAsync($"/api/v1/agents/servers/{state.SensorId}/collect", new AgentCollectRequest(null)))
            .ReadAsync<AgentRequestResult>();
        Assert.IsTrue(collected.Ok, collected.Error);
        var log = await (await admin.PostJsonAsync($"/api/v1/agents/servers/{state.SensorId}/send-log", new { })).ReadAsync<AgentLogFile>();
        Assert.IsTrue(log.Lines.Any(l => l.Contains("Connected")), string.Join("\n", log.Lines));
        // An agent that can parse also gets the script versions waiting for their parameters (W6): only these two are this test's.
        CollectionAssert.AreEqual(new[] { AgentMessageTypes.Collect, AgentMessageTypes.SendLog },
            simulator.Requests.Where(r => r is AgentMessageTypes.Collect or AgentMessageTypes.SendLog).ToList());

        // A viewer may not ask; an offline agent is a 409.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.Viewer).PostJsonAsync($"/api/v1/agents/servers/{state.SensorId}/collect",
            new AgentCollectRequest(null))).StatusCode);
        stop.Cancel();
        Assert.AreEqual("stopped", (await running.WaitAsync(TimeSpan.FromSeconds(10))).Reason);
        await AgentHarness.EventuallyAsync(() => Task.FromResult(!app.Services.GetRequiredService<AgentHub>().IsConnected(state.SensorId)));
        var offline = await app.Client(Roles.User).PostJsonAsync($"/api/v1/agents/servers/{state.SensorId}/collect", new AgentCollectRequest(null));
        Assert.AreEqual(HttpStatusCode.Conflict, offline.StatusCode);
    }

    [TestMethod]
    public async Task LoginsAreStoredOnceAndAnAdminLoginWrites254()
    {
        var joined = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null)).Token, "SRV-LOGINS");
        await using var session = await AgentHarness.OpenAsync(app, joined);
        var now = DateTimeOffset.UtcNow;
        var rdp = new LoginRecord("Security", 1001, now.AddSeconds(-5), null, 1, LogonOutcome.Success, null, "jdoe", "CONTOSO", true, LogonKind.Rdp, "Kerberos",
            "10.0.4.17", 50000, "WS17", true, "0x1A2B", null);
        var share = new LoginRecord("Security", 1002, now.AddSeconds(-4), null, 1, LogonOutcome.Success, null, "admin", "CONTOSO", true, LogonKind.Network, "NTLM V2",
            "10.0.4.18", null, null, null, "0x1A2C", null);
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch([rdp, share]), CancellationToken.None);
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count >= 1);
        Assert.AreEqual(PNMTStatusCodes.WINDOWS_ADMIN_LOGIN, events[0].Code);
        Assert.AreEqual(@"CONTOSO\jdoe: RDP from 10.0.4.17 (Kerberos, elevated)", events[0].Message);
        Assert.AreEqual(1, events.Count, "a network logon is recorded, but writes no 254");

        // The same record again, now with its logoff: updated, not added; no second 254.
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch([rdp with { LoggedOffAt = now }]), CancellationToken.None);
        var logins = await AgentHarness.EventuallyAsync(async () =>
            await (await app.Client(Roles.User).GetAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/logins")).ReadAsync<Paged<AgentLoginItem>>(),
            p => p.Items.Any(l => l.LoggedOffAt is not null));
        Assert.AreEqual(2, logins.TotalCount);
        Assert.AreEqual(1, (await AgentHarness.EventsAsync(app, joined.Grant.SensorId)).Count);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.Viewer).GetAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/logins")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await app.ClientWithRights("Monitoring:Details").GetAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/logins")).StatusCode,
            "Details reads logins without Edit (docs/cookie-object-notes-plan.md §4)");
    }

    [TestMethod]
    public async Task ABurstOfFailedLoginsAndALockoutWrite454()
    {
        var joined = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null)).Token, "SRV-BURST");
        await (await app.Client(Roles.User).PutJsonAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/settings",
            new AgentServerSettings(true, true, 5, false))).ReadAsync<AgentServerDetail>();
        await using var session = await AgentHarness.OpenAsync(app, joined);
        var now = DateTimeOffset.UtcNow;
        LoginRecord Failure(long id, string user, int count) => new("Security", id, now.AddSeconds(-30), now.AddSeconds(-1), count, LogonOutcome.Failure, "bad password",
            user, null, false, LogonKind.Network, "NTLM", "203.0.113.9", null, "KALI", null, null, null);
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch([Failure(2001, "administrator", 3)]), CancellationToken.None);
        await Task.Delay(300);
        Assert.AreEqual(0, (await AgentHarness.EventsAsync(app, joined.Grant.SensorId)).Count, "below the threshold");
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch([Failure(2002, "admin", 2)]), CancellationToken.None);
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count >= 1);
        Assert.AreEqual(PNMTStatusCodes.WINDOWS_LOGIN_FAILURES, events[0].Code);
        StringAssert.StartsWith(events[0].Message, "Failed logins from 203.0.113.9: at least 5 in 10 min");

        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch([new LoginRecord("Security", 2003, now, null, 1, LogonOutcome.Failure, "locked out", "jdoe",
            null, false, LogonKind.Lockout, null, null, null, "WS17", null, null, null)]), CancellationToken.None);
        await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Any(x => x.Message == "jdoe locked out (from WS17)"));
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

    [TestMethod]
    public async Task ALinuxTokenMakesALinuxSensorThatWritesLinuxCodes()
    {
        var created = await AgentHarness.TokenAsync(admin, hostId, null, AgentPlatform.Linux);
        Assert.AreEqual(AgentPlatform.Linux, created.Item.Platform);
        var joined = await AgentHarness.JoinAsync(app, created.Token, "web-01", AgentPlatform.Linux);
        Assert.AreEqual("Linux", joined.Grant.SensorName, "the platform's name when the token has none");
        await using (var db = app.NewDb())
        {
            var sensor = await db.Sensors.SingleAsync(s => s.Id == joined.Grant.SensorId);
            Assert.AreEqual(SensorType.LINUX, sensor.Type);
            Assert.AreEqual(hostId, sensor.ParentId);
            Assert.AreEqual(60, sensor.Interval);
            Assert.AreEqual(120, sensor.GracePeriod);
            Assert.AreEqual(AgentPlatform.Linux, (await db.Agents.SingleAsync(a => a.SensorId == sensor.Id)).Platform);
        }
        var linux = await (await admin.GetAsync("/api/v1/agents/servers?platform=Linux&pageSize=500")).ReadAsync<Paged<AgentServerItem>>();
        Assert.IsTrue(linux.Items.Any(i => i.SensorId == joined.Grant.SensorId));
        Assert.IsTrue(linux.Items.All(i => i.Platform == AgentPlatform.Linux));
        var windows = await (await admin.GetAsync("/api/v1/agents/servers?platform=Windows&pageSize=500")).ReadAsync<Paged<AgentServerItem>>();
        Assert.IsFalse(windows.Items.Any(i => i.SensorId == joined.Grant.SensorId));

        // Its heartbeat writes Linux's code (260) with its kernel, not Windows' 250.
        await using var session = await AgentHarness.OpenAsync(app, joined);
        await session.SendAsync(AgentMessageTypes.Heartbeat, AgentHarness.Heartbeat(session), CancellationToken.None);
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count == 1);
        Assert.AreEqual(PNMTStatusCodes.LINUX_OK, events[0].Code);
        Assert.AreEqual("web-01: Debian GNU/Linux 12 (bookworm), kernel 6.1.0-28-amd64, agent 1.2.0", events[0].Message);
        Assert.AreEqual("linux-agent", events[0].Source);
        // Windows' typed sections are not a Linux server's.
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/windows/servers/{joined.Grant.SensorId}/sections/system")).StatusCode);
    }

    [TestMethod]
    public async Task ATokenOfTheOtherPlatformIsRefusedAndStaysValid()
    {
        // A Windows agent with a Linux token, and a Linux agent with a Windows token.
        var linuxToken = await AgentHarness.TokenAsync(admin, hostId, null, AgentPlatform.Linux);
        Assert.AreEqual("wrong_platform", await AgentHarness.TryJoinAsync(app, linuxToken.Token, "SRV-WRONG"));
        var windowsToken = await AgentHarness.TokenAsync(admin, hostId, null);
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var refused = await Assert.ThrowsExactlyAsync<AgentJoinException>(() => AgentJoinClient.JoinAsync(app.Client(), new Uri("http://localhost/"),
                AgentHarness.Request(windowsToken.Token, key, "web-wrong", AgentPlatform.Linux), CancellationToken.None));
            Assert.AreEqual("wrong_platform", refused.Code);
            StringAssert.StartsWith(refused.Message, "This token was made for a Windows server; this agent runs on Linux.");
        }
        await using (var db = app.NewDb())
        {
            Assert.IsTrue(await db.AgentJoinTokens.Where(t => t.Id == linuxToken.Item.Id || t.Id == windowsToken.Item.Id).AllAsync(t => t.UsedAt == null),
                "a refused platform doesn't use the token up");
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.AgentJoinFailed && l.Detail!.Contains("is for Linux, the agent runs on Windows")));
        }

        // The right agent joins with it; its re-join token is of its platform too.
        var joined = await AgentHarness.JoinAsync(app, linuxToken.Token, "web-02", AgentPlatform.Linux);
        var rejoin = await (await admin.PostJsonAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/rejoin-token", new AgentRejoinTokenRequest(null)))
            .ReadAsync<AgentJoinTokenCreated>();
        Assert.AreEqual(AgentPlatform.Linux, rejoin.Item.Platform);
        Assert.AreEqual("wrong_platform", await AgentHarness.TryJoinAsync(app, rejoin.Token, "SRV-WRONG"));
        Assert.AreEqual(joined.Grant.SensorId, (await AgentHarness.JoinAsync(app, rejoin.Token, "web-02", AgentPlatform.Linux)).Grant.SensorId);
    }
}
