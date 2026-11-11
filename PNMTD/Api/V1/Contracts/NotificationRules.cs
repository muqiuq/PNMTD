using PNMTD.Lib.Models.Enum;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// A notification rule. <see cref="Provider"/> is what the recipient matched: <c>email</c>,
/// <c>pushover</c> or <c>http</c>. For a Viewer, pushover and http recipients show their scheme and
/// host only: they can contain keys.
/// </summary>
public sealed record NotificationRuleItem(
    Guid Id,
    string Name,
    string Recipient,
    string Provider,
    NotificationRuleType Type,
    bool Enabled,
    int SensorCount);

public sealed record NotificationRuleDetail(
    Guid Id,
    string Name,
    string Recipient,
    string Provider,
    NotificationRuleType Type,
    bool Enabled,
    IReadOnlyList<LinkedSensor> Sensors);

public sealed record LinkedSensor(Guid SensorId, string SensorName, SensorType Type, Guid HostId, string HostName);

/// <summary>
/// POST and PUT <c>/notification-rules</c>. The recipient must match a provider: an e-mail address,
/// <c>pushover://&lt;user key&gt;</c> or an <c>http(s)://</c> webhook.
/// </summary>
public sealed class NotificationRuleRequest
{
    public string? Name { get; init; }

    public string? Recipient { get; init; }

    public NotificationRuleType Type { get; init; } = NotificationRuleType.ALWAYS;

    public bool Enabled { get; init; } = true;
}
