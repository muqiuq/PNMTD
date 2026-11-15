using System.Text.Json;

namespace PNMTD.Lib.Agent;

// The messages of docs/windows-sensor-plan.md §3.6, shared by both sides. Every frame is an
// envelope; its body is one of the records below. Requests from PNMTD and its configuration are
// signed (AgentSigning): their body is a SignedEnvelope.

/// <summary>A frame: <c>{ "t": type, "id": message id, "at": time, "b": body }</c>. Binary frames carry the same JSON, gzip-compressed.</summary>
public sealed record AgentEnvelope(string T, string? Id, DateTimeOffset At, JsonElement? B);

/// <summary>
/// Something PNMTD signed: <see cref="P"/> is the JSON text of a <see cref="SignedContent"/>, <see cref="S"/> PNMTD's
/// signature over it (§3.4). The text is verified as it is, then parsed: no canonical JSON needed.
/// </summary>
public sealed record SignedEnvelope(string P, string S);

/// <summary>
/// What PNMTD signs: the type (so a signed config can't pass as a request), the agent it is for, a request id that
/// runs once, an expiry (requests only), and the body.
/// </summary>
public sealed record SignedContent(string Type, Guid AgentId, string RequestId, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt, JsonElement Body);

// --- the handshake (§3.4) -------------------------------------------------------------------------------

/// <summary>PNMTD → agent, first: a fresh nonce to sign.</summary>
public sealed record Challenge(string Nonce, DateTimeOffset ServerTime);

/// <summary>
/// The OS an agent runs on (§3.9). <see cref="Supported"/> is the agent's own check: another version is flagged in PNMT, and
/// the agent runs anyway. On Linux (docs/linux-sensor-plan.md §4.8): the caption is os-release's PRETTY_NAME, the edition its
/// ID (debian, ubuntu, or proxmox on Proxmox VE), the version VERSION_ID, the build the kernel's release, and the display
/// version the codename.
/// </summary>
public sealed record AgentOs(string Caption, string? Edition, string? Version, string Build, string? DisplayVersion, bool Supported, string? ProductType = null);

/// <summary>Agent → PNMTD: who it is, signed over the challenge (§3.4).</summary>
public sealed record Hello(
    Guid AgentId,
    string Version,
    string Architecture,
    AgentOs Os,
    int Protocol,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset AgentTime,
    string Origin,
    string Signature,
    bool RestartedAfterCrash = false,
    string? EncryptionKey = null,
    AgentPlatform Platform = AgentPlatform.Windows,
    AgentRightsSummary? Rights = null);

