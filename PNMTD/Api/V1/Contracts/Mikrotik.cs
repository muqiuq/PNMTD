using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// <c>SensorRequest.mikrotik</c> (docs/mikrotik-plan.md §10): how PNMTD reaches the router. Management
/// access, secrets in exports and diagnostics may be changed by an Admin only.
/// </summary>
public sealed class MikrotikRouterRequest
{
    public MikrotikMode Mode { get; init; } = MikrotikMode.WireGuardSsh;

    /// <summary>Direct SSH: a host name or address PNMTD can reach.</summary>
    public string? Address { get; init; }

    public int SshPort { get; init; } = 22;

    /// <summary>Direct SSH: PNMTD's address as the router sees it; the <c>pnmt</c> user may log in only from it. Empty: from anywhere.</summary>
    public string? SourceAddress { get; init; }

    /// <summary>
    /// The scheduled export: every 1 to 8760 hours, or null for off (docs/mikrotik-backup-plan.md §2). Left out, 24;
    /// the UI starts a new router with the installation's default (<c>GET /mikrotik/backup-settings</c>).
    /// </summary>
    public int? BackupIntervalHours { get; init; } = 24;

    /// <summary>
    /// The router's backups encrypted with the installation's backup key, one way (docs/mikrotik-backup-plan.md §3.3).
    /// Admin, and only while there is a key. Null leaves it as it is; a new router starts with the installation's default.
    /// </summary>
    public bool? EncryptBackups { get; init; }

    public bool ManagementAccess { get; init; }

    public bool IncludeSecrets { get; init; }

    public bool Diagnostics { get; init; }
}

/// <summary><c>SensorDetail.mikrotik</c>: the settings, the setup state, and the latest reading.</summary>
public sealed record MikrotikRouterDetail(
    MikrotikMode Mode,
    MikrotikSetupState SetupState,
    string? Address,
    int SshPort,
    string? SourceAddress,
    int? BackupIntervalHours,
    bool ManagementAccess,
    bool IncludeSecrets,
    bool Diagnostics,
    MikrotikHostKey? HostKey,
    MikrotikHostKey? PendingHostKey,
    string? Identity,
    string? Model,
    string? BoardName,
    string? SerialNumber,
    string? Architecture,
    string? Version,
    string? Channel,
    string? LatestVersion,
    bool UpdateAvailable,
    string? FirmwareCurrent,
    string? FirmwareUpgrade,
    DateTime? BootedAt,
    MikrotikResources? Resources,
    DateTime? LastPollAt,
    DateTime? LastSuccessAt,
    DateTime? FailingSince,
    string? LastError,
    int? LastErrorCode,
    bool ScriptOutdated,
    IReadOnlyList<string> ScriptDifferences,
    IReadOnlyList<string> Warnings,
    DateTime? LastExportAt = null,
    int BackupFailures = 0,
    string? LastBackupError = null,
    int BackupCount = 0,
    MikrotikBackupItem? LatestBackup = null,
    MikrotikTunnel? Tunnel = null,
    bool EncryptBackups = false,
    MikrotikUpdateMethod UpdateMethod = MikrotikUpdateMethod.Download,
    bool UpdateCheckStorage = true,
    bool LowStorage = false,
    ReleaseSeverity? SecurityFix = null);

/// <summary>A router's tunnel as the gateway sees it (§3.2): its address and key, the last handshake, where it comes from.</summary>
public sealed record MikrotikTunnel(
    string Address,
    string PublicKey,
    DateTime KeysCreatedAt,
    bool Up,
    DateTime? LatestHandshake,
    string? Endpoint,
    long RxBytes,
    long TxBytes,
    int EndpointChanges24h,
    bool GatewayUp);

/// <summary>A host key: its type and SHA-256 fingerprint (unpadded Base64), pinned or seen at <see cref="At"/>.</summary>
public sealed record MikrotikHostKey(string Type, string Fingerprint, DateTime? At);

public sealed record MikrotikResources(
    string? Cpu,
    int? CpuCount,
    int? CpuLoad,
    long? FreeMemory,
    long? TotalMemory,
    long? FreeStorage,
    long? TotalStorage,
    long? WriteSectorsTotal,
    string? BadBlocks,
    long? UptimeSeconds);

/// <summary>A join or removal script (§4.3, §4.6), as text, with the file name for <c>/import</c>.</summary>
public sealed record MikrotikScript(string Script, string FileName, MikrotikMode Mode, DateTime GeneratedAt, IReadOnlyList<string> Notes);

