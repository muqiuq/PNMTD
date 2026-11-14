using PNMTD.Audit;

namespace PNMTD.Api.V1.Contracts;

/// <summary>An entry of the audit log (docs/audit-redmine-zabbix-plan.md §3.2).</summary>
public sealed record AuditLogItem(
    Guid Id,
    DateTime CreatedAt,
    Guid? UserId,
    string Username,
    string EventType,
    AuditCategory Category,
    AuditActor Actor,
    string? ActorRef,
    AuditOutcome Outcome,
    string? Error,
    string? IpAddress,
    string? UserAgent,
    string? Detail,
    string? TargetType,
    string? TargetId,
    string? TargetName,
    string? Request,
    string? CorrelationId,
    IReadOnlyList<AuditChange> Changes);

/// <summary>
/// <c>GET /audit-log</c>: newest first. Search: user, detail and target name. <see cref="UserId"/> finds what a user did
/// and what was done to their account.
/// </summary>
public sealed class AuditLogQuery : PageQuery
{
    public Guid? UserId { get; set; }

    /// <summary>An event type, such as LoginFailed.</summary>
    public string? Type { get; set; }

    public List<AuditCategory>? Categories { get; set; }

    public AuditOutcome? Outcome { get; set; }

    public AuditActor? Actor { get; set; }

    /// <summary>An object and what belongs to it: a host with its sensors (§4.3).</summary>
    public Guid? Subject { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    /// <summary>The entries of one request.</summary>
    public string? CorrelationId { get; set; }
}

/// <summary>An event type of the catalog (§3.9).</summary>
public sealed record AuditEventTypeItem(string Type, AuditCategory Category, string Label, AuditTone Tone, string? TargetType);
