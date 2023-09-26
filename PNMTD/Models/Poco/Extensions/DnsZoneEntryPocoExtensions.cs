﻿using PNMTD.Lib.Models.Poco;
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
                Type = dnsZoneEntryEntity.Type,
                Updated = dnsZoneEntryEntity.Updated,
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
                Type = dnsZoneEntryPoco.Type,
                Updated = dnsZoneEntryPoco.Updated,
            };
        }

    }
}