/// <summary>A row of the admin page's routers (§11.2).</summary>
public sealed record MikrotikFleetItem(
    Guid SensorId,
    string Name,
    Ref Host,
    bool Enabled,
    MikrotikMode Mode,
    MikrotikSetupState SetupState,
    SensorState State,
    int? LastCode,
    string? LastMessage,
    string? Address,
    string? Identity,
    string? Model,
    string? SerialNumber,
    string? Version,
    string? Channel,
    string? LatestVersion,
    bool UpdateAvailable,
    string? FirmwareCurrent,
    string? FirmwareUpgrade,
    DateTime? BootedAt,
    DateTime? LastSuccessAt,
    string? LastError,
    bool ScriptOutdated,
    int? BackupIntervalHours = 24,
    bool EncryptBackups = false,
    long? FreeStorage = null,
    MikrotikUpdateMethod UpdateMethod = MikrotikUpdateMethod.Download,
    bool UpdateCheckStorage = true,
    bool LowStorage = false,
    ReleaseSeverity? SecurityFix = null);

/// <summary>
/// A RouterOS release from MikroTik's feed with its assessment (docs/ai-operations-plan.md §10): Critical alerts when
/// <c>RoutersBehind</c> is not empty; Important is only shown.
/// </summary>
public sealed record MikrotikReleaseItem(
    string Version,
    string Channel,
    DateTime? PublishedAt,
    ReleaseSeverity Severity,
    bool ZeroDay,
    bool Exploited,
    bool RemoteWithoutCredentials,
    IReadOnlyList<string> Cves,
    string? Summary,
    string AssessedBy,
    IReadOnlyList<Ref> RoutersBehind,
    IReadOnlyList<string> SecurityLines);

/// <summary>
/// <c>GET /mikrotik/routers</c>. Search: name, host, identity, serial number, address. Sort: name
/// (default), host, state, version, lastsuccess.
/// </summary>
public sealed class MikrotikFleetQuery : PageQuery
{
    public MikrotikMode? Mode { get; set; }

    public MikrotikSetupState? SetupState { get; set; }

    public SensorState? State { get; set; }

    public bool? UpdateAvailable { get; set; }

    public bool? ScriptOutdated { get; set; }

    public string? Channel { get; set; }
}

/// <summary>The admin page's overview (§11.2): the version catalog, PNMTD's SSH keys, counts.</summary>
public sealed record MikrotikStatus(
    bool Enabled,
    string Tag,
    string Instance,
    IReadOnlyList<MikrotikSshPublicKey> SshKeys,
    IReadOnlyList<MikrotikChannelVersion> LatestVersions,
    DateTime? VersionsCheckedAt,
    string? VersionCheckError,
    bool VersionCheckEnabled,
    int Routers,
    IReadOnlyList<MikrotikCount> BySetupState,
    IReadOnlyList<MikrotikCount> ByVersion,
    int UpdatesAvailable,
    int ScriptsOutdated);

public sealed record MikrotikSshPublicKey(string PublicKey, string Comment, string Fingerprint, DateTime CreatedAt, DateTime? RetiredAt);

/// <summary>
/// PNMTD's SSH keys, newest first (§4.2), the comment of the current one, and the routers that may still need
/// an older key: retiring the older keys would cut them off until their new join script is pasted.
/// </summary>
public sealed record MikrotikSshKeyStatus(IReadOnlyList<MikrotikSshPublicKey> Keys, string CurrentComment, IReadOnlyList<MikrotikKeyWaiting> Waiting);

public sealed record MikrotikKeyWaiting(Guid SensorId, string Name, string Reason);

public sealed record MikrotikChannelVersion(string Channel, string Version, DateTime? ReleasedAt);

public sealed record MikrotikCount(string Key, int Count);

/// <summary>A stored backup without its content (§6.3): the list of the Backups tab.</summary>
public sealed record MikrotikBackupItem(
    Guid Id,
    int Number,
    MikrotikBackupKind Kind,
    MikrotikBackupTrigger Trigger,
    DateTime CreatedAt,
    string? CreatedBy,
    string? RouterOsVersion,
    long Size,
    int LineCount,
    int? LinesAdded,
    int? LinesRemoved,
    bool IncludesSecrets,
    bool Encrypted,
    string? EncryptedWith = null);

/// <summary>
/// An export's text, normalised (§6.2); or, encrypted (<see cref="MikrotikBackupItem.EncryptedWith"/>), the age file as
/// base64 for the browser to decrypt with the backup key (docs/mikrotik-backup-plan.md §3.6).
/// </summary>
public sealed record MikrotikBackupContent(MikrotikBackupItem Backup, string? Content, string? Ciphertext = null);

/// <summary>Two versions compared (§6.4): the hunks to show, and the same as <c>diff -u</c> text.</summary>
public sealed record MikrotikBackupDiff(
    MikrotikBackupItem? From,
    MikrotikBackupItem To,
    int Added,
    int Removed,
    IReadOnlyList<MikrotikDiffHunk> Hunks,
    string Unified);

