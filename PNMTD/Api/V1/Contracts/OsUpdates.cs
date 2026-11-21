using PNMTD.Lib.Agent;
using PNMTD.Models.Db;
using PNMTD.OsUpdates;

namespace PNMTD.Api.V1.Contracts;

// OS updates on Linux servers (docs/linux-os-updates-plan.md §9): a server's state, its runs and logs, its findings and their
// acknowledgements, the dashboard's summary, and the advisory sources. The names of people need Monitoring's Details.

/// <summary>
/// A Linux server's OS updates (§10.1): the switches and what config.yaml allows, the schedule, the lists' age, the counts, what dpkg,
/// needrestart and a reboot say, both modes' plans, the runs in progress and last, the pending packages and the findings.
/// </summary>
public sealed record OsUpdatesView(
    Guid SensorId,
    string Hostname,
    bool Enabled,
    bool CheckEnabled,
    bool Connected,
    bool Capable,
    string? Unavailable,
    OsUpdatesRightsSummary? Rights,
    bool RightsRefused,
    string? Distribution,
    bool Proxmox,
    DateTime? ListsUpdatedAt,
    DateTime? NextUpdateAt,
    bool DailyUpdate,
    string WindowStart,
    int WindowMinutes,
    int Pending,
    int Security,
    int HighRisk,
    int HighRiskAcknowledged,
    int Important,
    bool DpkgInterrupted,
    bool RebootRequired,
    IReadOnlyList<string> RebootPackages,
    NeedsRestartInfo? NeedsRestart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> PhasedDeferred,
    AptPlan? Upgrade,
    AptPlan? FullUpgrade,
    OsUpgradeMode DefaultMode,
    OsUpdateRunItem? Current,
    OsUpdateRunItem? LastUpdate,
    OsUpdateRunItem? LastUpgrade,
    IReadOnlyList<OsPendingPackageView> Packages,
    IReadOnlyList<OsFindingView> Findings,
    OsUpdateServerSettings Settings,
    string? UpdateFailedMessage,
    string? HighRiskMessage,
    int UpgradeWarnMinutes,
    int AcknowledgeMaxDays);

/// <summary>A pending package with what the check knows: the CVEs its update fixes and the strongest rating among them.</summary>
public sealed record OsPendingPackageView(
    string Name,
    string Arch,
    string? Installed,
    string Candidate,
    string Source,
    string SourceVersion,
    string? Origin,
    bool Security,
    bool InUpgrade,
    bool Phased,
    DateTime FirstSeenAt,
    IReadOnlyList<string> Cves,
    OsFindingRating? Rating,
    bool HasChangelog);

/// <summary>A finding (§6.4, §6.5): one CVE in one source package, its reasons with their sources, and its acknowledgement.</summary>
public sealed record OsFindingView(
    Guid Id,
    string SourcePackage,
    string Cve,
    string Packages,
    string? Installed,
    string Candidate,
    OsFindingRating Rating,
    IReadOnlyList<OsFindingReason> Reasons,
    DateTime FirstSeenAt,
    OsFindingAcknowledgement? Acknowledgement,
    DateTime? AcknowledgementEndedAt,
    string? AcknowledgementEndedReason);

/// <summary>Who acknowledged a finding (null without Monitoring's Details), why, when, and until when.</summary>
public sealed record OsFindingAcknowledgement(string? ByName, string Reason, DateTime At, DateTime Until);

/// <summary>
/// A run in a list: what, who (null without Monitoring's Details), when, its state, its counts, and its log's size. <see cref="Waiting"/>:
/// running, and the server hasn't sent anything about it for 15 minutes (offline: it reports when it is back).
/// </summary>
public sealed record OsUpdateRunItem(
    Guid Id,
    OsUpdateKind Kind,
    OsUpgradeMode? Mode,
    OsUpdateTrigger Trigger,
    string? RequestedByName,
    DateTime RequestedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    OsUpdateState State,
    int? ExitCode,
    string? Message,
    string? Command,
    long LogBytes,
    bool LogTruncated,
    bool LogIncomplete,
    bool TakingLong,
    bool Dismissed,
    OsUpdateCounts Counts,
    bool Waiting);

public sealed record OsUpdateCounts(int Upgraded, int Installed, int Removed, int Warnings, int Errors, int ServicesToRestart, bool RebootRequired,
    int RepositoriesReached, int RepositoriesFailed, int ConfigFilesKept);

/// <summary>A run with its summary: the packages it changed, the files it kept, the services to restart, its warnings and errors.</summary>
public sealed record OsUpdateRunDetail(OsUpdateRunItem Run, OsUpdateSummary? Summary);

public sealed class OsUpdateRunQuery : PageQuery
{
    /// <summary>Update or Upgrade; none: both.</summary>
    public OsUpdateKind? Kind { get; set; }
}

