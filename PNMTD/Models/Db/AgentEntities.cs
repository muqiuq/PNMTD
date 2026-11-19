using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace PNMTD.Models.Db;

// The agents of both platforms (docs/linux-sensor-plan.md §3.3, §9.3; docs/windows-sensor-plan.md §8.3, where the tables
// were windows_*): renamed in L1 (migration GeneraliseAgents). What stays Windows' own is in WindowsEntities.cs.

/// <summary>
/// PNMTD's side of the agents' trust (docs/windows-sensor-plan.md §3.4, §8.3): its ECDSA P-256 signing key, under Data
/// Protection (purpose PNMTD.Windows.AgentSigning.v1, kept from before L1 so the key stays readable) with the key ring on
/// the keys volume, as the MikroTik keys are. One row, for agents of every platform.
/// </summary>
[Table("agent_settings")]
[Audited]
public class AgentSettingsEntity
{
    public const int SingletonId = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = SingletonId;

    /// <summary>The PKCS#8 private key, Base64, protected.</summary>
    [AuditSecret]
    public string SigningKeyProtected { get; set; } = "";

    /// <summary>SubjectPublicKeyInfo, Base64: what agents pin at their join.</summary>
    [AuditPlain("a public key")]
    public string SigningPublicKey { get; set; } = "";

    [AuditPlain("a fingerprint")]

    public string SigningKeyFingerprint { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>Raised whenever what agents are configured with changes (diagnostics, intervals): <c>config</c> goes out.</summary>
    public int ConfigVersion { get; set; } = 1;

    /// <summary>
    /// The version Windows agents get (docs/agent-update-plan.md §4): what their update installs, what the links offer, and
    /// what makes one outdated. Null: PNMTD's own version.
    /// </summary>
    [MaxLength(40)]
    public string? WindowsAgentVersion { get; set; }

    /// <summary>The version Linux agents get, as <see cref="WindowsAgentVersion"/>.</summary>
    [MaxLength(40)]
    public string? LinuxAgentVersion { get; set; }
}

/// <summary>
/// A join token (docs/windows-sensor-plan.md §3.3): single use, valid 24 hours by default, bound to an existing host, and
/// for a re-join to its sensor. Only the hash is stored; the token is shown once. It is made for one platform: an agent
/// of the other is refused (docs/linux-sensor-plan.md §3.3).
/// </summary>
[Table("agent_join_tokens")]
[Audited]
public class AgentJoinTokenEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The platform the token was made for; the join's own must match it.</summary>
    public Lib.Agent.AgentPlatform Platform { get; set; }

    [MaxLength(64)]
    [AuditSecret]
    public string TokenHash { get; set; } = "";

    /// <summary>The first two characters, for the list.</summary>
    [MaxLength(2)]
    public string Prefix { get; set; } = "";

    public Guid HostId { get; set; }

    /// <summary>A re-join: the server it replaces the key of.</summary>
    public Guid? SensorId { get; set; }

