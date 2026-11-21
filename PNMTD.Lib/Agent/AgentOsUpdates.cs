using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace PNMTD.Lib.Agent;

// OS updates on Linux servers (docs/linux-os-updates-plan.md §4): the os-updates section, the messages of a run (apt-get update,
// upgrade, full-upgrade), the history of other runs, the changelogs of pending packages, the daily window in the configuration,
// and what config.yaml's os_updates block allows. PNMT names only a kind and a mode: the agent builds every command line itself.

/// <summary>apt-get update (the lists) or an upgrade (installing).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OsUpdateKind>))]
public enum OsUpdateKind { Update, Upgrade }

/// <summary><c>apt-get upgrade --with-new-pkgs</c> (never removes) or <c>apt-get full-upgrade</c> (may remove).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OsUpgradeMode>))]
public enum OsUpgradeMode { Upgrade, FullUpgrade }

/// <summary>Who started a run: the daily schedule, a person in PNMT, root on the server (<c>pnmt-agent os-updates update</c>), or outside PNMT (history.log).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OsUpdateTrigger>))]
public enum OsUpdateTrigger { Schedule, Person, Server, External }

/// <summary>
/// A run's state (§4.4, §5.1): Requested and Refused are PNMTD's; Running while it lasts; Succeeded; Warnings (some repositories
/// failed, others were reached; or apt warned); Failed; Lost (no answer, or the server didn't report back within 7 days).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OsUpdateState>))]
public enum OsUpdateState { Requested, Refused, Running, Succeeded, Warnings, Failed, Lost }

/// <summary>
/// The daily update's window (§4.2), in <see cref="AgentConfig.OsUpdates"/>: on or off for this server, and the window's start
/// (HH:mm, the server's local time) and length. Each agent takes a fixed slot inside it from its id.
/// </summary>
public sealed record OsUpdatesConfig(bool Enabled, bool DailyUpdate, string WindowStart = "04:00", int WindowMinutes = 120);

/// <summary>config.yaml's <c>os_updates</c> (§3), in the hello's rights: what PNMT may run on this server.</summary>
public sealed record OsUpdatesRightsSummary(bool Update, bool Upgrade, bool FullUpgrade, bool Changelogs);

// --- the os-updates section (§4.1) ------------------------------------------------------------------------------------------

/// <summary>
/// The pending packages and what an upgrade would do, from apt's simulations (never <c>apt update</c> by itself): at most
/// <see cref="MaxPackages"/> packages, <see cref="PackagesTotal"/> counts them all.
/// </summary>
public sealed record OsUpdatesSection(
    string? Distribution,
    DateTimeOffset? ListsUpdatedAt,
    DateTimeOffset? NextUpdateAt,
    IReadOnlyList<PendingPackage> Packages,
    int PackagesTotal,
    AptPlan? Upgrade,
    AptPlan? FullUpgrade,
    bool DpkgInterrupted,
    IReadOnlyList<string> Warnings,
    NeedsRestartInfo? NeedsRestart,
    bool RebootRequired,
    IReadOnlyList<string> RebootPackages,
    OsUpdateRunRef? Current,
    OsUpdateRunRef? LastUpdate,
    IReadOnlyList<string> PhasedDeferred)
{
    public const int MaxPackages = 3000;
}

/// <summary>
/// A package an upgrade would install or upgrade: its versions, its source package and the source's versions (they differ for
/// binNMUs), its origin (<c>Debian-Security:12/oldstable-security</c>), whether that is a security archive, and whether a plain
/// upgrade installs it (false: only a full upgrade). <see cref="Installed"/> is null for a new package.
/// </summary>
public sealed record PendingPackage(
    string Name,
    string? Arch,
    string? Installed,
    string Candidate,
    string? Source,
    string? SourceVersion,
    string? InstalledSourceVersion,
    string? Origin,
    bool Security,
    bool InUpgrade,
    bool Phased = false);