public sealed record MikrotikDiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<MikrotikDiffLine> Lines);

public sealed record MikrotikDiffLine(DiffLineKind Kind, string Text, int? OldNumber, int? NewNumber);

/// <summary>"Back up now": a new version, or none because the configuration is unchanged.</summary>
public sealed record MikrotikBackupResult(bool Changed, MikrotikBackupItem? Backup, string Message);

/// <summary><c>GET /sensors/{id}/mikrotik/backups</c>: newest first.</summary>
public sealed class MikrotikBackupQuery : PageQuery
{
    public MikrotikBackupKind? Kind { get; set; }
}

/// <summary>
/// <c>POST /mikrotik/jobs</c> (§7.1): an update of one router or several, with the router admin's username
/// and password for this one batch. The password is held in memory only, and never stored or logged.
/// </summary>
public sealed class MikrotikJobRequest
{
    public IReadOnlyList<Guid> SensorIds { get; init; } = [];

    public MikrotikJobKind Kind { get; init; } = MikrotikJobKind.Update;

    /// <summary>stable, long-term, testing or development; empty: each router's own.</summary>
    public string? Channel { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public bool BinaryBackup { get; init; } = true;

    public bool Firmware { get; init; } = true;

    public bool StopAtFirstFailure { get; init; } = true;

    /// <summary>Download, then reboot (today's), or Download and install (docs/ai-operations-plan.md §12.2).</summary>
    public MikrotikUpdateMethod Method { get; init; } = MikrotikUpdateMethod.Download;

    /// <summary>Refuse below <c>Mikrotik:Updates:MinimumFreeStorageBytes</c>; off lets RouterOS decide (16 MB-flash routers update from RAM).</summary>
    public bool CheckStorage { get; init; } = true;

    /// <summary>Never the password.</summary>
    public override string ToString() => $"{Kind} of {SensorIds.Count} router(s) as {Username}";
}

/// <summary>The jobs created, and the routers refused before anything ran, with the reason.</summary>
public sealed record MikrotikJobsCreated(Guid BatchId, IReadOnlyList<MikrotikJobItem> Jobs, IReadOnlyList<MikrotikJobRefusal> Refused);

public sealed record MikrotikJobRefusal(Guid SensorId, string Name, string Reason);

public sealed record MikrotikJobItem(
    Guid Id,
    Guid BatchId,
    MikrotikJobKind Kind,
    Guid? SensorId,
    string RouterName,
    MikrotikJobState State,
    MikrotikJobStep Step,
    string? RequestedBy,
    string? AdminUsername,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? FromVersion,
    string? ToVersion,
    string? Channel,
    string? Error,
    string? Command,
    string? RunAs);

public sealed record MikrotikJobLogItem(long Id, DateTime At, MikrotikJobStep Step, MikrotikJobOutcome Outcome, string Message, string? Output);

public sealed record MikrotikJobDetail(MikrotikJobItem Job, IReadOnlyList<MikrotikJobLogItem> Log);

/// <summary><c>GET /mikrotik/jobs</c>: newest first.</summary>
public sealed class MikrotikJobQuery : PageQuery
{
    public MikrotikJobKind? Kind { get; set; }

    public MikrotikJobState? State { get; set; }

    public Guid? BatchId { get; set; }

    public Guid? SensorId { get; set; }
}

/// <summary>
/// <c>POST /mikrotik/commands</c> (§8.2): a command or a short script for one router or several, as <c>pnmt</c>,
/// or as the router's admin with the password for this batch only: held in memory, never stored or logged.
/// </summary>
public sealed class MikrotikCommandRequest
{
    public IReadOnlyList<Guid> SensorIds { get; init; } = [];

    /// <summary>As typed: one command or several lines, at most 16 KiB. Stored with the job.</summary>
    public string Command { get; init; } = "";

    public MikrotikRunAs RunAs { get; init; } = MikrotikRunAs.Pnmt;

    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>5 to 600 seconds; empty: 60.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>An export before and one after, for the diff (§8.2). Empty: on for admin runs, off for pnmt.</summary>
    public bool? BackupBeforeAndAfter { get; init; }

    /// <summary>One router at a time, stopping at the first failure. Empty: on for admin runs, off for pnmt.</summary>
    public bool? OneAtATime { get; init; }

