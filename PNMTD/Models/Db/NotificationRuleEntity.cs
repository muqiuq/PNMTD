using PNMTD.Lib.Models.Enum;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    /// <summary>
    /// A notification rule: a name, when it fires, whether it is on, and whom it tells (docs/notification-recipients-plan.md §3.1):
    /// an address, a user, a group, or the responsible of the day with a stand-in. Its sensors come through links, scopes and exclusions.
    /// </summary>
    [Table("notificationrules")]
    [Audited]
    public class NotificationRuleEntity
    {
        [Key]
        public Guid Id { get; set; }

        public string Name { get; set; }

        /// <summary>The address, for <see cref="NotificationRecipientKind.Address"/> only; null for the other kinds.</summary>
        [AuditSecret]
        public string? Recipient { get; set; }

        public bool Enabled { get; set; }

        public NotificationRuleType Type { get; set; }

        public NotificationRecipientKind RecipientKind { get; set; } = NotificationRecipientKind.Address;

        /// <summary><see cref="NotificationRecipientKind.User"/>: the person. <see cref="NotificationRecipientKind.Responsible"/>: the stand-in user.</summary>
        public Guid? UserId { get; set; }

        public virtual UserEntity? User { get; set; }

        /// <summary><see cref="NotificationRecipientKind.Group"/>: the group. <see cref="NotificationRecipientKind.Responsible"/>: the stand-in group.</summary>
        public Guid? GroupId { get; set; }

        public virtual GroupEntity? Group { get; set; }

        /// <summary>For the person kinds: reach people at the e-mail address of their account.</summary>
        public bool ByEmail { get; set; } = true;

        /// <summary>… and at their Pushover key.</summary>
        public bool ByPushover { get; set; } = true;

        /// <summary>For <see cref="NotificationRecipientKind.Responsible"/>: whether the plan is asked at any hour or in working hours only.</summary>
        public NotificationHours Hours { get; set; } = NotificationHours.Always;

        /// <summary>When the rule last delivered anything.</summary>
        [AuditIgnore]
        public DateTime? LastSentAt { get; set; }

        /// <summary>Whom, as code wrote it: "Anna Muster (responsible): Pushover, e-mail"; for an address, the provider's name.</summary>
        [MaxLength(500)]
        [AuditIgnore]
        public string? LastSentTo { get; set; }

        public virtual List<NotificationRuleSensorEntity> SubscribedSensors { get; set; } = new List<NotificationRuleSensorEntity>();

        /// <summary>Lasting assignments to many sensors (docs/ai-operations-plan.md §11.2).</summary>
        public virtual List<NotificationRuleScopeEntity> Scopes { get; set; } = new List<NotificationRuleScopeEntity>();

        public virtual List<NotificationRuleExclusionEntity> Exclusions { get; set; } = new List<NotificationRuleExclusionEntity>();
    }
}