/// <summary>
/// What the server's own configuration allows PNMT (docs/linux-sensor-plan.md §4.6): in the hello, which PNMT shows and uses to
/// grey out what a server refuses. On Linux from /etc/pnmt-agent/config.yaml; <see cref="Source"/> says whether the file was
/// applied (<c>config.yaml</c>), missing (<c>defaults</c>) or refused (<c>refused</c>, with <see cref="Problem"/>): a refused
/// file grants nothing.
/// </summary>
public sealed record AgentRightsSummary(
    string Source,
    string? Problem,
    bool KeyRemoval,
    IReadOnlyList<string> KeyRemovalUsers,
    int ProtectedKeys,
    IReadOnlyList<string> WritablePaths,
    bool Scripts,
    IReadOnlyList<string> ScriptRunAs,
    int AllowedScriptHashes,
    int MaxScriptTimeoutMinutes,
    bool SudoCommands,
    bool CronCommands,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// The configuration (§3.6): sent signed in <c>welcome</c> after the hello, and as <c>config</c> when it changes. The agent
/// keeps the last one it verified, so scheduled diagnostics run while PNMTD is away.
/// </summary>
public sealed record AgentConfig(
    int ConfigVersion,
    int HeartbeatSeconds,
    int FullUpdateHours,
    AccessConfig Access,
    IReadOnlyList<DiagnosticsAssignment> Diagnostics,
    string? LogLevel = null,
    WmiConfig? Wmi = null);

/// <summary>How logins and connections are recorded (§4.7).</summary>
public sealed record AccessConfig(int AggregateMinutes = 10, int MaxBatch = 500);

// --- reports (§3.5) ----------------------------------------------------------------------------------------

/// <summary>An interactive session now: who, how (Console, Rdp), its state.</summary>
public sealed record SessionInfo(string User, string Kind, string State, string? ClientAddress = null, DateTimeOffset? LogonAt = null);

/// <summary>Every minute (§3.5): about 500 bytes.</summary>
public sealed record Heartbeat(
    DateTimeOffset AgentTime,
    DateTimeOffset BootTime,
    double? CpuPercent,
    double? MemoryPercent,
    string? LowestVolume,
    double? LowestVolumeFreePercent,
    IReadOnlyList<SessionInfo> Sessions,
    bool RebootPending,
    IReadOnlyDictionary<string, string> SectionHashes,
    int ConfigVersion,
    string AgentVersion,
    int ProblemsSinceLast,
    string? Hostname = null,
    string? LastShutdown = null);

/// <summary>A section (§4): its name, schema version, hash, when it was collected, the data, and the collector's errors.</summary>
public sealed record SectionMessage(string Name, int SchemaVersion, string Hash, DateTimeOffset CollectedAt, JsonElement Data, IReadOnlyList<string>? Errors = null);

/// <summary>
/// Something failed on the agent (§3.8, rule 3): logged there, reported here, and the rest carried on. Deduplicated per
/// component and message per hour, so <see cref="Count"/> says how often since <see cref="FirstAt"/>.
/// </summary>
public sealed record AgentProblem(
    string Component,
    string Operation,
    string Message,
    string? ExceptionType,
    string? Stack,
    int Count,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    bool Fatal = false);

/// <summary>Problems in one message: those buffered while offline arrive together.</summary>
public sealed record ProblemBatch(IReadOnlyList<AgentProblem> Problems);

/// <summary>Agent → PNMTD: the answer to a signed request.</summary>
public sealed record Ack(string RequestId, bool Ok, string? Error = null);

/// <summary>Agent → PNMTD: the last lines of its log file, for <c>send-log</c>.</summary>
public sealed record LogLines(string RequestId, IReadOnlyList<string> Lines);

/// <summary>Agent → PNMTD: it is being uninstalled (§10.4).</summary>
public sealed record Bye(string Reason);

// --- requests (signed, §6) ---------------------------------------------------------------------------------

/// <summary>Collect these sections now, or all of them when empty.</summary>
public sealed record CollectRequest(IReadOnlyList<string> Sections);

/// <summary>Run an assignment the agent already has, with its checks as configured (§5.3).</summary>
public sealed record RunDiagnosticsRequest(Guid AssignmentId, Guid RunId);

/// <summary>Send the last lines of the log file, at most 500.</summary>
public sealed record SendLogRequest(int Lines);

// --- joining (§3.3) ------------------------------------------------------------------------------------------

/// <summary>
/// <c>GET /agent/v1/join</c>: the URL check. Over plain HTTP the agent shows the key's fingerprint for the administrator to compare
/// with PNMT's Add server dialog, and the join must answer the same key.
/// </summary>
public sealed record JoinInfo(string Product, string PnmtVersion, int Protocol, string SigningKeyFingerprint);

/// <summary><c>POST /agent/v1/join</c>.</summary>
public sealed record JoinRequest(
    string Token,
    string PublicKey,
    string Hostname,
    string? Fqdn,
    string? Domain,
    AgentOs Os,
    string AgentVersion,
    string Architecture,
    string? MachineGuid,
    string? Name,
    AgentPlatform Platform = AgentPlatform.Windows);

/// <summary>
/// The join's answer. <see cref="Signed"/> carries the agent's identity and first configuration (<see cref="JoinGrant"/>),
/// signed with the key in <see cref="SigningPublicKey"/>, which the agent pins from now on.
/// </summary>
public sealed record JoinResponse(string SigningPublicKey, string SigningKeyFingerprint, SignedEnvelope Signed);

/// <summary>The signed part of the join's answer.</summary>
public sealed record JoinGrant(Guid AgentId, Guid SensorId, string SensorName, string HostName, string AgentKeyFingerprint, AgentConfig Config);

/// <summary>A refused join or request: a stable code and a sentence for the window.</summary>
public sealed record AgentError(string Code, string Message);

// --- the agents' files (docs/linux-sensor-plan.md §4.4) ------------------------------------------------------

/// <summary>
/// A file PNMTD serves: its name, size and SHA-256 (hex, lower case), checked against its <c>.sha256</c>. <see cref="Source"/>
/// (docs/agent-update-plan.md §5.1): where PNMT got it, for the update to print: <i>GitHub's release sensor-1.8.0</i>, or
/// <i>uploaded to PNMT by alice on 2026-11-20</i>. Agents before 1.8 ignore it.
/// </summary>
public sealed record DownloadAsset(string Name, long Size, string Sha256, string? Source = null);

/// <summary>
/// <c>GET /agent/v1/download</c>'s signed part (<see cref="AgentMessageTypes.DownloadManifest"/>, agent id empty): the version a
/// platform's agents get (docs/agent-update-plan.md §4, §5.1; PNMTD's own unless an admin chose another) and its files that are
/// here and verified. <c>pnmt-agent update</c> checks it against the key it pinned at the join, so a TLS-inspecting proxy can't
/// hand over another binary. <see cref="Platform"/> and <see cref="PreRelease"/> from 1.8; the envelope expires after
/// <see cref="AgentProtocol.ManifestLifetime"/>, which agents before 1.8 don't check.
/// </summary>
public sealed record DownloadManifest(string Version, IReadOnlyList<DownloadAsset> Assets, AgentPlatform? Platform = null, bool PreRelease = false);

/// <summary><c>GET /agent/v1/download</c>: the manifest, signed with PNMTD's agent signing key, and that key's fingerprint.</summary>
public sealed record DownloadManifestResponse(string SigningKeyFingerprint, SignedEnvelope Signed);

// --- removing SSH keys (docs/linux-sensor-plan.md §6.5) ------------------------------------------------------

/// <summary>
/// PNMTD → agent (signed): remove every line with this key from this account's file. The agent checks config.yaml, the account,
/// the protected keys, and that the path is one it found itself for that account; PNMT can't name any other path.
/// </summary>
public sealed record RemoveSshKeyRequest(Guid RemovalId, string Account, string Path, string Fingerprint);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SshKeyRemovalOutcome>))]
public enum SshKeyRemovalOutcome { Removed, NotFound, Refused, Failed }

/// <summary>
/// Agent → PNMTD: what a removal did: the lines removed, the backup's name (for <c>pnmt-agent keys restore</c>), the file's new
/// SHA-256, or why not. Like <c>log</c> for <c>send-log</c>, it completes the waiting request.
/// </summary>
public sealed record SshKeyRemovalResult(
    string RequestId,
    Guid RemovalId,
    SshKeyRemovalOutcome Outcome,
    int LinesRemoved,
    string? Backup,
    string? NewFileHash,
    string? Message);
