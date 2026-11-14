using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Lib.Agent;

namespace PNMTD.Models.Db;

// Diagnostics (docs/windows-sensor-plan.md §5, §8.3): not Windows-specific; any agent that
// announces the check types can run them (§5.5).

/// <summary>A set of checks, defined on Admin → Diagnostics.</summary>
[Table("diagnostic_sets")]
[Audited]
public class DiagnosticSetEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(2000)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<DiagnosticCheckEntity> Checks { get; set; } = [];
}

/// <summary>A check of a set: a name, a type, and its typed parameters as JSON.</summary>
[Table("diagnostic_checks")]
[Audited]
public class DiagnosticCheckEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SetId { get; set; }

    public int Order { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    public DiagnosticCheckType Type { get; set; }

    /// <summary><see cref="PingParameters"/> or <see cref="DnsParameters"/>, as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string Parameters { get; set; } = "{}";
}

/// <summary>
/// A set assigned to an agent's sensor (§5.2), scheduled or by hand. It has a DIAGNOSTICS sensor of its own on the target's
/// host: deleting the assignment deletes that sensor and its history.
/// </summary>
[Table("diagnostic_assignments")]
[Audited]
public class DiagnosticAssignmentEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SetId { get; set; }

    /// <summary>The agent's sensor (a WINDOWS server).</summary>
    public Guid TargetSensorId { get; set; }

    /// <summary>The assignment's DIAGNOSTICS sensor.</summary>
    public Guid SensorId { get; set; }

    public DiagnosticMode Mode { get; set; }

    public int IntervalMinutes { get; set; } = 15;

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}

/// <summary>A run (§5.4): the trigger (schedule or manual, and who), when, the outcome and each check's result. Kept 30 days, at least 100 per assignment.</summary>
[Table("diagnostic_runs")]
[NotAudited("a run's results")]
public class DiagnosticRunEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid AssignmentId { get; set; }

    public Guid SensorId { get; set; }

    [MaxLength(20)]
    public string Trigger { get; set; } = "schedule";

    [MaxLength(200)]
    public string? RequestedBy { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>Null while a manual run waits for its result.</summary>
    public DiagnosticOutcome? Outcome { get; set; }

    [MaxLength(1000)]
    public string? Summary { get; set; }

    /// <summary>The checks' results (<see cref="DiagnosticCheckResult"/>), as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string? Results { get; set; }
}
