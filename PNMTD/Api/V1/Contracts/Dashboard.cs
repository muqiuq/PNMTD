using PNMTD.Lib.Models.Enum;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// <c>GET /dashboard</c>: everything the dashboard shows, in one response (§5.5). Computed in SQL and
/// cached for about 10 seconds; <see cref="GeneratedAt"/> says when.
/// </summary>
public sealed record Dashboard(
    DateTime GeneratedAt,
    string TimeZone,
    DashboardSummary Summary,
    IReadOnlyList<ProblemHost> Problems,
    IReadOnlyList<StateChange> RecentChanges,
    IReadOnlyList<DashboardHost> Hosts);

public sealed record DashboardSummary(
    HostCounts Hosts,
    SensorCounts Sensors,
    UplinkStatus Uplink,
    TaskSummary Tasks,
    DnsSummary Dns,
    MailSummary Mail);

public sealed record HostCounts(int Total, int Ok, int Failing, int NoData, int Disabled);

/// <summary>The background tasks against their intervals (details: <c>/system/tasks</c>, admins only).</summary>
public sealed record TaskSummary(int Ok, int Late, int NotRun, DateTime? LastRunAt);

public sealed record DnsSummary(int Zones, int Ok, int Mismatch, int Disabled);

/// <summary>The IMAP mailbox: whether it is configured, when it was last read, and mail no rule has processed yet.</summary>
public sealed record MailSummary(bool Configured, DateTime? LastCheckAt, int Unprocessed);

/// <summary>A host with failing sensors, longest-failing first.</summary>
public sealed record ProblemHost(Guid Id, string Name, IReadOnlyList<ProblemSensor> Sensors);

/// <summary>A failing sensor: the error, since when, and its last 24 hours (24 hourly values, oldest first).</summary>
public sealed record ProblemSensor(
    Guid Id,
    string Name,
    SensorType Type,
    int? Code,
    string? Message,
    DateTime? FailingSince,
    DateTime? LastEventAt,
    IReadOnlyList<double?> Last24Hours);

/// <summary>
/// A host row: failing hosts first, then by name; disabled hosts last. 90 days of uptime. <see cref="Location"/> is the
/// host's text; <see cref="AssignedLocation"/> the location it is assigned to (docs/locations-knowledge-plan.md §3.2).
/// </summary>
public sealed record DashboardHost(
    Guid Id,
    string Name,
    string? Location,
    bool Enabled,
    HostState State,
    int FailingCount,
    double? Uptime30Days,
    UptimeSeries Days,
    LocationRef? AssignedLocation = null);
