﻿using PNMTD.Models.Db;
using PNMTD.Models.Helper;
using PNMTD.Models.Poco;
using PNMTD.Models.Poco.Extensions;

namespace PNMTD.Data
{
    public static class DbContextNotificationExtensions
    {

        public static NotificationRuleEntity CreateNewNotificationRule(this PnmtdDbContext db, NotificationPoco notificationPoco)
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
                    Created = DateTime.UtcNow,
                    Id = Guid.NewGuid(),
                    Event = pendingNotification.EventEntity,
                    NotificationRule = pendingNotification.NotitificationRule
                });

                notificationRuleEventEntities.Add(notificationRuleEvent.Entity);
            }

            return notificationRuleEventEntities;
        }

        public static IList<PendingNotification> GetAllPendingNotifications(this PnmtdDbContext db)
        {
            var eventIdsWithNotifications = db.NotificationRuleEvents
                .Select(nre => nre.Event.Id)
                .Distinct()
                .ToList();

            var pendingEvents = db.Events
                .Where(e => !eventIdsWithNotifications.Contains(e.Id))
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
