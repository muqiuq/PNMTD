using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using PNMTD.Lib.Agent;

namespace PNMTD.Cli;

/// <summary>What a simulated agent keeps between runs: PNMT, its identity and key, and PNMTD's pinned key.</summary>
public sealed record SimulatedAgentState(string Url, Guid AgentId, Guid SensorId, string PrivateKey, string PnmtdKey, string Hostname);

/// <summary>
/// A fake Windows agent (docs/windows-sensor-plan.md §11), for development without Windows and for the tests: it joins with a
/// token, connects with the same session code as the real agent, sends heartbeats and canned sections and logins, and
/// answers collect, run-diagnostics and send-log.
/// </summary>
public sealed class WindowsSimulator(Uri baseUrl, HttpClient http, Func<CancellationToken, Task<WebSocket>> connect, TextWriter output) : IAgentSessionHandler
{
    public const string AgentVersion = "0.0.0-sim";

    private readonly DateTimeOffset bootTime = DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(-17);
    private readonly List<string> log = [];
    private AgentSession? session;
    private string hostname = "SIM-SRV01";

    public static AgentOs Os { get; } = new("Microsoft Windows Server 2019 Standard", "ServerStandard", "10.0.17763", "17763.6532", "1809", true, "Server");

    public static IReadOnlyList<string> Capabilities { get; } =
        [AgentCapabilities.Collect, AgentCapabilities.Access, AgentCapabilities.DiagPing, AgentCapabilities.DiagDns];

    /// <summary>Heartbeats sent, for the tests.</summary>
    public int Heartbeats { get; private set; }

    /// <summary>Requests answered, by type, for the tests.</summary>
    public List<string> Requests { get; } = [];

    public async Task<SimulatedAgentState> JoinAsync(string token, string? name, string hostname, CancellationToken ct)
    {
        this.hostname = hostname;
        var info = await AgentJoinClient.InfoAsync(http, baseUrl, ct);
        Write($"PNMT {info.PnmtVersion}, its key {info.SigningKeyFingerprint}");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new JoinRequest(token, AgentSigning.PublicKey(key), hostname, $"{hostname.ToLowerInvariant()}.sim.example", "SIM", Os, AgentVersion, "x64",
            Guid.NewGuid().ToString(), name);
        var (response, grant) = await AgentJoinClient.JoinAsync(http, baseUrl, request, ct);
        Write($"Joined as {grant.HostName} / {grant.SensorName}: agent {grant.AgentId}, this key {grant.AgentKeyFingerprint}");
        return new SimulatedAgentState(baseUrl.ToString(), grant.AgentId, grant.SensorId, Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            response.SigningPublicKey, hostname);
    }

