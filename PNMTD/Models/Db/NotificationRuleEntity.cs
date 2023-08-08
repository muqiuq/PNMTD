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
    }
}
