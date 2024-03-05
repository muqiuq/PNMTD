using Microsoft.EntityFrameworkCore;
using PNMTD.Lib.Models.Poco;
using PNMTD.Lib.Models.Poco.Extensions;
using PNMTD.Models.Db;
using PNMTD.Models.Helper;
using PNMTD.Models.Poco;
using PNMTD.Models.Poco.Extensions;

namespace PNMTD.Data
{
    public static class DbContextNotificationExtensions
    {

        public static NotificationRuleEntity CreateNewNotificationRule(this PnmtdDbContext db, NotificationRulePoco notificationPoco)
        {
            var notificationEntity = notificationPoco.ToEntity(isNew: true);
            db.NotificationRules.Add(notificationEntity);
            notificationPoco.SubscribedSensors.ForEach(sid =>
            {
                db.NotificationRuleSensor.Add(new NotificationRuleSensorEntity()
                {
                    Id = Guid.NewGuid(),
                    NotificationRule = notificationEntity,
                    Sensor = db.Sensors.Single(s => s.Id == sid)
                });
            });
            return notificationEntity;
        }

        public static IList<NotificationRuleEventEntity> CreateNotificationRuleEventEntitiesOfPendingNotifications(this PnmtdDbContext db
            , params PendingNotification[] pendingNotifications)
        {
            var notificationRuleEventEntities = new List<NotificationRuleEventEntity>();

            foreach(PendingNotification pendingNotification in pendingNotifications)
            {
                db.Attach(pendingNotification.EventEntity);
                db.Attach(pendingNotification.NotitificationRule);
                var notificationRuleEvent = db.NotificationRuleEvents.Add(new NotificationRuleEventEntity()
                {
                    Created = DateTime.Now,
                    Updated = DateTime.Now,
                    Id = Guid.NewGuid(),
                    NoAction = pendingNotification.NoAction,
                    Event = pendingNotification.EventEntity,
                    NotificationRule = pendingNotification.NotitificationRule,
                    LastCleanupEventCreated = pendingNotification.EventEntity.Created
                });

                notificationRuleEventEntities.Add(notificationRuleEvent.Entity);
            }

            return notificationRuleEventEntities;
        }

        public static int CleanNotificationRuleEventEntities(this PnmtdDbContext db)
        {
            var maxTimeSpanToKeepEntries = TimeSpan.FromHours(24);

            var oldEventEntities = db.NotificationRuleEvents
                .Include(n => n.Event)
                .Where(n => n.Event.Created == n.LastCleanupEventCreated)
                .ToList()
                .Where(nre => (DateTime.Now - nre.Updated) > maxTimeSpanToKeepEntries)
                .ToList();

            db.NotificationRuleEvents.RemoveRange(oldEventEntities);

            db.SaveChanges();

            return oldEventEntities.Count;
        }

        public static int UpdateLastCleanupEvents(this PnmtdDbContext db)
        {
            var oldEventEntities = db.NotificationRuleEvents
                .Include(n => n.Event)
                .Where(n => n.Event.Created != n.LastCleanupEventCreated)
                .ToList();

            oldEventEntities.ForEach(oE =>
            {
                oE.LastCleanupEventCreated = oE.Event.Created;
                oE.Updated = DateTime.Now;
            });

            db.SaveChanges();

            return oldEventEntities.Count;
        }

        public static IList<PendingNotification> GetAllPendingNotificationsForLastMinutes(this PnmtdDbContext db, int minutes = 30)
        {
            var eventIdsWithNotifications = db.NotificationRuleEvents
                .Select(nre => nre.Event.Id)
                .Distinct()
                .ToList();

            var maxTimeSpan = TimeSpan.FromMinutes(minutes);

            var pendingEvents = db.Events
                .Where(e => !eventIdsWithNotifications.Contains(e.Id))
                .Include(e => e.Sensor)
                .ThenInclude(es => es.Parent)
                .ToList()
                .Where(e => (DateTime.Now - e.Created) < maxTimeSpan)
                .ToList();

            var pendingNotifications = pendingEvents
                .SelectMany(pe => pe.Sensor.SubscribedByNotifications.Select(n => new PendingNotification()
                {
                    EventEntity = pe,
                    NotitificationRule = n.NotificationRule
                }))
                .ToList();

            return pendingNotifications;
        }
    }
}
