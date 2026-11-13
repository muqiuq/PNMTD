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

        /// <summary>The sensor's effective importance: Pushover's priority (docs/ai-operations-plan.md §11.5).</summary>
        public Lib.Models.Enum.Importance Importance { get; set; } = Lib.Models.Enum.Importance.Normal;

    }
}
