using Microsoft.AspNetCore.WebUtilities;
using PNMTD.Lib.Models.Poco;
using System.Net.Http;
using System.Net.Http.Json;

namespace PNMT.ApiClient.Data.Entities
{
    public class EventApiCrud
    {
        private readonly HttpClient httpClient;

        public EventApiCrud(HttpClient httpClient) 
        {
            this.httpClient = httpClient;
        }
        public async Task<List<EventEntityPoco>> GetEventsForSensor(Guid Id)
        {
            return await httpClient.GetFromJsonAsync<List<EventEntityPoco>>($"/events/sensor/{Id}");
        }

        public async Task<List<EventEntityPoco>> GetLastFewEventsWithError()
        {
            return await httpClient.GetFromJsonAsync<List<EventEntityPoco>>($"/events/lasterrors");
        }

        public async Task<List<EventEntityPoco>> GetLastEvents(int skip = 0, int count = 1000)
        {
            var query = new Dictionary<string, string?>
            {
                ["skip"] = skip.ToString(),
                ["count"] = count.ToString()
            };
            var url = QueryHelpers.AddQueryString("/events/last", query);
            return await httpClient.GetFromJsonAsync<List<EventEntityPoco>>(url);
        }
    }
}
