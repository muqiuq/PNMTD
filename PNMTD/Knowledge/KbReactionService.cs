using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Notifications;
using PNMTD.Services.Domain;

namespace PNMTD.Knowledge;

/// <summary>
/// Likes and <i>Used to solve a problem</i> (docs/knowledge-base-extensions-plan.md §9). A like is once per person, undone by liking
/// again; a use is an event, recorded again by the same person after <c>KnowledgeBase:UseRepeatHours</c>, with an optional note,
/// undone within <see cref="UndoWindow"/>. The page's counters move with the rows in the same transaction, by what the insert or
/// delete did, as views do: no revision, no change to <c>UpdatedAt</c>. Each like and use tells the page's authors
/// (<see cref="UserNotifications"/>); undoing it cancels what hasn't gone out. An archived page takes neither.
/// </summary>
public sealed class KbReactionService(PnmtdDbContext db, IHttpContextAccessor accessor, IOptionsMonitor<KnowledgeBaseOptions> kb, UserNotifications notifications)
{
    public const string UsedRecently = "used_recently";

    /// <summary>A use can be undone this long.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromMinutes(10);

    /// <summary>A use's note can be written or changed this long.</summary>
    public static readonly TimeSpan NoteWindow = TimeSpan.FromHours(24);

    public const int MaxNote = 200;

    private KbActor Actor => KbActor.Of(accessor);

    private TimeSpan RepeatAfter => TimeSpan.FromHours(Math.Max(0, kb.CurrentValue.UseRepeatHours));

    // --- reading -------------------------------------------------------------------------------------------------------

    /// <summary>The page's likes and uses as <paramref name="me"/> sees them.</summary>
    public Task<KbReactions> OfAsync(Guid pageId, Guid? me, CancellationToken ct) => OfAsync(db, pageId, me, RepeatAfter, ct);

