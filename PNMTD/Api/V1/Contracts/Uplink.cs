using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// The uplink page (docs/ai-operations-plan.md §7.3): the state, the configuration, each endpoint's statistics, and
/// the last 24 hours (48 half hours) and 7 days (7×24 hours) as buckets. <c>Address</c> is the whole URL for an Admin,
/// the host for the others. <c>PausedTasks</c>: what pauses while the uplink is down.
/// </summary>
public sealed record UplinkOverview(
    bool Configured,
    bool Up,
    int FailuresInARow,
    int FailuresForDown,
    DateTime? DownSince,
    DateTime? LastCheck,
    DateTime? LastSuccess,
    DateTime? NextCheck,
    int IntervalSeconds,
    int TimeoutSeconds,
    bool SharedKeySet,
    IReadOnlyList<UplinkEndpointStats> Endpoints,
    IReadOnlyList<string> PausedTasks,
    int RecoveryWaitMinutes,
    Guid? SensorId,
    IReadOnlyList<UplinkBucket> Day,
    IReadOnlyList<UplinkBucket> Week);

public sealed record UplinkEndpointStats(
    short Number,
    string Address,
    UplinkOutcome? LastOutcome,
    DateTime? LastAt,
    int? LastLatencyMs,
    double? SuccessRate24h,
    double? SuccessRate7d,
    int? MedianLatency24h,
    int? P95Latency24h,
    int Checks24h);

/// <summary>Checks in a period: how many succeeded and failed, and the successes' median latency.</summary>
public sealed record UplinkBucket(DateTime Start, int Ok, int Failed, int? MedianLatencyMs);

public sealed record UplinkCheckItem(long Id, DateTime At, short Endpoint, string Host, UplinkOutcome Outcome, int? HttpStatus, int? LatencyMs, string? Error);

/// <summary>From the first failure of a run that brought the uplink down, to the next success (open while it lasts).</summary>
public sealed record UplinkOutage(DateTime Start, DateTime? End, int DurationSeconds, string? FirstError, IReadOnlyList<short> Endpoints);

/// <summary><c>GET /uplink/checks</c>: newest first.</summary>
public sealed class UplinkCheckQuery : PageQuery
{
    public bool? FailuresOnly { get; set; }

    public short? Endpoint { get; set; }
}
