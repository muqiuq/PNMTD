using PNMTD.Models.Db;
using System.ComponentModel.DataAnnotations;

namespace PNMTD.Models.Poco
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