    /// <summary>Connects and reports until <paramref name="ct"/> ends, reconnecting after a second; <paramref name="heartbeats"/> stops after so many.</summary>
    public async Task<AgentSessionEnd> RunAsync(SimulatedAgentState state, CancellationToken ct, TimeSpan? interval = null, int? heartbeats = null)
    {
        hostname = state.Hostname;
        using var agentKey = ECDsa.Create();
        agentKey.ImportPkcs8PrivateKey(Convert.FromBase64String(state.PrivateKey), out _);
        using var pnmtdKey = AgentSigning.ImportPublicKey(state.PnmtdKey) ?? throw new InvalidOperationException("The state's PNMTD key is invalid.");
        var identity = new AgentIdentity(state.AgentId, agentKey, pnmtdKey, AgentSigning.Origin(baseUrl), AgentVersion, "x64", Os, Capabilities);
        while (!ct.IsCancellationRequested)
        {
            var socket = await connect(ct);
            AgentSession current;
            try
            {
                current = await AgentSession.HandshakeAsync(socket, identity, restartedAfterCrash: false, ct);
            }
            catch (AgentHandshakeException e)
            {
                Write($"Refused: {e.Message}");
                socket.Dispose();
                if (e.CloseCode == AgentCloseCodes.Revoked)
                {
                    return new AgentSessionEnd(e.CloseCode, e.Reason);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }
            await using (current)
            {
                session = current;
                Write($"Connected; configuration {current.Config.ConfigVersion}, heartbeat every {current.Config.HeartbeatSeconds} s, {current.Config.Diagnostics.Count} diagnostics assignments");
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var receiving = current.RunAsync(this, stop.Token);
                var sending = ReportAsync(current, interval ?? TimeSpan.FromSeconds(current.Config.HeartbeatSeconds), heartbeats, stop.Token);
                AgentSessionEnd end;
                try
                {
                    var first = await Task.WhenAny(receiving, sending);
                    if (first == sending)
                    {
                        await sending;
                        await current.CloseAsync("done", CancellationToken.None);
                        return new AgentSessionEnd(1000, "done");
                    }
                    end = await receiving;
                }
                catch (Exception e) when (ct.IsCancellationRequested || e is WebSocketException or OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await current.CloseAsync("stopped", closing.Token);
                        return new AgentSessionEnd(1000, "stopped");
                    }
                    end = new AgentSessionEnd(null, e.Message);
                }
                finally
                {
                    stop.Cancel();
                }
                Write($"Disconnected: {end.CloseCode} {end.Reason}");
                if (end.Revoked)
                {
                    return end;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return new AgentSessionEnd(null, "stopped");
    }

    private async Task ReportAsync(AgentSession current, TimeSpan interval, int? count, CancellationToken ct)
    {
        await SendSectionsAsync(current, [], ct);
        while (!ct.IsCancellationRequested && (count is null || Heartbeats < count))
        {
            await current.SendAsync(AgentMessageTypes.Heartbeat, Heartbeat(current), ct);
            Heartbeats++;
            if (count is null || Heartbeats < count)
            {
                await Task.Delay(interval, ct);
            }
        }
    }

    public Heartbeat Heartbeat(AgentSession current) => new(DateTimeOffset.UtcNow, bootTime, 7.5 + Random.Shared.NextDouble() * 10, 41.2, "C:", 63.5,
        [new SessionInfo("SIM\\admin", "Rdp", "Active", "10.0.4.17")], false, SimulatedSections.Hashes(hostname), current.Config.ConfigVersion, AgentVersion, 0,
        hostname, "Restarted for updates (planned)");

    private Task SendSectionsAsync(AgentSession current, IReadOnlyList<string> names, CancellationToken ct) =>
        SimulatedSections.SendAsync(current, hostname, names, ct);

    public Task OnConfigAsync(AgentConfig config, CancellationToken ct)
    {
        Write($"Configuration {config.ConfigVersion}: {config.Diagnostics.Count} diagnostics assignments");
        return Task.CompletedTask;
    }

    public async Task<Ack> OnRequestAsync(AgentSession current, SignedContent request, CancellationToken ct)
    {
        Requests.Add(request.Type);
        Write($"Request {request.Type} ({request.RequestId})");
        switch (request.Type)
        {
            case AgentMessageTypes.Collect:
                var collect = AgentJson.Deserialize<CollectRequest>(request.Body);
                await SendSectionsAsync(current, collect?.Sections ?? [], ct);
                return new Ack(request.RequestId, true);
            case AgentMessageTypes.SendLog:
                var lines = log.TakeLast(Math.Clamp(AgentJson.Deserialize<SendLogRequest>(request.Body)?.Lines ?? 100, 1, 500)).ToList();
                await current.SendAsync(AgentMessageTypes.Log, new LogLines(request.RequestId, lines), ct);
                return new Ack(request.RequestId, true);
            case AgentMessageTypes.RunDiagnostics:
                var run = AgentJson.Deserialize<RunDiagnosticsRequest>(request.Body)!;
                var result = SimulatedSections.Diagnostics(current.Config, run.AssignmentId, run.RunId, "manual");
                if (result is null)
                {
                    return new Ack(request.RequestId, false, "No such assignment in this agent's configuration.");
                }
                await current.SendAsync(AgentMessageTypes.DiagResult, new DiagnosticResultBatch([result]), ct);
                return new Ack(request.RequestId, true);
            default:
                return new Ack(request.RequestId, false, $"The simulator does not know \"{request.Type}\".");
        }
    }

    public void OnIgnored(string type, string reason) => Write($"Ignored {type}: {reason}");

    private void Write(string line)
    {
        var text = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [INF] simulator: {line}";
        lock (log)
        {
            log.Add(text);
            if (log.Count > 1000)
            {
                log.RemoveAt(0);
            }
        }
        output.WriteLine(line);
    }

    /// <summary>The WebSocket URL of a PNMT URL: http → ws, https → wss.</summary>
    public static Uri WebSocketUrl(Uri baseUrl)
    {
        var builder = new UriBuilder(new Uri(baseUrl, AgentProtocol.WebSocketPath)) { Scheme = baseUrl.Scheme == "https" ? "wss" : "ws" };
        return builder.Uri;
    }

    public static string Save(SimulatedAgentState state) => JsonSerializer.Serialize(state, AgentJson.Options);

    public static SimulatedAgentState Load(string json) => JsonSerializer.Deserialize<SimulatedAgentState>(json, AgentJson.Options)
        ?? throw new InvalidOperationException("The state file is empty.");
}

/// <summary>The simulator's canned data: sections (W2, W3), logins (W3) and diagnostics results (W4).</summary>
public static class SimulatedSections
{
    public static IReadOnlyDictionary<string, string> Hashes(string hostname) => new Dictionary<string, string>();

    public static Task SendAsync(AgentSession session, string hostname, IReadOnlyList<string> names, CancellationToken ct) => Task.CompletedTask;

    public static DiagnosticRunResult? Diagnostics(AgentConfig config, Guid assignmentId, Guid runId, string trigger) => null;
}
