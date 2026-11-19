using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Agents;

/// <summary>A request that needs a connected agent, which isn't.</summary>
public sealed class AgentOfflineException(string message) : Exception(message);

/// <summary>
/// An authenticated agent's connection: its socket, a send gate, and the requests waiting for their answer.
/// </summary>
public sealed class AgentConnection(Guid sensorId, Guid agentId, string agentName, WebSocket socket, string? address, int maxMessageBytes,
    AgentPlatform platform, string session)
{
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new(StringComparer.Ordinal);
    private readonly RateWindow messages = new();
    private readonly RateWindow reports = new();

    public Guid SensorId { get; } = sensorId;

    public Guid AgentId { get; } = agentId;

    /// <summary>
    /// The nonce of this connection's challenge. Every signed message to the agent names it, so one captured here is refused
    /// on the agent's next connection (docs/windows-sensor-plan.md §3.4).
    /// </summary>
    public string Session { get; } = session;

    /// <summary>The agent row's platform, from its join token (docs/linux-sensor-plan.md §3.3).</summary>
    public AgentPlatform Platform { get; } = platform;

    public string AgentName { get; set; } = agentName;

    public string? Address { get; } = address;

    public int MaxMessageBytes { get; } = maxMessageBytes;

    public DateTime ConnectedAt { get; } = Clock.UtcNow;

    public IReadOnlyList<string> Capabilities { get; set; } = [];

    internal WebSocket Socket { get; } = socket;

    /// <summary>Set when a newer connection of the same agent replaced this one.</summary>
    public bool Replaced { get; set; }

    public Task SendAsync<T>(string type, T body, CancellationToken ct, string? id = null) =>
        AgentFrames.SendAsync(Socket, AgentFrames.Envelope(type, body, id), sendGate, ct);

    /// <summary>Sends a signed request and waits for its answer: the <c>ack</c>, or for <c>send-log</c> the lines.</summary>
    public async Task<JsonElement> RequestAsync(AgentKeyStore keys, string type, object body, TimeSpan timeout, CancellationToken ct)
    {
        var requestId = AgentSigning.NewRequestId();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[requestId] = completion;
        try
        {
            var signed = await keys.SignAsync(type, AgentId, body, ct, AgentProtocol.RequestLifetime, requestId, Session);
            await SendAsync(type, signed, ct, requestId);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(timeout);
            return await completion.Task.WaitAsync(wait.Token);
        }
        finally
        {
            pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>An answer to a request; false when nothing waits for it.</summary>
    internal bool Complete(string requestId, JsonElement answer) => pending.TryRemove(requestId, out var completion) && completion.TrySetResult(answer);

    /// <summary>The message rate (§8.7): reports (sections, logins, results) have a budget of their own.</summary>
    internal bool Allow(bool report, int perMinute) => (report ? reports : messages).Allow(perMinute);

    public async Task CloseAsync(int code, string reason)
    {
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Socket.CloseAsync((WebSocketCloseStatus)code, reason, timeout.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            Socket.Abort();
        }
    }

    private sealed class RateWindow
    {
        private readonly Lock gate = new();
        private DateTime start = Clock.UtcNow;
        private int count;

        public bool Allow(int perMinute)
        {
            lock (gate)
            {
                var now = Clock.UtcNow;
                if (now - start >= TimeSpan.FromMinutes(1))
                {
                    start = now;
                    count = 0;
                }
                return ++count <= perMinute;
            }
        }
    }
}

/// <summary>
/// The agents' WebSocket (docs/windows-sensor-plan.md §3.4–§3.6) on PNMT's existing listener and port, for every platform:
/// the challenge, the signed hello within 10 s, one connection per agent, the signed welcome, then messages routed to their
/// services. Every message is isolated: a failure is logged and the connection goes on (§3.8).
/// </summary>
public sealed class AgentHub(
    IDbContextFactory<PnmtdDbContext> dbs,
    AgentKeyStore keys,
    IOptionsMonitor<AgentOptions> options,
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<AgentHub> logger)
{
    private readonly ConcurrentDictionary<Guid, AgentConnection> connections = new();

    public bool IsConnected(Guid sensorId) => connections.TryGetValue(sensorId, out var c) && c.Socket.State == WebSocketState.Open;

    public AgentConnection? Connection(Guid sensorId) => connections.TryGetValue(sensorId, out var c) && c.Socket.State == WebSocketState.Open ? c : null;

    public IReadOnlyCollection<AgentConnection> Connections => connections.Values.ToList();

    /// <summary>The connection, or a 409 for the API: the agent is offline.</summary>
    public AgentConnection Require(Guid sensorId) =>
        Connection(sensorId) ?? throw new AgentOfflineException("The agent is not connected: try again when it is.");

    /// <summary>Closes an agent's connection (revoked, re-joined, deleted).</summary>
    public async Task DisconnectAsync(Guid sensorId, int code, string reason)
    {
        if (connections.TryRemove(sensorId, out var connection))
        {
            connection.Replaced = true;
            await connection.CloseAsync(code, reason);
        }
    }

    /// <summary>Sends the current configuration to connected agents: all of them, or those named.</summary>
    public async Task PushConfigAsync(IEnumerable<Guid>? sensorIds, CancellationToken ct)
    {
        var targets = sensorIds is null ? connections.Values.ToList() : sensorIds.Select(Connection).OfType<AgentConnection>().ToList();
        foreach (var connection in targets)
        {
            try
            {
                await using var db = await dbs.CreateDbContextAsync(ct);
                var config = await AgentConfiguration.BuildAsync(db, keys, options.CurrentValue, connection.SensorId, ct);
                var signed = await keys.SignAsync(AgentMessageTypes.Config, connection.AgentId, config, ct, session: connection.Session);
                await connection.SendAsync(AgentMessageTypes.Config, signed, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Sending the configuration to agent {Sensor} failed", connection.SensorId);
            }
        }
    }

    public async Task HandleAsync(HttpContext context)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains(AgentProtocol.Subprotocol))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var address = context.Connection.RemoteIpAddress?.ToString();
        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = AgentProtocol.Subprotocol,
            KeepAliveInterval = AgentProtocol.KeepAlive,
        });
        var ct = context.RequestAborted;
        var connection = await AuthenticateAsync(context, socket, address, settings, ct);
        if (connection is null)
        {
            return;
        }
        try
        {
            await ReceiveLoopAsync(connection, ct);
        }
        catch (AgentFrameTooLargeException e)
        {
            await LogAsync(connection, AgentLogKind.Error, $"Closed: {e.Message}");
            await connection.CloseAsync(AgentCloseCodes.TooLarge, "message too large");
        }
        catch (AgentFrameInvalidException e)
        {
            await LogAsync(connection, AgentLogKind.Error, $"Closed: {e.Message}");
            await connection.CloseAsync(AgentCloseCodes.ProtocolError, "protocol error");
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            // The agent went away: it reconnects by itself.
        }
        finally
        {
            await DisconnectedAsync(connection);
        }
    }

    private async Task<AgentConnection?> AuthenticateAsync(HttpContext context, WebSocket socket, string? address, AgentOptions settings, CancellationToken ct)
    {
        var nonce = AgentSigning.NewNonce();
        var gate = new SemaphoreSlim(1, 1);
        await AgentFrames.SendAsync(socket, AgentFrames.Envelope(AgentMessageTypes.Challenge, new Challenge(nonce, DateTimeOffset.UtcNow)), gate, ct, compress: false);

        Hello? hello;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.HelloTimeoutSeconds, 1, 60)));
            try
            {
                var envelope = await AgentFrames.ReceiveAsync(socket, 64 * 1024, timeout.Token);
                hello = envelope is { T: AgentMessageTypes.Hello, B: { } body } ? AgentJson.Deserialize<Hello>(body) : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await CloseAsync(socket, AgentCloseCodes.HelloTimeout, "no hello in time");
                return null;
            }
            catch (Exception e) when (e is AgentFrameInvalidException or AgentFrameTooLargeException or JsonException)
            {
                await CloseAsync(socket, AgentCloseCodes.ProtocolError, "expected a hello");
                return null;
            }
        }
        if (hello is null || hello.Protocol != AgentProtocol.Version)
        {
            await CloseAsync(socket, AgentCloseCodes.ProtocolError, hello is null ? "expected a hello" : $"protocol {hello.Protocol} is not supported");
            return null;
        }

        await using var db = await dbs.CreateDbContextAsync(ct);
        var agent = await db.Agents.Include(a => a.Sensor).ThenInclude(s => s.Parent).SingleOrDefaultAsync(a => a.AgentId == hello.AgentId, ct);
        if (agent is null)
        {
            logger.LogInformation("Agent {Agent} from {Address}: unknown", hello.AgentId, address);
            await CloseAsync(socket, AgentCloseCodes.Unauthorized, "unknown agent");
            return null;
        }
        var name = $"{agent.Sensor.Parent.Name} / {agent.Sensor.Name}";
        if (agent.RevokedAt is not null || agent.PublicKey is null)
        {
            await CloseAsync(socket, AgentCloseCodes.Revoked, $"revoked{(agent.RevokedReason is { } r ? ": " + r : "")}");
            return null;
        }
        using var publicKey = AgentSigning.ImportPublicKey(agent.PublicKey);
        if (publicKey is null || !AgentSigning.VerifyHello(publicKey, hello, nonce))
        {
            AgentLog.Add(db, agent.SensorId, name, AgentLogKind.AuthFailed, "The hello's signature did not verify.", address: address);
            await db.SaveChangesAsync(ct);
            await CloseAsync(socket, AgentCloseCodes.Unauthorized, "signature");
            return null;
        }
        if (settings.CheckOrigin && !OriginMatches(context, hello.Origin))
        {
            AgentLog.Add(db, agent.SensorId, name, AgentLogKind.AuthFailed,
                $"The agent signed for {hello.Origin}, which is not this PNMT's address (PublicBaseUrl, or the Host the request came to).", address: address);
            await db.SaveChangesAsync(ct);
            await CloseAsync(socket, AgentCloseCodes.Unauthorized, "origin");
            return null;
        }

        var connection = new AgentConnection(agent.SensorId, agent.AgentId, name, socket, address, settings.MaxMessageBytes, agent.Platform, nonce)
        {
            Capabilities = hello.Capabilities.Take(50).Select(c => Cuts.Cut(c, 50)).ToList(),
        };
        if (connections.TryGetValue(agent.SensorId, out var previous))
        {
            previous.Replaced = true;
            await previous.CloseAsync(AgentCloseCodes.Replaced, "replaced by a newer connection");
        }
        connections[agent.SensorId] = connection;

        agent.Connected = true;
        agent.ConnectedAt = Clock.UtcNow;
        agent.LastSeenAt = agent.ConnectedAt;
        agent.RemoteAddress = address;
        agent.AgentVersion = Cuts.Cut(hello.Version, 50);
        agent.Architecture = Cuts.Cut(hello.Architecture, 20);
        agent.OsCaption = Cuts.CutOrNull(hello.Os.Caption, 200);
        agent.OsEdition = Cuts.CutOrNull(hello.Os.Edition, 100);
        agent.OsBuild = Cuts.CutOrNull(hello.Os.Build, 50);
        agent.OsDisplayVersion = Cuts.CutOrNull(hello.Os.DisplayVersion, 50);
        agent.OsSupported = hello.Os.Supported;
        agent.Capabilities = connection.Capabilities.ToList();
        agent.UninstalledAt = null;
        // W6 (§3.4): the agent's ECDH key, which the hello's signature covers; passwords typed per run are encrypted to it.
        if (hello.EncryptionKey is { Length: > 0 and <= 1024 } encryptionKey)
        {
            agent.EncryptionKey = encryptionKey;
        }
        // docs/linux-sensor-plan.md §4.6: the rights the server's own configuration gives PNMT, as the hello announces them. The
        // connection names them; a change since the last hello is a line of its own.
        var rights = hello.Rights is { } announced ? ServerRights.Bounded(announced) : null;
        var rightsJson = rights is null ? null : AgentJson.Serialize(rights);
        var previousRights = ServerRights.Read(agent.Rights);
        // jsonb comes back normalised (key order, spacing): compare what both serialise to, not the stored text.
        var rightsChanged = agent.Rights is not null && rightsJson != (previousRights is null ? null : AgentJson.Serialize(previousRights));
        agent.Rights = rightsJson;
        if (agent.Platform == AgentPlatform.Linux)
        {
            // §9.3: the kernel and distribution for the fleet; the system section sets the distribution's point release.
            agent.KernelRelease = Cuts.CutOrNull(hello.Os.Build, 100);
            agent.Distribution ??= Cuts.CutOrNull(hello.Os.Caption, 200);
        }
        AgentLog.Add(db, agent.SensorId, name, AgentLogKind.Connected,
            $"Connected: agent {agent.AgentVersion} ({agent.Architecture}) on {agent.OsCaption} {agent.OsBuild}{(hello.Os.Supported ? "" : ", not a supported version")}"
            + (rights is null ? "." : $"; rights on the server: {ServerRights.Describe(rights)}."),
            rights is null ? new { hello.Capabilities } : new { hello.Capabilities, Rights = rights }, address);
        if (rightsChanged)
        {
            AgentLog.Add(db, agent.SensorId, name, AgentLogKind.Warning,
                rights is null ? "The agent announced no rights any more: PNMT treats the server as granting nothing."
                    : $"Rights changed on the server: {ServerRights.Describe(rights)}.",
                new { Before = previousRights, After = rights }, address);
        }
        if (hello.Platform != agent.Platform)
        {
            // The row's platform is the token's, and authoritative; the hello's is only noted.
            AgentLog.Add(db, agent.SensorId, name, AgentLogKind.Warning,
                $"The agent says it runs on {hello.Platform}, but joined as a {agent.Platform} server: PNMT treats it as {agent.Platform}.", address: address);
        }
        if (hello.RestartedAfterCrash)
        {
            AgentLog.Add(db, agent.SensorId, name, AgentLogKind.Crashed, "The agent restarted after a crash: its problems follow.", address: address);
        }
        await db.SaveChangesAsync(ct);

        var config = await AgentConfiguration.BuildAsync(db, keys, settings, agent.SensorId, ct);
        var welcome = await keys.SignAsync(AgentMessageTypes.Welcome, agent.AgentId, config, ct, session: nonce);
        await connection.SendAsync(AgentMessageTypes.Welcome, welcome, ct);
        // docs/connectors-plan.md §9.1: a connector's call may have waited for this server.
        services.GetService<Connectors.ConnectorQueue>()?.Nudge();
        if (connection.Capabilities.Contains(AgentCapabilities.Parse))
        {
            // Script versions waiting for their parameters (§7.1) are parsed by the first agent that can.
            _ = Task.Run(async () =>
            {
                try
                {
                    await services.GetRequiredService<AgentScriptResults>().ParsePendingAsync(connection, CancellationToken.None);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Parsing pending scripts on {Name} failed", name);
                }
            });
        }
        if (agent.Platform == AgentPlatform.Linux)
        {
            // docs/linux-sensor-plan.md §6.5: removals that waited for this server go now.
            _ = Task.Run(async () =>
            {
                try
                {
                    await services.GetRequiredService<SshKeyManager.SshKeyRemovals>().SendPendingAsync(connection, CancellationToken.None);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Sending the pending key removals to {Name} failed", name);
                }
            });
        }
        return connection;
    }

    /// <summary>The origin the agent signed must be PNMT's: PublicBaseUrl's host, or the host the request came to (§3.4).</summary>
    private bool OriginMatches(HttpContext context, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var signed))
        {
            return false;
        }
        var hosts = new List<string>();
        if (configuration["PublicBaseUrl"] is { Length: > 0 } configured && Uri.TryCreate(configured, UriKind.Absolute, out var publicUrl))
        {
            hosts.Add(publicUrl.Host);
        }
        hosts.Add(context.Request.Host.Host);
        if (context.Request.Headers["X-Forwarded-Host"].FirstOrDefault() is { Length: > 0 } forwarded)
        {
            hosts.Add(forwarded.Split(',')[0].Trim().Split(':')[0]);
        }
        return hosts.Any(h => string.Equals(h, signed.Host, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ReceiveLoopAsync(AgentConnection connection, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var droppedAt = DateTime.MinValue;
        while (!ct.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
        {
            var envelope = await AgentFrames.ReceiveAsync(connection.Socket, connection.MaxMessageBytes, ct);
            if (envelope is null)
            {
                return;
            }
            var report = envelope.T is AgentMessageTypes.Section or AgentMessageTypes.Access or AgentMessageTypes.DiagResult;
            if (!connection.Allow(report, report ? settings.ReportsPerMinute : settings.MessagesPerMinute))
            {
                if (Clock.UtcNow - droppedAt > TimeSpan.FromMinutes(1))
                {
                    droppedAt = Clock.UtcNow;
                    await LogAsync(connection, AgentLogKind.Warning, $"Too many messages: \"{envelope.T}\" and others dropped for a minute.");
                }
                continue;
            }
            try
            {
                await DispatchAsync(connection, envelope, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException && e is not WebSocketException)
            {
                logger.LogWarning(e, "Agent {Name}: the message \"{Type}\" failed", connection.AgentName, envelope.T);
                await LogAsync(connection, AgentLogKind.Error, $"PNMTD could not process the message \"{envelope.T}\": {e.Message}");
            }
        }
    }

    private async Task DispatchAsync(AgentConnection connection, AgentEnvelope envelope, CancellationToken ct)
    {
        var body = envelope.B ?? default;
        switch (envelope.T)
        {
            case AgentMessageTypes.Heartbeat:
                await Report().HeartbeatAsync(connection, Read<Heartbeat>(body), ct);
                break;
            case AgentMessageTypes.Problem:
                await Report().ProblemsAsync(connection, Read<ProblemBatch>(body), ct);
                break;
            case AgentMessageTypes.Section:
                await Report().SectionAsync(connection, Read<SectionMessage>(body), ct);
                break;
            case AgentMessageTypes.Access:
                await services.GetRequiredService<AgentAccessService>().AccessAsync(connection, Read<AccessBatch>(body), ct);
                break;
            case AgentMessageTypes.DiagResult:
                await services.GetRequiredService<Diagnostics.DiagnosticsRunner>().ResultsAsync(connection, Read<DiagnosticResultBatch>(body), ct);
                break;
            case AgentMessageTypes.Ack:
                var ack = Read<Ack>(body);
                if (!connection.Complete(ack.RequestId, body) && !ack.Ok)
                {
                    await LogAsync(connection, AgentLogKind.Error, $"A request was refused: {ack.Error}");
                }
                else if (!ack.Ok)
                {
                    await LogAsync(connection, AgentLogKind.Request, $"The agent refused a request: {ack.Error}");
                }
                break;
            case AgentMessageTypes.Log:
                var lines = Read<LogLines>(body);
                connection.Complete(lines.RequestId, body);
                break;
            case AgentMessageTypes.KeyRemoval:
                // docs/linux-sensor-plan.md §6.5: recorded, then (as log does for send-log) it completes the request when it came first.
                // A refusal or failure is followed by an ack that says so, which completes the request itself and isn't logged as an error.
                var removal = Read<SshKeyRemovalResult>(body);
                await services.GetRequiredService<SshKeyManager.SshKeyRemovals>().ResultAsync(connection, removal, ct);
                if (removal.Outcome is SshKeyRemovalOutcome.Removed or SshKeyRemovalOutcome.NotFound)
                {
                    connection.Complete(removal.RequestId, body);
                }
                break;
            case AgentMessageTypes.ParseResult or AgentMessageTypes.ScriptResult:
                await services.GetRequiredService<AgentScriptResults>().ResultAsync(connection, envelope.T, body, ct);
                break;
            case AgentMessageTypes.Bye:
                await Report().ByeAsync(connection, Read<Bye>(body), ct);
                break;
            default:
                logger.LogInformation("Agent {Name}: ignored the unknown message \"{Type}\"", connection.AgentName, envelope.T);
                break;
        }
    }

    private AgentReportService Report() => services.GetRequiredService<AgentReportService>();

    private static T Read<T>(JsonElement body) =>
        AgentJson.Deserialize<T>(body) ?? throw new JsonException($"An empty {typeof(T).Name}.");

    private async Task DisconnectedAsync(AgentConnection connection)
    {
        if (connection.Replaced)
        {
            return;
        }
        connections.TryRemove(new KeyValuePair<Guid, AgentConnection>(connection.SensorId, connection));
        try
        {
            await using var db = await dbs.CreateDbContextAsync();
            var agent = await db.Agents.SingleOrDefaultAsync(a => a.SensorId == connection.SensorId);
            if (agent is not null && agent.AgentId == connection.AgentId)
            {
                agent.Connected = false;
                agent.DisconnectedAt = Clock.UtcNow;
                var socket = connection.Socket;
                var reason = socket.CloseStatus is { } status ? $" ({(int)status} {socket.CloseStatusDescription})".TrimEnd() : "";
                AgentLog.Add(db, connection.SensorId, connection.AgentName, AgentLogKind.Disconnected,
                    $"Disconnected after {Durations.Text(Clock.UtcNow - connection.ConnectedAt)}{reason}.", address: connection.Address);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Agent {Name}: recording the disconnect failed", connection.AgentName);
        }
    }

    private async Task LogAsync(AgentConnection connection, AgentLogKind kind, string message)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync();
            AgentLog.Add(db, connection.SensorId, connection.AgentName, kind, message, address: connection.Address);
            await db.SaveChangesAsync();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Agent {Name}: writing the agent log failed", connection.AgentName);
        }
    }

    private static async Task CloseAsync(WebSocket socket, int code, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseAsync((WebSocketCloseStatus)code, reason, timeout.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            socket.Abort();
        }
    }
}

/// <summary>Durations as the agent log and the UI write them: "3 d 4 h", "12 min", "40 s".</summary>
public static class Durations
{
    public static string Text(TimeSpan span)
    {
        span = span.Duration();
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays} d {span.Hours} h";
        }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours} h {span.Minutes} min";
        }
        if (span.TotalMinutes >= 1)
        {
            return $"{(int)span.TotalMinutes} min {span.Seconds} s";
        }
        return $"{(int)span.TotalSeconds} s";
    }
}
