using System.Text.Json.Serialization;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// Servers with an agent, Windows and Linux (docs/windows-sensor-plan.md §9.2; docs/linux-sensor-plan.md §10): under
// /api/v1/agents/ since L1. A Windows server's typed sections and WMI are in Windows.cs.

/// <summary>
/// A server in the fleet list (§10.1), of either platform. Linux (docs/linux-sensor-plan.md §10) adds the distribution, the kernel,
/// the security updates pending, whether config.yaml allows key removal, and from the key manager (§6.3) the keys found there that
/// aren't registered and the retired keys still authorized there (by fingerprint); null on Windows.
/// </summary>
public sealed record AgentServerItem(
    Guid SensorId,
    Ref Host,
    string Name,
    AgentPlatform Platform,
    SensorState State,
    int? LastCode,
    string? LastMessage,
    string Hostname,
    string? Domain,
    string? OsCaption,
    string? OsBuild,
    bool OsSupported,
    string? Architecture,
    string? AgentVersion,
    bool Outdated,
    bool Connected,
    DateTime? LastSeenAt,
    DateTime? BootTime,
    int? ClockSkewSeconds,
    bool Revoked,
    bool Uninstalled,
    DateTime? LastProblemAt,
    string? RemoteAccess,
    int AuditGaps,
    int DiagnosticsFailing,
    string? Distribution = null,
    string? KernelRelease = null,
    int? SecurityUpdates = null,
    bool? KeyRemovalAllowed = null,
    int? UnregisteredKeys = null,
    int? RetiredKeysAuthorized = null,
    string? ChosenVersion = null,
    bool Ahead = false);

public sealed class AgentServerQuery : PageQuery
{
    /// <summary>Only the servers of one platform (docs/linux-sensor-plan.md §10).</summary>
    public AgentPlatform? Platform { get; set; }

    public SensorState? State { get; set; }

    /// <summary>Only servers on an OS version the agent does not support.</summary>
    public bool? Unsupported { get; set; }

    /// <summary>Only servers whose agent is older than PNMTD.</summary>
    public bool? Outdated { get; set; }

    public bool? Connected { get; set; }

    /// <summary>Only servers with remote access open (RDP, WinRM, SMB, …).</summary>
    public bool? RemoteAccess { get; set; }

    /// <summary>Only servers whose audit policy misses something.</summary>
    public bool? AuditGaps { get; set; }

    /// <summary>Only servers with a failing diagnostics assignment.</summary>
    public bool? DiagnosticsFailing { get; set; }

    /// <summary>Linux: only servers with security updates pending.</summary>
    public bool? SecurityUpdates { get; set; }

    /// <summary>Linux: only servers whose config.yaml allows PNMT to remove keys.</summary>
    public bool? KeyRemoval { get; set; }

    /// <summary>Linux: only servers with keys that aren't registered (docs/linux-sensor-plan.md §11.1).</summary>
    public bool? UnregisteredKeys { get; set; }

    /// <summary>Linux: only servers where retired keys are still authorized.</summary>
    public bool? RetiredKeys { get; set; }

    public Guid? HostId { get; set; }
}

/// <summary>When a section was collected, and its collector's errors.</summary>
public sealed record AgentSectionInfo(string Name, DateTime CollectedAt, DateTime ReceivedAt, IReadOnlyList<string> Errors);

/// <summary>
/// A server's page (§10.2): the agent, its connection, the last heartbeat, its switches and recent problems. On Linux
/// (docs/linux-sensor-plan.md §4.6, §11.3) also the rights its config.yaml gives PNMT, and since when security updates are pending.
/// </summary>
public sealed record AgentServerDetail(
    AgentServerItem Server,
    Guid AgentId,
    string KeyFingerprint,
    string PnmtdFingerprint,
    DateTime JoinedAt,
    DateTime? ConnectedAt,
    DateTime? DisconnectedAt,
    string? RemoteAddress,
    string? Fqdn,
    string? OsEdition,
    string? OsDisplayVersion,
    string? MachineId,
    IReadOnlyList<string> Capabilities,
    int ConfigVersion,
    int CurrentConfigVersion,
    Heartbeat? Heartbeat,
    AgentServerSettings Settings,
    IReadOnlyList<AgentLogItem> RecentProblems,
    IReadOnlyList<Ref> SameMachine,
    IReadOnlyList<AgentSectionInfo> Sections,
    string? RevokedReason,
    DateTime? RevokedAt,
    DateTime? UninstalledAt,
    string PnmtVersion,
    AgentRightsSummary? Rights = null,
    DateTime? SecurityUpdatesSince = null);

