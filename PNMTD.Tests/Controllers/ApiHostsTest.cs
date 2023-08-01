using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Newtonsoft.Json;
using NuGet.Frameworks;
using PNMTD.Models.Poco;
using PNMTD.Models.Responses;
using System.Buffers.Text;
using System.Text;

namespace PNMTD.Tests.Controllers
{
    [TestClass]
    public class ApiHostsTest
    {
        private HttpClient _client;

        public static readonly CustomWebApplicationFactory<Program> _factory = new CustomWebApplicationFactory<Program>();

        [TestInitialize]
        public void Init()
        {
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
            Assert.IsTrue(hosts.First().Sensors != null && hosts.First().Sensors.Count > 0);
            Assert.IsTrue(_factory.DbTestHelper.HostEntities.Any(h => h.Id == hosts.First().Id));
        }

        [ClassCleanup]
        public static void Cleanup()
        {
            _factory.Dispose();
        }
    }
}