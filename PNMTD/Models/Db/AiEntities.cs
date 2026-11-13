using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>A work item's state (docs/ai-operations-plan.md §3.5).</summary>
public enum AiWorkState : short
{
    Queued = 0,
    Running = 1,
    WaitingRetry = 2,
    WaitingBudget = 3,
    Succeeded = 4,
    Failed = 5,
    Cancelled = 6,
}

/// <summary>Which model a request went to.</summary>
public enum AiRole : short
{
    Main = 0,
    Verify = 1,
    Guard = 2,
}

/// <summary>How a request ended (§3.6).</summary>
public enum AiOutcome : short
{
    Ok = 0,
    HttpError = 1,
    Timeout = 2,
    ConnectError = 3,
    InvalidOutput = 4,
    BlockedByGuard = 5,
}

/// <summary>One use of the AI (§3.5): retried on a schedule until it succeeds, is cancelled, or fails for good.</summary>
[Table("ai_work")]
public class AiWorkEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(64)]
    public string Purpose { get; set; } = "";

    [MaxLength(32)]
    public string? SubjectType { get; set; }

    public Guid? SubjectId { get; set; }

    [MaxLength(64)]
    public string? InputHash { get; set; }

    public AiWorkState State { get; set; }

    /// <summary>Failed attempts so far.</summary>
    public int Attempts { get; set; }

    public DateTime NextAttemptAt { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>This item's failures were notified (421).</summary>
    public bool Notified { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>The handler's result, as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string? Result { get; set; }
}

/// <summary>One HTTP request to a model (§3.6), whatever its outcome: the raw bodies gzip-compressed. The newest 10,000 are kept.</summary>
[Table("ai_requests")]
public class AiRequestEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public DateTime At { get; set; }

    public Guid? WorkId { get; set; }

    [MaxLength(64)]
    public string Purpose { get; set; } = "";

    public AiRole Role { get; set; }

    public int Attempt { get; set; }

    [MaxLength(128)]
    public string ModelAsked { get; set; } = "";

    [MaxLength(128)]
    public string? ModelServed { get; set; }

    [MaxLength(128)]
    public string? Provider { get; set; }

    [MaxLength(128)]
    public string? GenerationId { get; set; }

    [MaxLength(32)]
    public string? PromptVersion { get; set; }

    public byte[] Request { get; set; } = [];

    public byte[]? Response { get; set; }

    public bool Truncated { get; set; }

    public int? HttpStatus { get; set; }

    public AiOutcome Outcome { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    public int DurationMs { get; set; }

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public int? ReasoningTokens { get; set; }

    public int? CachedTokens { get; set; }

    [Column(TypeName = "numeric(12,6)")]
    public decimal? Cost { get; set; }
}

/// <summary>Spending per day, purpose and model (§3.7): kept 2 years, independent of the request log.</summary>
[Table("ai_spending")]
public class AiSpendingEntity
{
    public DateOnly Day { get; set; }

    [MaxLength(64)]
    public string Purpose { get; set; } = "";

    [MaxLength(128)]
    public string Model { get; set; } = "";

    public int Requests { get; set; }

    [Column(TypeName = "numeric(12,6)")]
    public decimal Cost { get; set; }
}

/// <summary>Which budget thresholds of a period were written (§3.7): each once.</summary>
[Table("ai_budget_state")]
public class AiBudgetStateEntity
{
    [Key]
    [MaxLength(16)]
    public string Period { get; set; } = "";

    public bool Notified80 { get; set; }

    public bool Notified90 { get; set; }

    public bool Notified100 { get; set; }
}
