﻿using PNMTD.Lib.Models.Poco;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;

namespace PNMT.ApiClient.Data.Entities
{
    public class DnsZoneApiCrud : ApiCrud<DnsZonePoco>
    {
        public DnsZoneApiCrud(HttpClient httpClient) : base(httpClient, "dnszone")
        {
        }

        public async Task<List<DnsZoneLogEntryPoco>> GetLogs(Guid Id)
        {
            return await httpClient.GetFromJsonAsync<List<DnsZoneLogEntryPoco>>($"/dnszone/logs/{Id}");
        }

        public async Task<List<DnsZoneLogEntryPoco>> GetLogs()
        {
            return await httpClient.GetFromJsonAsync<List<DnsZoneLogEntryPoco>>($"/dnszone/logs");
        }
    }
}