/// <summary>
/// A server's event switches (§4.7, §4.8), with its platform's codes (254, 454, 453; 264, 464, 463). Linux's own
/// (docs/linux-sensor-plan.md §9.2, §11.3): <see cref="KeyAddedEvents"/> (465, from L5), <see cref="SecurityUpdateEvents"/> (462)
/// after <see cref="SecurityUpdateDays"/> (1 to 365); Windows servers ignore them.
/// </summary>
public sealed record AgentServerSettings(bool AdminLoginEvents, bool FailedLoginEvents, int FailedLoginThreshold, bool CriticalEvents,
    bool KeyAddedEvents = true, bool SecurityUpdateEvents = false, int SecurityUpdateDays = 7);

public sealed record AgentLogItem(Guid Id, Guid? SensorId, string AgentName, DateTime At, AgentLogKind Kind, string Message,
    string? Details, string? Address);

public sealed class AgentLogQuery : PageQuery
{
    public AgentLogKind? Kind { get; set; }

    public Guid? SensorId { get; set; }
}

/// <summary>A join token in the list: never the token itself, which is shown once.</summary>
public sealed record AgentJoinTokenItem(
    Guid Id,
    AgentPlatform Platform,
    string Prefix,
    Ref Host,
    string? Name,
    Ref? Rejoin,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime ExpiresAt,
    DateTime? UsedAt,
    string? UsedFromAddress,
    Ref? Result,
    DateTime? RevokedAt,
    AgentJoinTokenState State);

[JsonConverter(typeof(JsonStringEnumConverter<AgentJoinTokenState>))]
public enum AgentJoinTokenState { Pending, Used, Expired, Revoked }

public sealed class AgentJoinTokenQuery : PageQuery
{
    public AgentJoinTokenState? State { get; set; }
}

/// <summary>
/// Add server (§3.3): the host it joins, an optional name, how long the token is valid (1 to 168 hours), and the platform
/// it is for (docs/linux-sensor-plan.md §10; Windows when missing): an agent of the other platform is refused.
/// </summary>
public sealed record AgentJoinTokenRequest(Guid HostId, string? Name, int? ValidHours, AgentPlatform Platform = AgentPlatform.Windows);

/// <summary>A re-join token (§3.3): bound to the server, whose key the join replaces.</summary>
public sealed record AgentRejoinTokenRequest(int? ValidHours);

/// <summary>
/// The new token, shown once, with what the server's administrator needs. <see cref="CommandLine"/>: Windows' command line for
/// unattended installs; for a Linux token the one install line (docs/linux-sensor-plan.md §4.4: x64, from PNMT, with wget, the
/// token in it), or only the join when the binary's hash isn't known yet. <see cref="Downloads"/>' lines carry
/// <see cref="AgentDownloadLinks.TokenPlaceholder"/>.
/// </summary>
public sealed record AgentJoinTokenCreated(
    AgentJoinTokenItem Item,
    string Token,
    string Url,
    string PnmtdFingerprint,
    string CommandLine,
    AgentDownloadLinks Downloads);

/// <summary>
/// The agents' files of the versions each platform's agents get (§10.1; docs/linux-sensor-plan.md §4.4, §10;
/// docs/agent-update-plan.md §5.2): <see cref="Version"/> is PNMTD's, <see cref="WindowsVersion"/> and <see cref="LinuxVersion"/> the
/// chosen ones (PNMTD's unless an admin chose another). <see cref="Files"/>: Windows' executables as before L3 (GitHub's link, the
/// release's SHA-256, PNMT's link). <see cref="Assets"/>: every file of both platforms, of its platform's version, with its state in
/// PNMT's download folder. <see cref="Linux"/>: the install lines per architecture, from PNMT and GitHub, with wget and curl, PNMT's
/// URL (<see cref="Url"/>) and <see cref="TokenPlaceholder"/> for the token. <see cref="ServeDownloads"/> false: PNMT's links are
/// null, GitHub's remain. <see cref="ReleaseUrl"/> is the Windows version's release, <see cref="LinuxReleaseUrl"/> the Linux one's.
/// </summary>
public sealed record AgentDownloadLinks(
    string Version,
    IReadOnlyList<AgentDownloadLink> Files,
    string? ReleaseUrl,
    string? Note,
    IReadOnlyList<AgentDownloadFile> Assets,
    IReadOnlyList<LinuxInstallLines> Linux,
    string UpdateLine,
    bool ServeDownloads,
    string Url,
    string TokenPlaceholder,
    string WindowsVersion,
    string LinuxVersion,
    string? LinuxReleaseUrl);

