using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("notificationruleevent")]
    public class NotificationRuleEventEntity
    {
        [Key]
        public Guid Id { get; set; }

        /// <summary>
        /// If the NotificationRule did not trigger an notification action (was not executed) this is set to true
        /// </summary>
        public bool NoAction { get; set; }

        public virtual NotificationRuleEntity NotificationRule { get; set; }

        public virtual EventEntity Event { get; set; }

        public DateTime Created { get; set; }

    }
}
