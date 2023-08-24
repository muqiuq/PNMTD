using System.Net.Http.Headers;
using System.Net.Http.Json;
using PNMT.ApiClient.Data.Entities;
using PNMTD.Lib.Authentification;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Poco;


namespace PNMT.ApiClient.Data
{
    public class PNMTDApi
    {
        private readonly HttpClient httpClient;

        public static string BaseAddress = "https://localhost:7328";

        public static string BaseUrlForEventSubmission = "https://localhost:7328";

        public readonly HostApiCrud Hosts;
        public readonly SensorApiCrud Sensors;
        public readonly NotificationRuleApiCrud NotificationRules;
        public readonly MailInputApiCrud MailInputs;
        public readonly EventApiCrud Events;
        public readonly KeyValueCrud KeyValues;
        public readonly MailLogApiCrud MailLogs;

        public PNMTDApi(HttpClient httpClient, JwtTokenProvider jwtTokenProvider)
        {
            this.httpClient = httpClient;
            httpClient.BaseAddress = new Uri(BaseAddress);
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtTokenProvider.JwtToken);
            this.Hosts = new HostApiCrud(httpClient);
            this.Sensors = new SensorApiCrud(httpClient);
            this.NotificationRules = new NotificationRuleApiCrud(httpClient);
            this.Events = new EventApiCrud(httpClient);
            this.MailInputs = new MailInputApiCrud(httpClient);
            this.KeyValues = new KeyValueCrud(httpClient);
            this.MailLogs = new MailLogApiCrud(httpClient);
        }

        public string GetSensorEventUrl(SensorPoco sensor) {
            return $"{BaseUrlForEventSubmission}/event/{sensor.SecretToken}/";
        }

        public string GetSensorEventUrl(SensorPoco sensor, string code, string message)
        {
            return $"{GetSensorEventUrl(sensor)}{code}/{message}";
        }

        public async Task<UplinkStatePoco> GetUplinkState()
        {
            return await httpClient.GetFromJsonAsync<UplinkStatePoco>($"/state");
        }

    }
}
