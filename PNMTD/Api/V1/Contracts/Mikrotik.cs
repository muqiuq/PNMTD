using PNMTD.Lib.Mikrotik;
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

    public int BackupIntervalHours { get; init; } = 24;

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
    int BackupIntervalHours,
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
    MikrotikBackupItem? LatestBackup = null);

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
    bool ScriptOutdated);

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
    bool Encrypted);

/// <summary>An export's text, normalised (§6.2).</summary>
public sealed record MikrotikBackupContent(MikrotikBackupItem Backup, string Content);

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
