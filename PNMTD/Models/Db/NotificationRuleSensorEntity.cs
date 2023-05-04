using System.ComponentModel.DataAnnotations;

namespace PNMTD.Models.Db
{
    public class NotificationRuleSensorEntity
    {
        [Key]
        public Guid Id { get; set; }
        public virtual NotificationRuleEntity NotificationRule { get; set; }

        public virtual SensorEntity Sensor { get; set; }

    }
}
