using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using PNMTD.Data;
using PNMTD.Models.Poco;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Models.Responses;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests.Controllers
{
    [TestClass]
    public class ApiNotificationTest
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
        public async Task T01_AddNewNotifications()
        {
            var num_of_notification = Db.Notifications.Count();
            var host = Db.Hosts.First();
            var notificationPoco = new NotificationPoco()
            {
                Enabled = true,
                Recipient = "test@test.com",
                SubscribedHosts = new List<Guid>() { host.Id },
                SubscribedSensors = new List<Guid>() { }
            };


            var resp_hosts = await _client.PostAsync("/notifications", TestHelper.SerializeToHttpContent(notificationPoco));
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            var parsedResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            var notifications = Db.Notifications.Where(n => n.Id == Guid.Parse((string)parsedResponse.Data)).ToList();

            Assert.IsTrue(notifications.Count() == 1);
            Assert.AreEqual(DbTestHelper.NUMBER_OF_HOST_ENTITIES, Db.Hosts.Count());
            Assert.AreEqual(num_of_notification + 1, Db.Notifications.Count());
        }

        [TestMethod]
        public async Task T02_GetNotifications()
        {
            var resp_hosts = await _client.GetAsync("/notifications");
            var num_of_notification = Db.Notifications.Count();
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var content = await resp_hosts.Content.ReadAsStringAsync();
            var notifictions = JsonConvert.DeserializeObject<List<NotificationPoco>>(content);

            Assert.AreEqual(num_of_notification, notifictions.Count);
            Assert.IsTrue(num_of_notification >= DbTestHelper.NUMBER_OF_NOTIFICATIONS);
        }

        [TestMethod]
        public async Task T03_UpdateNotifications()
        {
            var notificationFromDB = Db.Notifications.First();
            var notification = notificationFromDB.ToPoco();
            Db.ChangeTracker.Clear();
            var num_of_notification = Db.Notifications.Count();
            var originalEnabled = notification.Enabled;
            var originalRecipient = notification.Recipient;
            notification.Recipient = notification.Recipient + "2";
            notification.Enabled = !notification.Enabled;
            var resp_hosts = await _client.PutAsync("/notifications", TestHelper.SerializeToHttpContent(notification));

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var content = await resp_hosts.Content.ReadAsStringAsync();
            var defaultResponse = JsonConvert.DeserializeObject<DefaultResponse>(content);

            var notificationFromDb = Db.Notifications.Where(n => n.Id == notification.Id).Single();

            Assert.IsTrue(defaultResponse.Success);
            Assert.AreEqual(num_of_notification, Db.Notifications.Count());
            Assert.AreEqual(!originalEnabled, notificationFromDb.Enabled);
            Assert.AreEqual(originalRecipient + "2", notificationFromDb.Recipient);
        }

        [TestMethod]
        public async Task T04_DeleteNotifications()
        {
            var notificationFromDbBefore = Db.Notifications.First();
            var notification = notificationFromDbBefore.ToPoco();
            Db.ChangeTracker.Clear();
            var num_of_notification = Db.Notifications.Count();

            var resp_hosts = await _client.DeleteAsync($"/notifications/{notification.Id}");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var content = await resp_hosts.Content.ReadAsStringAsync();
            var defaultResponse = JsonConvert.DeserializeObject<DefaultResponse>(content);

            var notificationFromDb = Db.Notifications.Where(n => n.Id == notification.Id).SingleOrDefault();

            Assert.IsTrue(defaultResponse.Success);
            Assert.AreEqual(num_of_notification - 1, Db.Notifications.Count());
            Assert.IsNull(notificationFromDb);
        }

    }
}
