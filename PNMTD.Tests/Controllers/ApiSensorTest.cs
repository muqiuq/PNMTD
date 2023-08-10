using Newtonsoft.Json;
using PNMTD.Data;
using PNMTD.Models.Poco;
using PNMTD.Models.Responses;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Lib.Models.Enum;

namespace PNMTD.Tests.Controllers
{
    [TestClass]
    public class ApiSensorTest
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
        public async Task T01_AddNewSensor()
        {
            var num_of_sensor = Db.Sensors.Count();
            var host = Db.Hosts.First();
            var sensor = Db.Sensors.First();
            var sensorPoco = new SensorPoco()
            {
                Enabled = true,
                GracePeriod = 15,
                Interval = 10,
                Name = "SomeNewName",
                ParentId = host.Id,
            };

            var resp_sensors = await _client.PostAsync("/sensor", TestHelper.SerializeToHttpContent(sensorPoco));

            var rawContent = await resp_sensors.Content.ReadAsStringAsync();
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_sensors.StatusCode);
            var parsedResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            var sensors = Db.Sensors.Where(n => n.Id == Guid.Parse((string)parsedResponse.Data)).ToList();

            Assert.IsTrue(sensors.Count() == 1);
            Assert.AreEqual(num_of_sensor + 1, Db.Sensors.Count());
        }

        [TestMethod]
        public async Task T02_GetSensors()
        {
            var resp_sensors = await _client.GetAsync("/sensor");
            var num_of_sensor = Db.Sensors.Count();
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_sensors.StatusCode);
            var content = await resp_sensors.Content.ReadAsStringAsync();
            var sensors = JsonConvert.DeserializeObject<List<SensorPoco>>(content);

            Assert.AreEqual(num_of_sensor, sensors.Count);
            Assert.IsTrue(num_of_sensor >= DbTestHelper.NUMBER_OF_HOST_ENTITIES);
        }

        [TestMethod]
        public async Task T03_GetSensorsByType()
        {
            var resp_sensors = await _client.GetAsync($"/sensor/bytype/{SensorType.HEARTBEAT.ToString()}");
            var num_of_sensor = Db.Sensors.Where(s => s.Type == SensorType.HEARTBEAT).Count();
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_sensors.StatusCode);
            var content = await resp_sensors.Content.ReadAsStringAsync();
            var sensors = JsonConvert.DeserializeObject<List<SensorPoco>>(content);

            Assert.IsTrue(sensors.All(s => s.Type == SensorType.HEARTBEAT));
            Assert.AreEqual(num_of_sensor, sensors.Count);
        }

        [TestMethod]
        public async Task T04_GetSensorByHost()
        {
            var host = Db.Hosts.First();
            var resp_sensors = await _client.GetAsync($"/sensor/byhost/{host.Id}");
            var num_of_sensor = Db.Sensors.Where(s => s.ParentId == host.Id).Count();
            
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_sensors.StatusCode);
            var content = await resp_sensors.Content.ReadAsStringAsync();
            var sensors = JsonConvert.DeserializeObject<List<SensorPoco>>(content);

            Assert.IsTrue(sensors.All(s => s.ParentId == host.Id));
            Assert.AreEqual(num_of_sensor, sensors.Count);
        }

        [TestMethod]
        public async Task T05_UpdateSensor()
        {
            var sensorFromDB = Db.Sensors.First();
            var sensor = sensorFromDB.ToPoco();
            Db.ChangeTracker.Clear();
            var num_of_sensor = Db.Sensors.Count();
            var originalEnabled = sensor.Enabled;
            var originalName = sensor.Name;
            sensor.Name = sensor.Name + "2";
            sensor.Enabled = !sensor.Enabled;
            var resp_sensors = await _client.PutAsync("/sensor", TestHelper.SerializeToHttpContent(sensor));

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_sensors.StatusCode);
            var content = await resp_sensors.Content.ReadAsStringAsync();
            var defaultResponse = JsonConvert.DeserializeObject<DefaultResponse>(content);

            var sensorFromDb = Db.Sensors.Where(n => n.Id == sensor.Id).Single();

            Assert.IsTrue(defaultResponse.Success);
            Assert.AreEqual(num_of_sensor, Db.Sensors.Count());
            Assert.AreEqual(!originalEnabled, sensorFromDb.Enabled);
            Assert.AreEqual(originalName + "2", sensorFromDb.Name);
        }

        [TestMethod]
        public async Task T06_DeleteSensor()
        {
            var sensorFromDbBefore = Db.Sensors.First();
            var sensor = sensorFromDbBefore.ToPoco();
            Db.ChangeTracker.Clear();
            var num_of_sensors = Db.Sensors.Count();

            var resp_sensors = await _client.DeleteAsync($"/sensor/{sensor.Id}");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_sensors.StatusCode);
            var content = await resp_sensors.Content.ReadAsStringAsync();
            var defaultResponse = JsonConvert.DeserializeObject<DefaultResponse>(content);

            var sensorFromDb = Db.Sensors.Where(n => n.Id == sensor.Id).SingleOrDefault();

            Assert.IsTrue(defaultResponse.Success);
            Assert.AreEqual(num_of_sensors - 1, Db.Sensors.Count());
            Assert.IsNull(sensorFromDb);
        }
    }
}