public sealed record AgentDownloadLink(string Architecture, string FileName, string Url, string? Sha256, string? PnmtUrl = null);

/// <summary>Where an agent's file is (docs/linux-sensor-plan.md §4.4): here and verified, being fetched, failed (with the reason), or not fetched yet.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentDownloadState>))]
public enum AgentDownloadState { Here, Fetching, Failed, NotFetched }

/// <summary>
/// An agent's file: PNMT's link (null when PNMT doesn't serve them), GitHub's, the SHA-256 (verified when it is here, else the
/// release's), its size when here, its state, why it failed, and when PNMT fetched it.
/// </summary>
public sealed record AgentDownloadFile(
    string Name,
    AgentPlatform Platform,
    string Architecture,
    string? PnmtUrl,
    string GitHubUrl,
    string? Sha256,
    long? Size,
    AgentDownloadState State,
    string? Error,
    DateTime? FetchedAt,
    string Version = "");

/// <summary>
/// The one line of docs/linux-sensor-plan.md §4.4 for an architecture: download, <c>sha256sum -c</c>, install, join. From PNMT
/// (<see cref="Pnmt"/>, <see cref="PnmtCurl"/>; null when PNMT doesn't serve the files) or GitHub, with wget or curl; null while the
/// hash isn't known. For <c>sudo sh -c '…'</c> the line goes in single quotes as it is.
/// </summary>
public sealed record LinuxInstallLines(string Architecture, string FileName, string? Sha256, string? Pnmt, string? PnmtCurl, string? GitHub, string? GitHubCurl);

/// <summary><i>Fetch again</i>: these files (empty: every file that isn't here and verified).</summary>
public sealed record AgentDownloadFetchRequest(IReadOnlyList<string>? Files);

// --- the agents' versions (docs/agent-update-plan.md §3, §4, §8.1, §9) -------------------------------------------

/// <summary>Where PNMT got an agent's file: GitHub's release, an admin's upload, or the download folder itself (a mount).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentFileSource>))]
public enum AgentFileSource { GitHub, Upload, Folder }

/// <summary>
/// <i>Admin → Servers → Versions</i>: PNMTD's version, the version each platform's agents get, every version here or in GitHub's
/// list (newest first), and the download folder. <see cref="Catalog"/>: whether GitHub's list could be read, when, and why not.
/// </summary>
public sealed record AgentVersionList(
    string PnmtVersion,
    AgentChosenVersion Windows,
    AgentChosenVersion Linux,
    IReadOnlyList<AgentVersionItem> Versions,
    AgentReleaseCatalogState Catalog,
    string Folder,
    long FolderBytes,
    int KeepVersions,
    bool ServeDownloads);

/// <summary>
/// The version a platform's agents get: chosen, or PNMTD's own (<see cref="FollowsPnmt"/>); newer than PNMTD (a warning) or a
/// pre-release; how many agents of the platform run it, are older (outdated) and newer.
/// </summary>
public sealed record AgentChosenVersion(
    AgentPlatform Platform,
    string Version,
    bool FollowsPnmt,
    bool NewerThanPnmt,
    bool PreRelease,
    int Agents,
    int Running,
    int Older,
    int Newer);

/// <summary>GitHub's list of releases (§3.2): read or not, when, why not, and whether there is one at all (a mirror has none).</summary>
public sealed record AgentReleaseCatalogState(bool Configured, bool Available, DateTime? ReadAt, string? Error);

