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
                RequiresProcessing = dnsZoneEntity.RequiresProcessing,
                LastChecked = dnsZoneEntity.LastChecked,
                RecordsMatch = dnsZoneEntity.RecordsMatch,
                ForceUpdate = dnsZoneEntity.ForceUpdate,
                Interval = dnsZoneEntity.Interval,
                NextCheck = dnsZoneEntity.NextCheck,
                Host = dnsZoneEntity.Host?.ToPoco(),
                HostId = dnsZoneEntity.HostId,
            };
        }

        public static DnsZoneEntity ToEntity(this DnsZonePoco dnsZonePoco)
        {
            return new DnsZoneEntity()
            {
                Id = dnsZonePoco.Id,
                Enabled = dnsZonePoco.Enabled,
                ZoneFileContent = dnsZonePoco.ZoneFileContent,
                ZoneName = dnsZonePoco.ZoneName,
                RequiresProcessing = dnsZonePoco.RequiresProcessing,
                LastChecked = dnsZonePoco.LastChecked,
                RecordsMatch = dnsZonePoco.RecordsMatch,
                Interval = dnsZonePoco.Interval,
                NextCheck = dnsZonePoco.NextCheck,
                HostId = dnsZonePoco.HostId,
            };
        }

    }
}
