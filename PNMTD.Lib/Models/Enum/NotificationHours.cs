namespace PNMTD.Lib.Models.Enum;

/// <summary>
/// When a rule to the responsible asks the plan (docs/notification-recipients-plan.md §4.3): at any hour, the day's
/// responsible; in working hours only, with the night and the closed days the stand-in's or nobody's. Stored as its number.
/// </summary>
public enum NotificationHours : short
{
    Always = 0,

    WorkingHours = 1,
}
