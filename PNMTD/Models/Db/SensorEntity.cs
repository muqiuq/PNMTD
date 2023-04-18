using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("sensors")]
    public class SensorEntity
    {
        public HostEntity Parent { get; set; }

        public SensorType Type { get; set; }

        public Guid Id { get; set; }

        public string Name { get; set; }

        public string? TextId { get; set; }

        public DateTime Created { get; set; }

        public bool Enabled { get; set; }
    }
}

