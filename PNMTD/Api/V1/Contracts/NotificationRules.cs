using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// A notification rule. For an address, <see cref="Provider"/> is what it matched: <c>email</c>, <c>pushover</c> or <c>http</c>,
/// and for a Viewer, pushover and http recipients show their scheme and host only: they can contain keys. For the other kinds
/// (docs/notification-recipients-plan.md §6.2), <see cref="Provider"/> is <c>user</c>, <c>group</c> or <c>responsible</c>, and
/// <see cref="Recipient"/> carries the label: the user's name, the group's name, or "The responsible of the day".
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
    string? Via = null,
    NotificationRecipientKind RecipientKind = NotificationRecipientKind.Address);

/// <summary>
/// A rule with everything (§6.2). <see cref="User"/> and <see cref="Group"/> are the person, the group, or the responsible's stand-in;
/// null when deleted. <see cref="Reach"/> says whom it reaches now; <see cref="LastSentTo"/> whom it last reached.
/// </summary>
public sealed record NotificationRuleDetail(
    Guid Id,
    string Name,
    string Recipient,
    string Provider,
    NotificationRuleType Type,
    bool Enabled,
    IReadOnlyList<LinkedSensor> Sensors,
    IReadOnlyList<NotificationScope>? Scopes = null,
    IReadOnlyList<LinkedSensor>? Exclusions = null,
    NotificationRecipientKind RecipientKind = NotificationRecipientKind.Address,
    Ref? User = null,
    Ref? Group = null,
    bool ByEmail = true,
    bool ByPushover = true,
    NotificationHours Hours = NotificationHours.Always,
    NotificationReach? Reach = null,
    DateTime? LastSentAt = null,
    string? LastSentTo = null);

/// <summary>Whom a rule reaches now (§6.2): how many people and messages, who, or why nobody.</summary>
public sealed record NotificationReach(int People, int Deliveries, string Who, string? Why);

/// <summary>What the rule editor chooses a user or a group from (§6.3): names and whether a user can be reached, never the details.</summary>
public sealed record NotificationPeople(IReadOnlyList<NotificationPerson> Users, IReadOnlyList<NotificationGroupRef> Groups);

public sealed record NotificationPerson(Guid Id, string Username, string DisplayName, bool IsActive, bool HasEmail, bool HasPushover);

public sealed record NotificationGroupRef(Guid Id, string Name, int Members);

/// <summary>An enabled rule that reaches nobody right now (§9), and why.</summary>
public sealed record NotificationRuleReach(Ref Rule, string Why);

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

/// <summary>
/// Sensors no enabled rule covers, by effective importance (§11.3); <c>Sensors</c> lists at most 500, High first. A rule that
/// reaches nobody right now (docs/notification-recipients-plan.md §9) covers nothing, and is listed in <c>RulesReachingNobody</c>.
/// </summary>
public sealed record NotificationCoverage(int Total, int Covered, int UncoveredHigh, int UncoveredNormal, int UncoveredLow,
    IReadOnlyList<NotificationMatrixRow> Sensors, IReadOnlyList<NotificationRuleReach> RulesReachingNobody);

public sealed record LinkedSensor(Guid SensorId, string SensorName, SensorType Type, Guid HostId, string HostName);

/// <summary>
/// The Pushover priorities (docs/cookie-object-notes-plan.md §5): what a Low, Normal and High message is on the phone (−2 lowest,
/// −1 low, 0 normal, 1 high, 2 emergency), and how an emergency repeats until it is acknowledged. Every Pushover PNMT sends
/// follows them: the rules', Cookie's, the access watch's, MCP's strike notices.
/// </summary>
public sealed record NotificationSettings(int PushoverLow, int PushoverNormal, int PushoverHigh, int EmergencyRetrySeconds, int EmergencyExpireSeconds,
    DateTime? ChangedAt, string? ChangedBy);

/// <summary>
/// PUT <c>/notifications/settings</c> (Monitoring Edit): each priority −2 to 2; the retry 30 to 3600 seconds, the expiry at
/// least the retry and at most 10800 seconds. A field left out takes the default of a new installation.
/// </summary>
public sealed class NotificationSettingsRequest
{
    public int PushoverLow { get; init; } = -1;

    public int PushoverNormal { get; init; }

    public int PushoverHigh { get; init; } = 1;

    public int EmergencyRetrySeconds { get; init; } = 60;

    public int EmergencyExpireSeconds { get; init; } = 3600;
}

/// <summary>
/// POST and PUT <c>/notification-rules</c>. For an address (the default kind) the recipient must match a provider: an e-mail
/// address, <c>pushover://&lt;user key&gt;</c> or an <c>http(s)://</c> webhook. For a user or a group, their id; for the responsible,
/// an optional stand-in (a user or a group, not both) and the hours (docs/notification-recipients-plan.md §6.1). The person kinds
/// need at least one channel. Fields of another kind are ignored.
/// </summary>
public sealed class NotificationRuleRequest
{
    public string? Name { get; init; }

    public string? Recipient { get; init; }

    public NotificationRuleType Type { get; init; } = NotificationRuleType.ALWAYS;

    public bool Enabled { get; init; } = true;

    public NotificationRecipientKind RecipientKind { get; init; } = NotificationRecipientKind.Address;

    public Guid? UserId { get; init; }

    public Guid? GroupId { get; init; }

    public bool ByEmail { get; init; } = true;

    public bool ByPushover { get; init; } = true;

    public NotificationHours Hours { get; init; } = NotificationHours.Always;
}
