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

        public async Task<SensorPoco> BySecretToken(string secretToken)
        {
            try
            {
                return await httpClient.GetFromJsonAsync<SensorPoco>($"/sensor/bysecrettoken/{secretToken}");
            }catch(HttpRequestException e)
            {
                return null;
            }
        }

    }
}
