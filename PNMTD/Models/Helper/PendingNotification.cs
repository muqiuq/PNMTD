using PNMTD.Models.Db;

namespace PNMTD.Models.Helper
{
    public class PendingNotification
    {
        public NotificationRuleEntity NotitificationRule { get; set; }

        public EventEntity EventEntity { get; set; }

        /// <summary>
        /// If the NotificationRule did not trigger an notification action (was not executed) this is set to true
        /// </summary>
        public bool NoAction { get; set; }

    }
}
