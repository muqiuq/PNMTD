using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Advanced;

namespace PNMTD.Models.Db;

/// <summary>
/// An ADVANCED sensor's setting and state (docs/advanced-sensor-plan.md §5.1), 1:1, deleted with it. The state has two
/// writers: the receipt columns the report endpoint's, the verdict columns the evaluator's.
/// </summary>
[Table("advanced_sensors")]
[Audited]
public class AdvancedSensorEntity
{
    [Key]
    public Guid SensorId { get; set; }

    public virtual SensorEntity Sensor { get; set; } = null!;

    /// <summary>The data names people: the reports need Monitoring: Details (§13).</summary>
    public bool NamesPeople { get; set; }

    // --- the receipt, written by the endpoint (§7.1) ----------------------------------------------------------------------

    [AuditIgnore]
    public Guid? LastReportId { get; set; }

    /// <summary>Any report, valid or not: staleness reads this, so a stored report is a sign of life before it is judged.</summary>
    [AuditIgnore]
    public DateTime? LastReportAt { get; set; }

    [AuditIgnore]
    public DateTime? LastValidReportAt { get; set; }

    [AuditIgnore]
    [MaxLength(AdvancedLimits.MaxSource)]
    public string? LastSource { get; set; }

    // --- the verdict, written by the evaluator (§7.3) ---------------------------------------------------------------------

    [AuditIgnore]
    [MaxLength(AdvancedLimits.MaxVerdictKey)]
    public string? LastVerdictKey { get; set; }

    [AuditIgnore]
    public int? LastCode { get; set; }

    [AuditIgnore]
    [MaxLength(AdvancedLimits.MaxMessage)]
    public string? LastMessage { get; set; }

    [AuditIgnore]
    public DateTime? LastEvaluatedAt { get; set; }

    /// <summary>Each rule's latest outcome and since when (<see cref="AdvancedRuleState"/> by rule id), for <i>failing since</i>.</summary>
    [AuditIgnore]
    [Column(TypeName = "jsonb")]
    public string? RuleStates { get; set; }

    /// <summary>Set by a change of the rules or the settings (§7.5); cleared by the evaluator once it re-judged the newest report.</summary>
    [AuditIgnore]
    public DateTime? ReevaluateRequestedAt { get; set; }

    [AuditIgnore]
    public DateTime Created { get; set; }
}

/// <summary>A rule of an ADVANCED sensor (§5.2): a path, an operator and its operand, and how it counts.</summary>
[Table("advanced_rules")]
[Audited]
public class AdvancedRuleEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    [AuditIgnore]
    public int Order { get; set; }

    [MaxLength(AdvancedLimits.MaxRuleName)]
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Why the rule exists, shown with a failure.</summary>
    [MaxLength(AdvancedLimits.MaxNote)]
    public string? Note { get; set; }

    [MaxLength(AdvancedLimits.MaxPath)]
    public string Path { get; set; } = "";

    public AdvancedOperator Operator { get; set; }

    [MaxLength(AdvancedLimits.MaxOperand)]
    public string? Operand { get; set; }

    [MaxLength(AdvancedLimits.MaxOperand2)]
    public string? Operand2 { get; set; }

    public bool IgnoreCase { get; set; }

    public AdvancedQuantifier Quantifier { get; set; } = AdvancedQuantifier.All;

    public AdvancedWhenMissing WhenMissing { get; set; } = AdvancedWhenMissing.Fail;

    public AdvancedSeverity Severity { get; set; } = AdvancedSeverity.Fail;

    [AuditIgnore]
    public DateTime Created { get; set; }

    public AdvancedRuleSpec ToSpec() => new(Id, Name, Enabled, Path, Operator, Operand, Operand2, IgnoreCase, Quantifier, WhenMissing, Severity, Note);
}

/// <summary>A field of an ADVANCED sensor (§5.3): a path a person gave a label, a format and a unit.</summary>
[Table("advanced_fields")]
[Audited]
public class AdvancedFieldEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    [AuditIgnore]
    public int Order { get; set; }

    /// <summary>The label; called Name so that the audit names the row by it.</summary>
    [MaxLength(AdvancedLimits.MaxFieldName)]
    public string Name { get; set; } = "";

    [MaxLength(AdvancedLimits.MaxPath)]
    public string Path { get; set; } = "";

    public AdvancedFieldFormat Format { get; set; } = AdvancedFieldFormat.Auto;

    [MaxLength(AdvancedLimits.MaxUnit)]
    public string? Unit { get; set; }

    public int? Decimals { get; set; }

    /// <summary>Shown on the overview.</summary>
    public bool Pinned { get; set; }

    /// <summary>Samples are kept for it (§5.5).</summary>
    public bool Chart { get; set; }

    [AuditIgnore]
    public DateTime Created { get; set; }
}

/// <summary>
/// One report as received (§5.4), with the verdict at it once the evaluator judged it. The body is text, not jsonb, to keep
/// the app's order of keys. A report identical to the one before folds into its row (<see cref="Repeats"/>).
/// </summary>
[Table("advanced_reports")]
[NotAudited("what an app reported")]
public class AdvancedReportEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    public DateTime ReceivedAt { get; set; }

    /// <summary>The latest repeat's receipt; <see cref="ReceivedAt"/> without repeats.</summary>
    public DateTime LastReceivedAt { get; set; }

    public int Repeats { get; set; }

    [MaxLength(AdvancedLimits.MaxSource)]
    public string? Source { get; set; }

    public int Bytes { get; set; }

    /// <summary>Hex, of the body as received.</summary>
    [MaxLength(64)]
    public string Sha256 { get; set; } = "";

    /// <summary>The JSON as received, or null for a report too large to keep.</summary>
    public string? Body { get; set; }

    public bool Valid { get; set; }

    public AdvancedReportError? ErrorKind { get; set; }

    [MaxLength(AdvancedLimits.MaxError)]
    public string? Error { get; set; }

    [MaxLength(AdvancedLimits.MaxStatus)]
    public string? Status { get; set; }

    [MaxLength(AdvancedLimits.MaxMessage)]
    public string? Message { get; set; }

    public DateTime? MeasuredAt { get; set; }

    /// <summary>Null until the evaluator judged it: the evaluator's queue (§7.3).</summary>
    public DateTime? EvaluatedAt { get; set; }

    /// <summary>Runs in which judging this report threw; at three it is marked judged (496 internal).</summary>
    public short EvaluationAttempts { get; set; }

    public int? Code { get; set; }

    public int Failed { get; set; }

    public int Warnings { get; set; }

    /// <summary><see cref="AdvancedResults"/>: each rule's outcome with its name and condition as they were, and the fields' values.</summary>
    [Column(TypeName = "jsonb")]
    public string? Results { get; set; }
}

/// <summary>A charted field's values in one minute (§5.5), upserted by the evaluator.</summary>
[Table("advanced_samples")]
[NotAudited("charted values")]
public class AdvancedSampleEntity
{
    public Guid SensorId { get; set; }

    public Guid FieldId { get; set; }

    /// <summary>The minute of the report's receipt, UTC.</summary>
    public DateTime Minute { get; set; }

    public double Min { get; set; }

    public double Max { get; set; }

    public double Last { get; set; }

    public int Count { get; set; }
}
