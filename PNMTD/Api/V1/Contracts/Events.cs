namespace PNMTD.Api.V1.Contracts;

/// <summary>An event as the sensor API stored it. Success: the code is 399 or below.</summary>
public sealed record EventItem(
    Guid Id,
    DateTime Created,
    int Code,
    bool Success,
    string? Message,
    string? Source,
    Guid SensorId,
    string SensorName,
    Guid HostId,
    string HostName);

/// <summary><c>GET /events</c> and <c>/sensors/{id}/events</c>. Sort: created (default -created), code.</summary>
public sealed class EventQuery : PageQuery
{
    public Guid? HostId { get; set; }

    public Guid? SensorId { get; set; }

    /// <summary><c>ok</c> or <c>failed</c>.</summary>
    public string? Status { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

/// <summary><c>GET /events/state-changes</c>: default the last 24 hours, newest first.</summary>
public sealed class StateChangeQuery
{
    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public Guid? HostId { get; set; }

    public Guid? SensorId { get; set; }

    /// <summary>At most this many (default 50, at most 500).</summary>
    public int Limit { get; set; } = 50;
}

/// <summary>
/// A sensor turning from OK to failed or back. For a recovery, <see cref="FailingSince"/> is when the
/// failure began, and <see cref="DownSeconds"/> how long it lasted.
/// </summary>
public sealed record StateChange(
    DateTime At,
    Guid SensorId,
    string SensorName,
    Guid HostId,
    string HostName,
    int PreviousCode,
    int Code,
    bool Success,
    string? Message,
    DateTime? FailingSince,
    double? DownSeconds);
