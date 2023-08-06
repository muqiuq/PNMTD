using System.ComponentModel.DataAnnotations;

namespace PNMTD.Lib.Models.Poco
{
    public class NotificationPoco
    {
        [Key]
        public Guid Id { get; set; }

        public string Recipient { get; set; }

        public bool Enabled { get; set; }

        public List<Guid> SubscribedSensors { get; set; }


    }
}
