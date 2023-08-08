using PNMTD.Lib.Models.Enum;
using System.ComponentModel.DataAnnotations;

namespace PNMTD.Lib.Models.Poco
{
    public class NotificationRulePoco
    {
        [Key]
        public Guid Id { get; set; }

        public string Name { get; set; }

        public string Recipient { get; set; }

        public bool Enabled { get; set; }

        public NotificationRuleType Type { get; set; }


        public List<Guid> SubscribedSensors { get; set; }


    }
}
