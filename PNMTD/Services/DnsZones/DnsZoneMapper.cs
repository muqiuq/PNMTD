using PNMTD.Models.Db;

namespace PNMTD.Services.DnsZones
{
    public static class DnsZoneMapper
    {

        public static DnsZoneEntity DnsZoneToEntity(this DnsZoneFile dnsZoneFile)
        {
            var dnsZoneEntity = new DnsZoneEntity()
            {
                ZoneName = dnsZoneFile.Name,
                Id = Guid.NewGuid(),
                Enabled = true,
                ZoneFileContent = dnsZoneFile.Raw
            };

            var dnsZoneEntities = dnsZoneFile.Records.Select(r =>
                new DnsZoneEntryEntity()
                {
                    DnsZoneId = dnsZoneEntity.Id,
                    DnsZone = dnsZoneEntity,
                    ReferenceValue = r.Value,
                    RecordType = r.RecordType,
                    Id = Guid.NewGuid(),
                    TTL = r.Timeout,
                    Updated = DateTime.MinValue,
                    Ignore = r.RecordType == Lib.Models.Enum.DnsZoneResourceType.SOA ? true : false,
                    Name = r.Name
                }
            ).ToList();

            dnsZoneEntity.DnsZoneEntries.AddRange(dnsZoneEntities);

            return dnsZoneEntity;
        }

    }
}
