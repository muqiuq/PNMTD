using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("events")]
    public class EventEntity
    {
        public const int END_OF_SUCCESS_CODES = 500;

        public Guid Id { get; set; }

        public SensorEntity Sensor { get; set; }

        public DateTime Created { get; set; }

        public string? Message { get; set; }

        public bool IsSuccess { get
            {
                return Code <= END_OF_SUCCESS_CODES;
            }
        }

        public int Code { get; set; }

    }
}

