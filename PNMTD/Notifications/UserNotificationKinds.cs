using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Models.Db;

namespace PNMTD.Notifications;

/// <summary>
/// Something a person can be told about their own work (docs/knowledge-base-extensions-plan.md §10.1): its key, its label and help on
/// <i>Account → Notifications</i>, the right the recipient must hold when it is sent, and whether e-mail is on by default.
/// </summary>
public sealed record UserNotificationKind(string Key, string Label, string Help, Area Area, Level Level, bool EmailByDefault);

/// <summary>The catalog, in code: adding a kind needs no migration and no column on <c>users</c>.</summary>
public static class UserNotificationKinds
{
    public const string KbLiked = "kb.liked";
    public const string KbUsed = "kb.used";

    public static readonly IReadOnlyList<UserNotificationKind> All =
    [
        new(KbLiked, "Someone liked a page I wrote", "Pages you created or edited. One e-mail per page for the likes of a quarter of an hour.",
            Area.KnowledgeBase, Level.View, true),
        new(KbUsed, "Someone used a page I wrote to solve a problem", "With the note they wrote, if any.", Area.KnowledgeBase, Level.View, true),
    ];

    public static UserNotificationKind? Find(string key) => All.FirstOrDefault(k => k.Key == key);
}

/// <summary>A batch of one kind for one person (§10.4): the rows that go out as one e-mail.</summary>
public sealed record UserNotificationBatch(UserNotificationKind Kind, Guid UserId, string UserName, Guid? SubjectId, IReadOnlyList<UserNotificationEntity> Rows);

/// <summary>
/// A batch's e-mail: <see cref="Purpose"/> names it in the notification log; <see cref="Because"/> tells the person why they got it
/// (<i>you wrote or edited KB-42</i>).
/// </summary>
public sealed record RenderedNotification(string Subject, string Body, string Purpose, string Because);

/// <summary>Writes a kind's e-mail from a batch and the subject as it is now; null when there is nothing left to say.</summary>
public interface IUserNotificationRenderer
{
    string Kind { get; }

    Task<RenderedNotification?> RenderAsync(PnmtdDbContext db, UserNotificationBatch batch, string? baseUrl, CancellationToken ct);
}
