using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Lib.Models.Enum;

namespace PNMTD.Models.Db;

/// <summary>What a scope covers (docs/ai-operations-plan.md §11.2): today's sensors and those added later.</summary>
public enum NotificationScopeKind : short
{
    /// <summary>Every sensor whose effective importance is at least <see cref="NotificationRuleScopeEntity.Importance"/>.</summary>
    Importance = 0,

    /// <summary>Every sensor of <see cref="NotificationRuleScopeEntity.HostId"/>.</summary>
    Host = 1,

    /// <summary>Every sensor.</summary>
    Everything = 2,
}

/// <summary>A lasting assignment of a rule to many sensors (§11.2), next to its links to single sensors.</summary>
[Table("notification_rule_scopes")]
public class NotificationRuleScopeEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid RuleId { get; set; }

    public virtual NotificationRuleEntity Rule { get; set; } = null!;

    public NotificationScopeKind Kind { get; set; }

    public Importance? Importance { get; set; }

    public Guid? HostId { get; set; }

    public virtual HostEntity? Host { get; set; }

    /// <summary>Whether the scope covers a sensor of <paramref name="hostId"/> with <paramref name="importance"/>.</summary>
    public bool Covers(Guid hostId, Importance importance) => Kind switch
    {
        NotificationScopeKind.Everything => true,
        NotificationScopeKind.Host => HostId == hostId,
        NotificationScopeKind.Importance => Importance is { } minimum && importance >= minimum,
        _ => false,
    };
}

/// <summary>A sensor a rule's scopes must not cover (§11.2): "everything on host X except its ping sensor". Links still apply.</summary>
[Table("notification_rule_exclusions")]
public class NotificationRuleExclusionEntity
{
    public Guid RuleId { get; set; }

    public Guid SensorId { get; set; }
}