    /// <summary>The same, for a reader without this service: a page's detail needs no notifications.</summary>
    public static async Task<KbReactions> OfAsync(PnmtdDbContext db, Guid pageId, Guid? me, TimeSpan repeatAfter, CancellationToken ct)
    {
        var counts = await db.KbPages.AsNoTracking().Where(p => p.Id == pageId).Select(p => new { p.Likes, p.Uses, p.LastUsedAt }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Page");
        var liked = me is { } user && await db.KbPageLikes.AnyAsync(l => l.PageId == pageId && l.UserId == user, ct);
        var people = await db.KbPageUses.Where(u => u.PageId == pageId).Select(u => u.UserId).Distinct().CountAsync(ct);
        var now = Clock.UtcNow;
        var recent = await db.KbPageUses.AsNoTracking().Where(u => u.PageId == pageId).OrderByDescending(u => u.At).Take(5).ToListAsync(ct);
        var mine = me is { } own
            ? await db.KbPageUses.AsNoTracking().Where(u => u.PageId == pageId && u.UserId == own).OrderByDescending(u => u.At).FirstOrDefaultAsync(ct)
            : null;
        DateTime? again = mine is not null && Clock.AsUtc(mine.At) + repeatAfter > now ? Clock.AsUtc(mine.At) + repeatAfter : null;
        return new KbReactions(counts.Likes, liked, counts.Uses, people, counts.LastUsedAt is { } last ? Clock.AsUtc(last) : null,
            mine is null ? null : Item(mine, me, now), again, recent.Select(u => Item(u, me, now)).ToList());
    }

    private static KbUseItem Item(KbPageUseEntity u, Guid? me, DateTime now) =>
        new(u.Id, u.UserName, Clock.AsUtc(u.At), u.Note, u.UserId == me, u.UserId == me && now - Clock.AsUtc(u.At) <= UndoWindow);

    public async Task<IReadOnlyList<KbLikeItem>> LikesAsync(Guid pageId, CancellationToken ct)
    {
        await ExistsAsync(pageId, ct);
        return await db.KbPageLikes.AsNoTracking().Where(l => l.PageId == pageId).OrderByDescending(l => l.At)
            .Select(l => new KbLikeItem(l.UserName, l.At)).ToListAsync(ct);
    }

    public async Task<Paged<KbUseItem>> UsesAsync(Guid pageId, PageQuery query, CancellationToken ct)
    {
        await ExistsAsync(pageId, ct);
        var me = Actor.Id;
        var now = Clock.UtcNow;
        var (number, size) = Paging.Normalize(query);
        var uses = db.KbPageUses.AsNoTracking().Where(u => u.PageId == pageId);
        var total = await uses.CountAsync(ct);
        var rows = await uses.OrderByDescending(u => u.At).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Paged<KbUseItem>(rows.Select(u => Item(u, me, now)).ToList(), number, size, total);
    }

    // --- likes ---------------------------------------------------------------------------------------------------------

    /// <summary>Likes or unlikes the page for the person; only a change moves the counter and tells anyone.</summary>
    public async Task<KbReactions> LikeAsync(Guid pageId, bool liked, CancellationToken ct)
    {
        var actor = Actor;
        var user = actor.Id ?? throw ApiException.Forbidden("Only a person likes a page.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var page = await PageAsync(pageId, ct);
        if (liked)
        {
            var id = Guid.CreateVersion7();
            var now = Clock.UtcNow;
            var added = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO kb_page_likes ("Id", "PageId", "UserId", "UserName", "At") VALUES ({id}, {pageId}, {user}, {actor.Name}, {now})
                ON CONFLICT ("PageId", "UserId") DO NOTHING
                """, ct);
            if (added == 1)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE kb_pages SET "Likes" = "Likes" + 1 WHERE "Id" = {pageId}""", ct);
                await TellAsync(UserNotificationKinds.KbLiked, page, id, actor, null, ct);
            }
        }
        else
        {
            var removed = await db.KbPageLikes.Where(l => l.PageId == pageId && l.UserId == user).Select(l => l.Id).ToListAsync(ct);
            if (removed.Count > 0)
            {
                await db.KbPageLikes.Where(l => removed.Contains(l.Id)).ExecuteDeleteAsync(ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE kb_pages SET "Likes" = "Likes" - {removed.Count} WHERE "Id" = {pageId}""", ct);
                foreach (var id in removed)
                {
                    await UserNotifications.CancelAsync(db, id, ct);
                }
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await OfAsync(pageId, user, ct);
    }

    // --- uses ----------------------------------------------------------------------------------------------------------

    /// <summary>A use, now; refused with 409 <c>used_recently</c> within the repeat window of the person's last.</summary>
    public async Task<KbReactions> UseAsync(Guid pageId, KbUseRequest request, CancellationToken ct)
    {
        var actor = Actor;
        var user = actor.Id ?? throw ApiException.Forbidden("Only a person uses a page.");
        var note = Note(request.Note);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The page's row lock serialises one person's clicks: two can't both pass the repeat check.
        var page = await PageAsync(pageId, ct, lockRow: true);
        var now = Clock.UtcNow;
        var last = await db.KbPageUses.AsNoTracking().Where(u => u.PageId == pageId && u.UserId == user).MaxAsync(u => (DateTime?)u.At, ct);
        if (last is { } at && Clock.AsUtc(at) + RepeatAfter > now)
        {
            var again = Clock.AsUtc(at) + RepeatAfter;
            throw new ApiException(StatusCodes.Status409Conflict, UsedRecently,
                $"You recorded a use of KB-{page.Number} at {Clock.ToLocal(at):HH:mm}; another one after {Clock.ToLocal(again):HH:mm}.",
                extensions: new Dictionary<string, object?> { ["at"] = Clock.AsUtc(at), ["again"] = again });
        }
        var use = new KbPageUseEntity { Id = Guid.CreateVersion7(), PageId = pageId, UserId = user, UserName = Cut(actor.Name, 200), At = now, Note = note };
        db.KbPageUses.Add(use);
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE kb_pages SET "Uses" = "Uses" + 1, "LastUsedAt" = {now} WHERE "Id" = {pageId}""", ct);
        await TellAsync(UserNotificationKinds.KbUsed, page, use.Id, actor, note, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await OfAsync(pageId, user, ct);
    }

    /// <summary>One's own use's note, within a day; a notification that hasn't gone out takes it too.</summary>
    public async Task<KbReactions> NoteAsync(Guid pageId, Guid useId, KbUseRequest request, CancellationToken ct)
    {
        var use = await OwnUseAsync(pageId, useId, NoteWindow, "A note can be added within a day of the use.", ct);
        use.Note = Note(request.Note);
        await db.UserNotifications.Where(n => n.SourceId == use.Id && n.State == UserNotificationState.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.Detail, use.Note), ct);
        await db.SaveChangesAsync(ct);
        return await OfAsync(pageId, use.UserId, ct);
    }

    /// <summary>One's own use undone, within <see cref="UndoWindow"/>: the counter, the last use, and its notifications.</summary>
    public async Task<KbReactions> UndoUseAsync(Guid pageId, Guid useId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PageAsync(pageId, ct, lockRow: true);
        var use = await OwnUseAsync(pageId, useId, UndoWindow, "A use can be undone within 10 minutes.", ct);
        db.KbPageUses.Remove(use);
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE kb_pages SET "Uses" = "Uses" - 1, "LastUsedAt" = (SELECT max("At") FROM kb_page_uses WHERE "PageId" = {pageId}) WHERE "Id" = {pageId}
            """, ct);
        await UserNotifications.CancelAsync(db, use.Id, ct);
        await tx.CommitAsync(ct);
        return await OfAsync(pageId, use.UserId, ct);
    }

    private async Task<KbPageUseEntity> OwnUseAsync(Guid pageId, Guid useId, TimeSpan window, string tooLate, CancellationToken ct)
    {
        var user = Actor.Id ?? throw ApiException.Forbidden("Only a person changes a use.");
        var use = await db.KbPageUses.SingleOrDefaultAsync(u => u.Id == useId && u.PageId == pageId, ct) ?? throw ApiException.NotFound("Use");
        if (use.UserId != user)
        {
            throw ApiException.Forbidden("Only your own use.");
        }
        if (Clock.UtcNow - Clock.AsUtc(use.At) > window)
        {
            throw ApiException.Forbidden(tooLate);
        }
        return use;
    }

    // --- helpers -------------------------------------------------------------------------------------------------------

    private sealed record PageFacts(Guid Id, int Number);

    /// <summary>The page, not archived; with its row locked until the transaction ends, when asked.</summary>
    private async Task<PageFacts> PageAsync(Guid pageId, CancellationToken ct, bool lockRow = false)
    {
        if (lockRow)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM kb_pages WHERE "Id" = {pageId} FOR UPDATE""", ct);
        }
        var page = await db.KbPages.AsNoTracking().Where(p => p.Id == pageId).Select(p => new { p.Id, p.Number, p.ArchivedAt }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Page");
        if (page.ArchivedAt is not null)
        {
            throw ApiException.Conflict(KbErrors.Archived, $"KB-{page.Number} is archived.");
        }
        return new PageFacts(page.Id, page.Number);
    }

    /// <summary>The page's authors (§10.1): who created it, and who made an edit to its title, summary or content.</summary>
    public static Task<List<Guid>> AuthorsAsync(PnmtdDbContext db, Guid pageId, CancellationToken ct) =>
        db.KbRevisions.AsNoTracking().Where(r => r.PageId == pageId && r.IsEdit && r.ByUserId != null).Select(r => r.ByUserId!.Value).Distinct().ToListAsync(ct);

    private async Task TellAsync(string kind, PageFacts page, Guid source, KbActor actor, string? note, CancellationToken ct) =>
        await notifications.EnqueueAsync(db, new UserNotice(kind, await AuthorsAsync(db, page.Id, ct), page.Id, $"{kind}:{page.Id}", source, actor.Id, actor.Name, note), ct);

    private async Task ExistsAsync(Guid pageId, CancellationToken ct)
    {
        if (!await db.KbPages.AnyAsync(p => p.Id == pageId, ct))
        {
            throw ApiException.NotFound("Page");
        }
    }

    private static string? Note(string? note)
    {
        var text = note?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (text.Length > MaxNote)
        {
            throw ApiException.Invalid("note", $"At most {MaxNote} characters.");
        }
        return text;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
