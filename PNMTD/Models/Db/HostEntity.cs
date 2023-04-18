using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("hosts")]
    public class HostEntity
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public string? Location { get; set; }

        public string? Notes { get; set; }

        public DateTime Created { get; set; }

        public bool Enabled { get; set; }

        public List<SensorEntity> Sensors { get; } = new();

    }
}

