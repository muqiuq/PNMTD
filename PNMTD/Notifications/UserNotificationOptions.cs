namespace PNMTD.Notifications;

/// <summary>
/// The <c>UserNotifications</c> section (docs/knowledge-base-extensions-plan.md §10.5): <c>UserNotifications__*</c> in .env. Read
/// through <c>IOptionsMonitor</c>.
/// </summary>
public sealed class UserNotificationOptions
{
    public const string Section = "UserNotifications";

    /// <summary>Enqueuing and sending. Off, nothing is enqueued or sent; the person's choices stay.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The notification task's second timer.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>How long a batch collects before it goes out as one e-mail.</summary>
    public int BatchMinutes { get; set; } = 15;

    /// <summary>E-mails per person and day at most; what is over is skipped.</summary>
    public int MaxPerUserDay { get; set; } = 20;

    /// <summary>Batches per run of the timer.</summary>
    public int PerRun { get; set; } = 10;

    /// <summary>Rows that are finished are deleted this many days later.</summary>
    public int KeepDays { get; set; } = 30;
}
