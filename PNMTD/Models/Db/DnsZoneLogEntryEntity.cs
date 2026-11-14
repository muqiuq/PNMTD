using PNMTD.Lib.Models.Enum;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("dnszonelogentries")]
    [NotAudited("the DNS check's log")]
    public class DnsZoneLogEntryEntity
    {
        [Key]
        public Guid Id { get; set; }

        public DateTime Created { get; set; }

        public Guid? DnsZoneId { get; set; }

        public virtual DnsZoneEntity? DnsZone { get; set; }

        public DnsZoneLogEntryType EntryType { get; set; }

        public string Message { get; set; }

    }
}
