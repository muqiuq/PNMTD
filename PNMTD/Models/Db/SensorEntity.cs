using PNMTD.Lib.Models.Enum;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("sensors")]
    public class SensorEntity
    {
        [Key]
        public Guid Id { get; set; }

        public Guid? OlderSiblingId { get; set; }

        public virtual SensorEntity? OlderSibling { get; set; }

        public Guid ParentId { get; set; }

        public virtual HostEntity Parent { get; set; }

        public SensorType Type { get; set; }

        public string Name { get; set; }

        public string? TextId { get; set; }

        public DateTime Created { get; set; }

        public bool Enabled { get; set; }

        public int Interval { get; set; }

        public int GracePeriod { get; set; }

        public virtual List<NotificationRuleSensorEntity> SubscribedByNotifications { get; set; } = new List<NotificationRuleSensorEntity>();
    }
}

