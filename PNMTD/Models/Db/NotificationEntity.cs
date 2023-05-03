using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("notifications")]
    public class NotificationEntity
    {
        [Key]
        public Guid Id { get; set; }

        public string Recipient { get; set; }

        public bool Enabled { get; set; }

        public virtual List<HostEntity> SubscribedHosts { get; set; } = new List<HostEntity>();
        
        public virtual List<SensorEntity> SubscribedSensors { get; set; } = new List<SensorEntity>();

    }
}
