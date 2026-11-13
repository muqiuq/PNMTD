using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace PNMTD.Models.Db;

/// <summary>
/// PNMTD's side of the agents' trust (docs/windows-sensor-plan.md §3.4, §8.3): its ECDSA P-256 signing key, under Data
/// Protection (purpose PNMTD.Windows.AgentSigning.v1) with the key ring on the keys volume, as the MikroTik keys are.
/// One row.
/// </summary>
[Table("windows_settings")]
public class WindowsSettingsEntity
{
    public const int SingletonId = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = SingletonId;

    /// <summary>The PKCS#8 private key, Base64, protected.</summary>
    public string SigningKeyProtected { get; set; } = "";

    /// <summary>SubjectPublicKeyInfo, Base64: what agents pin at their join.</summary>
    public string SigningPublicKey { get; set; } = "";

    public string SigningKeyFingerprint { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>Raised whenever what agents are configured with changes (diagnostics, intervals): <c>config</c> goes out.</summary>
    public int ConfigVersion { get; set; } = 1;
}

/// <summary>
/// A join token (§3.3): single use, valid 24 hours by default, bound to an existing host, and for a re-join to its
/// sensor. Only the hash is stored; the token is shown once.
/// </summary>
[Table("windows_join_tokens")]
public class WindowsJoinTokenEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(64)]
    public string TokenHash { get; set; } = "";

    /// <summary>The first two characters, for the list.</summary>
    [MaxLength(2)]
    public string Prefix { get; set; } = "";

    public Guid HostId { get; set; }

    /// <summary>A re-join: the server it replaces the key of.</summary>
    public Guid? SensorId { get; set; }

    /// <summary>The new sensor's name; null: "Windows".</summary>
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
/// A WINDOWS sensor's agent, 1:1, deleted with it (§8.3): its key, what it reported about itself, its connection, its
/// last heartbeat, and the server's event switches.
/// </summary>
[Table("windows_agents")]
public class WindowsAgentEntity
{
    [Key]
    public Guid SensorId { get; set; }

    public virtual SensorEntity Sensor { get; set; } = null!;

    /// <summary>The agent's id: new at each join, so a re-join's key has a name of its own on the server.</summary>
    public Guid AgentId { get; set; }

    /// <summary>The agent's ECDSA P-256 public key, SPKI Base64. Null once revoked.</summary>
    public string? PublicKey { get; set; }

    public string KeyFingerprint { get; set; } = "";

    /// <summary>W6 (§3.4): the agent's ECDH P-256 key for passwords typed per run.</summary>
    public string? EncryptionKey { get; set; }

    public DateTime JoinedAt { get; set; }

    [MaxLength(64)]
    public string? MachineGuid { get; set; }

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

    public DateTime? RevokedAt { get; set; }

    [MaxLength(500)]
    public string? RevokedReason { get; set; }

    /// <summary>The agent said <c>bye</c>: the service was uninstalled (§10.4). The sensor and its history stay.</summary>
    public DateTime? UninstalledAt { get; set; }

    /// <summary>From the remote-access section, for the list: "RDP · WinRM · SMB open" (§4.6).</summary>
    [MaxLength(300)]
    public string? RemoteAccessSummary { get; set; }

    /// <summary>From the access section, for the list: what the audit policy and the logs miss (§4.7).</summary>
    public int AuditGaps { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowsAgentLogKind>))]
public enum WindowsAgentLogKind
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
[Table("windows_agent_log")]
public class WindowsAgentLogEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid? SensorId { get; set; }

    /// <summary>The server's host and sensor name when this was written: what is left once it is gone.</summary>
    [MaxLength(500)]
    public string AgentName { get; set; } = "";

    public DateTime At { get; set; }

    public WindowsAgentLogKind Kind { get; set; }

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
[Table("windows_sections")]
public class WindowsSectionEntity
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
/// and the record id of its first event: sent again, it is updated (a higher count, a logoff). Kept 180 days.
/// </summary>
[Table("windows_logins")]
public class WindowsLoginEntity
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
}

/// <summary>A readable change of a section (§4.9): "Service AnyDesk added (third-party, automatic, running)". Kept 180 days.</summary>
[Table("windows_changes")]
public class WindowsChangeEntity
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