/// <summary>A run's log from an offset: the text, the offset to ask from next, whether the run ended, and the log's bytes so far.</summary>
public sealed record OsUpdateLogPart(string Text, long NextOffset, bool Finished, long TotalBytes, bool Truncated, bool Incomplete);

/// <summary><i>Install updates…</i> (§10.2): the mode, and for a full upgrade the removals' hash the person saw.</summary>
public sealed record OsUpdateUpgradeRequest(OsUpgradeMode Mode, string? RemovalsHash);

/// <summary>A run asked for: its id and state (Running, or Refused with the reason).</summary>
public sealed record OsUpdateStarted(Guid RunId, OsUpdateState State, string? Message);

/// <summary>The server's switches (§3, §5.4).</summary>
public sealed record OsUpdateServerSettings(bool Enabled, bool HighRiskEvents, bool UpdateFailedEvents);

/// <summary>Acknowledging high-risk findings (§6.5): the findings of one server, the reason (required), and for how many days.</summary>
public sealed record OsFindingAcknowledgeRequest(IReadOnlyList<Guid> FindingIds, string Reason, int Days);

/// <summary>A pending package's changelog part (§6.2), as a server fetched it.</summary>
public sealed record OsPackageChangelogView(string Source, string Version, string? InstalledVersion, IReadOnlyList<string> Cves, IReadOnlyList<string> Urgencies,
    string Text, string? FetchedFrom, DateTime ReceivedAt);

// --- the dashboard (§10.3) ---------------------------------------------------------------------------------------------------

public sealed record OsUpdatesSummary(
    bool Enabled,
    bool CheckEnabled,
    int Servers,
    int ServersWithUpdates,
    int Packages,
    int Security,
    int HighRisk,
    int HighRiskAcknowledged,
    int Failed,
    int ListsStale,
    int Running,
    IReadOnlyList<OsUpdatesServerRow> ByServer,
    IReadOnlyList<OsUpdatesPackageRow> ByPackage);

/// <summary>A server on the card: its counts and badges.</summary>
public sealed record OsUpdatesServerRow(
    Guid SensorId,
    string Name,
    string HostName,
    int Pending,
    int Security,
    int HighRisk,
    int HighRiskAcknowledged,
    DateTime? AcknowledgedUntil,
    bool RebootRequired,
    bool ListsStale,
    DateTime? ListsUpdatedAt,
    bool Failed,
    bool Running,
    string? FailedMessage);

/// <summary>A source package and candidate across the fleet: on how many servers (and which), security, its rating and CVEs.</summary>
public sealed record OsUpdatesPackageRow(
    string Source,
    string Version,
    int ServerCount,
    IReadOnlyList<string> Servers,
    bool Security,
    OsFindingRating? Rating,
    IReadOnlyList<string> Cves);

// --- the advisory sources (§6.1, §10.4) -------------------------------------------------------------------------------------

public sealed record OsAdvisorySourceView(
    Guid Id,
    OsAdvisorySourceKind Kind,
    string? Role,
    string Name,
    string Url,
    bool Enabled,
    int IntervalMinutes,
    DateTime CreatedAt,
    DateTime? LastFetchAt,
    DateTime? LastSuccessAt,
    int Entries,
    string? Error,
    bool Stale);

/// <summary>
/// The sources, the coverage per role, the thresholds from .env, and whether the check and its AI are on. <see cref="Ok"/>: what the system
/// sensor says, 220 (true) or 434, with <see cref="State"/> its message.
/// </summary>
public sealed record OsAdvisorySourcesView(
    bool CheckEnabled,
    bool AiEnabled,
    IReadOnlyList<OsAdvisorySourceView> Sources,
    IReadOnlyList<OsAdvisoryRole> Roles,
    double HighRiskEpssPercentile,
    double ImportantEpssPercentile,
    int StaleHours,
    bool AllowPrivateAddresses,
    string State,
    bool Ok,
    int AcknowledgeMaxDays);

public sealed record OsAdvisoryRole(string Role, string Label, IReadOnlyList<string> Working, bool Covered);

public sealed record OsAdvisorySourceRequest(OsAdvisorySourceKind Kind, string Name, string Url, bool Enabled, int IntervalMinutes);

/// <summary>An advisory text with its assessment (§7, §10.4): code's CVEs and keywords, the AI's claims, the verdict, what code dropped.</summary>
public sealed record OsAdvisoryItemView(
    Guid Id,
    Guid SourceId,
    string SourceName,
    string Title,
    string? Link,
    DateTime? PublishedAt,
    DateTime FirstSeenAt,
    IReadOnlyList<string> Cves,
    IReadOnlyList<string> Keywords,
    string AssessedBy,
    DateTime? AssessedAt,
    OsAdvisoryAssessment? Assessment);
