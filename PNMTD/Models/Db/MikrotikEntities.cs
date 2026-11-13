using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Enums;

namespace PNMTD.Models.Db;

/// <summary>
/// The settings and latest state of a MIKROTIK sensor, 1:1, deleted with it (docs/mikrotik-plan.md §9.2).
/// Holds no secret: PNMTD logs in with the installation's key (<see cref="MikrotikSshKeyEntity"/>).
/// </summary>
[Table("mikrotik_routers")]
public class MikrotikRouterEntity
{
    [Key]
    public Guid SensorId { get; set; }

    public virtual SensorEntity Sensor { get; set; } = null!;

    public MikrotikMode Mode { get; set; }

    /// <summary>The router's tunnel in the WireGuard modes (§3.2).</summary>
    public Guid? WgPeerId { get; set; }

    /// <summary>Direct SSH (§3.6): a host name or address PNMTD can reach.</summary>
    public string? Address { get; set; }

    /// <summary>22 by default; the first login corrects it when the router's SSH service uses another.</summary>
    public int SshPort { get; set; } = 22;

    /// <summary>Direct SSH: PNMTD's address as the router sees it, for the <c>pnmt</c> user's <c>address</c>. Null: unrestricted (the UI warns).</summary>
    public string? PnmtSourceAddress { get; set; }

    /// <summary>Custom tunnels may reach the management ports (§3.5). Admin.</summary>
    public bool ManagementAccess { get; set; }

    /// <summary><c>sensitive</c> in the group, <c>show-sensitive</c> in exports (§6.6). Admin.</summary>
    public bool IncludeSecrets { get; set; }

    /// <summary><c>test</c> in the group: ping and traceroute as <c>pnmt</c> (§8.4). Admin.</summary>
    public bool Diagnostics { get; set; }

    public int BackupIntervalHours { get; set; } = 24;

    public MikrotikSetupState SetupState { get; set; }

    /// <summary>When the settings that the join script carries last changed: "script outdated" (§3.3).</summary>
    public DateTime ScriptChangedAt { get; set; }

    /// <summary>When the join script was last shown: in WireGuard only mode, the only clue to what the router has (§3.3).</summary>
    public DateTime? ScriptGeneratedAt { get; set; }

    // --- the host key (§4.4) -------------------------------------------------------------------------

    public string? HostKeyType { get; set; }

    /// <summary>SHA-256, unpadded Base64 as <c>ssh-keygen -l</c> writes it (without "SHA256:").</summary>
    public string? HostKeyFingerprint { get; set; }

    public DateTime? HostKeyPinnedAt { get; set; }

    /// <summary>A different key the router presented, refused until an Admin accepts it.</summary>
    public string? PendingHostKeyType { get; set; }

    public string? PendingHostKeyFingerprint { get; set; }

    public DateTime? PendingHostKeySeenAt { get; set; }

    // --- the latest reading (§5.1) ---------------------------------------------------------------------

    public string? Identity { get; set; }

    public string? Model { get; set; }

    public string? BoardName { get; set; }

    public string? SerialNumber { get; set; }

    public string? Architecture { get; set; }

    public string? Version { get; set; }

    public string? Channel { get; set; }

    public string? FirmwareCurrent { get; set; }

    public string? FirmwareUpgrade { get; set; }

    public DateTime? BootedAt { get; set; }

    /// <summary>The latest <see cref="Lib.Mikrotik.RouterReading"/> as JSON: resources, the group, the tunnel's settings.</summary>
    [Column(TypeName = "jsonb")]
    public string? Reading { get; set; }

    public DateTime? LastPollAt { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Since when polls fail; 440 once this is older than the GracePeriod.</summary>
    public DateTime? FailingSince { get; set; }

    public string? LastError { get; set; }

    /// <summary>The code the last failure maps to: 440 (unreachable) or 441 (access denied).</summary>
    public int? LastErrorCode { get; set; }

    /// <summary>244 is written once per version (§5.3).</summary>
    public string? NotifiedUpdateVersion { get; set; }

    // --- backups (§6) -----------------------------------------------------------------------------------

    /// <summary>The last successful export, whether it stored a new version or found the configuration unchanged.</summary>
    public DateTime? LastExportAt { get; set; }

    /// <summary>The last export tried; after a failure the next try waits <c>Backups:RetryMinutes</c>.</summary>
    public DateTime? LastExportAttemptAt { get; set; }

    /// <summary>SHA-256 of the newest stored export, normalised: an equal export stores nothing (§6.2).</summary>
    public string? LastExportHash { get; set; }

    /// <summary>Exports that failed in a row: 442 from two on (§5.3).</summary>
    public int BackupFailures { get; set; }

    public string? LastBackupError { get; set; }

    public DateTime Created { get; set; }
}

/// <summary>
/// The installation's MikroTik settings, one row (§9.2): its tag (the comment "pnmt:&lt;tag&gt;" on
/// everything a join script creates) and the latest versions per channel.
/// </summary>
[Table("mikrotik_settings")]
public class MikrotikSettingsEntity
{
    public const int SingletonId = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = SingletonId;

    /// <summary>Four hex digits, random per installation.</summary>
    public string Tag { get; set; } = "";