    /// <summary>The new sensor's name; null: the platform's ("Windows", "Linux").</summary>
    [MaxLength(200)]
    public string? Name { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    [MaxLength(200)]
    public string? CreatedByName { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    [MaxLength(100)]
    public string? UsedFromAddress { get; set; }

    /// <summary>The server the join created or re-joined.</summary>
    public Guid? ResultSensorId { get; set; }

    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// A WINDOWS or LINUX sensor's agent, 1:1, deleted with it (docs/windows-sensor-plan.md §8.3): its platform, its key, what
/// it reported about itself, its connection, its last heartbeat, and the server's event switches.
/// </summary>
[Table("agents")]
[Audited]
public class AgentEntity
{
    [Key]
    public Guid SensorId { get; set; }

    public virtual SensorEntity Sensor { get; set; } = null!;

    /// <summary>From the join token, and authoritative: the hello's own platform is only logged (docs/linux-sensor-plan.md §3.3).</summary>
    public Lib.Agent.AgentPlatform Platform { get; set; }

    /// <summary>The agent's id: new at each join, so a re-join's key has a name of its own on the server.</summary>
    public Guid AgentId { get; set; }

    /// <summary>The agent's ECDSA P-256 public key, SPKI Base64. Null once revoked.</summary>
    [AuditPlain("the agent's public key")]
    public string? PublicKey { get; set; }

    [AuditPlain("a fingerprint")]

    public string KeyFingerprint { get; set; } = "";

    /// <summary>W6 (§3.4): the agent's ECDH P-256 key for passwords typed per run.</summary>
    [AuditSecret]
    public string? EncryptionKey { get; set; }

    public DateTime JoinedAt { get; set; }

    /// <summary>Windows' MachineGuid, Linux's /etc/machine-id: two servers with the same one are noted in the agent log.</summary>
    [MaxLength(64)]
    public string? MachineId { get; set; }

    [MaxLength(255)]
    public string Hostname { get; set; } = "";

    [MaxLength(255)]
    public string? Fqdn { get; set; }

    [MaxLength(255)]
    public string? Domain { get; set; }

    [MaxLength(200)]
    public string? OsCaption { get; set; }

    [MaxLength(100)]
    public string? OsEdition { get; set; }

    [MaxLength(50)]
    public string? OsBuild { get; set; }

    [MaxLength(50)]
    public string? OsDisplayVersion { get; set; }

    /// <summary>The agent's own version check (§3.9): false shows "not a supported version".</summary>
    public bool OsSupported { get; set; } = true;

    [MaxLength(20)]
    public string? Architecture { get; set; }

    [MaxLength(50)]
    public string? AgentVersion { get; set; }

    /// <summary>What it announced in its last hello.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>The configuration version the agent last confirmed in a heartbeat.</summary>
    public int ConfigVersion { get; set; }

    public bool Connected { get; set; }

    public DateTime? ConnectedAt { get; set; }

    public DateTime? DisconnectedAt { get; set; }

    public DateTime? LastSeenAt { get; set; }

    [MaxLength(100)]
    public string? RemoteAddress { get; set; }

    public DateTime? BootTime { get; set; }

    /// <summary>The agent's clock minus PNMTD's, from the last heartbeat.</summary>
    public int? ClockSkewSeconds { get; set; }

    /// <summary>The last heartbeat as JSON (<see cref="Lib.Agent.Heartbeat"/>).</summary>
    [Column(TypeName = "jsonb")]
    public string? Heartbeat { get; set; }

    /// <summary>Heartbeats in a row that reported problems: 451 from three on (§3.8, rule 3).</summary>
    public int ProblemHeartbeats { get; set; }

    public DateTime? LastProblemAt { get; set; }

    // --- the server's event switches (§4.7, §4.8) ---------------------------------------------------------

    public bool AdminLoginEvents { get; set; } = true;

    public bool FailedLoginEvents { get; set; } = true;

    public int FailedLoginThreshold { get; set; } = 10;

    public bool CriticalEvents { get; set; }

    /// <summary>Windows only. WMI (§16): built-in queries switched off or with another timeout, and own queries, as JSON (<see cref="Lib.Agent.WmiConfig"/>).</summary>
    public string? WmiSettings { get; set; }

    // --- Linux (docs/linux-sensor-plan.md §9.3, migration AddLinuxAgents) --------------------------------------

    /// <summary>
    /// What the server's own configuration allows PNMT, from its last hello (<see cref="Lib.Agent.AgentRightsSummary"/>, §4.6): on
    /// Linux from /etc/pnmt-agent/config.yaml. Null when the agent announced none (Windows today).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Rights { get; set; }

    /// <summary>Linux: 465 for a key that isn't registered as active (§6.3). Stored now, used from L5 on.</summary>
    [AuditPlain("a setting")]
    public bool KeyAddedEvents { get; set; } = true;

    /// <summary>Linux: 462 when security updates have been pending longer than <see cref="SecurityUpdateDays"/> (§9.2). Off by default.</summary>
    public bool SecurityUpdateEvents { get; set; }

    public int SecurityUpdateDays { get; set; } = 7;

    /// <summary>Linux: when the system section first reported pending security updates; cleared when none are pending.</summary>
    public DateTime? SecurityUpdatesSince { get; set; }

    /// <summary>Linux: the security updates pending now, from the system section, for the list and 462.</summary>
    public int SecurityUpdates { get; set; }

    /// <summary>Linux: the first of their packages, for 462's message: "openssl, libssl3, …".</summary>
    [MaxLength(500)]
    public string? SecurityPackages { get; set; }

    /// <summary>Linux: the kernel's release (6.1.0-28-amd64), from the hello and the system section.</summary>
    [MaxLength(100)]
    public string? KernelRelease { get; set; }

    /// <summary>Linux: the distribution with its point release (Debian 12.8, Proxmox VE 9.0.10), from the system section; the pretty name before.</summary>
    [MaxLength(200)]
    public string? Distribution { get; set; }

    public DateTime? RevokedAt { get; set; }

    [MaxLength(500)]
    public string? RevokedReason { get; set; }

    /// <summary>The agent said <c>bye</c>: the service was uninstalled (§10.4). The sensor and its history stay.</summary>
    public DateTime? UninstalledAt { get; set; }

    /// <summary>Windows: from the remote-access section, for the list: "RDP · WinRM · SMB open" (§4.6).</summary>
    [MaxLength(300)]
    public string? RemoteAccessSummary { get; set; }

    /// <summary>Windows: from the access section, for the list: what the audit policy and the logs miss (§4.7).</summary>
    public int AuditGaps { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentLogKind>))]
public enum AgentLogKind
{
    Joined,
    Connected,
    Disconnected,
    AuthFailed,
    Revoked,
    Uninstalled,
    ConfigSent,
    Request,
    Problem,
    Crashed,
    Error,
    Warning,
}

/// <summary>
/// The agents' permanent log (§8.3): joins, connections, problems, crashes, failures, revocations. It outlives the server,
/// like mikrotik_job_log, and is kept a year.
/// </summary>
[Table("agent_log")]
[NotAudited("the agents' own log")]
public class AgentLogEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid? SensorId { get; set; }

    /// <summary>The server's host and sensor name when this was written: what is left once it is gone.</summary>
    [MaxLength(500)]
    public string AgentName { get; set; } = "";

    public DateTime At { get; set; }

    public AgentLogKind Kind { get; set; }

    [MaxLength(4000)]
    public string Message { get; set; } = "";

    /// <summary>A problem's component, exception, stack and count; a join's fingerprints.</summary>
    [Column(TypeName = "jsonb")]
    public string? Details { get; set; }

    [MaxLength(100)]
    public string? Address { get; set; }
}

/// <summary>
/// A server's latest section of each kind (docs/windows-sensor-plan.md §4, §8.3): the gzip-compressed JSON, its hash, when it
/// was collected and received, and its collector's errors. A section with a new hash is compared with this one first (§4.9).
/// </summary>
[Table("agent_sections")]
[NotAudited("what an agent reported")]
public class AgentSectionEntity
{
    public Guid SensorId { get; set; }

