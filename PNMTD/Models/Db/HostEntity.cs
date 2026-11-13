using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db
{
    [Table("hosts")]
    public class HostEntity
    {
        [Key]
        public Guid Id { get; set; }

        public string Name { get; set; }

        public string? Location { get; set; }

        public string? Notes { get; set; }

        public DateTime Created { get; set; }

        public bool Enabled { get; set; }

        /// <summary>Low, Normal or High (docs/ai-operations-plan.md §11.1); a sensor without its own takes this.</summary>
        public Lib.Models.Enum.Importance Importance { get; set; } = Lib.Models.Enum.Importance.Normal;

        public virtual List<SensorEntity> Sensors { get; set; } = new List<SensorEntity>();
    }
}

