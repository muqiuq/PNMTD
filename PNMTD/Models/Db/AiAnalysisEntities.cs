using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>A situation analysis run's state (docs/ai-operations-plan.md §9).</summary>
public enum AnalysisRunState : short
{
    Queued = 0,
    Done = 1,
    Failed = 2,
    Cancelled = 3,
}

/// <summary>The AI's overall view of the monitoring state (§9.3).</summary>
public enum SituationStatus : short
{
    Normal = 0,
    Degraded = 1,
    Incident = 2,
}

/// <summary>What a finding is about (§9.3).</summary>
public enum FindingKind : short
{
    Incident = 0,
    Pattern = 1,
    Risk = 2,
    Noise = 3,
}

/// <summary>A finding's severity (§9.3): each has a system sensor of its own.</summary>
public enum FindingSeverity : short
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// One situation analysis (§9): when and why it ran, the pseudonyms and placeholders it used (they stay in PNMT), and
/// the AI's status and summary, with the real names mapped back.
/// </summary>
[Table("ai_analysis_runs")]
public class AiAnalysisRunEntity
{
    [Key]
    public Guid Id { get; set; }

    public DateTime At { get; set; }

    /// <summary>interval, storm, uplink-back, gateway-down or manual.</summary>
    [MaxLength(32)]
    public string Trigger { get; set; } = "";

    /// <summary>The snapshot's structural hash (states, failure counts, uplink): unchanged means no new run.</summary>
    [MaxLength(64)]
    public string SnapshotHash { get; set; } = "";

    /// <summary>Pseudonym or placeholder to the real value: <c>S1</c> → a sensor's id, <c>&lt;ip:1&gt;</c> → an address.</summary>
    [Column(TypeName = "jsonb")]
    public string Mapping { get; set; } = "{}";

    public AnalysisRunState State { get; set; }

    public SituationStatus? Status { get; set; }

    [MaxLength(1000)]
    public string? Summary { get; set; }

    public Guid? WorkId { get; set; }

    public DateTime? CompletedAt { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    /// <summary>Sensor messages withheld from the AI: flagged by the scan, or all of them when the guard flagged the batch.</summary>
    public int Withheld { get; set; }

    public bool GuardFlagged { get; set; }

    /// <summary>The snapshot's size in characters, as sent.</summary>
    public int SnapshotChars { get; set; }

    public List<AiFindingEntity> Findings { get; set; } = [];
}

/// <summary>A finding of a run (§9.3), its texts with the real names mapped back.</summary>
[Table("ai_findings")]
public class AiFindingEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public AiAnalysisRunEntity Run { get; set; } = null!;

    /// <summary>The AI's slug, such as <c>uplink-flapping-evenings</c>.</summary>
    [MaxLength(80)]
    public string Key { get; set; } = "";

    /// <summary>The key with the finding's sensors: what "new", mutes and decisions compare.</summary>
    [MaxLength(200)]
    public string DedupeKey { get; set; } = "";

    public FindingKind Kind { get; set; }

    public FindingSeverity Severity { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(1200)]
    public string Explanation { get; set; } = "";

    [MaxLength(600)]
    public string Recommendation { get; set; } = "";

    public double Confidence { get; set; }

    public List<Guid> Sensors { get; set; } = [];

    public List<Guid> Hosts { get; set; } = [];

    public List<string> Evidence { get; set; } = [];

    /// <summary>A critical finding the verification model confirmed.</summary>
    public bool Verified { get; set; }

    /// <summary>Rated critical, but not confirmed: stored as a warning.</summary>
    public bool Disputed { get; set; }

    /// <summary>Not reported in the 24 hours before: it went to its sensor.</summary>
    public bool New { get; set; }

    /// <summary>Its key was muted by a decision: neither sent nor raised.</summary>
    public bool Muted { get; set; }

    public Guid? DecisionId { get; set; }
}
