using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Responses;
using System.Net.Http;
using System.Net.Http.Json;

namespace PNMT.ApiClient.Data.Entities
{
    public class NotificationRuleApiCrud : ApiCrud<NotificationRulePoco>
    {
        public NotificationRuleApiCrud(HttpClient httpClient) : base(httpClient, "notificationrule")
        {
        }
        public async Task<List<NotificationRulePoco>> GetNotificationRulesForSensor(Guid Id)
        {
            return await httpClient.GetFromJsonAsync<List<NotificationRulePoco>>($"/notificationrule/forsensor/{Id}");
        }

        public async Task LinkSensor(Guid idNotificationRule, Guid idSensor)
        {
            var result = await httpClient.PostAsJsonAsync<object>($"notificationrule/{idNotificationRule}/link-sensor/{idSensor}", new object() { });
            var response = await result.Content.ReadFromJsonAsync<DefaultResponse>();

            if (!result.IsSuccessStatusCode || !response.Success)
            {
                throw new PNMTDApiException($"HTTP LinkSensor {result.StatusCode}");
            }
        }

        public async Task UnlinkSensor(Guid idNotificationRule, Guid idSensor)
        {
            var result = await httpClient.DeleteAsync($"notificationrule/{idNotificationRule}/unlink-sensor/{idSensor}");
            var response = await result.Content.ReadFromJsonAsync<DefaultResponse>();

            if (!result.IsSuccessStatusCode || !response.Success)
            {
                throw new PNMTDApiException($"HTTP UnlinkSensor {result.StatusCode}");
            }
        }
    }
}
