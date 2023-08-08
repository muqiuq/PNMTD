using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Models.Db;

namespace PNMTD.Lib.Models.Poco.Extensions
{
    public static class NotificationPocoExtensions
    {
        public static NotificationRulePoco ToPoco(this NotificationRuleEntity notificationEntity)
        {
            return new NotificationRulePoco
            {
                Id = notificationEntity.Id,
                Recipient = notificationEntity.Recipient,
                Enabled = notificationEntity.Enabled,
                Name = notificationEntity.Name,
                Type = notificationEntity.Type,
                SubscribedSensors = notificationEntity.SubscribedSensors.Select(s => s.Sensor.Id).ToList()
            };
        }

        public static NotificationRuleEntity ToEntity(this NotificationRulePoco notificationPoco, bool isNew)
        {
            var notificationRule = new NotificationRuleEntity()
            {
                Enabled = notificationPoco.Enabled,
                Recipient = notificationPoco.Recipient,
                Name = notificationPoco.Name,
                Type = notificationPoco.Type
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
