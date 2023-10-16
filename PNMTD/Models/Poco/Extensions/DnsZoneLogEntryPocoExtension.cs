using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;

namespace PNMTD.Models.Poco.Extensions
{
    public static class DnsZoneLogEntryPocoExtension
    {

        public static DnsZoneLogEntryPoco ToPoco(this DnsZoneLogEntryEntity entity)
        {
            return new DnsZoneLogEntryPoco()
            {
                Id = entity.Id,
                DnsZoneId = entity.DnsZoneId,
                Created = entity.Created,
                EntryType = entity.EntryType,
                Message = entity.Message,
            };
        }

    }
}
