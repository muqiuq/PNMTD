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
[Audited]
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
    [AuditPlain("a setting")]
    public bool IncludeSecrets { get; set; }

    /// <summary><c>test</c> in the group: ping and traceroute as <c>pnmt</c> (§8.4). Admin.</summary>
    public bool Diagnostics { get; set; }

    /// <summary>The scheduled export's interval (§6.1, docs/mikrotik-backup-plan.md §2): 1 to 8760 hours; null is off.</summary>
    public int? BackupIntervalHours { get; set; } = 24;

    /// <summary>
    /// The router's backups are encrypted with the installation's backup key, one way (docs/mikrotik-backup-plan.md
    /// §3.3). Admin. Only while there is a key: retiring it without a successor turns this off everywhere.
    /// </summary>
    public bool EncryptBackups { get; set; }

    /// <summary>The update method and storage check this router was last updated with: the update dialog's defaults (docs/ai-operations-plan.md §12.2).</summary>
    public MikrotikUpdateMethod UpdateMethod { get; set; }

    public bool UpdateCheckStorage { get; set; } = true;

    public MikrotikSetupState SetupState { get; set; }

    /// <summary>When the settings that the join script carries last changed: "script outdated" (§3.3).</summary>
    public DateTime ScriptChangedAt { get; set; }

    /// <summary>When the join script was last shown: in WireGuard only mode, the only clue to what the router has (§3.3).</summary>
    public DateTime? ScriptGeneratedAt { get; set; }

    // --- the host key (§4.4) -------------------------------------------------------------------------

    [AuditPlain("the router's public host key")]

    public string? HostKeyType { get; set; }

    /// <summary>SHA-256, unpadded Base64 as <c>ssh-keygen -l</c> writes it (without "SHA256:").</summary>
    [AuditPlain("the router's public host key")]
    public string? HostKeyFingerprint { get; set; }

    [AuditPlain("the router's public host key")]

    public DateTime? HostKeyPinnedAt { get; set; }

    /// <summary>A different key the router presented, refused until an Admin accepts it.</summary>
    [AuditPlain("the router's public host key")]
    public string? PendingHostKeyType { get; set; }

    [AuditPlain("the router's public host key")]

    public string? PendingHostKeyFingerprint { get; set; }

    [AuditPlain("the router's public host key")]

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
    [AuditIgnore]
    public string? LastExportHash { get; set; }

    /// <summary>
    /// The keyed digests of the newest export's lines, 16 bytes each (docs/mikrotik-backup-plan.md §3.7): what the
    /// next export is counted against, without its text. <see cref="LastExportHash"/> is then the keyed hash too.
    /// </summary>
    public byte[]? LastExportLineDigests { get; set; }

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
[Audited]
public class MikrotikSettingsEntity
{
    public const int SingletonId = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = SingletonId;

    /// <summary>Four hex digits, random per installation.</summary>
    public string Tag { get; set; } = "";

    /// <summary>The row as a new installation gets it: with its random tag.</summary>
    public static MikrotikSettingsEntity Create() =>
        new() { Tag = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2)) };

    /// <summary>The newest version of each channel: <c>{"stable":{"version":"7.25.1","releasedAt":"…"}, …}</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string? LatestVersions { get; set; }

    public DateTime? VersionsCheckedAt { get; set; }

    public string? VersionCheckError { get; set; }

    /// <summary>The schedule new routers start with (docs/mikrotik-backup-plan.md §2.2); null is off.</summary>
    public int? DefaultBackupIntervalHours { get; set; } = 24;

    /// <summary>
    /// The key of the exports' keyed digests (docs/mikrotik-backup-plan.md §3.7), 32 random bytes, base64, under Data
    /// Protection. Made on first use.
    /// </summary>
    [AuditSecret]
    public string? BackupDigestKeyProtected { get; set; }

    /// <summary>The active backup key (docs/mikrotik-backup-plan.md §3.5); null while encryption is off.</summary>
    [AuditPlain("which backup key, not the key")]
    public Guid? BackupKeyId { get; set; }

    /// <summary>New routers start with their backups encrypted (only while there is a key).</summary>
    public bool DefaultEncryptBackups { get; set; }
}

/// <summary>
/// A public key the routers' backups are encrypted with, one way (docs/mikrotik-backup-plan.md §3.2, §3.5): an age
/// recipient. PNMT never holds the private key. Keys are retired, never deleted, so every backup can say which key it
/// needs.
/// </summary>
[Table("mikrotik_backup_keys")]
[Microsoft.EntityFrameworkCore.Index(nameof(Fingerprint), IsUnique = true)]
[Audited]
public class MikrotikBackupKeyEntity
{
    public const string X25519 = "x25519";

    [Key]
    public Guid Id { get; set; }

    /// <summary><c>x25519</c>; the post-quantum hybrid (<c>mlkem768x25519</c>) later.</summary>
    public string Kind { get; set; } = X25519;

    /// <summary>The age recipient, <c>age1…</c>.</summary>
    public string Recipient { get; set; } = "";

    /// <summary>12 hex digits of the key's SHA-256: what a backup names as its key.</summary>
    public string Fingerprint { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public string? CreatedByName { get; set; }

    public DateTime? RetiredAt { get; set; }
}

/// <summary>
/// PNMTD's SSH key pairs for the <c>pnmt</c> user (§4.2): one in use, and while a rotation runs, the
/// one before it. The private key is under Data Protection.
/// </summary>
[Table("mikrotik_ssh_keys")]
[Audited]
public class MikrotikSshKeyEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The OpenSSH line, "ssh-ed25519 AAAA… pnmt-&lt;id&gt;@&lt;host&gt;".</summary>
    [AuditPlain("a public key")]
    public string PublicKey { get; set; } = "";

    /// <summary>The comment, which RouterOS shows as the key's owner: how a poll tells PNMTD's keys apart.</summary>
    public string Comment { get; set; } = "";

    [AuditSecret]

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
[NotAudited("the backups taken; a deletion is an entry of its own")]
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

    /// <summary>
    /// The keyed hash (HMAC-SHA256) of the normalised export, or of a binary backup's file (docs/mikrotik-backup-plan.md
    /// §3.7). Versions stored before the keyed digests have their SHA-256 here, until an unchanged export replaces it.
    /// </summary>
    public string ContentHash { get; set; } = "";

    /// <summary>Exported with <c>show-sensitive</c> by a group that has <c>sensitive</c>: only Admins see it (§6.6).</summary>
    public bool IncludesSecrets { get; set; }

    /// <summary>Against the version before; null for the first.</summary>
    public int? LinesAdded { get; set; }

    public int? LinesRemoved { get; set; }

    /// <summary>A binary backup encrypted with the job's admin password (§6.5).</summary>
    public bool Encrypted { get; set; }

    /// <summary>
    /// The fingerprint of the backup key the content is encrypted with, in the age format (docs/mikrotik-backup-plan.md
    /// §3.4): the gzip of the export, or the binary file. PNMT cannot read it. Null: as stored before.
    /// </summary>
    public string? EncryptedWith { get; set; }
}

/// <summary>
/// An update or a command on one router (docs/mikrotik-plan.md §7, §8), in a batch with the others started
/// together. Permanent; only a redaction (§8.3) changes a finished one. Never holds a password.
/// </summary>
[Table("mikrotik_jobs")]
[NotAudited("update jobs at work; starting and cancelling are entries of their own")]
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
[NotAudited("an update job's log")]
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
