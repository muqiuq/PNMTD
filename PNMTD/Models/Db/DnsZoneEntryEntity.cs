using PNMTD.Lib.Models.Enum;
using PNMTD.Services.DnsZones;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("dnszoneentries")]
    public class DnsZoneEntryEntity
    {
        [Key]
        public Guid Id { get; set; }

        public bool Ignore { get; set; }

        public Guid DnsZoneId { get; set; }

        public virtual DnsZoneEntity DnsZone { get; set; }

        public int TTL { get; set; }

        public DnsZoneResourceType RecordType { get; set; }

        public string Name { get; set; }

        public string ReferenceValue { get; set; }

        public string? ActualValue { get; set; }

        public DateTime Updated { get; set; }

        public virtual List<DnsZoneLogEntryEntity> DnsZoneLogEntries { get; set; } = new List<DnsZoneLogEntryEntity>();

        [NotMapped]
        public bool IsMatch { get
            {
                return ActualValue == ReferenceValue;
            } 
        }
    }
}
