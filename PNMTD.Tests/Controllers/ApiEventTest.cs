using Newtonsoft.Json;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Requests;
using PNMTD.Models.Responses;
using System.Text;
using Microsoft.EntityFrameworkCore;
using JsonSerializer = System.Text.Json.JsonSerializer;

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
        public async Task T01_Api_GetEvent()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.First(s => s.Type == SensorType.HEARTBEAT);
            var testMessage = "TestMessage 1234, this is test {\":,\"}";
            var resp_hosts = await _client.GetAsync($"/event/{sensorEntity.SecretWriteToken}/200/{testMessage}");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            var paredResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            Assert.IsTrue(paredResponse.Success);
            var eventT = _factory.DbTestHelper.DbContext.Events.FirstOrDefault(e => e.Id == Guid.Parse(paredResponse.Data.ToString()));

            Assert.IsNotNull(eventT);
            Assert.AreEqual(numberOfEventsBefore + 1, _factory.DbTestHelper.DbContext.Events.Count());
            Assert.AreEqual(testMessage, eventT.Message);
            Assert.AreEqual(200, eventT.Code);
        }

        private async Task Api_PostEvent_Hearbeat(int code, bool willAddOneMore)
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.First(s => s.Type == SensorType.HEARTBEAT);
            var testMessage = "TestMessage 1234, this is test {\":,\"}";
            var content = new StringContent(testMessage, Encoding.UTF8, "text/plain");
            var resp_hosts = await _client.PostAsync($"/event/{sensorEntity.SecretWriteToken}/{code}", content);

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            var paredResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            Assert.IsTrue(paredResponse.Success);
            var eventT = _factory.DbTestHelper.DbContext.Events.FirstOrDefault(e => e.Id == Guid.Parse(paredResponse.Data.ToString()));

            Assert.IsNotNull(eventT);
            Assert.AreEqual(numberOfEventsBefore + (willAddOneMore ? 1 : 0), _factory.DbTestHelper.DbContext.Events.Count());
            Assert.AreEqual(testMessage, eventT.Message);
            Assert.AreEqual(code, eventT.Code);
        }

        [TestMethod]
        public async Task T02_Api_PostEvent_Heartbeat()
        {
            await Api_PostEvent_Hearbeat(210, willAddOneMore: true);
        }

        [TestMethod]
        public async Task T03_Api_PostEvent_Heartbeat_Duplicate()
        {
            await Api_PostEvent_Hearbeat(220, willAddOneMore: true);
            await Api_PostEvent_Hearbeat(220, willAddOneMore: true);
            await Api_PostEvent_Hearbeat(220, willAddOneMore: false);
        }

        [TestMethod]
        public async Task T04_Api_PostEvent_Encapsulated()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            var numberOfSensorEntitiesBefore = _factory.DbTestHelper.DbContext.Sensors.Count();

            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.First(s => s.Type == SensorType.ENCAPSULADED);
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
            var resp_hosts = await _client.PostAsync($"/event/{sensorEntity.SecretWriteToken}/200", content);

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            var paredResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            Assert.IsTrue(paredResponse.Success);
            var eventT = _factory.DbTestHelper.DbContext.Events.FirstOrDefault(e => e.Id == Guid.Parse(paredResponse.Data.ToString()));

            Assert.IsNotNull(eventT);
            Assert.AreEqual(numberOfEventsBefore + 2, _factory.DbTestHelper.DbContext.Events.Count());
            Assert.AreEqual(numberOfSensorEntitiesBefore + 2, _factory.DbTestHelper.DbContext.Sensors.Count());
            Assert.AreEqual(encapsulatedEvent2.Message, eventT.Message);
            Assert.AreEqual(encapsulatedEvent2.Code, eventT.Code);
        }

        [TestMethod]
        public async Task T05_Api_GetEvent_Base64()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.First();
            var testMessage = "{}K:,-()%*";
            var encodedMessage = "$" + Convert.ToBase64String(Encoding.UTF8.GetBytes(testMessage));
            var resp_hosts = await _client.GetAsync($"/event/{sensorEntity.SecretWriteToken}/200/{encodedMessage}");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            var paredResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            Assert.IsTrue(paredResponse.Success);
            var eventT = _factory.DbTestHelper.DbContext.Events.FirstOrDefault(e => e.Id == Guid.Parse(paredResponse.Data.ToString()));

            Assert.IsNotNull(eventT);
            Assert.AreEqual(numberOfEventsBefore + 1, _factory.DbTestHelper.DbContext.Events.Count());
            Assert.AreEqual(testMessage, eventT.Message);
            Assert.AreEqual(200, eventT.Code);
        }

        [TestMethod]
        public async Task T06_Api_GetEvent_InvalidSensorId()
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
        public async Task T07_Api_Test_Maximal_Number_of_Events()
        {
            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.First();
            GlobalConfiguration.MAXIMUM_NUM_OF_EVENTS_PER_SENSOR = 10;
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count(i => i.Sensor == sensorEntity);

            for (int a = 0; a < GlobalConfiguration.MAXIMUM_NUM_OF_EVENTS_PER_SENSOR + 5; a++)
            {
                await createRandomEventForSensorId(sensorEntity.SecretWriteToken);
            }
            //Do prevent two SaveChanges only one save changes occurs. It will always return one event more. Thats the +1
            Assert.AreEqual(GlobalConfiguration.MAXIMUM_NUM_OF_EVENTS_PER_SENSOR + 1,
                _factory.DbTestHelper.DbContext.Events.Count(i => i.Sensor == sensorEntity));
        }

        [TestMethod]
        public async Task T08_Api_GetEvent_Valuecheck_OK()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            
            var sensorEntity = _factory.DbTestHelper.DbContext.Sensors.First(s => s.Type == SensorType.VALUECHECK);
            sensorEntity.Parameters = "NEEDLE";
            _factory.DbTestHelper.DbContext.SaveChanges();
            var testMessage = "TestMessage 1234, this is test with a NEEDLE";
            var content = new StringContent(testMessage, Encoding.UTF8, "text/plain");
            var resp_hosts = await _client.PostAsync($"/event/{sensorEntity.SecretWriteToken}/0", content);

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            var paredResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            Assert.IsTrue(paredResponse.Success);
            var eventT = _factory.DbTestHelper.DbContext.Events.FirstOrDefault(e => e.Id == Guid.Parse(paredResponse.Data.ToString()));

            Assert.IsNotNull(eventT);
            Assert.AreEqual(PNMTStatusCodes.VALUECHECK_OK, eventT.Code);
            Assert.AreEqual(testMessage, eventT.Message);
            Assert.AreEqual(PNMTStatusCodes.VALUECHECK_OK, eventT.Code);
        }

        [TestMethod]
        public async Task T09_Api_GetLastErrorEvents()
        {
            var response = (await _client.GetAsync($"/events/lasterrors"));
            var rawContent = await response.Content.ReadAsStringAsync();
            var parsedResponse = JsonConvert.DeserializeObject<List<EventEntityPoco>>(rawContent);

            Assert.IsTrue(parsedResponse.All(e => !e.IsSuccess));
        }

        [TestMethod]
        public async Task T10_Api_GetEvent_Valuecheck_FAIL()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();

            var sensorEntity = _factory.DbTestHelper.DbContext.Sensors.First(s => s.Type == SensorType.VALUECHECK);
            sensorEntity.Parameters = "NEEDLE";
            _factory.DbTestHelper.DbContext.SaveChanges();
            var testMessage = "TestMessage 1234, this is test with a";
            var content = new StringContent(testMessage, Encoding.UTF8, "text/plain");
            var resp_hosts = await _client.PostAsync($"/event/{sensorEntity.SecretWriteToken}/0", content);

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            var paredResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            Assert.IsTrue(paredResponse.Success);
            var eventT = _factory.DbTestHelper.DbContext.Events.FirstOrDefault(e => e.Id == Guid.Parse(paredResponse.Data.ToString()));

            Assert.IsNotNull(eventT);
            Assert.AreEqual(PNMTStatusCodes.VALUECHECK_FAILED, eventT.Code);
            Assert.AreEqual(testMessage, eventT.Message);
            Assert.AreEqual(PNMTStatusCodes.VALUECHECK_FAILED, eventT.Code);
        }


        [TestMethod]
        public async Task T11_Api_ReadEvent_Raw()
        {
            var someEvent = _factory.DbTestHelper.DbContext.Events
                .Include(s => s.Sensor)
                .Where(s => s.Sensor.Type != SensorType.ONE_WITHIN_TIMESPAN && s.Sensor.Type != SensorType.ALL_WITHIN_TIMESPAN)
                .OrderByDescending(d => d.Created)
                .First();

            var sensorEntity = someEvent.Sensor;

            var resp_entity = await _client.GetAsync($"/read/{sensorEntity.SecretReadToken}/raw");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_entity.StatusCode);
            var rawContent = await resp_entity.Content.ReadAsStringAsync();
            
            Assert.AreEqual(($"{someEvent.IsSuccess};{someEvent.Message}").Trim(), rawContent);
        }

        [TestMethod]
        public async Task T12_Api_ReadEvent_Json()
        {
            var someEvent = _factory.DbTestHelper.DbContext.Events
                .Include(s => s.Sensor)
                .Where(s => s.Sensor.Type != SensorType.ONE_WITHIN_TIMESPAN && s.Sensor.Type != SensorType.ALL_WITHIN_TIMESPAN)
                .OrderByDescending(d => d.Created)
                .First();

            var sensorEntity = someEvent.Sensor;

            var resp_entity = await _client.GetAsync($"/read/{sensorEntity.SecretReadToken}/json");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_entity.StatusCode);
            var rawContent = await resp_entity.Content.ReadAsStringAsync();

            var returnObj = new
            {
                lastEventSuccess = someEvent.IsSuccess,
                lastEventMessage = someEvent.Message,
            };

            Assert.AreEqual(JsonSerializer.Serialize(returnObj), rawContent);
        }

        private async Task createRandomEventForSensorId(string secretToken)
        {
            var random = new Random();
            var testMessage = random.Next(0, 10000).ToString();
            var resp_hosts = await _client.GetAsync($"/event/{secretToken}/200/{testMessage}");
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
        }

        [ClassCleanup]
        public static void Cleanup()
        {
            _factory.Dispose();
        }

    }
}
