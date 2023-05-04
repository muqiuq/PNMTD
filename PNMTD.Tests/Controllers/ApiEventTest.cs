using Newtonsoft.Json;
using PNMTD.Models.Poco;
using PNMTD.Models.Responses;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests.Controllers
{
    [TestClass]
    internal class ApiEventTest
    {
        private HttpClient _client;

        public static readonly CustomWebApplicationFactory<Program> _factory = new CustomWebApplicationFactory<Program>();

        [TestInitialize]
        public void Init()
        {
            _client = _factory.CreateClient();
        }

        [TestMethod]
        public async Task Api_GetEvent()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.First();
            var testMessage = "TestMessage 1234, this is test {\":,\"}";
            var resp_hosts = await _client.GetAsync($"/event/{sensorEntity.Id}/200/{testMessage}");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            var paredResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            Assert.IsTrue(paredResponse.Success);
            var eventT = _factory.DbTestHelper.DbContext.Events.Where(e => e.Id == Guid.Parse(paredResponse.Data.ToString())).FirstOrDefault();

            Assert.IsNotNull(eventT);
            Assert.AreEqual(numberOfEventsBefore + 1, _factory.DbTestHelper.DbContext.Events.Count());
            Assert.AreEqual(testMessage, eventT.Message);
            Assert.AreEqual(200, eventT.Code);
        }

        [TestMethod]
        public async Task Api_GetEvent_Base64()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.First();
            var testMessage = "{}K:,-()%*";
            var encodedMessage = "$" + Convert.ToBase64String(Encoding.UTF8.GetBytes(testMessage));
            var resp_hosts = await _client.GetAsync($"/event/{sensorEntity.Id}/200/{encodedMessage}");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            var paredResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            Assert.IsTrue(paredResponse.Success);
            var eventT = _factory.DbTestHelper.DbContext.Events.Where(e => e.Id == Guid.Parse(paredResponse.Data.ToString())).FirstOrDefault();

            Assert.IsNotNull(eventT);
            Assert.AreEqual(numberOfEventsBefore + 1, _factory.DbTestHelper.DbContext.Events.Count());
            Assert.AreEqual(testMessage, eventT.Message);
            Assert.AreEqual(200, eventT.Code);
        }

        [TestMethod]
        public async Task Api_GetEvent_InvalidSensorId()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var randomGuid = Guid.NewGuid();
            while (_factory.DbTestHelper.SensorEntities.Any(i => i.Id == randomGuid))
            {
                randomGuid = Guid.NewGuid();
            }
            var testMessage = "{}K:,-()%*";
            var encodedMessage = "$" + Convert.ToBase64String(Encoding.UTF8.GetBytes(testMessage));
            var resp_hosts = await _client.GetAsync($"/event/{randomGuid}/200/{encodedMessage}");

            Assert.AreEqual(System.Net.HttpStatusCode.InternalServerError, resp_hosts.StatusCode);

            Assert.AreEqual(numberOfEventsBefore, _factory.DbTestHelper.DbContext.Events.Count());
        }

        [TestMethod]
        public async Task Api_Test_Maximal_Number_of_Events()
        {
            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.First();
            GlobalConfiguration.MAXIMUM_NUM_OF_EVENTS_PER_SENSOR = 10;
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Where(i => i.Sensor == sensorEntity).Count();

            for (int a = 0; a < GlobalConfiguration.MAXIMUM_NUM_OF_EVENTS_PER_SENSOR + 5; a++)
            {
                await createRandomEventForSensorId(sensorEntity.Id);
            }
            //Do prevent two SaveChanges only one save changes occurs. It will always return one event more. Thats the +1
            Assert.AreEqual(GlobalConfiguration.MAXIMUM_NUM_OF_EVENTS_PER_SENSOR + 1,
                _factory.DbTestHelper.DbContext.Events.Where(i => i.Sensor == sensorEntity).Count());
        }

        private async Task createRandomEventForSensorId(Guid SensorId)
        {
            var random = new Random();
            var testMessage = random.Next(0, 10000).ToString();
            var resp_hosts = await _client.GetAsync($"/event/{SensorId}/200/{testMessage}");
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
        }

        [ClassCleanup]
        public static void Cleanup()
        {
            _factory.Dispose();
        }

    }
}
