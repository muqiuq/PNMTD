using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNMTD.Lib.Agent;

/// <summary>
/// The protocol between PNMTD and its agents (docs/windows-sensor-plan.md §3.5, §3.6): the paths on PNMT's existing
/// address and port, the WebSocket subprotocol, the message types, the close codes and the capabilities. Shared by
/// PNMTD, the Windows agent and <c>pnmtd windows simulate</c>.
/// </summary>
public static class AgentProtocol
{
    public const int Version = 1;

    public const string Subprotocol = "pnmt-agent.v1";

    public const string JoinPath = "/agent/v1/join";

    public const string WebSocketPath = "/agent/v1/ws";

    /// <summary>A message, compressed or not (§3.5): 4 MiB on the wire, 16 MiB decompressed.</summary>
    public const int DefaultMaxMessageBytes = 4 * 1024 * 1024;

    public const int DecompressionFactor = 4;

    /// <summary>A socket that has not authenticated within this time is closed (4408).</summary>
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The WebSocket ping, below nginx's 60 s idle timeout.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    /// <summary>How long a signed request from PNMTD is valid (§3.6).</summary>
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(5);
}

/// <summary>The message types (§3.6). Unknown types are ignored and logged on both sides.</summary>
public static class AgentMessageTypes
{
    public const string Challenge = "challenge";
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Config = "config";
    public const string Heartbeat = "heartbeat";
    public const string Section = "section";
    public const string Access = "access";
    public const string DiagResult = "diag-result";
    public const string Problem = "problem";
    public const string Collect = "collect";
    public const string RunDiagnostics = "run-diagnostics";
    public const string SendLog = "send-log";
    public const string Log = "log";
    public const string Ack = "ack";
    public const string Bye = "bye";

    // W6 (§7): scripts.
    public const string ParseScript = "parse-script";
    public const string ParseResult = "parse-result";
    public const string RunScript = "run-script";
    public const string ScriptResult = "script-result";

    /// <summary>What the join answer's signed part is typed as.</summary>
    public const string Join = "join";

    /// <summary>The requests an agent accepts from PNMTD, all signed. Before W6 there is no way to run anything (§6).</summary>
    public static readonly IReadOnlySet<string> SignedRequests = new HashSet<string>(StringComparer.Ordinal)
    {
        Collect, RunDiagnostics, SendLog, ParseScript, RunScript,
    };
}

/// <summary>The WebSocket close codes (§3.4).</summary>
public static class AgentCloseCodes
{
    /// <summary>A message that could not be read, or a hello that was wrong.</summary>
    public const int ProtocolError = 4400;

    /// <summary>The hello's signature did not verify, or the agent is unknown.</summary>
    public const int Unauthorized = 4401;

    /// <summary>The agent is revoked: it stops retrying.</summary>
    public const int Revoked = 4403;

    /// <summary>No hello within <see cref="AgentProtocol.HelloTimeout"/>.</summary>
    public const int HelloTimeout = 4408;

    /// <summary>A second connection of the same agent replaced this one.</summary>
    public const int Replaced = 4409;

    /// <summary>A message above the limits.</summary>
    public const int TooLarge = 4413;

    /// <summary>The Windows agents are turned off on PNMTD.</summary>
    public const int Disabled = 4404;
}

/// <summary>What an agent announces in its hello (§3.6, §5.5).</summary>
public static class AgentCapabilities
{
    public const string Collect = "collect.v1";
    public const string Access = "access.v1";
    public const string DiagPing = "diag.ping";
    public const string DiagDns = "diag.dns";

    /// <summary>W6: only when an administrator allowed scripts on the server itself (§7.2).</summary>
    public const string Scripts = "scripts.v1";

    /// <summary>W6: parsing scripts, which never runs them (§7.1).</summary>
    public const string Parse = "parse.v1";
}

/// <summary>The JSON of the protocol: camelCase, enums as their names, no nulls written, numbers strict.</summary>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
            // Messages and event log texts carry < and >: no need to escape them in a protocol that is never HTML.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Depth for nested sections; anything deeper is not ours.
            MaxDepth = 32,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? Deserialize<T>(JsonElement element) => element.Deserialize<T>(Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
