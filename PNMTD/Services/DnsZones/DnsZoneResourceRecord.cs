namespace PNMTD.Services.DnsZones
{
    public class DnsZoneResourceRecord
    {

        public string Name { get; set; }

        public int Timeout { get; set; }

        public int? Priority { get; set; }

        public DnsZoneResourceType RecordType { get; set; }

        public string Value { get; set; }

    }
}
