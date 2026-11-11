using PNMTD.Lib.Helper;
using Microsoft.EntityFrameworkCore;
using PNMTD.Models.Db;
using PNMTD.Models.Helper;

namespace PNMTD.Data
{
    public static class DbContextNotificationExtensions
    {

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
                    Created = Clock.UtcNow,
                    Updated = Clock.UtcNow,
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
                .Where(nre => (Clock.UtcNow - nre.Updated) > maxTimeSpanToKeepEntries)
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
                oE.Updated = Clock.UtcNow;
            });

            db.SaveChanges();

            return oldEventEntities.Count;
        }

        public static IList<PendingNotification> GetAllPendingNotificationsForLastMinutes(this PnmtdDbContext db, int minutes = 30)
        {
            // The window in SQL: v0.12 loaded every event without a notification row into memory
            // and filtered by age afterwards, every 10 s.
            var cutoff = Clock.UtcNow - TimeSpan.FromMinutes(minutes);

            var pendingEvents = db.Events
                .Where(e => e.Created > cutoff && !db.NotificationRuleEvents.Any(nre => nre.Event.Id == e.Id))
                .Include(e => e.Sensor)
                    .ThenInclude(s => s.Parent)
                .Include(e => e.Sensor)
                    .ThenInclude(s => s.SubscribedByNotifications)
                        .ThenInclude(n => n.NotificationRule)
                .AsSplitQuery()
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
