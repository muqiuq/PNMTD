using PNMTD.Lib.Models.Enum;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// A row of <c>GET /hosts</c>, with the last 30 days. <see cref="Location"/> is the host's text (<i>Location text</i>);
/// <see cref="AssignedLocation"/> the location it is assigned to, with its path (docs/locations-knowledge-plan.md §3.2).
/// </summary>
public sealed record HostListItem(
    Guid Id,
    string Name,
    string? Location,
    bool Enabled,
    DateTime Created,
    HostState State,
    SensorCounts Sensors,
    double? Uptime30Days,
    DateTime? LastEventAt,
    UptimeSeries Days,
    Importance Importance = Importance.Normal,
    LocationRef? AssignedLocation = null);

/// <summary><c>GET /hosts/{id}</c>: the host, its top-level sensors (ENCAPSULADED children are on the parent) and 90 days.</summary>
public sealed record HostDetail(
    Guid Id,
    string Name,
    string? Location,
    string? Notes,
    bool Enabled,
    DateTime Created,
    HostState State,
    SensorCounts SensorCounts,
    double? Uptime24Hours,
    double? Uptime30Days,
    DateTime? LastEventAt,
    UptimeSeries Days,
    int EventCount,
    IReadOnlyList<SensorSummary> Sensors,
    Importance Importance = Importance.Normal,
    LocationRef? AssignedLocation = null);

/// <summary>
/// POST and PUT <c>/hosts</c>. Name: 2–64 characters. <see cref="Location"/> is the text (a rack, a room; at most 128
/// characters); <see cref="LocationId"/> the location the host is assigned to, at any level, or null (§3.2).
/// </summary>
public sealed class HostRequest
{
    public string? Name { get; init; }

    public string? Location { get; init; }

    public Guid? LocationId { get; init; }

    public string? Notes { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Low, Normal or High (docs/ai-operations-plan.md §11.1): its sensors' unless they have their own.</summary>
    public Importance Importance { get; init; } = Importance.Normal;
}

/// <summary><c>PUT /hosts/importance</c> and <c>PUT /sensors/importance</c> (§11.1): many at once. Null, for sensors: the host's.</summary>
public sealed class ImportanceRequest
{
    public IReadOnlyList<Guid> Ids { get; init; } = [];

    public Importance? Importance { get; init; }
}

/// <summary>
/// <c>GET /hosts</c>. Sort: name (default), state (failing first), uptime (lowest first), created, lastEvent. The search
/// matches the name, the location text and the assigned location's path.
/// </summary>
public sealed class HostQuery : PageQuery
{
    public HostState? State { get; set; }

    public Importance? Importance { get; set; }

    /// <summary>Hosts at this location or one of its sub-locations (§3.2).</summary>
    public Guid? LocationId { get; set; }

    /// <summary>True: only hosts without a location (the filter's <i>None</i>).</summary>
    public bool? NoLocation { get; set; }
}
