using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("maillogs")]
    public class MailLogEntity
    {
        [Key]
        public Guid Id { get; set; }

        public bool Processed { get; set; }


        public Guid? ProcessedById { get; set; }
        public virtual MailInputRuleEntity? ProcessedBy { get; set; }

        public string ProcessLog { get; set; }

        public DateTime Created { get; set; }

        public string From { get; set; }

        public string To { get; set; }

        public string Subject { get; set; }
        public string Content { get; set; }
    }
}
