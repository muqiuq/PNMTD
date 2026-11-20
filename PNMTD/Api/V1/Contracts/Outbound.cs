using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// The two outbound logs and the Redmine queue (docs/redmine-queue-outbound-plan.md §6, §7, §8).

/// <summary>An e-mail, Pushover message or HTTP notification in the notification log, without its headers and bodies.</summary>
public sealed record OutboundNotificationItem(
    long Id,
    DateTime StartedAt,
    int DurationMs,
    OutboundChannel Channel,
    string Method,
    string Target,
    string? Recipient,
    string Summary,
    string Purpose,
    string AskedBy,
    OutboundOutcome Outcome,
    int? StatusCode,
    string? Error,
    long RequestBytes,
    long ResponseBytes,
    bool Truncated);

/// <summary>A call in the Redmine log, without its headers and bodies.</summary>
public sealed record RedmineCallItem(
    long Id,
    DateTime StartedAt,
    int DurationMs,
    int QueuedMs,
    RedmineCallKind Kind,
    string Method,
    string Target,
    string Summary,
    string Purpose,
    string AskedBy,
    Guid? JobId,
    RedmineLane Lane,
    OutboundOutcome Outcome,
    int? StatusCode,
    string? Error,
    long RequestBytes,
    long ResponseBytes,
    bool Truncated);

public sealed record OutboundHeader(string Name, string Value);

/// <summary>
/// A record in full (§7.3): one of <see cref="Notification"/> and <see cref="Redmine"/>, the headers (masked, §6.5), the bodies
/// as stored (cut at <c>Outbound__MaxBodyKiB</c> when the item says <c>Truncated</c>), and an e-mail's SMTP conversation.
/// </summary>
public sealed record OutboundDetail(
    OutboundNotificationItem? Notification,
    RedmineCallItem? Redmine,
    IReadOnlyList<OutboundHeader> RequestHeaders,
    string? RequestBody,
    IReadOnlyList<OutboundHeader> ResponseHeaders,
    string? ResponseBody,
    string? Transcript);

/// <summary><c>GET /system/outbound/notifications</c>: newest first.</summary>
public sealed class OutboundNotificationQuery : PageQuery
{
    public OutboundChannel? Channel { get; set; }

    public OutboundOutcome? Outcome { get; set; }
}

/// <summary><c>GET /system/outbound/redmine</c>: newest first; <c>job</c> for one job's calls.</summary>
public sealed class RedmineCallQuery : PageQuery
{
    public RedmineCallKind? Kind { get; set; }

    public RedmineLane? Lane { get; set; }

    public OutboundOutcome? Outcome { get; set; }

    public Guid? Job { get; set; }
}

/// <summary>An operation in flight (§6.7).</summary>
public sealed record OutboundRunningItem(string Log, string Channel, string Method, string Target, string Purpose, string AskedBy, DateTime StartedAt);

/// <summary>A Redmine job, queued (no slot yet) or running.</summary>
public sealed record RedmineJobItem(Guid Id, string? Key, string Purpose, string AskedBy, RedmineLane Lane, bool Started, DateTime QueuedAt,
    DateTime? StartedAt, int CallsRunning, int CallsDone);

/// <summary>The Redmine queue now (§6.7): jobs queued per lane, jobs running, slots in use, the background lane's pause.</summary>
public sealed record RedmineQueueState(int Button, int Write, int Interactive, int Background, int Running, int SlotsInUse, int Slots,
    DateTime? BackgroundPausedUntil, IReadOnlyList<RedmineJobItem> Jobs);

/// <summary><c>GET /system/outbound/live</c>: what runs now, the Redmine queue, each log's newest record and dropped records.</summary>
public sealed record OutboundLive(
    IReadOnlyList<OutboundRunningItem> Running,
    RedmineQueueState Redmine,
    long NewestNotificationId,
    long NewestRedmineId,
    long DroppedNotifications,
    long DroppedRedmine);
