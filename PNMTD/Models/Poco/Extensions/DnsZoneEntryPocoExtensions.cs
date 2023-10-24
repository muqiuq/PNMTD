using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;

namespace PNMTD.Models.Poco.Extensions
{
    public static class DnsZoneEntryPocoExtensions
    {

        public static DnsZoneEntryPoco ToPoco(this DnsZoneEntryEntity dnsZoneEntryEntity)
        {
            return new DnsZoneEntryPoco()
            {
                Id = dnsZoneEntryEntity.Id,
                DnsZoneId = dnsZoneEntryEntity.DnsZoneId,
                ActualValue = dnsZoneEntryEntity.ActualValue,
                ReferenceValue = dnsZoneEntryEntity.ReferenceValue,
                Ignore = dnsZoneEntryEntity.Ignore,
                TTL = dnsZoneEntryEntity.TTL,
                RecordType = dnsZoneEntryEntity.RecordType,
                Updated = dnsZoneEntryEntity.Updated,
                Name = dnsZoneEntryEntity.Name,
                IsMatch = dnsZoneEntryEntity.IsMatch,
                NumOfFailuresInARow = dnsZoneEntryEntity.NumOfFailuresInARow,
            };
        }

        public static DnsZoneEntryEntity ToEntity(this DnsZoneEntryPoco dnsZoneEntryPoco)
        {
            return new DnsZoneEntryEntity()
            {
                Id = dnsZoneEntryPoco.Id,
                DnsZoneId = dnsZoneEntryPoco.DnsZoneId,
                ActualValue = dnsZoneEntryPoco.ActualValue,
                ReferenceValue = dnsZoneEntryPoco.ReferenceValue,
                Ignore = dnsZoneEntryPoco.Ignore,
                TTL = dnsZoneEntryPoco.TTL,
                RecordType = dnsZoneEntryPoco.RecordType,
                Updated = dnsZoneEntryPoco.Updated,
                Name = dnsZoneEntryPoco.Name,
                IsMatch= dnsZoneEntryPoco.IsMatch,
                NumOfFailuresInARow = dnsZoneEntryPoco.NumOfFailuresInARow,
            };
        }

    }
}
