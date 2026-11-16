using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>
/// The installation's notification settings, one row (docs/cookie-object-notes-plan.md §5.1): the Pushover priority of each
/// importance (−2 lowest, −1 low, 0 normal, 1 high, 2 emergency), and how an emergency repeats. Read for every Pushover PNMT
/// sends. The defaults are the fixed mapping of before 1.10.
/// </summary>
[Table("notification_settings")]
[NotAudited("each save is in the audit log as NotificationSettingsChanged")]
public class NotificationSettingsEntity
{
    public const int Id = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int SingletonId { get; set; } = Id;

    public int PushoverLow { get; set; } = -1;

    public int PushoverNormal { get; set; }

    public int PushoverHigh { get; set; } = 1;

    /// <summary>Pushover's <c>retry</c>: how often the phone repeats an emergency, 30 to 3600 seconds.</summary>
    public int EmergencyRetrySeconds { get; set; } = 60;

    /// <summary>Pushover's <c>expire</c>: when it stops repeating, at least the retry, at most 10800 seconds.</summary>
    public int EmergencyExpireSeconds { get; set; } = 3600;

    public DateTime? ChangedAt { get; set; }

    [MaxLength(200)]
    public string? ChangedBy { get; set; }
}
