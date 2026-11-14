using PNMTD.Lib.Models.Enum;

namespace PNMTD.Api.V1.Contracts;

/// <summary>A sensor with its current state, as lists and the host page show it.</summary>
public sealed record SensorSummary(
    Guid Id,
    string Name,
    SensorType Type,
    bool Enabled,
    bool Ignore,
    SensorState State,
    int? LastCode,
    string? LastMessage,
    DateTime? LastEventAt,
    DateTime? FailingSince,
    int ChildCount);

/// <summary>A row of <c>GET /sensors</c>.</summary>
public sealed record SensorListItem(
    Guid Id,
    Ref Host,
    Guid? ParentSensorId,
    string Name,
    SensorType Type,
    bool Enabled,
    bool Ignore,
    SensorState State,
    int? LastCode,
    string? LastMessage,
    DateTime? LastEventAt,
    DateTime? FailingSince,
    Importance? Importance = null,
    Importance EffectiveImportance = Importance.Normal);

/// <summary>
/// <c>GET /sensors/{id}</c>. <see cref="Integration"/> (tokens and URLs) is null for a Viewer.
/// <see cref="ParentSensorId"/> is the ENCAPSULADED sensor that created this one; <see cref="Children"/>
/// the sensors an ENCAPSULADED sensor created.
/// </summary>
public sealed record SensorDetail(
    Guid Id,
    Ref Host,
    Guid? ParentSensorId,
    string Name,
    SensorType Type,
    bool Enabled,
    bool Ignore,
    int Interval,
    int GracePeriod,
    string? Parameters,
    string? TextId,
    string? Status,
    DateTime Created,
    SensorState State,
    int? LastCode,
    string? LastMessage,
    DateTime? LastEventAt,
    DateTime? FailingSince,
    double? Uptime24Hours,
    double? Uptime30Days,
    UptimeSeries Days,
    IReadOnlyList<SensorSummary> Children,
    SensorIntegration? Integration,
    string? Notes = null,
    UdpNodeDetail? Udp = null,
    MikrotikRouterDetail? Mikrotik = null,
    Importance? Importance = null,
    Importance EffectiveImportance = Importance.Normal,
    ZabbixSensorDetail? Zabbix = null);

/// <summary>
/// How an application reports: <c>{WriteUrl}/{code}/{message}</c> (GET) or a POST of the message to
/// <c>{WriteUrl}/{code}</c>; <c>{ReadUrl}</c> returns the last state. The base is PublicBaseUrl,
/// or the address of the request.
/// </summary>
public sealed record SensorIntegration(string WriteToken, string? ReadToken, string WriteUrl, string? ReadUrl, UdpSecrets? Udp = null);

/// <summary>
/// POST and PUT <c>/sensors</c>. The type cannot be DNS (DnsCheckTask creates those), and a DNS
/// sensor takes only Enabled and Ignore. Parameters by type: PING the target, VALUECHECK and
/// HEARTBEAT_VALUECHECK a regular expression, ONE/ALL_WITHIN_TIMESPAN a descriptor ("daily 8").
/// </summary>
public sealed class SensorRequest
{
    public Guid HostId { get; init; }

    public SensorType Type { get; init; } = SensorType.SIMPLE;

    public string? Name { get; init; }

    public bool Enabled { get; init; } = true;

    public bool Ignore { get; init; }

    /// <summary>Seconds: the expected report interval (heartbeats) or the check interval (ping).</summary>
    public int Interval { get; init; }

    /// <summary>Seconds a heartbeat may be late before it counts as missing.</summary>
    public int GracePeriod { get; init; }

    public string? Parameters { get; init; }

    public string? TextId { get; init; }

    /// <summary>Free text for every type, at most 4000 characters (docs/udp-iot-plan.md §4.1).</summary>
    public string? Notes { get; init; }

    /// <summary>A UDP_NODE's protocol settings (docs/udp-iot-plan.md §5); ignored for other types.</summary>
    public UdpNodeRequest? Udp { get; init; }

    /// <summary>A MIKROTIK router's settings (docs/mikrotik-plan.md §10); ignored for other types.</summary>
    public MikrotikRouterRequest? Mikrotik { get; init; }

    /// <summary>Null: the host's (docs/ai-operations-plan.md §11.1).</summary>
    public Importance? Importance { get; init; }

    /// <summary>A ZABBIX sensor's Zabbix host and rules (docs/audit-redmine-zabbix-plan.md §7.7); ignored for other types.</summary>
    public ZabbixSensorRequest? Zabbix { get; init; }
}

/// <summary><c>POST /sensors/{id}/tokens/rotate</c>: which tokens get new values. The old URLs stop working at once.</summary>
public sealed class RotateTokensRequest
{
    public bool Write { get; init; } = true;

    public bool Read { get; init; } = true;
}

/// <summary><c>POST /sensors/lookup</c>: a write or read token, in the body so it stays out of access logs.</summary>
public sealed class TokenLookupRequest
{
    public string? Token { get; init; }
}

/// <summary>Which sensor a token belongs to, and whether it is its write or its read token.</summary>
public sealed record TokenLookupResult(Guid SensorId, string SensorName, Ref Host, string Matched);

/// <summary><c>GET /sensors</c>. Sort: name (default), host, state (failing first), type, lastEvent.</summary>
public sealed class SensorQuery : PageQuery
{
    public Guid? HostId { get; set; }

    public SensorType? Type { get; set; }

    public SensorState? State { get; set; }

    /// <summary>The effective importance: the sensor's own, or its host's.</summary>
    public Importance? Importance { get; set; }
}
