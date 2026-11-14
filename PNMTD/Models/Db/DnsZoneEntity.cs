using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("dnszones")]
    [Audited]
    public class DnsZoneEntity
    {
        [Key]
        public Guid Id { get; set; }

        public bool Enabled { get; set; }

        [AuditIgnore]

        public bool RequiresProcessing { get; set; }

        public bool ForceUpdate { get; set; }

        public int Interval { get; set; }

        [AuditIgnore]

        public DateTime LastChecked { get; set; }

        [AuditIgnore]

        public DateTime NextCheck { get; set; }

        [AuditIgnore]

        public bool RecordsMatch { get; set; }

        public string ZoneName { get; set; }

        [AuditText]

        public string ZoneFileContent { get; set; }

        public Guid? HostId { get; set; }

        public virtual HostEntity? Host { get; set; }

        public virtual List<DnsZoneEntryEntity> DnsZoneEntries { get; set; } = new List<DnsZoneEntryEntity>();

        
    }
}
