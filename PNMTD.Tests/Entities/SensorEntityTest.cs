using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Models.Db;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests.Entities
{
    [TestClass]
    public class SensorEntityTest
    {

        private HttpClient _client;

        public static readonly CustomWebApplicationFactory<Program> _factory = new CustomWebApplicationFactory<Program>();

        [TestInitialize]
        public void Init()
        {
            _client = _factory.CreateClient();
        }

        public PnmtdDbContext Db { get
            {
                return _factory.DbTestHelper.DbContext;
            } 
        }

        [TestMethod]
        public void CreateSensorEntitySibling()
        {
            var host = Db.Hosts.First();
            var olderSensor = new SensorEntity()
            {
                Id = Guid.NewGuid(),
                Created = DateTime.Now,
                Enabled = true,
                Name = $"Encapsulated",
                Parent = host,
                Type = SensorType.ENCAPSULADED,
                Interval = 60,
                GracePeriod = 60

            };
            Db.Sensors.Add(olderSensor);
            var siblingSensor = new SensorEntity()
            {
                Id = Guid.NewGuid(),
                Created = DateTime.Now,
                OlderSibling = olderSensor,
                Enabled = true,
                Name = $"Younger Sibling of {olderSensor.Name}",
                Parent = host,
                Type = SensorType.VALUECHECK,
                Interval = 60,
                GracePeriod = 60,
            };
            Db.Sensors.Add(siblingSensor);
            Db.SaveChanges();
            Assert.IsNotNull(siblingSensor.OlderSiblingId);
            Assert.AreEqual(siblingSensor.OlderSiblingId, siblingSensor.OlderSibling.Id);
        }
    }
}
