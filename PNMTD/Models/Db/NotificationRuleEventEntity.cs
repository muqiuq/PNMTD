using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("notificationruleevent")]
    public class NotificationRuleEventEntity
    {
        [Key]
        public Guid Id { get; set; }

        public virtual NotificationRuleEntity NotificationRule { get; set; }

        public virtual EventEntity Event { get; set; }

        public DateTime Created { get; set; }

    }
}
