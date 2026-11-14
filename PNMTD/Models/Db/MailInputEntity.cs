using PNMTD.Models.Poco;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("mailinputrules")]
    [Audited]
    public class MailInputRuleEntity
    {

        [Key]
        public Guid Id { get; set; }

        public bool Enabled { get; set; }
        public string Name { get; set; }

        public string? BodyTest { get; set; }

        public string? FromTest { get; set; }

        public string? ExtractMessageRegex { get; set; }

        public string? SubjectTest { get; set; }

        public int? OkCode { get; set; }

        public string? OkTest { get; set; }

        public int? FailCode { get; set;}

        public string? FailTest { get; set; }

        public int DefaultCode { get; set; }

        public virtual SensorEntity? SensorOutput { get; set; }

        public Guid? SensorOutputId { get; set; }

        /// <summary>Event (as before), AI every time, Ignore or Delete (docs/ai-operations-plan.md §6.4).</summary>
        public MailRuleAction Action { get; set; }

        /// <summary>When neither the OK nor the FAIL test matches, the AI proposes better tests (§6.2, trigger B).</summary>
        public bool AskAiWhenUnsure { get; set; }

        /// <summary>For AI every time: the verification model's second opinion before an event.</summary>
        public bool VerifyAi { get; set; } = true;
    }
}
