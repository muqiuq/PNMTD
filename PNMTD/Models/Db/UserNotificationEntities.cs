using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace PNMTD.Models.Db;

// Notifications to people about their own work (docs/knowledge-base-extensions-plan.md §10): each person's choices per kind and
// channel, and the queue the notification task's second timer sends from. Migration UserNotifications.

/// <summary>How a person is told. E-mail for now; another channel needs no migration of the preferences.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UserNotificationChannel>))]
public enum UserNotificationChannel
{
    Email = 0,
}

/// <summary>Where a user notification is (§10.4). Stored as its number.</summary>
public enum UserNotificationState
{
    Pending = 0,
    Sending = 1,
    Sent = 2,
    Simulated = 3,
    Skipped = 4,
    Failed = 5,
    Cancelled = 6,
}

/// <summary>
/// A person's choice for a kind and a channel (§10.2). Only a choice that differs from the kind's default is stored: a missing row
/// is the default, so a new kind needs no migration and a new person gets the defaults.
/// </summary>
[Table("user_notification_preferences")]
[NotAudited("each save is in the audit log as NotificationPreferencesChanged")]
public class UserNotificationPreferenceEntity
{
    public Guid UserId { get; set; }

    [MaxLength(64)]
    public string Kind { get; set; } = "";

    public UserNotificationChannel Channel { get; set; }

    public bool Enabled { get; set; }

    public DateTime ChangedAt { get; set; }
}

/// <summary>
/// One event for one person (§10.4): enqueued in the transaction of what happened, sent with the others of its batch
/// (<see cref="BatchKey"/>) as one e-mail once <see cref="DueAt"/> has passed.
/// </summary>
[Table("user_notifications")]
[NotAudited("what went out is in the notification log")]
public class UserNotificationEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>The recipient.</summary>
    public Guid UserId { get; set; }

    /// <summary>The catalog's key: <c>kb.liked</c>.</summary>
    [MaxLength(64)]
    public string Kind { get; set; } = "";

    /// <summary>What goes out as one e-mail: <c>kb.liked:&lt;page id&gt;</c>.</summary>
    [MaxLength(128)]
    public string BatchKey { get; set; } = "";

    /// <summary>What it is about: the page.</summary>
    public Guid? SubjectId { get; set; }

    /// <summary>What made it (the like, the use), so undoing that cancels it.</summary>
    public Guid? SourceId { get; set; }

    public Guid? ActorUserId { get; set; }

    [MaxLength(200)]
    public string ActorName { get; set; } = "";

    /// <summary>A use's note, at most 300 characters.</summary>
    [MaxLength(300)]
    public string? Detail { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When the batch goes out: the batch's first row's time plus <c>UserNotifications:BatchMinutes</c>.</summary>
    public DateTime DueAt { get; set; }

    /// <summary>The next attempt after a failure; while sending, when the attempt started.</summary>
    public DateTime NextAttemptAt { get; set; }

    public UserNotificationState State { get; set; }

    public int Attempts { get; set; }

    public DateTime? SentAt { get; set; }

    /// <summary>The e-mail the row went out in: the daily limit counts these.</summary>
    public Guid? MessageId { get; set; }

    /// <summary>Why it was skipped or failed, or the provider's answer.</summary>
    [MaxLength(500)]
    public string? Outcome { get; set; }
}
