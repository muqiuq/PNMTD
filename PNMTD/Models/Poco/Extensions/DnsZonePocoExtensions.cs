using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;

namespace PNMTD.Models.Poco.Extensions
{
    public static class DnsZonePocoExtensions
    {

        public static DnsZonePoco ToPoco(this DnsZoneEntity dnsZoneEntity)
        {
            return new DnsZonePoco()
            {
                Id = dnsZoneEntity.Id,
                Enabled = dnsZoneEntity.Enabled,
                ZoneEntries = dnsZoneEntity.DnsZoneEntries.Select(d => d.ToPoco()).ToList(),
                ZoneFileContent = dnsZoneEntity.ZoneFileContent,
                ZoneName = dnsZoneEntity.ZoneName,
            };
        }

        public static DnsZoneEntity ToEntity(this DnsZonePoco dnsZonePoco)
        {
            return new DnsZoneEntity()
            {
                Id = dnsZonePoco.Id,
                Enabled = dnsZonePoco.Enabled,
                ZoneFileContent = dnsZonePoco.ZoneFileContent,
                ZoneName = dnsZonePoco.ZoneName
            };
        }

    }
}
