using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Knowledge;

/// <summary>
/// A page's views (docs/locations-knowledge-plan.md §6.6): opening it in the UI, which sends one beacon. The same user on
/// the same page counts once per 30 minutes, remembered in memory; each view counts in the page's total and in its day
/// (kb_page_views, the day in PNMT's time zone), so the last 30 days can be summed.
/// </summary>
public sealed class KbViews
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private readonly object gate = new();
    private readonly Dictionary<(Guid Page, Guid User), DateTime> counted = [];

    /// <summary>True when the view counted.</summary>
    public async Task<bool> RecordAsync(PnmtdDbContext db, Guid page, Guid user, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        lock (gate)
        {
            if (counted.TryGetValue((page, user), out var last) && now - last < Window)
            {
                return false;
            }
            counted[(page, user)] = now;
            if (counted.Count > 10_000)
            {
                foreach (var key in counted.Where(c => now - c.Value >= Window).Select(c => c.Key).ToList())
                {
                    counted.Remove(key);
                }
            }
        }
        var day = DateOnly.FromDateTime(Clock.LocalNow);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE kb_pages SET "Views" = "Views" + 1 WHERE "Id" = {page}""", ct);
        if (updated == 0)
        {
            return false;
        }
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO kb_page_views ("PageId", "Day", "Views") VALUES ({page}, {day}, 1)
            ON CONFLICT ("PageId", "Day") DO UPDATE SET "Views" = kb_page_views."Views" + 1
            """, ct);
        return true;
    }
}
