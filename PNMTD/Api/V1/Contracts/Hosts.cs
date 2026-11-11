namespace PNMTD.Api.V1.Contracts;

/// <summary>A row of <c>GET /hosts</c>, with the last 30 days.</summary>
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
    UptimeSeries Days);

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
    IReadOnlyList<SensorSummary> Sensors);

/// <summary>POST and PUT <c>/hosts</c>. Name: 2–64 characters.</summary>
public sealed class HostRequest
{
    public string? Name { get; init; }

    public string? Location { get; init; }

    public string? Notes { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary><c>GET /hosts</c>. Sort: name (default), state (failing first), uptime (lowest first), created, lastEvent.</summary>
public sealed class HostQuery : PageQuery
{
    public HostState? State { get; set; }
}
