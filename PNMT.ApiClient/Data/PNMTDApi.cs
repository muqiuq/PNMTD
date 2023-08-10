using System.Net.Http.Headers;
using PNMT.ApiClient.Data.Entities;
using PNMTD.Lib.Authentification;
using PNMTD.Models.Poco;


namespace PNMT.ApiClient.Data
{
    public class PNMTDApi
    {
        private readonly HttpClient httpClient;

        public string BaseAddress = "https://localhost:7238";

        public readonly HostApiCrud Hosts;
        public readonly SensorApiCrud Sensors;
        public readonly NotificationRuleApiCrud NotificationRules;
        public readonly EventApiCrud Events;

        public PNMTDApi(HttpClient httpClient, JwtTokenProvider jwtTokenProvider)
        {
            this.httpClient = httpClient;
            httpClient.BaseAddress = new Uri(BaseAddress);
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtTokenProvider.JwtToken);
            this.Hosts = new HostApiCrud(httpClient);
            this.Sensors = new SensorApiCrud(httpClient);
            this.NotificationRules = new NotificationRuleApiCrud(httpClient);
            this.Events = new EventApiCrud(httpClient);
        }

        public string GetSensorEventUrl(SensorPoco sensor) {
            return $"{BaseAddress}/event/{sensor.SecretToken}/";
        }

        public string GetSensorEventUrl(SensorPoco sensor, string code, string message)
        {
            return $"{GetSensorEventUrl(sensor)}{code}/{message}";
        }

    }
}