    /// <summary>The newest version of each channel: <c>{"stable":{"version":"7.25.1","releasedAt":"…"}, …}</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string? LatestVersions { get; set; }

    public DateTime? VersionsCheckedAt { get; set; }

    public string? VersionCheckError { get; set; }
}

/// <summary>
/// PNMTD's SSH key pairs for the <c>pnmt</c> user (§4.2): one in use, and while a rotation runs, the
/// one before it. The private key is under Data Protection.
/// </summary>
[Table("mikrotik_ssh_keys")]
public class MikrotikSshKeyEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The OpenSSH line, "ssh-ed25519 AAAA… pnmt-&lt;id&gt;@&lt;host&gt;".</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>The comment, which RouterOS shows as the key's owner: how a poll tells PNMTD's keys apart.</summary>
    public string Comment { get; set; } = "";

    public string PrivateKeyProtected { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    /// <summary>
    /// Set when an Admin retired it after a rotation: no longer offered. Until then an older key is offered next to
    /// the newest, so routers keep working until their new join script is pasted.
    /// </summary>
    public DateTime? RetiredAt { get; set; }
}

/// <summary>
/// A stored configuration (§6.3): an export as gzip, or a binary backup taken in an update (§6.5). It
/// outlives the router (<c>ON DELETE SET NULL</c>, with the router's name kept); retention deletes old ones.
/// </summary>
[Table("mikrotik_backups")]
public class MikrotikBackupEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid? SensorId { get; set; }

    /// <summary>The router's name when the backup was taken.</summary>
    public string RouterName { get; set; } = "";

    /// <summary>1, 2, 3, … per router: "version 42".</summary>
    public int Number { get; set; }

    public MikrotikBackupKind Kind { get; set; }

    public MikrotikBackupTrigger Trigger { get; set; }

    /// <summary>The update or command job that took it (§7.3, §8.2).</summary>
    public Guid? JobId { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public string? CreatedByName { get; set; }

    public string? RouterOsVersion { get; set; }

    /// <summary>gzip of the normalised export (exports), or the file as the router wrote it (binary).</summary>
    public byte[] Content { get; set; } = [];

    /// <summary>The size before compression.</summary>
    public long Size { get; set; }

    public int LineCount { get; set; }

    /// <summary>Of the normalised export.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Exported with <c>show-sensitive</c> by a group that has <c>sensitive</c>: only Admins see it (§6.6).</summary>
    public bool IncludesSecrets { get; set; }

    /// <summary>Against the version before; null for the first.</summary>
    public int? LinesAdded { get; set; }

    public int? LinesRemoved { get; set; }

    /// <summary>A binary backup encrypted with the job's admin password (§6.5).</summary>
    public bool Encrypted { get; set; }
}

/// <summary>
/// An update or a command on one router (docs/mikrotik-plan.md §7, §8), in a batch with the others started
/// together. Permanent; only a redaction (§8.3) changes a finished one. Never holds a password.
/// </summary>
[Table("mikrotik_jobs")]
public class MikrotikJobEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid BatchId { get; set; }

    public MikrotikJobKind Kind { get; set; }

    /// <summary>Null once the router is deleted; <see cref="RouterName"/> keeps it readable.</summary>
    public Guid? SensorId { get; set; }

    public string RouterName { get; set; } = "";

    public MikrotikJobState State { get; set; }

    public MikrotikJobStep Step { get; set; }

    public Guid? RequestedBy { get; set; }

    public string? RequestedByName { get; set; }

    /// <summary>The router's admin whose password the job used (§7.2): the name only.</summary>
    public string? AdminUsername { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    // --- updates (§7) ---------------------------------------------------------------------------------------

    public string? FromVersion { get; set; }

    public string? ToVersion { get; set; }

    public string? Channel { get; set; }

    /// <summary>The options as chosen: a binary backup first, the firmware after, one router at a time.</summary>
    [Column(TypeName = "jsonb")]
    public string? Options { get; set; }

    public string? Error { get; set; }

    // --- commands (§8) --------------------------------------------------------------------------------------

    /// <summary>At most 16 KiB; null after a redaction.</summary>
    public string? Command { get; set; }

    /// <summary>"pnmt", or the admin's username.</summary>
    public string? RunAs { get; set; }

    public int? TimeoutSeconds { get; set; }

    public int? ExitStatus { get; set; }

    /// <summary>At most <c>Commands:MaxStoredOutputBytes</c>; null after a redaction.</summary>
    public string? Output { get; set; }

    public bool OutputTruncated { get; set; }

    public Guid? BackupBeforeId { get; set; }

    public Guid? BackupAfterId { get; set; }

    public DateTime? RedactedAt { get; set; }

    public string? RedactedBy { get; set; }
}

/// <summary>A job's steps as they happened, with the router's answers (at most 64 KiB each). Permanent.</summary>
[Table("mikrotik_job_log")]
public class MikrotikJobLogEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public Guid JobId { get; set; }

    public DateTime At { get; set; }

    public MikrotikJobStep Step { get; set; }

    public MikrotikJobOutcome Outcome { get; set; }

    public string Message { get; set; } = "";

    public string? Output { get; set; }
}
