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

        /// <summary>The free text, shown as <i>Location text</i>: a rack or a room (docs/locations-knowledge-plan.md §3.2).</summary>
        public string? Location { get; set; }

        /// <summary>The location the host is assigned to, at any level (§3.2); set to null when the location is deleted.</summary>
        public Guid? LocationId { get; set; }

        public LocationEntity? AssignedLocation { get; set; }

        public string? Notes { get; set; }

        public DateTime Created { get; set; }

        public bool Enabled { get; set; }

        /// <summary>Low, Normal or High (docs/ai-operations-plan.md §11.1); a sensor without its own takes this.</summary>
        public Lib.Models.Enum.Importance Importance { get; set; } = Lib.Models.Enum.Importance.Normal;

        public virtual List<SensorEntity> Sensors { get; set; } = new List<SensorEntity>();
    }
}

