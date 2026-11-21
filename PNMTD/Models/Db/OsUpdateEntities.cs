using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using PNMTD.Lib.Agent;

namespace PNMTD.Models.Db;

// OS updates on Linux servers (docs/linux-os-updates-plan.md §5.2, §6, §7; migration AddOsUpdates): the runs and their logs, the
// pending packages, the findings with their acknowledgements, and the advisory sources with what they hold.

/// <summary>
/// A run of apt on a server (§5.1): an update or an upgrade, PNMT's (requested, scheduled, on the server) or another one from
/// history.log (<see cref="OsUpdateTrigger.External"/>, no log). Deleted with its server; the newest 50 updates and 200 upgrades per
/// server are kept (§5.3).
/// </summary>
[Table("os_update_runs")]
[NotAudited("a run's results; starting it is an entry of its own")]
public class OsUpdateRunEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    public OsUpdateKind Kind { get; set; }

    public OsUpgradeMode? Mode { get; set; }

    public OsUpdateTrigger Trigger { get; set; }

    public Guid? RequestedById { get; set; }

    [MaxLength(200)]
    public string? RequestedByName { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public OsUpdateState State { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>What it did (<see cref="OsUpdateSummary"/>) as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string? Summary { get; set; }

    [MaxLength(2000)]
    public string? Message { get; set; }

    /// <summary>The command line as the agent ran it, or history.log's for another run.</summary>
    [MaxLength(1000)]
    public string? Command { get; set; }

    /// <summary>The bytes of the log PNMTD holds.</summary>
    public long LogBytes { get; set; }

    /// <summary>The log's size and SHA-256 as the agent reported them at the end.</summary>
    public long? ReportedLogBytes { get; set; }

    [MaxLength(64)]
    public string? LogSha256 { get; set; }

    /// <summary>Longer than <c>OsUpdates__MaxLogBytes</c>: the start and end are kept.</summary>
    public bool LogTruncated { get; set; }

    /// <summary>The agent couldn't send all of it (its copy was gone), or the hash didn't match.</summary>
    public bool LogIncomplete { get; set; }

    /// <summary>The last time the agent sent anything about it.</summary>
    public DateTime? LastHeardAt { get; set; }

    /// <summary>history.log's entry id, for another run (so it is stored once).</summary>
    [MaxLength(32)]
    public string? ExternalId { get; set; }

    /// <summary>A failed upgrade a person marked handled: its 467 ends (§5.4).</summary>
    public DateTime? DismissedAt { get; set; }

    [MaxLength(200)]
    public string? DismissedByName { get; set; }

    public bool Finished => State is not (OsUpdateState.Requested or OsUpdateState.Running);
}

/// <summary>A part of a run's log, in order of <see cref="Offset"/> (bytes of UTF-8); PostgreSQL compresses the text (TOAST).</summary>
[Table("os_update_log_chunks")]
[NotAudited("what an agent reported")]
public class OsUpdateLogChunkEntity
{
    public Guid RunId { get; set; }

    public long Offset { get; set; }

    public string Text { get; set; } = "";

    public int Bytes { get; set; }
}

/// <summary>A package pending on a server now (§4.1), from its os-updates section; <see cref="FirstSeenAt"/> survives each new section.</summary>
[Table("os_pending_packages")]
[NotAudited("what an agent reported")]
public class OsPendingPackageEntity
{
    public Guid SensorId { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(40)]
    public string Arch { get; set; } = "";

    [MaxLength(200)]
    public string? Installed { get; set; }

    [MaxLength(200)]
    public string Candidate { get; set; } = "";

    [MaxLength(200)]
    public string Source { get; set; } = "";

    [MaxLength(200)]
    public string SourceVersion { get; set; } = "";

    [MaxLength(200)]
    public string? InstalledSourceVersion { get; set; }

    [MaxLength(300)]
    public string? Origin { get; set; }

    public bool Security { get; set; }

    public bool InUpgrade { get; set; }

    public bool Phased { get; set; }

    public DateTime FirstSeenAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<OsFindingRating>))]
public enum OsFindingRating { Important, HighRisk }

/// <summary>
/// A finding (§6.4, §6.5): one CVE in one source package on one server, for as long as a pending update fixes it; a newer candidate
/// for the same CVE keeps it. Its reasons with their sources, and its acknowledgement. Resolved findings are kept a year (§5.3).
/// </summary>
[Table("os_update_findings")]
[NotAudited("findings; acknowledging one is an entry of its own")]
public class OsUpdateFindingEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    [MaxLength(200)]
    public string SourcePackage { get; set; } = "";

    [MaxLength(40)]
    public string Cve { get; set; } = "";

    /// <summary>The binaries that bring the fix, "openssl, libssl3".</summary>
    [MaxLength(1000)]
    public string Packages { get; set; } = "";

    [MaxLength(200)]
    public string? Installed { get; set; }

    /// <summary>The newest candidate that fixes it.</summary>
    [MaxLength(200)]
    public string Candidate { get; set; } = "";

    public OsFindingRating Rating { get; set; }

    /// <summary>The reasons (<c>OsFindingReason</c>) as JSON: kind, source, detail, link.</summary>
    [Column(TypeName = "jsonb")]
    public string Reasons { get; set; } = "[]";

    public DateTime FirstSeenAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    public Guid? AcknowledgedById { get; set; }

    [MaxLength(200)]
    public string? AcknowledgedByName { get; set; }

    [MaxLength(500)]
    public string? AcknowledgedReason { get; set; }

    public DateTime? AcknowledgedUntil { get; set; }

    /// <summary>The reasons' kinds when it was acknowledged; a new kind ends the acknowledgement (§6.5).</summary>
    public List<string> AcknowledgedKinds { get; set; } = [];

    /// <summary>When and why the last acknowledgement ended early or ran out ("now in CISA KEV", "it expired"); 466 says so for a day.</summary>
    public DateTime? AcknowledgementEndedAt { get; set; }

    [MaxLength(300)]
    public string? AcknowledgementEndedReason { get; set; }

    public bool IsAcknowledged(DateTime now) => AcknowledgedAt is not null && AcknowledgedUntil is { } until && until > now;
}