    [MaxLength(40)]
    public string Section { get; set; } = "";

    public int SchemaVersion { get; set; }

    [MaxLength(64)]
    public string Hash { get; set; } = "";

    public DateTime CollectedAt { get; set; }

    public DateTime ReceivedAt { get; set; }

    /// <summary>When the hash last changed.</summary>
    public DateTime ChangedAt { get; set; }

    public byte[] Content { get; set; } = [];

    public List<string> Errors { get; set; } = [];
}

/// <summary>
/// A login or connection (docs/windows-sensor-plan.md §4.7, §8.3), or an aggregate of identical ones, deduplicated by its log
/// and the record id of its first event: sent again, it is updated (a higher count, a logoff). Kept 180 days. On Linux
/// (docs/linux-sensor-plan.md §5.6) also sudo and su, file transfers, and the key of a key login.
/// </summary>
[Table("agent_logins")]
[NotAudited("logins an agent reported")]
public class AgentLoginEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    public DateTime At { get; set; }

    public DateTime? LastAt { get; set; }

    public int Count { get; set; } = 1;

    public Lib.Agent.LogonOutcome Outcome { get; set; }

    [MaxLength(100)]
    public string? FailureReason { get; set; }

    [MaxLength(256)]
    public string User { get; set; } = "";

    [MaxLength(256)]
    public string? Domain { get; set; }

    public bool IsAdmin { get; set; }

    public Lib.Agent.LogonKind Kind { get; set; }

    [MaxLength(50)]
    public string? AuthPackage { get; set; }

    [MaxLength(100)]
    public string? SourceAddress { get; set; }

    public int? SourcePort { get; set; }

    [MaxLength(256)]
    public string? Workstation { get; set; }

    public bool? Elevated { get; set; }

    [MaxLength(40)]
    public string? LogonId { get; set; }

    public DateTime? LoggedOffAt { get; set; }

    [MaxLength(200)]
    public string Channel { get; set; } = "";

    public long RecordId { get; set; }

    // --- Linux (docs/linux-sensor-plan.md §5.6, migration AddLinuxAgents) --------------------------------------

    /// <summary>The key of an SSH key login (<c>SHA256:…</c>), which the key manager names (§6.4).</summary>
    [MaxLength(100)]
    public string? KeyFingerprint { get; set; }