/// <summary>
/// What one mode would do: the counts, the download and the extra disk space (negative: freed), and the packages it would remove,
/// with their hash, which a full upgrade's request repeats so the agent refuses when the removals changed (§4.4).
/// </summary>
public sealed record AptPlan(int Upgrade, int Install, int Remove, int KeptBack, long? DownloadBytes, long? DiskBytes, IReadOnlyList<string> Removals,
    string RemovalsHash)
{
    /// <summary>The removals' hash: SHA-256 of the sorted names, one per line, the first 16 hex digits; the same on both sides.</summary>
    public static string HashOf(IEnumerable<string> removals)
    {
        var text = string.Join('\n', removals.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
    }
}

/// <summary>needrestart's batch mode (<c>needrestart -b -r l</c>, which only lists, D4): the services still running old libraries, and a pending kernel.</summary>
public sealed record NeedsRestartInfo(IReadOnlyList<string> Services, bool KernelPending, string? RunningKernel, string? ExpectedKernel);

/// <summary>A run, briefly: its id, kind and mode, when it started, and its state.</summary>
public sealed record OsUpdateRunRef(Guid RunId, OsUpdateKind Kind, OsUpgradeMode? Mode, DateTimeOffset StartedAt, OsUpdateState State);

// --- runs (§4.5, §4.6) ----------------------------------------------------------------------------------------------------------

/// <summary>
/// PNMTD → agent (signed): run apt-get. Only the kind and the mode: no package names, options or command lines. A full upgrade
/// repeats the removals' hash the person saw (<see cref="AptPlan.RemovalsHash"/>); the agent refuses when it changed.
/// </summary>
public sealed record OsUpdateRunRequest(Guid RunId, OsUpdateKind Kind, OsUpgradeMode? Mode, string? RequestedByName, string? RemovalsHash = null);

/// <summary>Agent → PNMTD: a run started (a scheduled one with the agent's own id).</summary>
public sealed record OsUpdateStartedMessage(Guid RunId, OsUpdateKind Kind, OsUpgradeMode? Mode, OsUpdateTrigger Trigger, string? RequestedByName,
    DateTimeOffset StartedAt, string Command);

/// <summary>Agent → PNMTD: the log from <see cref="Offset"/> (bytes of UTF-8) on, at most <see cref="MaxBytes"/>.</summary>
public sealed record OsUpdateOutputMessage(Guid RunId, long Offset, string Text)
{
    public const int MaxBytes = 256 * 1024;
}

/// <summary>
/// Agent → PNMTD: a run ended. It repeats the kind, mode and trigger, so it stands on its own when the start was never received
/// (offline). The log's size and SHA-256 let PNMTD check that it has all of it.
/// </summary>
public sealed record OsUpdateResultMessage(
    Guid RunId,
    OsUpdateKind Kind,
    OsUpgradeMode? Mode,
    OsUpdateTrigger Trigger,
    string? RequestedByName,
    OsUpdateState State,
    int? ExitCode,
    OsUpdateSummary Summary,
    string? Message,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long LogBytes,
    string LogSha256,
    string? Command = null);

/// <summary>
/// What a run did (§4.4): from history.log (upgraded, installed, removed), and from its output: configuration files kept,
/// needrestart's services, the warnings and errors, the repositories that failed (an update), and apt's "Fetched …" line.
/// </summary>
public sealed record OsUpdateSummary(
    IReadOnlyList<PackageChange> Upgraded,
    IReadOnlyList<PackageChange> Installed,
    IReadOnlyList<PackageChange> Removed,
    IReadOnlyList<string> ConfigFilesKept,
    IReadOnlyList<string> ServicesToRestart,
    bool RebootRequired,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    int RepositoriesReached = 0,
    int RepositoriesFailed = 0,
    string? Fetched = null)
{
    public static OsUpdateSummary Empty { get; } = new([], [], [], [], [], false, [], []);
}

/// <summary>A package a run changed: its name, architecture, and the versions before and after (null for new or removed).</summary>
public sealed record PackageChange(string Name, string? Arch, string? From, string? To);

/// <summary>PNMTD → agent (signed): send this run's log again from this offset (a part went missing).</summary>
public sealed record OsUpdateLogRequest(Guid RunId, long FromOffset);

/// <summary>PNMTD → agent (signed): PNMTD stored these runs in full; the agent may forget their results.</summary>
public sealed record OsUpdateReceivedRequest(IReadOnlyList<Guid> RunIds);

/// <summary>The limits both sides keep to.</summary>
public static class OsUpdateLimits
{
    /// <summary>Runs whose result PNMTD hasn't acknowledged, kept on the server (§4.5).</summary>
    public const int MaxPendingRuns = 20;

    /// <summary>A run's log on the wire and on the server: longer logs keep their start and end.</summary>
    public const long MaxLogBytes = 64L * 1024 * 1024;
}

// --- other runs (history.log) and changelogs (§4.5, §6.2) ---------------------------------------------------------------------------

/// <summary>Agent → PNMTD: runs outside PNMT, from <c>/var/log/apt/history.log</c>.</summary>
public sealed record OsUpdateHistoryMessage(IReadOnlyList<AptHistoryEntry> Entries);

/// <summary>
/// One history.log entry: its id (a hash of its start and command line, stable on the server), when, the command line, who asked
/// (<c>Requested-By</c>, sudo's user), what changed, and apt's error when it failed.
/// </summary>
public sealed record AptHistoryEntry(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Commandline,
    string? RequestedBy,
    IReadOnlyList<PackageChange> Upgraded,
    IReadOnlyList<PackageChange> Installed,
    IReadOnlyList<PackageChange> Removed,
    string? Error);

/// <summary>Agent → PNMTD: changelogs of pending source versions (§6.2).</summary>
public sealed record OsChangelogsMessage(IReadOnlyList<PackageChangelog> Changelogs);

/// <summary>
/// The part of a source package's changelog newer than the installed version: the CVEs it names, each upload's urgency, and the text
/// (at most <see cref="MaxTextChars"/>). <see cref="Distribution"/> as in the section: <c>debian:bookworm</c>, <c>ubuntu:24.04</c>.
/// </summary>
public sealed record PackageChangelog(string Distribution, string Source, string Version, string? InstalledVersion, IReadOnlyList<string> Cves,
    IReadOnlyList<string> Urgencies, string Text, string? From = null)
{
    public const int MaxTextChars = 16 * 1024;
}
