using PNMTD.Lib.Models.Poco;
using System.Net.Http;
using System.Net.Http.Json;

namespace PNMT.ApiClient.Data.Entities
{
    public class HostApiCrud : ApiCrud<HostPoco>
    {
        public HostApiCrud(HttpClient httpClient) : base(httpClient, "host")
        {
        }
        public async Task<HostStatePoco> GetHostWithSensors(Guid Id)
        {
            return await httpClient.GetFromJsonAsync<HostStatePoco>($"/hosts/{Id}");
        }

        public async Task<List<HostStatePoco>> GetAllHostsWithSensors()
        {
            return await httpClient.GetFromJsonAsync<List<HostStatePoco>>($"/hosts");
        }
    }
}
