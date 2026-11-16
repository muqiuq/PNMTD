using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Notifications;

/// <summary>
/// The Pushover priority of each importance, and emergency's repeat (docs/cookie-object-notes-plan.md §5.1): −2 lowest, −1 low,
/// 0 normal, 1 high, 2 emergency, which the phone repeats every <see cref="RetrySeconds"/> for <see cref="ExpireSeconds"/>
/// until it is acknowledged.
/// </summary>
public sealed record PushoverPrioritySettings(int Low, int Normal, int High, int RetrySeconds, int ExpireSeconds)
{
    public const int Lowest = -2;
    public const int Emergency = 2;

    /// <summary>A new installation's, and the fixed mapping of before 1.10: Low quiet, High bypassing the quiet hours.</summary>
    public static readonly PushoverPrioritySettings Default = new(-1, 0, 1, 60, 3600);

    public int Of(Importance importance) => importance switch
    {
        Importance.Low => Low,
        Importance.High => High,
        _ => Normal,
    };

    public static PushoverPrioritySettings Of(NotificationSettingsEntity row) =>
        new(row.PushoverLow, row.PushoverNormal, row.PushoverHigh, row.EmergencyRetrySeconds, row.EmergencyExpireSeconds);
}

/// <summary>
/// The settings as every Pushover is sent with (§5.2): read once from <c>notification_settings</c>, dropped when they are saved
/// (<see cref="Invalidate"/>), so that the next send follows a change without a restart. A singleton, as the providers are.
/// </summary>
public sealed class PushoverPriorities(IDbContextFactory<PnmtdDbContext> dbs)
{
    private PushoverPrioritySettings? current;

    public async Task<PushoverPrioritySettings> GetAsync(CancellationToken ct)
    {
        if (current is { } known)
        {
            return known;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var row = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        return current = row is null ? PushoverPrioritySettings.Default : PushoverPrioritySettings.Of(row);
    }

    public void Invalidate() => current = null;
}
