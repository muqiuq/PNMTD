using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Models.Db;

namespace PNMTD.Models.Poco.Extensions
{
    public static class NotificationPocoExtensions
    {
        public static NotificationPoco ToPoco(this NotificationEntity notificationEntity)
        {
            return new NotificationPoco
            {
                Id = notificationEntity.Id,
                Recipient = notificationEntity.Recipient,
                Enabled = notificationEntity.Enabled,
                SubscribedHosts = notificationEntity.SubscribedHosts.Select(s => s.Id).ToList(),
                SubscribedSensors = notificationEntity.SubscribedSensors.Select(s => s.Id).ToList()
            };
        }

        public static NotificationEntity ToEntity(this NotificationPoco notificationPoco, PnmtdDbContext dbContext)
        {
            var hosts = dbContext.Hosts.Where(x => notificationPoco.SubscribedHosts.Contains(x.Id)).ToList();
            if(hosts.Count() != notificationPoco.SubscribedHosts.Count)
            {
                throw new ArgumentException("invalid host GUID");
            }
            var sensors = dbContext.Sensors.Where(x => notificationPoco.SubscribedSensors.Contains(x.Id)).ToList();
            return new NotificationEntity()
            {
                Id = notificationPoco.Id,
                Enabled = notificationPoco.Enabled,
                Recipient = notificationPoco.Recipient,
                SubscribedHosts = hosts,
                SubscribedSensors = sensors
            };
        }

    }
}
