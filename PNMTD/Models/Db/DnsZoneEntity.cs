using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("dnszones")]
    public class DnsZoneEntity
    {
        [Key]
        public Guid Id { get; set; }

        public bool Enabled { get; set; }

        public bool RequiresProcessing { get; set; }

        public bool ForceUpdate { get; set; }

        public int Interval { get; set; }

        public DateTime LastChecked { get; set; }

        public DateTime NextCheck { get; set; }

        public bool RecordsMatch { get; set; }

        public string ZoneName { get; set; }

        public string ZoneFileContent { get; set; }

        public virtual List<DnsZoneEntryEntity> DnsZoneEntries { get; set; } = new List<DnsZoneEntryEntity>();

        
    }
}