    /// <summary>Whom sudo or su became.</summary>
    [MaxLength(256)]
    public string? TargetUser { get; set; }

    /// <summary>sudo's command, only where config.yaml's collect.sudo_commands sends it.</summary>
    [MaxLength(2000)]
    public string? Command { get; set; }

    /// <summary>For the "invalid user" aggregate: the first 10 names the source tried.</summary>
    public List<string>? TriedUsers { get; set; }
}

/// <summary>A readable change of a section (§4.9): "Service AnyDesk added (third-party, automatic, running)". Kept 180 days.</summary>
[Table("agent_changes")]
[NotAudited("what an agent reported changed on its server")]
public class AgentChangeEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    [MaxLength(40)]
    public string Section { get; set; } = "";

    public DateTime At { get; set; }

    [MaxLength(1000)]
    public string Summary { get; set; } = "";

    /// <summary>The values before and after, small.</summary>
    [Column(TypeName = "jsonb")]
    public string? Details { get; set; }

    /// <summary>Notable changes write 253 (§4.9).</summary>
    public bool Notable { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ScriptParseState>))]
public enum ScriptParseState { Pending, Parsed, Failed }

/// <summary>
/// A script in PNMT's library (docs/windows-sensor-plan.md §7.1), with a version for each change: PowerShell, and bash from
/// docs/linux-sensor-plan.md §8.1 on.
/// </summary>
[Table("agent_scripts")]
[Audited]
public class AgentScriptEntity
{
    [Key]
    public Guid Id { get; set; }

    public Lib.Agent.ScriptLanguage Language { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(2000)]
    public string? Description { get; set; }

    [AuditIgnore]

    public DateTime CreatedAt { get; set; }

    [AuditIgnore]

    public DateTime UpdatedAt { get; set; }

    [AuditIgnore]

    public int CurrentVersion { get; set; }

    public List<AgentScriptVersionEntity> Versions { get; set; } = [];
}

/// <summary>A version of a script: its text and hash, and what an agent's parser found in it (§7.1).</summary>
[Table("agent_script_versions")]
[NotAudited("a script's versions are its history")]
public class AgentScriptVersionEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid ScriptId { get; set; }

    public int Number { get; set; }

    public string Text { get; set; } = "";

    [MaxLength(64)]
    public string Hash { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    public string? CreatedByName { get; set; }

    public ScriptParseState ParseState { get; set; }

    /// <summary>The parameters, help, syntax errors and findings (<see cref="Lib.Agent.ParsedScript"/>), as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string? Parsed { get; set; }

    [MaxLength(1000)]
    public string? ParseError { get; set; }

    public DateTime? ParsedAt { get; set; }

    [MaxLength(500)]
    public string? ParsedOn { get; set; }
}

/// <summary>A script's run on one server (§7.3): what, as whom, by whom, the arguments (redactable), and the outcome. A history.</summary>
[Table("agent_script_runs")]
[NotAudited("a run's results; starting it is an entry of its own")]
public class AgentScriptRunEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid BatchId { get; set; }

    public Guid? ScriptId { get; set; }

    [MaxLength(200)]
    public string ScriptName { get; set; } = "";

    public int VersionNumber { get; set; }

    [MaxLength(64)]
    public string Hash { get; set; } = "";

    public Guid? SensorId { get; set; }

    [MaxLength(500)]
    public string ServerName { get; set; } = "";

    [MaxLength(200)]
    public string RunAsName { get; set; } = "";

    public Lib.Agent.RunAsKind RunAsKind { get; set; }

    [MaxLength(256)]
    public string? Account { get; set; }

    /// <summary>The plain arguments as JSON; secure ones are never stored. Null once redacted.</summary>
    [Column(TypeName = "jsonb")]
    public string? Arguments { get; set; }

    public bool Redacted { get; set; }

    [MaxLength(200)]
    public string? RequestedBy { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public Lib.Agent.ScriptRunState State { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>stdout and the error stream, at most 1 MiB.</summary>
    public string? Output { get; set; }

    [MaxLength(2000)]
    public string? Message { get; set; }

    public double? DurationMs { get; set; }

    public int TimeoutSeconds { get; set; }

    /// <summary>The connector whose call started the run (docs/connectors-plan.md §9.2); no foreign key, like <see cref="ScriptId"/>.</summary>
    public Guid? ConnectorId { get; set; }

    /// <summary>The function called instead of the whole script (docs/connectors-plan.md §8); null: the whole script.</summary>
    [MaxLength(100)]
    public string? Function { get; set; }
}
