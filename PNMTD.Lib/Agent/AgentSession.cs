using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace PNMTD.Lib.Agent;

/// <summary>
/// Who an agent is, as its session needs it: the ids, its key, PNMTD's pinned key, and what it announces. The platform and the
/// rights the server's own configuration grants (docs/linux-sensor-plan.md §3.4, §4.6) go into the hello as they are.
/// </summary>
public sealed record AgentIdentity(
    Guid AgentId,
    ECDsa AgentKey,
    ECDsa PnmtdKey,
    string Origin,
    string Version,
    string Architecture,
    AgentOs Os,
    IReadOnlyList<string> Capabilities,
    string? EncryptionKey = null,
    AgentPlatform Platform = AgentPlatform.Windows,
    AgentRightsSummary? Rights = null);

/// <summary>What the agent does with what PNMTD sends. Each call is isolated by the session: an exception is logged and answered, never fatal.</summary>
public interface IAgentSessionHandler
{
    /// <summary>A verified configuration: the welcome's, or a later <c>config</c>.</summary>
    Task OnConfigAsync(AgentConfig config, CancellationToken ct);

    /// <summary>A verified request (collect, run-diagnostics, send-log; W6 scripts). The session sends the answer as <c>ack</c>.</summary>
    Task<Ack> OnRequestAsync(AgentSession session, SignedContent request, CancellationToken ct);

    /// <summary>Something not acted on: an unknown type, a bad signature, a replay, an expired request.</summary>
    void OnIgnored(string type, string reason);
}

/// <summary>How a session ended: the close code and reason, when PNMTD sent one.</summary>
public sealed record AgentSessionEnd(int? CloseCode, string? Reason)
{
    /// <summary>4403: revoked. The agent stops retrying (§3.4).</summary>
    public bool Revoked => CloseCode == AgentCloseCodes.Revoked;
}

/// <summary>The handshake failed: PNMTD refused the hello, or answered something unexpected.</summary>
public sealed class AgentHandshakeException(string message, int? closeCode = null, string? reason = null) : Exception(message)
{
    public int? CloseCode { get; } = closeCode;

    public string? Reason { get; } = reason;
}