[JsonConverter(typeof(JsonStringEnumConverter<OsAdvisorySourceKind>))]
public enum OsAdvisorySourceKind { DebianTracker, UbuntuNotices, CisaKev, Epss, Rss }

/// <summary>
/// An upstream source of the high-risk check (§6.1): its kind (one of the parsers, which are code), name, URL and interval, and its state.
/// Created with the check's defaults; an Admin changes, adds and removes them, which is audited.
/// </summary>
[Table("os_advisory_sources")]
[Audited]
public class OsAdvisorySourceEntity
{
    [Key]
    public Guid Id { get; set; }

    public OsAdvisorySourceKind Kind { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(1000)]
    public string Url { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; }

    public DateTime CreatedAt { get; set; }

    [AuditIgnore]
    public DateTime? LastFetchAt { get; set; }

    [AuditIgnore]
    public DateTime? LastSuccessAt { get; set; }

    [AuditIgnore]
    [MaxLength(300)]
    public string? ETag { get; set; }

    [AuditIgnore]
    [MaxLength(100)]
    public string? LastModified { get; set; }

    [AuditIgnore]
    public int Entries { get; set; }

    [AuditIgnore]
    [MaxLength(1000)]
    public string? Error { get; set; }

    /// <summary>The Ubuntu notices' newest id per release, so later fetches stop where the last one did.</summary>
    [AuditIgnore]
    [Column(TypeName = "jsonb")]
    public string? Cursor { get; set; }
}

/// <summary>What a source says an update fixes (§6.3): in a distribution, a source package's CVE is fixed in this version.</summary>
[Table("os_fixes")]
[NotAudited("what an advisory source said")]
public class OsFixEntity
{
    public Guid SourceId { get; set; }

    /// <summary><c>debian:bookworm</c>, <c>ubuntu:24.04</c>.</summary>
    [MaxLength(60)]
    public string Distribution { get; set; } = "";

    [MaxLength(200)]
    public string SourcePackage { get; set; } = "";

    [MaxLength(40)]
    public string Cve { get; set; } = "";

    [MaxLength(200)]
    public string FixedVersion { get; set; } = "";

    /// <summary>The tracker's urgency (<c>high</c>, <c>not yet assigned</c>, …), or Ubuntu's notice id.</summary>
    [MaxLength(60)]
    public string? Urgency { get; set; }

    /// <summary>The tracker's scope: <c>local</c>, or null (not local).</summary>
    [MaxLength(20)]
    public string? Scope { get; set; }
}

/// <summary>How dangerous a CVE is (§6.1): in CISA KEV (since, ransomware), its EPSS score and percentile.</summary>
[Table("os_cve_risks")]
[NotAudited("what an advisory source said")]
public class OsCveRiskEntity
{
    [Key]
    [MaxLength(40)]
    public string Cve { get; set; } = "";

    public bool Kev { get; set; }

    public DateOnly? KevAddedAt { get; set; }

    public bool KevRansomware { get; set; }

    [MaxLength(300)]
    public string? KevName { get; set; }

    public double? Epss { get; set; }

    public double? EpssPercentile { get; set; }

    public DateOnly? EpssDate { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>A pending source version's changelog part (§6.2), from a server; one server's fetch serves the fleet.</summary>
[Table("os_package_changelogs")]
[NotAudited("what an agent reported")]
public class OsPackageChangelogEntity
{
    [MaxLength(60)]
    public string Distribution { get; set; } = "";

    [MaxLength(200)]
    public string SourcePackage { get; set; } = "";

    [MaxLength(200)]
    public string Version { get; set; } = "";

    [MaxLength(200)]
    public string? InstalledVersion { get; set; }

    public List<string> Cves { get; set; } = [];

    public List<string> Urgencies { get; set; } = [];

    public string Text { get; set; } = "";

    [MaxLength(40)]
    public string? FetchedFrom { get; set; }

    public DateTime ReceivedAt { get; set; }
}

/// <summary>
/// An advisory text from an RSS source (§7): stored once per source, guid and hash; the CVEs code found in it, its keywords, and the
/// AI's assessment once it ran (code, ai, verified, disputed, suspicious) with the claims and both models' answers.
/// </summary>
[Table("os_advisory_items")]
[NotAudited("what an advisory source said")]
public class OsAdvisoryItemEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }

    [MaxLength(500)]
    public string Guid { get; set; } = "";

    [MaxLength(64)]
    public string Hash { get; set; } = "";

    [MaxLength(500)]
    public string Title { get; set; } = "";

    [MaxLength(1000)]
    public string? Link { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime FirstSeenAt { get; set; }

    /// <summary>The cleaned text, as the AI would see it.</summary>
    public string Text { get; set; } = "";

    public List<string> Cves { get; set; } = [];

    public List<string> Keywords { get; set; } = [];

    /// <summary><c>code</c> (no AI), <c>pending</c>, <c>ai</c>, <c>verified</c>, <c>disputed</c>, <c>suspicious</c>, <c>failed</c>.</summary>
    [MaxLength(20)]
    public string AssessedBy { get; set; } = "code";

    /// <summary>The claims and the answers as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string? Assessment { get; set; }

    public DateTime? AssessedAt { get; set; }

    public Guid? AiWorkId { get; set; }
}
