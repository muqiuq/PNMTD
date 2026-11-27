using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Models.Db;

namespace PNMTD.Notifications;

/// <summary>
/// Something a person can be told about their own work (docs/knowledge-base-extensions-plan.md §10.1): its key, its label and help on
/// <i>Account → Notifications</i>, the right the recipient must hold when it is sent, and whether e-mail is on by default.
/// </summary>
public sealed record UserNotificationKind(string Key, string Label, string Help, Area Area, Level Level, bool EmailByDefault,
    Func<IConfiguration, bool>? Available = null)
{
    /// <summary>Whether the deployment has the kind now: the licences' only while <c>Licenses__Enabled</c> is on (docs/licences-plan.md §13.2).</summary>
    public bool IsAvailable(IConfiguration configuration) => Available?.Invoke(configuration) ?? true;
}

/// <summary>The catalog, in code: adding a kind needs no migration and no column on <c>users</c>.</summary>
public static class UserNotificationKinds
{
    public const string KbLiked = "kb.liked";
    public const string KbUsed = "kb.used";

    // Licences & services (docs/licences-plan.md §5.5): the owner's notices, and the ones about items nobody owns.
    public const string LicenseWarning = "license.warning";
    public const string LicenseReview = "license.review";
    public const string LicenseUnowned = "license.unowned";

    public static readonly IReadOnlyList<UserNotificationKind> All =
    [
        new(KbLiked, "Someone liked a page I wrote", "Pages you created or edited.", Area.KnowledgeBase, Level.View, true),
        new(KbUsed, "Someone used a page I wrote to solve a problem", "Pages you created or edited, with the note they wrote, if any.",
            Area.KnowledgeBase, Level.View, true),
        new(LicenseWarning, "Licences and services I own: warnings",
            "When one of yours goes bad (it is about to expire, its cancellation deadline is near, it expired, its end of life is near or has passed, more seats are used than bought), a reminder a few days before the date, and when it renewed by itself.",
            Area.Licenses, Level.View, true, Licenses.LicenseSetup.Enabled),
        new(LicenseReview, "Licences and services I own: reviews due", "When a review of one of yours falls due.",
            Area.Licenses, Level.View, true, Licenses.LicenseSetup.Enabled),
        new(LicenseUnowned, "Licences and services nobody owns: warnings",
            "The same warnings, for the licences and services without an owner: for whoever looks after the contracts.",
            Area.Licenses, Level.Edit, false, Licenses.LicenseSetup.Enabled),
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
