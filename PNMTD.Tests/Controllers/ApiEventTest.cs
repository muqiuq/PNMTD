using Newtonsoft.Json;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Poco;
using PNMTD.Models.Requests;
using PNMTD.Models.Responses;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests.Controllers
{
    [TestClass]
    public class ApiEventTest
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
            var sensorEntity = hostEntity.Sensors.Where(s => s.Type == SensorType.HEARTBEAT).First();
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
        public async Task Api_PostEvent_Heartbeat()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.Where(s => s.Type == SensorType.HEARTBEAT).First();
            var testMessage = "TestMessage 1234, this is test {\":,\"}";
            var content = new StringContent(testMessage, Encoding.UTF8, "text/plain");
            var resp_hosts = await _client.PostAsync($"/event/{sensorEntity.Id}/200", content);

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
        public async Task Api_PostEvent_Encapsulated()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            var numberOfSensorEntitiesBefore = _factory.DbTestHelper.DbContext.Sensors.Count();

            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.Where(s => s.Type == SensorType.ENCAPSULADED).First();
            var encapsulatedEvent1 = new EncapsulatedEvent()
            {
                Name = "Number1",
                Code = 200,
                Message = "OK, 2ms"
            };
            var encapsulatedEvent2 = new EncapsulatedEvent()
            {
                Name = "Number2",
                Code = 300,
                Message = "No route to host"
            };
            var encapsulatedEvents = new List<EncapsulatedEvent>()
            {
                encapsulatedEvent1, encapsulatedEvent2
            };
            var testMessage = JsonConvert.SerializeObject(encapsulatedEvents, Formatting.Indented);
            var content = new StringContent(testMessage, Encoding.UTF8, "text/plain");
            var resp_hosts = await _client.PostAsync($"/event/{sensorEntity.Id}/200", content);

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            var paredResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            Assert.IsTrue(paredResponse.Success);
            var eventT = _factory.DbTestHelper.DbContext.Events.Where(e => e.Id == Guid.Parse(paredResponse.Data.ToString())).FirstOrDefault();

            Assert.IsNotNull(eventT);
            Assert.AreEqual(numberOfEventsBefore + 2, _factory.DbTestHelper.DbContext.Events.Count());
            Assert.AreEqual(numberOfSensorEntitiesBefore + 2, _factory.DbTestHelper.DbContext.Sensors.Count());
            Assert.AreEqual(encapsulatedEvent2.Message, eventT.Message);
            Assert.AreEqual(encapsulatedEvent2.Code, eventT.Code);
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

            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, resp_hosts.StatusCode);

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
