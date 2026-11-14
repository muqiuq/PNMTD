using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Audit;

namespace PNMTD.Models.Db;

/// <summary>
/// The audit log (docs/audit-redmine-zabbix-plan.md §3.2): every operation, by whom, from where, on what, with what
/// outcome and changes. It was <c>auth_log</c> until 1.4 and keeps its rows. Kept for Audit:KeepDays, at most
/// Audit:MaxEntries; no one deletes entries through PNMT.
/// </summary>
[Table("audit_log")]
[NotAudited("the audit log itself")]
public class AuditLogEntity
{
    [Key]
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Who did it. No foreign key: the entries outlive a deleted user.</summary>
    public Guid? UserId { get; set; }

    /// <summary>As the user typed it, for failed logins with unknown names too; empty for the CLI and PNMT itself.</summary>
    public required string Username { get; set; }

    public required string EventType { get; set; }

    public AuditCategory Category { get; set; }

    public AuditActor Actor { get; set; }

    /// <summary>Which agent (or later, token) when the actor isn't a person.</summary>
    [MaxLength(200)]
    public string? ActorRef { get; set; }

    public AuditOutcome Outcome { get; set; }

    /// <summary>The status and PNMT's error code of a failed or refused operation: "409 last_admin".</summary>
    [MaxLength(200)]
    public string? Error { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>A sentence for people, at most 2,000 characters: what changed, why a login failed.</summary>
    public string? Detail { get; set; }

    [MaxLength(64)]
    public string? TargetType { get; set; }

    [MaxLength(64)]
    public string? TargetId { get; set; }

    /// <summary>The target's name as it was then.</summary>
    [MaxLength(500)]
    public string? TargetName { get; set; }

    /// <summary>The target and what it belongs to (a sensor's host, a host's location): the objects' History reads it.</summary>
    public List<Guid> Subjects { get; set; } = [];

    /// <summary>The changes, field by field (<see cref="AuditChange"/>s); never a secret's value.</summary>
    [Column(TypeName = "jsonb")]
    public string? Changes { get; set; }

    /// <summary>The endpoint: "PUT /api/v1/sensors/{id:guid}".</summary>
    [MaxLength(300)]
    public string? Request { get; set; }

    /// <summary>Joins the entries of one request.</summary>
    [MaxLength(100)]
    public string? CorrelationId { get; set; }
}
