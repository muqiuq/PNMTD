using PNMTD.Models.Db;

namespace PNMTD.Models.Helper
{
    public class PendingNotification
    {
        public NotificationRuleEntity NotitificationRule { get; set; }

        public EventEntity EventEntity { get; set; }

    }
}
