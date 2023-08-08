using PNMTD.Lib.Models.Poco;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("events")]
    public class EventEntity
    {
        [Key]
        public Guid Id { get; set; }

        public Guid SensorId { get; set; }

        public virtual SensorEntity Sensor { get; set; }

        public DateTime Created { get; set; }

        public string? Message { get; set; }

        public bool IsSuccess { get
            {
                return Code <= EventEntityPoco.END_OF_SUCCESS_CODES;
            }
        }

        public int Code { get; set; }

        public virtual List<NotificationRuleEventEntity> NotificationRuleEvents { get; set; } = new List<NotificationRuleEventEntity>();

    }
}