    /// <summary>Neither the password nor the text.</summary>
    public override string ToString() => $"command on {SensorIds.Count} router(s) as {(RunAs == MikrotikRunAs.Admin ? Username : "pnmt")}";
}

/// <summary>The command jobs created, one per router, and the routers refused before anything ran.</summary>
public sealed record MikrotikCommandsCreated(Guid BatchId, IReadOnlyList<MikrotikCommandItem> Jobs, IReadOnlyList<MikrotikJobRefusal> Refused);

/// <summary>A command on one router (§8.3). <see cref="Command"/> is null once redacted.</summary>
public sealed record MikrotikCommandItem(
    Guid Id,
    Guid BatchId,
    Guid? SensorId,
    string RouterName,
    MikrotikJobState State,
    MikrotikJobStep Step,
    string? RequestedBy,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? Command,
    string RunAs,
    bool AsAdmin,
    int TimeoutSeconds,
    int? ExitStatus,
    string? Error,
    bool OutputTruncated,
    Guid? BackupBeforeId,
    Guid? BackupAfterId,
    DateTime? RedactedAt,
    string? RedactedBy);

/// <summary>A command with its output, its log, and what it changed: the two backups' line counts.</summary>
public sealed record MikrotikCommandDetail(MikrotikCommandItem Command, string? Output, IReadOnlyList<MikrotikJobLogItem> Log, int? Added, int? Removed);

/// <summary>A batch's results grouped by outcome and output (§8.3), the largest group first.</summary>
public sealed record MikrotikCommandBatch(
    Guid BatchId,
    string? Command,
    string RunAs,
    bool AsAdmin,
    string? RequestedBy,
    DateTime CreatedAt,
    IReadOnlyList<MikrotikCommandGroup> Groups);

public sealed record MikrotikCommandGroup(MikrotikJobState State, string? Output, bool OutputTruncated, IReadOnlyList<MikrotikCommandItem> Jobs);

/// <summary><c>GET /mikrotik/commands</c>: newest first. Admin runs only for Admins.</summary>
public sealed class MikrotikCommandQuery : PageQuery
{
    public Guid? SensorId { get; set; }

    public Guid? BatchId { get; set; }

    public MikrotikJobState? State { get; set; }
}

/// <summary>A RouterOS release's changelog, from MikroTik's update server (§5.4).</summary>
public sealed record MikrotikChangelog(string Version, string Text);

/// <summary>
/// <c>GET /mikrotik/backup-settings</c> (docs/mikrotik-backup-plan.md §2.2, §3.5): what new routers start with, the
/// active backup key (null: encryption is off), how many routers use it, and every key, newest first.
/// </summary>
public sealed record MikrotikBackupSettings(
    int? DefaultBackupIntervalHours,
    bool DefaultEncryptBackups = false,
    MikrotikBackupKeyItem? Key = null,
    int RoutersEncrypted = 0,
    IReadOnlyList<MikrotikBackupKeyItem>? Keys = null);

/// <summary>A backup key: an age recipient. PNMT holds no private key.</summary>
public sealed record MikrotikBackupKeyItem(
    string Fingerprint,
    string Recipient,
    string Kind,
    DateTime CreatedAt,
    string? CreatedBy,
    DateTime? RetiredAt,
    int Backups);

/// <summary><c>PUT /mikrotik/backup-settings</c> (Admin).</summary>
public sealed class MikrotikBackupSettingsRequest
{
    /// <summary>1 to 8760 hours, or null for off.</summary>
    public int? DefaultBackupIntervalHours { get; init; } = 24;

    /// <summary>New routers start with their backups encrypted; only while there is a key.</summary>
    public bool DefaultEncryptBackups { get; init; }
}

/// <summary><c>POST /mikrotik/backup-keys</c> (Admin, HTTPS): the public key new backups are encrypted with.</summary>
public sealed class MikrotikBackupKeyRequest
{
    /// <summary>An age X25519 recipient, <c>age1…</c>.</summary>
    public string Recipient { get; init; } = "";
}

/// <summary>
/// <c>POST /mikrotik/routers/backup-settings</c> (Admin, docs/mikrotik-backup-plan.md §2.2): the backup schedule of
/// many routers at once.
/// </summary>
public sealed class MikrotikRouterBackupRequest
{
    /// <summary>1 to 500 routers.</summary>
    public IReadOnlyList<Guid> SensorIds { get; init; } = [];

    /// <summary>Set the schedule to <see cref="BackupIntervalHours"/>; false leaves it as it is.</summary>
    public bool SetSchedule { get; init; }

    /// <summary>1 to 8760 hours, or null for off.</summary>
    public int? BackupIntervalHours { get; init; }

    /// <summary>Encrypt their backups (on only while there is a key); null leaves it as it is.</summary>
    public bool? EncryptBackups { get; init; }
}

/// <summary>How many routers the change touched.</summary>
public sealed record MikrotikRouterBackupResult(int Changed);
