using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Newtonsoft.Json;
using NuGet.Frameworks;
using PNMTD.Models.Poco;

namespace PNMTD.Tests
{
    [TestClass]
    public class ApiUnitTest
    {
        private HttpClient _client;

        public static readonly CustomWebApplicationFactory<Program> _factory = new CustomWebApplicationFactory<Program>();

        [TestInitialize]
        public void Init() { 
            _client = _factory.CreateClient();
        }


        [TestMethod]
        public async Task Api_GetHosts()
        {
            var resp_hosts = await _client.GetAsync("/hosts");
            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var content = await resp_hosts.Content.ReadAsStringAsync();
            var hosts = JsonConvert.DeserializeObject<List<HostStatePoco>>(content);
            Assert.AreEqual(DbTestHelper.NUMBER_OF_HOST_ENTITIES, hosts.Count);
            Assert.IsTrue(_factory.DbTestHelper.HostEntities.Any(h => h.Id == hosts.First().Id));
        }

        [TestMethod]
        public async Task Api_GetEvent()
        {
            var numberOfEventsBefore = _factory.DbTestHelper.DbContext.Events.Count();
            var hostEntity = _factory.DbTestHelper.HostEntities[0];
            var sensorEntity = hostEntity.Sensors.First();
            var testMessage = "TestMessage1234";
            var resp_hosts = await _client.GetAsync($"/event/{sensorEntity.Id}/200/{testMessage}");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, resp_hosts.StatusCode);
            var content = await resp_hosts.Content.ReadAsStringAsync();

            var eventT = _factory.DbTestHelper.DbContext.Events.Where(e => e.Id == Guid.Parse(content)).FirstOrDefault();

            Assert.IsNotNull(eventT);
            Assert.AreEqual(numberOfEventsBefore + 1, _factory.DbTestHelper.DbContext.Events.Count());
        }

        [ClassCleanup]
        public static void Cleanup() {
            _factory.Dispose();
        }
    }
}