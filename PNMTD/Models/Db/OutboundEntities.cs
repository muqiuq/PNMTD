using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Audit;

namespace PNMTD.Models.Db;

/// <summary>How a notification went out (docs/redmine-queue-outbound-plan.md §6.1).</summary>
public enum OutboundChannel : short
{
    Email = 0,
    Pushover = 1,
    Http = 2,
}

/// <summary>What became of an operation (§6.2).</summary>
public enum OutboundOutcome : short
{
    Ok = 0,
    Failed = 1,
    Simulated = 2,
    Cancelled = 3,
}

/// <summary>A call to Redmine reads or writes (§6.2).</summary>
public enum RedmineCallKind : short
{
    Read = 0,
    Write = 1,
}

/// <summary>
/// A Redmine job's place in the worker's queue (§3.3), in the order the lanes are worked: what a person started with a
/// button, Cookie's writes, what someone waits for, the rest.
/// </summary>
public enum RedmineLane : short
{
    Button = 0,
    Write = 1,
    Interactive = 2,
    Background = 3,
}

/// <summary>
/// One e-mail, Pushover message or HTTP notification (§6.2), whatever its outcome: the headers masked (§6.5), the bodies
/// gzip-compressed and capped at <c>Outbound__MaxBodyKiB</c>. The newest <c>Outbound__KeepNotifications</c> are kept.
/// </summary>
[Table("outbound_notifications")]
[NotAudited("the notification log")]
public class OutboundNotificationEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public DateTime StartedAt { get; set; }

    public int DurationMs { get; set; }

    public OutboundChannel Channel { get; set; }

    [MaxLength(16)]
    public string Method { get; set; } = "";

    [MaxLength(2000)]
    public string Target { get; set; } = "";

    [MaxLength(2000)]
    public string? Recipient { get; set; }

    [MaxLength(500)]
    public string Summary { get; set; } = "";

    [MaxLength(500)]
    public string Purpose { get; set; } = "";

    [MaxLength(200)]
    public string AskedBy { get; set; } = "";

    public OutboundOutcome Outcome { get; set; }

    public int? StatusCode { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    /// <summary><c>[[name, value], …]</c>, masked.</summary>
    [Column(TypeName = "jsonb")]
    public string RequestHeaders { get; set; } = "[]";

    [Column(TypeName = "jsonb")]
    public string ResponseHeaders { get; set; } = "[]";

    public byte[]? RequestBody { get; set; }

    public byte[]? ResponseBody { get; set; }

    public long RequestBytes { get; set; }

    public long ResponseBytes { get; set; }

    public bool Truncated { get; set; }

    /// <summary>E-mail only: the SMTP conversation, <c>AUTH</c> redacted, gzip-compressed.</summary>
    public byte[]? Transcript { get; set; }
}

/// <summary>
/// One HTTP request to Redmine (§6.2), whatever its outcome, with the job it was for, its lane and how long it waited for a
/// slot. The newest <c>Outbound__KeepRedmine</c> are kept.
/// </summary>
[Table("redmine_calls")]
[NotAudited("the Redmine log")]
public class RedmineCallEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public DateTime StartedAt { get; set; }

    public int DurationMs { get; set; }

    public int QueuedMs { get; set; }

    public RedmineCallKind Kind { get; set; }

    [MaxLength(16)]
    public string Method { get; set; } = "";

    [MaxLength(4000)]
    public string Target { get; set; } = "";

    [MaxLength(500)]
    public string Summary { get; set; } = "";

    [MaxLength(500)]
    public string Purpose { get; set; } = "";

    [MaxLength(200)]
    public string AskedBy { get; set; } = "";

    public Guid? JobId { get; set; }

    public RedmineLane Lane { get; set; }

    public OutboundOutcome Outcome { get; set; }

    public int? StatusCode { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    [Column(TypeName = "jsonb")]
    public string RequestHeaders { get; set; } = "[]";

    [Column(TypeName = "jsonb")]
    public string ResponseHeaders { get; set; } = "[]";

    public byte[]? RequestBody { get; set; }

    public byte[]? ResponseBody { get; set; }

    public long RequestBytes { get; set; }

    public long ResponseBytes { get; set; }

    public bool Truncated { get; set; }
}
