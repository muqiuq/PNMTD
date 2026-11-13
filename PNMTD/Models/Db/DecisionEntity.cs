using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>A decision's state (docs/ai-operations-plan.md §5.1).</summary>
public enum DecisionState : short
{
    Open = 0,
    Accepted = 1,
    Rejected = 2,
    Dismissed = 3,
    Expired = 4,
    Superseded = 5,
    Failed = 6,
}

/// <summary>
/// A question PNMT asks a person (§5): a kind, a typed payload with the proposal and its evidence, the options, and
/// who decided what and when. One open decision per dedupe key: more of the same raise its occurrences.
/// </summary>
[Table("decisions")]
public class DecisionEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(64)]
    public string Kind { get; set; } = "";

    public DecisionState State { get; set; }

    /// <summary>User or Admin.</summary>
    [MaxLength(16)]
    public string RequiredRole { get; set; } = "User";

    [MaxLength(300)]
    public string Title { get; set; } = "";

    [MaxLength(2000)]
    public string Summary { get; set; } = "";

    /// <summary>The kind's payload, as JSON, with its version.</summary>
    [Column(TypeName = "jsonb")]
    public string Payload { get; set; } = "{}";

    public int PayloadVersion { get; set; } = 1;

    [MaxLength(32)]
    public string? SourceType { get; set; }

    public Guid? SourceId { get; set; }

    [MaxLength(300)]
    public string? DedupeKey { get; set; }

    public int Occurrences { get; set; } = 1;

    /// <summary>The subjects that attached to it: mails, findings.</summary>
    public List<Guid> Subjects { get; set; } = [];

    /// <summary>The verification model disagreed.</summary>
    public bool Disputed { get; set; }

    /// <summary>Higher first.</summary>
    public int Priority { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    public Guid? DecidedBy { get; set; }

    [MaxLength(128)]
    public string? DecidedByName { get; set; }

    [MaxLength(64)]
    public string? Choice { get; set; }

    [Column(TypeName = "jsonb")]
    public string? Edits { get; set; }

    [Column(TypeName = "jsonb")]
    public string? Result { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    public Guid? AiWorkId { get; set; }
}
