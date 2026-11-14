using System.Text.Json.Serialization;
using PNMTD.Lib.Models.Enum;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// <c>GET /system/info</c>. Signed out, only <see cref="Identity"/> (for the login screen); signed in,
/// also the version, the public address, the time zone of per-day values, the 2FA policy, the
/// caller's role, and whether the WireGuard gateway is on (docs/mikrotik-plan.md §3.1).
/// </summary>
public sealed record SystemInfo(
    string Identity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicBaseUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeZone = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TotpPolicy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? WireGuard = null);

/// <summary>
/// <c>GET /system/uplink</c>: UplinkCheckTask's view of the internet connection. Without the
/// UplinkCheck settings it never runs, and the uplink counts as up.
/// </summary>
public sealed record UplinkStatus(bool Configured, bool IsUp, DateTime? LastRun, DateTime? LastSuccessfulRun);

/// <summary>
/// A background task's last run against its interval. State: <c>ok</c>, <c>late</c> (no run for
/// three intervals and a minute), <c>notRun</c> (never ran) or <c>off</c> (not configured, or
/// background tasks disabled).
/// </summary>
public sealed record TaskStatus(string Name, string Key, int IntervalSeconds, DateTime? LastRun, string State);

/// <summary>A status code as the docs and the UI's code labels show it. Success: 399 or below.</summary>
public sealed record StatusCodeInfo(int Code, string Name, bool Success, string Description);

public sealed record SensorTypeInfo(SensorType Type, string Description, string? Parameters, bool CanCreate, bool UsesInterval);

public sealed record NotificationRuleTypeInfo(NotificationRuleType Type, string Description);
