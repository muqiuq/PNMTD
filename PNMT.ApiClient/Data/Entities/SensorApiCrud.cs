using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Poco;
using System.Net.Http.Json;

namespace PNMT.ApiClient.Data.Entities
{
    public class SensorApiCrud : ApiCrud<SensorPoco>
    {
        public SensorApiCrud(HttpClient httpClient) : base(httpClient, "sensor")
        {
        }

        public async Task<List<SensorPoco>> ByType(SensorType sensorType)
        {
            return await httpClient.GetFromJsonAsync<List<SensorPoco>>($"/sensor/bytype/{sensorType.ToString()}");
        }

    }
}