/// <summary>
/// A version: a pre-release, its release on GitHub (date, link) when the list has it, its four files, the agents running it per
/// platform, whether it is chosen, PNMTD's own or newer than PNMTD, and why it can't be deleted (null: it can).
/// <see cref="KeptUntilDeleted"/>: it has an uploaded file, so it is never deleted automatically.
/// </summary>
public sealed record AgentVersionItem(
    string Version,
    bool PreRelease,
    bool OnGitHub,
    DateTime? ReleasedAt,
    string? ReleaseUrl,
    bool Here,
    IReadOnlyList<AgentVersionFile> Files,
    int WindowsAgents,
    int LinuxAgents,
    bool ChosenForWindows,
    bool ChosenForLinux,
    bool IsPnmtVersion,
    bool NewerThanPnmt,
    bool KeptUntilDeleted,
    string? DeleteRefused);

/// <summary>
/// One of a version's four files: its state here (here and verified, being fetched, failed with the reason, not here), whether
/// GitHub's release has it (null: not known), the SHA-256 and size when here, and where it came from, when and by whom.
/// </summary>
public sealed record AgentVersionFile(
    string Name,
    AgentPlatform Platform,
    string Architecture,
    AgentDownloadState State,
    bool? InRelease,
    string? Sha256,
    long? Size,
    string? Error,
    AgentFileSource? Source,
    DateTime? AddedAt,
    string? AddedBy);

/// <summary>
/// The versions the platforms' agents get (§4): null or empty for PNMTD's own. One newer than PNMTD needs
/// <see cref="ConfirmNewer"/> (409 <c>newer_than_pnmt</c> otherwise).
/// </summary>
public sealed record AgentVersionChooseRequest(string? Windows, string? Linux, bool ConfirmNewer = false);

/// <summary>Fetch a version from GitHub's release (§3.3): these files (empty: every file of it that isn't here and verified).</summary>
public sealed record AgentVersionFetchRequest(IReadOnlyList<string>? Files);

public sealed record AgentRevokeRequest(string? Reason);

/// <summary>Collect these sections now (empty: all).</summary>
public sealed record AgentCollectRequest(IReadOnlyList<string>? Sections);

/// <summary>What the agent answered to a request.</summary>
public sealed record AgentRequestResult(bool Ok, string? Error);

/// <summary>The last lines of the agent's log file.</summary>
public sealed record AgentLogFile(DateTime At, IReadOnlyList<string> Lines);

/// <summary>
/// A login or connection (§4.7): an aggregate when <see cref="Count"/> is above 1. On Linux (docs/linux-sensor-plan.md §5.6) the key
/// of a key login, whom sudo or su became, sudo's command (where config.yaml sends it), and the names an "invalid user" source tried;
/// with the key manager (§6.4) the key's registered name, owner and id, for a key registered now (past logins included).
/// </summary>
public sealed record AgentLoginItem(
    Guid Id,
    DateTime At,
    DateTime? LastAt,
    int Count,
    LogonOutcome Outcome,
    string? FailureReason,
    string User,
    string? Domain,
    bool IsAdmin,
    LogonKind Kind,
    string? AuthPackage,
    string? SourceAddress,
    int? SourcePort,
    string? Workstation,
    bool? Elevated,
    DateTime? LoggedOffAt,
    string Channel,
    string? KeyFingerprint = null,
    string? TargetUser = null,
    string? Command = null,
    IReadOnlyList<string>? TriedUsers = null,
    string? KeyName = null,
    string? KeyOwner = null,
    Guid? KeyId = null,
    AccessReviewRef? Review = null);

public sealed class AgentLoginQuery : PageQuery
{
    public LogonOutcome? Outcome { get; set; }

    public bool? Admin { get; set; }

    /// <summary>The kind; on Linux also Sudo, Su and Sftp (docs/linux-sensor-plan.md §10).</summary>
    public LogonKind? Kind { get; set; }

    /// <summary>Linux: the logins with this key (<c>SHA256:…</c>, as pasted: the prefix and padding are optional).</summary>
    public string? KeyFingerprint { get; set; }

    /// <summary>Linux: the logins with a key of this owner (docs/linux-sensor-plan.md §10): a user's id, or a text (an owner's text or a user name).</summary>
    public string? KeyOwner { get; set; }

    public string? User { get; set; }

    public string? Source { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

public sealed record AgentChangeItem(Guid Id, string Section, DateTime At, string Summary, bool Notable, string? Details);

public sealed class AgentChangeQuery : PageQuery
{
    public string? Section { get; set; }

    public bool? Notable { get; set; }
}
