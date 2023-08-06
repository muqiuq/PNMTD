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
using PNMTD.Lib.Models.Poco;

namespace PNMTD.Tests.Controllers
{
    [TestClass]
    public class ApiHostTest
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
        public async Task T01_AddNewHost()
        {
            var num_of_host = Db.Hosts.Count();
            var host = Db.Hosts.First();
            var hostPoco = new HostPoco()
            {
                Enabled = true,
                Location = "TestLocation",
                Name = "UnitTestHost",
                Notes = "Notes"
            };

            var resp_hosts = await _client.PostAsync("/host", TestHelper.SerializeToHttpContent(hostPoco));
            
            var rawContent = await resp_hosts.Content.ReadAsStringAsync();
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var parsedResponse = JsonConvert.DeserializeObject<DefaultResponse>(rawContent);

            var hosts = Db.Hosts.Where(n => n.Id == Guid.Parse((string)parsedResponse.Data)).ToList();

            Assert.IsTrue(hosts.Count() == 1);
            Assert.AreEqual(num_of_host + 1, Db.Hosts.Count());
        }

        [TestMethod]
        public async Task T02_GetHosts()
        {
            var resp_hosts = await _client.GetAsync("/host");
            var num_of_host = Db.Hosts.Count();
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var content = await resp_hosts.Content.ReadAsStringAsync();
            var hosts = JsonConvert.DeserializeObject<List<HostPoco>>(content);

            Assert.AreEqual(num_of_host, hosts.Count);
            Assert.IsTrue(num_of_host >= DbTestHelper.NUMBER_OF_HOST_ENTITIES);
        }

        [TestMethod]
        public async Task T03_UpdateHost()
        {
            var hostFromDB = Db.Hosts.First();
            var host = hostFromDB.ToPoco();
            Db.ChangeTracker.Clear();
            var num_of_host = Db.Hosts.Count();
            var originalEnabled = host.Enabled;
            var originalName = host.Name;
            host.Name = host.Name + "2";
            host.Enabled = !host.Enabled;
            var resp_hosts = await _client.PutAsync("/host", TestHelper.SerializeToHttpContent(host));

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var content = await resp_hosts.Content.ReadAsStringAsync();
            var defaultResponse = JsonConvert.DeserializeObject<DefaultResponse>(content);

            var hostFromDb = Db.Hosts.Where(n => n.Id == host.Id).Single();

            Assert.IsTrue(defaultResponse.Success);
            Assert.AreEqual(num_of_host, Db.Hosts.Count());
            Assert.AreEqual(!originalEnabled, hostFromDb.Enabled);
            Assert.AreEqual(originalName + "2", hostFromDb.Name);
        }

        [TestMethod]
        public async Task T04_DeleteHost()
        {
            var hosts = Db.Hosts.ToList();
            var hostFromDbBefore = hosts.Where(i => i.Sensors.Any()).First();
            var host = hostFromDbBefore.ToPoco();
            Db.ChangeTracker.Clear();
            var num_of_host = Db.Hosts.Count();
            var num_of_sensors = Db.Sensors.Count();
            var num_of_sensors_in_host = hostFromDbBefore.Sensors.Count();
            var diff = (num_of_sensors - num_of_sensors_in_host);

            var resp_hosts = await _client.DeleteAsync($"/host/{host.Id}");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var content = await resp_hosts.Content.ReadAsStringAsync();
            var defaultResponse = JsonConvert.DeserializeObject<DefaultResponse>(content);

            var hostFromDb = Db.Hosts.Where(n => n.Id == host.Id).SingleOrDefault();

            Assert.IsTrue(defaultResponse.Success);
            Assert.AreEqual(num_of_host - 1, Db.Hosts.Count());
            Assert.AreEqual(diff, Db.Sensors.Count());
            Assert.IsNull(hostFromDb);
        }
    }
}
