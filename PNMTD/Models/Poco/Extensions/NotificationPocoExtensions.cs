using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Models.Db;

namespace PNMTD.Lib.Models.Poco.Extensions
{
    public static class NotificationPocoExtensions
    {
        public static NotificationPoco ToPoco(this NotificationRuleEntity notificationEntity)
        {
            return new NotificationPoco
            {
                Id = notificationEntity.Id,
                Recipient = notificationEntity.Recipient,
                Enabled = notificationEntity.Enabled,
                SubscribedSensors = notificationEntity.SubscribedSensors.Select(s => s.Sensor.Id).ToList()
            };
        }

        public static NotificationRuleEntity ToEntity(this NotificationPoco notificationPoco, bool isNew)
        {
            var notificationRule = new NotificationRuleEntity()
            {
                Enabled = notificationPoco.Enabled,
                Recipient = notificationPoco.Recipient,
            };

            if(!isNew)
            {
                notificationRule.Id = notificationPoco.Id;
            }
            else
            {
                notificationRule.Id = Guid.NewGuid();
            }

            return notificationRule;
        }

    }
}
