using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

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
    int SensorCount,
    int ScopeCount = 0,
    string? Via = null);

public sealed record NotificationRuleDetail(
    Guid Id,
    string Name,
    string Recipient,
    string Provider,
    NotificationRuleType Type,
    bool Enabled,
    IReadOnlyList<LinkedSensor> Sensors,
    IReadOnlyList<NotificationScope>? Scopes = null,
    IReadOnlyList<LinkedSensor>? Exclusions = null);

/// <summary>
/// A lasting assignment (docs/ai-operations-plan.md §11.2): every sensor of at least an importance, of a host, or
/// every sensor, today's and those added later. <c>Host</c> names the host of a Host scope.
/// </summary>
public sealed record NotificationScope(NotificationScopeKind Kind, Importance? Importance, Ref? Host);

/// <summary><c>PUT /notification-rules/{id}/scopes</c>: the whole list.</summary>
public sealed class NotificationScopeRequest
{
    public NotificationScopeKind Kind { get; init; }

    public Importance? Importance { get; init; }

    public Guid? HostId { get; init; }
}

/// <summary>What a list of scopes would cover now, and how many events a day those sensors wrote in the last 7 days.</summary>
public sealed record NotificationScopePreview(int Sensors, double EventsPerDay);

/// <summary>
/// The Notification Manager's matrix (§11.3): the enabled rules as columns, sensors as rows, grouped by host. A row's
/// cells name only the rules that cover it: <c>link</c>, <c>scope</c> (with which), or <c>excluded</c>.
/// </summary>
public sealed record NotificationMatrix(IReadOnlyList<NotificationRuleItem> Rules, IReadOnlyList<NotificationMatrixRow> Rows, int Page, int PageSize, int TotalCount);

public sealed record NotificationMatrixRow(
    Guid SensorId,
    string SensorName,
    SensorType Type,
    Ref Host,
    Importance? Importance,
    Importance EffectiveImportance,
    SensorState State,
    IReadOnlyList<NotificationMatrixCell> Cells);

/// <summary><c>Via</c>: link, scope or excluded. <c>Scope</c> says which scope covers it ("importance ≥ High").</summary>
public sealed record NotificationMatrixCell(Guid RuleId, string Via, string? Scope);

/// <summary><c>GET /notifications/matrix</c>. Search: sensor and host. <c>Uncovered</c>: sensors no rule covers.</summary>
public sealed class NotificationMatrixQuery : PageQuery
{
    public Guid? HostId { get; set; }

    public Importance? Importance { get; set; }

    public SensorType? Type { get; set; }

    public SensorState? State { get; set; }

    public bool? Uncovered { get; set; }
}

public enum AssignMode
{
    Add,
    Remove,
}

/// <summary>
/// <c>POST /notifications/assign</c>: rules to or from sensors. Adding links them (and lifts their exclusions); removing
/// unlinks them, and excludes them where a scope still covers them.
/// </summary>
public sealed class NotificationAssignRequest
{
    public IReadOnlyList<Guid> RuleIds { get; init; } = [];

    public IReadOnlyList<Guid> SensorIds { get; init; } = [];

    public AssignMode Mode { get; init; } = AssignMode.Add;
}

public sealed record NotificationAssignResult(int Linked, int Unlinked, int Excluded, int Included);

/// <summary>Sensors no enabled rule covers, by effective importance (§11.3); <c>Sensors</c> lists at most 500, High first.</summary>
public sealed record NotificationCoverage(int Total, int Covered, int UncoveredHigh, int UncoveredNormal, int UncoveredLow,
    IReadOnlyList<NotificationMatrixRow> Sensors);

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
