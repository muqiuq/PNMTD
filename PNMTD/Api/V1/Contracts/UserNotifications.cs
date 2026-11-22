using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// Notifications to people (docs/knowledge-base-extensions-plan.md §10.2): one's own choices on Account → Notifications.

/// <summary>A channel of a kind: on or off for the person, and its default.</summary>
public sealed record UserNotificationChannelItem(UserNotificationChannel Channel, bool Enabled, bool Default);

/// <summary>A kind the person can be told about: only those whose right they hold.</summary>
public sealed record UserNotificationKindItem(string Kind, string Label, string Help, IReadOnlyList<UserNotificationChannelItem> Channels);

/// <summary>
/// <c>GET /account/notifications</c>: whether the account has an e-mail address, whether PNMT sends them at all, the batch window and
/// the daily limit, and the kinds.
/// </summary>
public sealed record UserNotificationSettings(bool HasEmail, bool Enabled, int BatchMinutes, int MaxPerDay, IReadOnlyList<UserNotificationKindItem> Kinds);

public sealed class UserNotificationChoice
{
    public string Kind { get; set; } = "";

    public UserNotificationChannel Channel { get; set; }

    public bool Enabled { get; set; }
}

/// <summary><c>PUT /account/notifications</c>: the choices to change.</summary>
public sealed class UserNotificationSettingsRequest
{
    public List<UserNotificationChoice> Items { get; set; } = [];
}
