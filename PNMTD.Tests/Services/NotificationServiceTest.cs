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

namespace PNMTD.Tests.Services
{
    [TestClass]
    public class NotificationServiceTest
    {
        private HttpClient _client;

        public static readonly CustomWebApplicationFactory<Program> _factory = new CustomWebApplicationFactory<Program>();

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
        }

        [TestMethod]
        public void Get_All_Pending_Notifications()
        {
            var random = new Random();
            var allEvents = Db.Events.ToList();
            var randomEvent = allEvents[random.Next(allEvents.Count)];

            var pendingNotificationBefore = Db.GetAllPendingNotifications();

            var notificationPoco = new NotificationRulePoco()
            {
                Enabled = true,
                Recipient = "nst@nst.nst",
                SubscribedSensors = new List<Guid>() { randomEvent.Sensor.Id }
            };

            Db.CreateNewNotificationRule(notificationPoco);

            Db.SaveChanges();

            var pendingNotificationAfter = Db.GetAllPendingNotifications();

            Assert.IsTrue(pendingNotificationAfter.Count() != pendingNotificationBefore.Count());
            Assert.IsTrue(pendingNotificationAfter.Where(x => x.NotitificationRule.Recipient == notificationPoco.Recipient).Any());
        }

        [TestMethod]
        public void Is_NotificationEvent_Deleted()
        {
            var random = new Random();
            var allEvents = Db.Events.ToList();
            var randomEvent = allEvents[random.Next(allEvents.Count)];

            var notificationPoco = new NotificationRulePoco()
            {
                Enabled = true,
                Recipient = "nst@nst2.nst",
                SubscribedSensors = new List<Guid>() { randomEvent.Sensor.Id }
            };

            var notificationRule = Db.CreateNewNotificationRule(notificationPoco);

            Db.NotificationRuleEvents.Add(new Models.Db.NotificationRuleEventEntity()
            {
                Created = DateTime.Now,
                Event = randomEvent,
                Id = Guid.NewGuid(),
                NotificationRule = notificationRule
            });

            Db.SaveChanges();

            Assert.IsTrue(randomEvent.NotificationRuleEvents.Any(x => x.NotificationRule == notificationRule));

            Db.Remove(randomEvent);
            Db.SaveChanges();

            Assert.IsFalse(Db.NotificationRuleEvents.Any(nre => nre.NotificationRule == notificationRule));
        }

    }
}
