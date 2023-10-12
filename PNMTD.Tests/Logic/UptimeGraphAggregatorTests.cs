using PNMTD.Data;
using PNMTD.Data.UptimeGraphs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests.Logic
{
    [TestClass]
    public class UptimeGraphAggregatorTests
    {
        private HttpClient _client;

        public static readonly CustomWebApplicationFactory<Program> _factory = new CustomWebApplicationFactory<Program>();

        [TestInitialize]
        public void Init()
        {
            _client = _factory.CreateClient();
        }

        public static PnmtdDbContext Db
        {
            get
            {
                return _factory.DbTestHelper.DbContext;
            }
        }

        [TestMethod]
        public async Task GetTimespansWithDayForPeriodTest()
        {
            var e = Db.Events.First();
            var sensor = Db.Sensors.Where(s => s.Id == e.SensorId).First();
            var timeSpans = sensor.GetTimespansWithDayForPeriod(Db);
            Assert.IsFalse(timeSpans.Any(s => s.From.Date != s.To.Date));
        }

        [TestMethod]
        public async Task GetTimespansWithDayForPeriodTestWithNoEventBefore()
        {
            var e = Db.Events.First();
            var sensor = Db.Sensors.Where(s => s.Id == e.SensorId).First();
            var timeSpans = sensor.GetTimespansWithDayForPeriod(Db, useNoEventBefore: true);
            Assert.IsFalse(timeSpans.Any(s => s.From.Date != s.To.Date));
        }

        [TestMethod]
        public async Task UptimeGraphAggregatorTest()
        {
            var e = Db.Events.First();
            var sensor = Db.Sensors.Where(s => s.Id == e.SensorId).First();
            var values = sensor.AggregateStatesForTimeperiodInDays(Db);
            Assert.IsFalse(values.Any(i => i.Uptime > 1 || (i.Uptime < 0 && i.Uptime != -1)));
        }

        [TestMethod]
        public async Task UptimeGraphAggregatorWithNoEventBefore()
        {
            var e = Db.Events.First();
            var sensor = Db.Sensors.Where(s => s.Id == e.SensorId).First();
            var values = sensor.AggregateStatesForTimeperiodInDays(Db, useNoEventBefore: true);
            Assert.IsFalse(values.Any(i => i.Uptime > 1 || (i.Uptime < 0 && i.Uptime != -1)));
        }
    }
}
