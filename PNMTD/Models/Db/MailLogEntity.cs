using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("maillogs")]
    [NotAudited("the mails received")]
    public class MailLogEntity
    {
        [Key]
        public Guid Id { get; set; }

        public string RemoteId { get; set; }

        public bool Processed { get; set; }


        public Guid? ProcessedById { get; set; }
        public virtual MailInputRuleEntity? ProcessedBy { get; set; }

        public string ProcessLog { get; set; }

        public DateTime Created { get; set; }

        public DateTime MessageDate { get; set; }

        public string From { get; set; }

        public string To { get; set; }

        public string Subject { get; set; }
        public string Content { get; set; }

        /// <summary>What became of the mail, and the AI's part in it (docs/ai-operations-plan.md §6.5).</summary>
        public MailDisposition Disposition { get; set; }

        public MailAiState AiState { get; set; }

        public Guid? DecisionId { get; set; }
    }
}