/// <summary>
/// The agent's side of a connection (docs/windows-sensor-plan.md §3.4–§3.6), shared by the Windows agent and
/// <c>pnmtd windows simulate</c>: the challenge and hello, the signed welcome, then messages both ways. Every request from
/// PNMTD is verified against the pinned key, bound to this agent and to this connection (the challenge's nonce, so a
/// request captured on one connection is refused on the next), unexpired by PNMTD's clock (the challenge's time gives
/// the offset), and run once.
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly AgentIdentity identity;
    private readonly int maxMessageBytes;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> seenRequests = new(StringComparer.Ordinal);
    private readonly string nonce;

    private AgentSession(WebSocket socket, AgentIdentity identity, int maxMessageBytes, TimeSpan serverOffset, AgentConfig config, string nonce)
    {
        this.socket = socket;
        this.identity = identity;
        this.nonce = nonce;
        this.maxMessageBytes = maxMessageBytes;
        ServerOffset = serverOffset;
        Config = config;
    }

    /// <summary>PNMTD's clock minus the agent's, from the challenge.</summary>
    public TimeSpan ServerOffset { get; }

    /// <summary>The configuration of the welcome, then of each <c>config</c>.</summary>
    public AgentConfig Config { get; private set; }

    public Guid AgentId => identity.AgentId;

    public bool IsOpen => socket.State == WebSocketState.Open;

    /// <summary>Answers the challenge on an open socket and waits for the signed welcome.</summary>
    public static async Task<AgentSession> HandshakeAsync(WebSocket socket, AgentIdentity identity, bool restartedAfterCrash, CancellationToken ct,
        int maxMessageBytes = AgentProtocol.DefaultMaxMessageBytes)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var challengeEnvelope = await AgentFrames.ReceiveAsync(socket, maxMessageBytes, timeout.Token)
            ?? throw Closed(socket, "PNMTD closed the connection before its challenge");
        if (challengeEnvelope.T != AgentMessageTypes.Challenge || challengeEnvelope.B is not { } cb
            || AgentJson.Deserialize<Challenge>(cb) is not { Nonce.Length: > 0 } challenge)
        {
            throw new AgentHandshakeException($"Expected a challenge, got \"{challengeEnvelope.T}\".");
        }
        var now = DateTimeOffset.UtcNow;
        var offset = challenge.ServerTime - now;
        var hello = new Hello(identity.AgentId, identity.Version, identity.Architecture, identity.Os, AgentProtocol.Version,
            identity.Capabilities, now, identity.Origin,
            AgentSigning.SignHello(identity.AgentKey, identity.Origin, identity.AgentId, challenge.Nonce, now, identity.EncryptionKey),
            restartedAfterCrash, identity.EncryptionKey, identity.Platform, identity.Rights);
        var gate = new SemaphoreSlim(1, 1);
        await AgentFrames.SendAsync(socket, AgentFrames.Envelope(AgentMessageTypes.Hello, hello), gate, timeout.Token, compress: false);

        var welcomeEnvelope = await AgentFrames.ReceiveAsync(socket, maxMessageBytes, timeout.Token)
            ?? throw Closed(socket, "PNMTD refused the hello");
        if (welcomeEnvelope.T != AgentMessageTypes.Welcome || welcomeEnvelope.B is not { } wb)
        {
            throw new AgentHandshakeException($"Expected the welcome, got \"{welcomeEnvelope.T}\".");
        }
        var config = VerifyConfig(identity, wb, AgentMessageTypes.Welcome, challenge.Nonce)
            ?? throw new AgentHandshakeException("The welcome is not signed by the PNMTD this agent joined for this connection: refused.");
        return new AgentSession(socket, identity, maxMessageBytes, offset, config, challenge.Nonce);
    }

    /// <summary>Sends a message; large ones go compressed.</summary>
    public Task SendAsync<T>(string type, T body, CancellationToken ct, bool? compress = null) =>
        AgentFrames.SendAsync(socket, AgentFrames.Envelope(type, body), sendGate, ct, compress);

    /// <summary>Receives until PNMTD closes the connection or <paramref name="ct"/> ends it.</summary>
    public async Task<AgentSessionEnd> RunAsync(IAgentSessionHandler handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AgentEnvelope? envelope;
            try
            {
                envelope = await AgentFrames.ReceiveAsync(socket, maxMessageBytes, ct);
            }
            catch (AgentFrameInvalidException e)
            {
                handler.OnIgnored("?", e.Message);
                continue;
            }
            if (envelope is null)
            {
                return new AgentSessionEnd((int?)socket.CloseStatus, socket.CloseStatusDescription);
            }
            await DispatchAsync(handler, envelope, ct);
        }
        return new AgentSessionEnd(null, "stopped");
    }

    private async Task DispatchAsync(IAgentSessionHandler handler, AgentEnvelope envelope, CancellationToken ct)
    {
        try
        {
            if (envelope.T == AgentMessageTypes.Config)
            {
                if (envelope.B is { } body && VerifyConfig(identity, body, AgentMessageTypes.Config, nonce) is { } config)
                {
                    Config = config;
                    await handler.OnConfigAsync(config, ct);
                }
                else
                {
                    handler.OnIgnored(envelope.T, "a configuration without PNMTD's valid signature");
                }
                return;
            }
            if (!AgentMessageTypes.SignedRequests.Contains(envelope.T))
            {
                handler.OnIgnored(envelope.T, "an unknown message type");
                return;
            }
            var signed = envelope.B is { } b ? AgentJson.Deserialize<SignedEnvelope>(b) : null;
            var content = signed is null ? null : AgentSigning.Verify(identity.PnmtdKey, signed);
            if (content is null)
            {
                handler.OnIgnored(envelope.T, "a request without PNMTD's valid signature");
                return;
            }
            var refusal = Check(content, envelope.T);
            if (refusal is not null)
            {
                handler.OnIgnored(envelope.T, refusal);
                await SendAsync(AgentMessageTypes.Ack, new Ack(content.RequestId, false, refusal), ct);
                return;
            }
            Ack ack;
            try
            {
                ack = await handler.OnRequestAsync(this, content, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                ack = new Ack(content.RequestId, false, $"{e.GetType().Name}: {e.Message}");
            }
            await SendAsync(AgentMessageTypes.Ack, ack, ct);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            handler.OnIgnored(envelope.T, $"unreadable: {e.Message}");
        }
    }

    /// <summary>
    /// Why a verified request is refused, or null: another agent's, another connection's, the wrong type, expired, or seen
    /// before.
    /// </summary>
    private string? Check(SignedContent content, string type)
    {
        if (content.Type != type)
        {
            return $"signed as {content.Type}, sent as {type}";
        }
        if (content.AgentId != identity.AgentId)
        {
            return "a request for another agent";
        }
        if (content.Session is not null && !string.Equals(content.Session, nonce, StringComparison.Ordinal))
        {
            return "a request for another connection";
        }
        var serverNow = DateTimeOffset.UtcNow + ServerOffset;
        if (content.ExpiresAt is not { } expires || expires < serverNow)
        {
            return "expired";
        }
        foreach (var (id, at) in seenRequests)
        {
            if (at < serverNow - TimeSpan.FromHours(1))
            {
                seenRequests.TryRemove(id, out _);
            }
        }
        return seenRequests.TryAdd(content.RequestId, serverNow) ? null : "a request seen before";
    }

    /// <summary>The welcome or a config: PNMTD's signature, the type, this agent, and this connection (when PNMTD names one).</summary>
    private static AgentConfig? VerifyConfig(AgentIdentity identity, JsonElement body, string type, string nonce)
    {
        var signed = AgentJson.Deserialize<SignedEnvelope>(body);
        var content = AgentSigning.Verify(identity.PnmtdKey, signed);
        if (content is null || content.Type != type || content.AgentId != identity.AgentId
            || (content.Session is not null && !string.Equals(content.Session, nonce, StringComparison.Ordinal)))
        {
            return null;
        }
        return AgentJson.Deserialize<AgentConfig>(content.Body);
    }

    private static AgentHandshakeException Closed(WebSocket socket, string what) =>
        new($"{what}: {(int?)socket.CloseStatus} {socket.CloseStatusDescription}".TrimEnd(), (int?)socket.CloseStatus, socket.CloseStatusDescription);

    public async Task CloseAsync(string reason, CancellationToken ct)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, ct);
            }
            catch (Exception)
            {
                // Best effort: the other side may be gone already.
                socket.Abort();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        sendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The HTTP side of joining (§3.3), shared by the agent's window, its command line and the simulator.</summary>
public static class AgentJoinClient
{
    public static async Task<JoinInfo> InfoAsync(HttpClient http, Uri baseUrl, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(baseUrl, AgentProtocol.JoinPath), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentJoinException("not_pnmt", response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "This URL is not a PNMT with Windows agents turned on (404)."
                : $"PNMT answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return AgentJson.Deserialize<JoinInfo>(text) is { Product: "pnmt" } info
                ? info
                : throw new AgentJoinException("not_pnmt", "This URL does not answer like PNMT.");
        }
        catch (JsonException)
        {
            throw new AgentJoinException("not_pnmt", "This URL does not answer like PNMT.");
        }
    }

    /// <summary>Joins, and verifies the answer with the key it carries: that key is pinned from now on.</summary>
    public static async Task<(JoinResponse Response, JoinGrant Grant)> JoinAsync(HttpClient http, Uri baseUrl, JoinRequest request, CancellationToken ct)
    {
        using var content = new StringContent(AgentJson.Serialize(request), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri(baseUrl, AgentProtocol.JoinPath), content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            AgentError? error = null;
            try
            {
                error = AgentJson.Deserialize<AgentError>(text);
            }
            catch (JsonException)
            {
            }
            throw new AgentJoinException(error?.Code ?? $"http_{(int)response.StatusCode}",
                error?.Message ?? $"PNMT answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        var answer = AgentJson.Deserialize<JoinResponse>(text) ?? throw new AgentJoinException("invalid_answer", "PNMT's answer is empty.");
        using var key = AgentSigning.ImportPublicKey(answer.SigningPublicKey)
            ?? throw new AgentJoinException("invalid_answer", "PNMT's signing key is not a P-256 key.");
        var signed = AgentSigning.Verify(key, answer.Signed);
        if (signed is null || signed.Type != AgentMessageTypes.Join || AgentJson.Deserialize<JoinGrant>(signed.Body) is not { } grant
            || grant.AgentId != signed.AgentId)
        {
            throw new AgentJoinException("invalid_answer", "PNMT's answer is not signed by its key: refused.");
        }
        if (AgentSigning.Fingerprint(answer.SigningPublicKey) != answer.SigningKeyFingerprint)
        {
            throw new AgentJoinException("invalid_answer", "PNMT's key and its fingerprint differ: refused.");
        }
        return (answer, grant);
    }
}

public sealed class AgentJoinException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
