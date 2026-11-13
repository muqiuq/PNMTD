using PNMTD.Lib.Models.Enum;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("notificationrules")]
    public class NotificationRuleEntity
    {
        [Key]
        public Guid Id { get; set; }
        
        public string Name { get; set; }
        
        public string Recipient { get; set; }
        
        public bool Enabled { get; set; }
        
        public NotificationRuleType Type { get; set; }

        public virtual List<NotificationRuleSensorEntity> SubscribedSensors { get; set; } = new List<NotificationRuleSensorEntity>();

        /// <summary>Lasting assignments to many sensors (docs/ai-operations-plan.md §11.2).</summary>
        public virtual List<NotificationRuleScopeEntity> Scopes { get; set; } = new List<NotificationRuleScopeEntity>();

        public virtual List<NotificationRuleExclusionEntity> Exclusions { get; set; } = new List<NotificationRuleExclusionEntity>();
    }
}
