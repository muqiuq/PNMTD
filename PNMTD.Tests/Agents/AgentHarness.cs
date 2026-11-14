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

/// <summary>Joining, connecting and reading events for the agents' tests, of either platform.</summary>
internal static class AgentHarness
{
    public sealed record Joined(JoinResponse Response, JoinGrant Grant, ECDsa Key, AgentPlatform Platform = AgentPlatform.Windows);

    /// <summary>What a Linux agent reports about its system (docs/linux-sensor-plan.md §4.8): os-release, and the kernel as the build.</summary>
    public static AgentOs LinuxOs { get; } = new("Debian GNU/Linux 12 (bookworm)", "debian", "12", "6.1.0-28-amd64", "bookworm", true);

    public static AgentOs Os(AgentPlatform platform) => platform == AgentPlatform.Linux ? LinuxOs : AgentSimulator.Os;

    public static async Task<Guid> CreateHostAsync(HttpClient admin, string prefix)
    {
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"{prefix}-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        return host.Id;
    }

    public static async Task<AgentJoinTokenCreated> TokenAsync(HttpClient admin, Guid hostId, string? name, AgentPlatform platform = AgentPlatform.Windows) =>
        await (await admin.PostJsonAsync("/api/v1/agents/join-tokens", new AgentJoinTokenRequest(hostId, name, null, platform))).ReadAsync<AgentJoinTokenCreated>();

    public static JoinRequest Request(string token, ECDsa key, string hostname, AgentPlatform platform = AgentPlatform.Windows) => new(token,
        AgentSigning.PublicKey(key), hostname, null, "TEST", Os(platform), "1.2.0", "x64", Guid.NewGuid().ToString(), null, platform);

    public static async Task<Joined> JoinAsync(TestApp app, string token, string hostname, AgentPlatform platform = AgentPlatform.Windows)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (response, grant) = await AgentJoinClient.JoinAsync(app.Client(), new Uri("http://localhost/"), Request(token, key, hostname, platform), CancellationToken.None);
        return new Joined(response, grant, key, platform);
    }

    /// <summary>The refusal's code, or "joined".</summary>
    public static async Task<string> TryJoinAsync(TestApp app, string token, string hostname, AgentPlatform platform = AgentPlatform.Windows)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            await AgentJoinClient.JoinAsync(app.Client(), new Uri("http://localhost/"), Request(token, key, hostname, platform), CancellationToken.None);
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

    /// <summary>
    /// A connected session; <paramref name="rights"/>: what the hello announces of the server's config.yaml (docs/linux-sensor-plan.md
    /// §4.6); <paramref name="capabilities"/>: what it announces (the simulator's Windows set by default).
    /// </summary>
    public static async Task<AgentSession> OpenAsync(TestApp app, Joined joined, ECDsa? key = null, AgentRightsSummary? rights = null,
        IReadOnlyList<string>? capabilities = null)
    {
        var pnmtd = AgentSigning.ImportPublicKey(joined.Response.SigningPublicKey)!;
        var identity = new AgentIdentity(joined.Grant.AgentId, key ?? joined.Key, pnmtd, "http://localhost", "1.2.0", "x64", Os(joined.Platform),
            capabilities ?? AgentSimulator.Capabilities, Platform: joined.Platform, Rights: rights);
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

    public static SectionMessage Section<T>(string name, T data)
    {
        var element = AgentJson.ToElement(data);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element.GetRawText())), 0, 16).ToLowerInvariant();
        return new SectionMessage(name, SectionNames.SchemaVersion, hash, DateTimeOffset.UtcNow, element);
    }

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
