using Microsoft.Extensions.Hosting;
using PNMTD.Data;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests.Services
{
    [TestClass]
    public class TimespanConditionCheckerTest
    {
        private HttpClient _client;

        public static readonly CustomWebApplicationFactory<Program> _factory = new CustomWebApplicationFactory<Program>();

        [TestInitialize]
        public void Init()
        {
            _client = _factory.CreateClient();
        }

        private PnmtdDbContext Db
        {
            get
            {
                return _factory.DbTestHelper.DbContext;
            }
        }

        public const string ONE_WITHIN_TIMESPAN_MARKER = "One within timespan 1";

        public const string ALL_WITHIN_TIMESPAN_MARKER = "All within timespan 1";
        private (SensorEntity, EventEntity) PopulateDB(HostEntity host, string marker, 
            SensorType sensorType, int numOfSuccess = 1, int numOfFailures = 2)
        {
            var sensor = new SensorEntity()
            {
                Created = DateTime.Now,
                Enabled = true,
                GracePeriod = 0,
                Interval = 0,
                Id = Guid.NewGuid(),
                Ignore = false,
                Name = marker,
                OlderSiblingId = null,
                Parameters = "daily",
                ParentId = host.Id,
                Parent = host,
                Source = "",
                TextId = "",
                Type = sensorType
            };
            sensor.SetNewSecretToken();
            Db.Sensors.Add(sensor);
            Db.SaveChanges();
            EventEntity lastSuccessEvent = null;
            for(int a = 1; a <= numOfSuccess; a++)
            {
                var successfullEvent = new EventEntity()
                {
                    Created = DateTime.Now.AddMinutes(-30),
                    Code = 200,
                    Id = Guid.NewGuid(),
                    Message = "OK",
                    Sensor = sensor,
                    SensorId = sensor.Id,
                    Source = ""
                };
                Db.Events.Add(successfullEvent);
                lastSuccessEvent = successfullEvent;
            }
            
            for (int a = 1; a <= numOfFailures; a++)
            {
                var failedEvent = new EventEntity()
                {
                    Created = DateTime.Now.AddMinutes(-35 * a),
                    Code = 500,
                    Id = Guid.NewGuid(),
                    Message = "FAIL",
                    Sensor = sensor,
                    SensorId = sensor.Id,
                    Source = ""
                };
                Db.Events.Add(failedEvent);
            }
            Db.SaveChanges();
            return (sensor, lastSuccessEvent);
        }

        [TestMethod]
        public void T01_ONE_WITHIN_TIMESPAN()
        {
            var host = Db.Hosts.First();
            var tuple = PopulateDB(host, ONE_WITHIN_TIMESPAN_MARKER, SensorType.ONE_WITHIN_TIMESPAN);
            var successfullEvent = tuple.Item2;
            var sensor = tuple.Item1;
            var shouldBeSuccessfullEvent = Db.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).First();
            Assert.AreEqual(successfullEvent.Id, shouldBeSuccessfullEvent.Id);
            var now = DateTime.Now;
            now = now.AddMinutes(60 - now.Minute);
            now = now.AddHours(24 - now.Hour);
            TimespanConditionsCheckTask.ProcessSensor(null, Db, sensor, 
                PNMTStatusCodes.ONE_WITHIN_TIMESPAN_OK, PNMTStatusCodes.ONE_WITHIN_TIMESPAN_FAILED,
                now: now);
            Db.SaveChanges();
            var shouldBeOkEvent = Db.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).First();
            Assert.AreNotEqual(successfullEvent.Id, shouldBeOkEvent.Id);
            Assert.AreEqual(PNMTStatusCodes.ONE_WITHIN_TIMESPAN_OK, shouldBeOkEvent.Code);
        }

        [TestMethod]
        public void T02_ONE_WITHIN_TIMESPAN()
        {
            var host = Db.Hosts.Where(h => h.Sensors.Any(x => x.Name == ONE_WITHIN_TIMESPAN_MARKER)).FirstOrDefault();
            if(host == null)
            {
                host = Db.Hosts.First();
                PopulateDB(host, ONE_WITHIN_TIMESPAN_MARKER, SensorType.ONE_WITHIN_TIMESPAN);
            }

            var sensor = host.Sensors.Where(s => s.Name == ONE_WITHIN_TIMESPAN_MARKER).First();

            for (int a = 1; a <= 2; a++)
            {
                var failedEvent = new EventEntity()
                {
                    Created = DateTime.Now.AddDays(1).AddMinutes(30 * a),
                    Code = 501,
                    Id = Guid.NewGuid(),
                    Message = "FAIL",
                    Sensor = sensor,
                    SensorId = sensor.Id,
                    Source = ""
                };
                Db.Events.Add(failedEvent);
            }
            Db.SaveChanges();

            var shouldBeFailedEvent = Db.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).First();
            Assert.AreEqual(501, shouldBeFailedEvent.Code);
            var now = DateTime.Now;
            now = now.AddMinutes(60 - now.Minute);
            now = now.AddHours(24 - now.Hour);
            now = now.AddDays(1);
            TimespanConditionsCheckTask.ProcessSensor(null, Db, sensor,
                PNMTStatusCodes.ONE_WITHIN_TIMESPAN_OK, PNMTStatusCodes.ONE_WITHIN_TIMESPAN_FAILED,
                now: now);
            Db.SaveChanges();
            var shouldBeFailedEvent2 = Db.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).First();
            Assert.AreEqual(PNMTStatusCodes.ONE_WITHIN_TIMESPAN_FAILED, shouldBeFailedEvent2.Code);
        }

        [TestMethod]
        public void T03_ALL_WITHIN_TIMESPAN()
        {
            var host = Db.Hosts.First();
            var tuple = PopulateDB(host, ALL_WITHIN_TIMESPAN_MARKER, SensorType.ALL_WITHIN_TIMESPAN);
            var successfullEvent = tuple.Item2;
            var sensor = tuple.Item1;
            var shouldBeSuccessfullEvent = Db.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).First();
            Assert.AreEqual(successfullEvent.Id, shouldBeSuccessfullEvent.Id);
            var now = DateTime.Now;
            now = now.AddMinutes(60 - now.Minute);
            now = now.AddHours(24 - now.Hour);
            TimespanConditionsCheckTask.ProcessSensor(null, Db, sensor,
                PNMTStatusCodes.ALL_WITHIN_TIMESPAN_OK, PNMTStatusCodes.ALL_WITHIN_TIMESPAN_FAILED,
                now: now);
            Db.SaveChanges();
            var shouldBeOkEvent = Db.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).First();
            Assert.AreNotEqual(successfullEvent.Id, shouldBeOkEvent.Id);
            Assert.AreEqual(PNMTStatusCodes.ALL_WITHIN_TIMESPAN_FAILED, shouldBeOkEvent.Code);
        }

        [TestMethod]
        public void T04_ALL_WITHIN_TIMESPAN()
        {
            var host = Db.Hosts.Where(h => h.Sensors.Any(x => x.Name == ALL_WITHIN_TIMESPAN_MARKER)).FirstOrDefault();
            if (host == null)
            {
                host = Db.Hosts.First();
                PopulateDB(host, ALL_WITHIN_TIMESPAN_MARKER, SensorType.ALL_WITHIN_TIMESPAN);
            }

            var sensor = host.Sensors.Where(s => s.Name == ALL_WITHIN_TIMESPAN_MARKER).First();

            for (int a = 1; a <= 2; a++)
            {
                var successEvent = new EventEntity()
                {
                    Created = DateTime.Now.AddDays(1).AddMinutes(30 * a),
                    Code = 201,
                    Id = Guid.NewGuid(),
                    Message = "SUCCESS",
                    Sensor = sensor,
                    SensorId = sensor.Id,
                    Source = ""
                };
                Db.Events.Add(successEvent);
            }
            Db.SaveChanges();

            var now = DateTime.Now;
            now = now.AddMinutes(60 - now.Minute);
            now = now.AddHours(24 - now.Hour);
            now = now.AddDays(1);
            TimespanConditionsCheckTask.ProcessSensor(null, Db, sensor,
                PNMTStatusCodes.ALL_WITHIN_TIMESPAN_OK, PNMTStatusCodes.ALL_WITHIN_TIMESPAN_FAILED,
                now: now);
            Db.SaveChanges();
            var shouldBeOkEvent = Db.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).First();
            Assert.AreEqual(PNMTStatusCodes.ALL_WITHIN_TIMESPAN_OK, shouldBeOkEvent.Code);
        }

        [TestMethod]
        public void T05_NOT_YET()
        {
            var host = Db.Hosts.First();
            var tuple = PopulateDB(host, ALL_WITHIN_TIMESPAN_MARKER, SensorType.ALL_WITHIN_TIMESPAN, numOfSuccess: 1, numOfFailures: 0);
            var successfullEvent = tuple.Item2;
            var sensor = tuple.Item1;
            var shouldBeSuccessfullEvent = Db.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).First();
            Assert.AreEqual(successfullEvent.Id, shouldBeSuccessfullEvent.Id);
            var now = DateTime.Now;
            now = now.AddMinutes(60 - now.Minute);
            now = now.AddHours(24 - now.Hour);
            TimespanConditionsCheckTask.ProcessSensor(null, Db, sensor,
                PNMTStatusCodes.ALL_WITHIN_TIMESPAN_OK, PNMTStatusCodes.ALL_WITHIN_TIMESPAN_FAILED,
                now: now);
            Db.SaveChanges();
            var successEvent = new EventEntity()
            {
                Created = DateTime.Now.AddDays(1).AddMinutes(30),
                Code = 201,
                Id = Guid.NewGuid(),
                Message = "SUCCESS",
                Sensor = sensor,
                SensorId = sensor.Id,
                Source = ""
            };
            Db.Events.Add(successEvent);
            TimespanConditionsCheckTask.ProcessSensor(null, Db, sensor,
                PNMTStatusCodes.ALL_WITHIN_TIMESPAN_OK, PNMTStatusCodes.ALL_WITHIN_TIMESPAN_FAILED,
                now: now);
            Db.SaveChanges();
            var shouldBeOkEvent = Db.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).First();
            Assert.AreEqual(successEvent.Id, shouldBeOkEvent.Id);
        }

    }
}
