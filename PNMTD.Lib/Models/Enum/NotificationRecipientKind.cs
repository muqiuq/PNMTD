namespace PNMTD.Lib.Models.Enum;

/// <summary>
/// Whom a notification rule tells (docs/notification-recipients-plan.md §3.1): an address as the rule always took it, a PNMT
/// user, every member of a group, or whoever the availability plan makes responsible. Stored as its number: append, never reorder.
/// </summary>
public enum NotificationRecipientKind : short
{
    /// <summary>An e-mail address, <c>pushover://&lt;user key&gt;</c> or an <c>http(s)://</c> webhook, read by its form.</summary>
    Address = 0,

    /// <summary>A user, at the e-mail address and Pushover key of their account.</summary>
    User = 1,

    /// <summary>Every active member of a group, each at their own contact details.</summary>
    Group = 2,

    /// <summary>Whoever the availability plan makes responsible, with the rule's stand-in when it names nobody.</summary>
    Responsible = 3,
}
