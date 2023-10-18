using PNMTD.Lib.Models.Poco;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMT.ApiClient.Data.Entities
{
    public class DnsZoneEntryApiCrud : ApiCrud<DnsZoneEntryPoco>
    {
        public DnsZoneEntryApiCrud(HttpClient httpClient) : base(httpClient, "dnszoneentry")
        {
        }
    }
}
