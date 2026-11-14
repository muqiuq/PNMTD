using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("keyvalues")]
    [Audited]
    public class KeyValueEntity
    {
        [Key]
        public Guid Id { get; set; }

        public bool Enabled { get; set; }

        public string Note { get; set; }

        [AuditPlain("the setting's name")]

        public string Key { get; set; }

        public string Value { get; set; }  

        public bool IsReadOnly { get; set; }
    }
}
