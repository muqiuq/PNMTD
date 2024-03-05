using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Poco;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Services
{
    [TestClass]
    public class NotificationServiceTest
    {
        private HttpClient _client;

        public static readonly CustomWebApplicationFactory<Program> _factory = new CustomWebApplicationFactory<Program>();
        private NotificationRuleEntity notificationRule;
        private EventEntity? eventEntity;
        private EventEntity? eventEntity2;
        private EventEntity? eventEntity3;
        private EventEntity? eventEntity4;

        public static PnmtdDbContext Db
        {
            get
            {
                return _factory.DbTestHelper.DbContext;
            }
        }

        [TestInitialize]
        public void Init()
        {
            _client = _factory.CreateClient();

            notificationRule = new NotificationRuleEntity()
            {
                Enabled = true,
                Id = Guid.NewGuid(),
                Name = "Unit",
                Recipient = "",
                Type = NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK
            };
            Db.NotificationRules.Add(notificationRule);
            Db.SaveChanges();
            eventEntity = Db.Events.Skip(0).FirstOrDefault();
            eventEntity2 = Db.Events.Skip(1).FirstOrDefault();
            eventEntity3 = Db.Events.Skip(2).FirstOrDefault();
            eventEntity4 = Db.Events.Skip(3).FirstOrDefault();
        }

        [TestMethod]
        public void T01_CleanNotificationRuleEventEntitiesTest()
        {

            var notificationRuleEntity1 = new NotificationRuleEventEntity()
            {
                Created = DateTime.Now,
                Updated = DateTime.Now,
                Event = eventEntity!,
                Id = Guid.NewGuid(),
                LastCleanupEventCreated = eventEntity.Created,
                NoAction = false,
                NotificationRule = notificationRule
            };
            Db.NotificationRuleEvents.Add(notificationRuleEntity1);
            Db.SaveChanges();

            var numbersOfNotifcationRuleEventites = Db.NotificationRuleEvents.Count();

            Db.CleanNotificationRuleEventEntities();

            Assert.AreEqual(numbersOfNotifcationRuleEventites, Db.NotificationRuleEvents.Count());

            
            var notificationRuleEntity2 = new NotificationRuleEventEntity()
            {
                Created = DateTime.Now,
                Updated = DateTime.Now.AddDays(-2),
                Event = eventEntity2!,
                Id = Guid.NewGuid(),
                LastCleanupEventCreated = eventEntity2.Created,
                NoAction = false,
                NotificationRule = notificationRule
            };
            Db.NotificationRuleEvents.Add(notificationRuleEntity2);
            Db.SaveChanges();

            var numbersOfNotifcationRuleEventites2 = Db.NotificationRuleEvents.Count();

            Db.CleanNotificationRuleEventEntities();

            Assert.AreEqual(numbersOfNotifcationRuleEventites2 - 1, Db.NotificationRuleEvents.Count());

            
            var notificationRuleEntity3 = new NotificationRuleEventEntity()
            {
                Created = DateTime.Now,
                Updated = DateTime.Now.AddDays(-2),
                Event = eventEntity3!,
                Id = Guid.NewGuid(),
                LastCleanupEventCreated = eventEntity3.Created,
                NoAction = false,
                NotificationRule = notificationRule
            };
            Db.NotificationRuleEvents.Add(notificationRuleEntity3);
            Db.SaveChanges();
            eventEntity3.Created = DateTime.Now.AddSeconds(1);
            Db.SaveChanges();

            var numbersOfNotifcationRuleEventites3 = Db.NotificationRuleEvents.Count();

            Db.CleanNotificationRuleEventEntities();

            Assert.AreEqual(numbersOfNotifcationRuleEventites3, Db.NotificationRuleEvents.Count());
        }

        [TestMethod]
        public void T02_UpdateLastCleanupEvents()
        {
            Db.UpdateLastCleanupEvents();
            Db.CleanNotificationRuleEventEntities();

            var notificationRuleEntity1 = new NotificationRuleEventEntity()
            {
                Created = DateTime.Now.AddDays(-2),
                Updated = DateTime.Now.AddDays(-2),
                Event = eventEntity4!,
                Id = Guid.NewGuid(),
                LastCleanupEventCreated = eventEntity4.Created,
                NoAction = false,
                NotificationRule = notificationRule
            };
            Db.NotificationRuleEvents.Add(notificationRuleEntity1);
            Db.SaveChanges();

            eventEntity4.Created = DateTime.Now.AddSeconds(1);
            Db.SaveChanges();

            var numbersOfNotifcationRuleEventitesBegin = Db.NotificationRuleEvents.Count();

            Db.CleanNotificationRuleEventEntities();

            Assert.AreEqual(numbersOfNotifcationRuleEventitesBegin, Db.NotificationRuleEvents.Count());

            Db.UpdateLastCleanupEvents();

            Assert.AreEqual(numbersOfNotifcationRuleEventitesBegin, Db.NotificationRuleEvents.Count());

            Db.CleanNotificationRuleEventEntities();

            Assert.AreEqual(numbersOfNotifcationRuleEventitesBegin, Db.NotificationRuleEvents.Count());

            notificationRuleEntity1.Updated = notificationRuleEntity1.Updated.AddDays(-2);
            Db.SaveChanges();

            Db.CleanNotificationRuleEventEntities();

            Assert.AreEqual(numbersOfNotifcationRuleEventitesBegin - 1, Db.NotificationRuleEvents.Count());
        }

        [TestMethod]
        public void T03_Get_All_Pending_Notifications()
        {
            var random = new Random();
            var allEvents = Db.Events.ToList();
            var randomEvent = allEvents[random.Next(allEvents.Count)];

            randomEvent.Created = DateTime.Now;

            Db.SaveChanges();

            var pendingNotificationBefore = Db.GetAllPendingNotificationsForLastMinutes();

            var notificationPoco = new NotificationRulePoco()
            {
                Enabled = true,
                Recipient = "nst@nst.nst",
                Name = "Test",
                SubscribedSensors = new List<Guid>() { randomEvent.Sensor.Id }
            };

            Db.CreateNewNotificationRule(notificationPoco);

            Db.SaveChanges();

            var pendingNotificationAfter = Db.GetAllPendingNotificationsForLastMinutes();

            Assert.IsTrue(pendingNotificationAfter.Count() != pendingNotificationBefore.Count());
            Assert.IsTrue(pendingNotificationAfter.Where(x => x.NotitificationRule.Recipient == notificationPoco.Recipient).Any());
        }

        [TestMethod]
        public void T04_Is_NotificationEvent_Deleted()
        {
            var random = new Random();
            var allEvents = Db.Events.ToList();
            var randomEvent = allEvents[random.Next(allEvents.Count)];

            var notificationPoco = new NotificationRulePoco()
            {
                Enabled = true,
                Recipient = "nst@nst2.nst",
                Name = "Test",
                SubscribedSensors = new List<Guid>() { randomEvent.Sensor.Id }
            };

            var notificationRule = Db.CreateNewNotificationRule(notificationPoco);

            Db.NotificationRuleEvents.Add(new Models.Db.NotificationRuleEventEntity()
            {
                Created = DateTime.Now,
                Event = randomEvent,
                Id = Guid.NewGuid(),
                NotificationRule = notificationRule,
                LastCleanupEventCreated = randomEvent.Created
            });

            Db.SaveChanges();

            Assert.IsTrue(randomEvent.NotificationRuleEvents.Any(x => x.NotificationRule == notificationRule));

            Db.Remove(randomEvent);
            Db.SaveChanges();

            Assert.IsFalse(Db.NotificationRuleEvents.Any(nre => nre.NotificationRule == notificationRule));
        }

    }
}
