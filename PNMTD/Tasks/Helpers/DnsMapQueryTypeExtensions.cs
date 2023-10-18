using DnsClient;
using PNMTD.Lib.Models.Enum;

namespace PNMTD.Tasks.Helpers
{
    public static class DnsMapQueryTypeExtensions
    {

        public static QueryType ToQueryType(this DnsZoneResourceType dnsZoneResourceType)
        {
            if (dnsZoneResourceType == DnsZoneResourceType.A) return QueryType.A;
            if (dnsZoneResourceType == DnsZoneResourceType.AAAA) return QueryType.AAAA;
            if (dnsZoneResourceType == DnsZoneResourceType.NS) return QueryType.NS;
            if (dnsZoneResourceType == DnsZoneResourceType.MX) return QueryType.MX;
            if (dnsZoneResourceType == DnsZoneResourceType.CNAME) return QueryType.CNAME;
            if (dnsZoneResourceType == DnsZoneResourceType.TXT) return QueryType.TXT;
            if (dnsZoneResourceType == DnsZoneResourceType.PTR) return QueryType.PTR;
            if (dnsZoneResourceType == DnsZoneResourceType.SRV) return QueryType.SRV;
            if (dnsZoneResourceType == DnsZoneResourceType.SOA) return QueryType.SOA;
            throw new ArgumentException($"No mapping found for DnsZoneResourceType {dnsZoneResourceType}");
        }

    }
}
