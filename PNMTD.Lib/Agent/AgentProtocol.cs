using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNMTD.Lib.Agent;

/// <summary>
/// The protocol between PNMTD and its agents (docs/windows-sensor-plan.md §3.5, §3.6; docs/linux-sensor-plan.md §3.4): the
/// paths on PNMT's existing address and port, the WebSocket subprotocol, the message types, the close codes and the
/// capabilities. Shared by PNMTD, the Windows and Linux agents and <c>pnmtd agent simulate</c>.
/// </summary>
public static class AgentProtocol
{
    public const int Version = 1;

    public const string Subprotocol = "pnmt-agent.v1";

    public const string JoinPath = "/agent/v1/join";

    public const string WebSocketPath = "/agent/v1/ws";

    /// <summary>
    /// The agents' files, served by PNMTD itself (docs/linux-sensor-plan.md §4.4): <c>GET /agent/v1/download</c> answers the
    /// signed manifest, <c>GET /agent/v1/download/{asset}</c> one of <see cref="AgentDownloadAssets.All"/>. Anonymous: a server
    /// isn't joined yet when it downloads, and the files are public on GitHub anyway.
    /// </summary>
    public const string DownloadPath = "/agent/v1/download";

    /// <summary>
    /// How long a signed manifest is valid (docs/agent-update-plan.md §5.1): an old one can't be replayed to hand over an older
    /// version. Agents from 1.8 check it.
    /// </summary>
    public static readonly TimeSpan ManifestLifetime = TimeSpan.FromMinutes(10);

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

/// <summary>
/// The platform an agent runs on (docs/linux-sensor-plan.md §3.4): in the hello and the join, optional, Windows when missing, so
/// Windows agents built before it need no change. A join token is made for one platform.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentPlatform>))]
public enum AgentPlatform { Windows, Linux }

/// <summary>The files PNMTD serves under <see cref="AgentProtocol.DownloadPath"/>: a fixed list, anything else is 404.</summary>
public static class AgentDownloadAssets
{
    public const string LinuxX64 = "pnmt-agent-linux-x64";
    public const string LinuxArm64 = "pnmt-agent-linux-arm64";
    public const string WindowsX64 = "pnmt-sensor-x64.exe";
    public const string WindowsArm64 = "pnmt-sensor-arm64.exe";

    /// <summary>The executables; each has its <c>.sha256</c> next to it (<c>sha256sum</c>'s format: the hash, two spaces, the name).</summary>
    public static readonly IReadOnlyList<string> Executables = [LinuxX64, LinuxArm64, WindowsX64, WindowsArm64];

    public static readonly IReadOnlyList<string> All = [.. Executables, .. Executables.Select(e => e + ".sha256")];

    public static bool IsKnown(string? name) => name is not null && All.Contains(name, StringComparer.Ordinal);

    public static AgentPlatform PlatformOf(string executable) => executable.EndsWith(".exe", StringComparison.Ordinal) ? AgentPlatform.Windows : AgentPlatform.Linux;

    /// <summary>The executable for a platform and an architecture (<c>x64</c>, <c>arm64</c>).</summary>
    public static string For(AgentPlatform platform, string architecture) => (platform, architecture) switch
    {
        (AgentPlatform.Linux, "arm64") => LinuxArm64,
        (AgentPlatform.Linux, _) => LinuxX64,
        (_, "arm64") => WindowsArm64,
        _ => WindowsX64,
    };
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

    // Linux (docs/linux-sensor-plan.md §6.5): removing a key from an authorized_keys file, where config.yaml allows it.
    public const string RemoveSshKey = "remove-ssh-key";
    public const string KeyRemoval = "key-removal";

    // Linux (docs/linux-os-updates-plan.md §4.6): OS updates. PNMTD → agent: run, send a log again, results received; agent → PNMTD
    // the rest.
    public const string OsUpdateRun = "os-update-run";
    public const string OsUpdateLog = "os-update-log";
    public const string OsUpdateReceived = "os-update-received";
    public const string OsUpdateStarted = "os-update-started";
    public const string OsUpdateOutput = "os-update-output";
    public const string OsUpdateResult = "os-update-result";
    public const string OsUpdateHistory = "os-update-history";
    public const string OsChangelogs = "os-changelogs";

    /// <summary>What the download manifest's signed part is typed as (docs/linux-sensor-plan.md §4.4).</summary>
    public const string DownloadManifest = "download-manifest";

    /// <summary>What the join answer's signed part is typed as.</summary>
    public const string Join = "join";

    /// <summary>The requests an agent accepts from PNMTD, all signed. Before W6 there is no way to run anything (§6).</summary>
    public static readonly IReadOnlySet<string> SignedRequests = new HashSet<string>(StringComparer.Ordinal)
    {
        Collect, RunDiagnostics, SendLog, ParseScript, RunScript, RemoveSshKey, OsUpdateRun, OsUpdateLog, OsUpdateReceived,
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

    /// <summary>The agents are turned off on PNMTD.</summary>
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

    /// <summary>Windows: the parse lists the functions defined at a script's top level (docs/connectors-plan.md §8.3); always with <see cref="Parse"/>.</summary>
    public const string ParseFunctions = "parse.functions.v1";

    /// <summary>
    /// Windows and Linux: the agent calls a function when a run names one (docs/connectors-plan.md §8.3), only with <see cref="Scripts"/>.
    /// PNMTD never sends a function to an agent without it: an older agent would ignore the field and run the whole script.
    /// </summary>
    public const string ScriptFunctions = "scripts.functions.v1";

    /// <summary>Linux: the <c>ssh-keys</c> section (docs/linux-sensor-plan.md §6).</summary>
    public const string Keys = "keys.v1";

    /// <summary>Linux: removing keys, only while config.yaml allows it (§6.5).</summary>
    public const string KeysRemove = "keys.remove.v1";

    /// <summary>
    /// Linux: OS updates (docs/linux-os-updates-plan.md §4.6): the os-updates section, runs, history and changelogs. Announced by every
    /// agent that has the feature; what config.yaml allows is in the hello's rights (<see cref="AgentRightsSummary.OsUpdates"/>).
    /// </summary>
    public const string OsUpdates = "os-updates.v1";
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
