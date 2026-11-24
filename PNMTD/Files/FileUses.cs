using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Files;

/// <summary>
/// What uses a file: a page, a location, a responsibility's description (docs/responsibilities-plan.md §10.4), or a board's image
/// element (docs/board-messages-plan.md §9.4).
/// </summary>
public readonly record struct FileOwner(FileUserKind Kind, Guid Id)
{
    public static FileOwner Page(Guid id) => new(FileUserKind.Page, id);

    public static FileOwner Location(Guid id) => new(FileUserKind.Location, id);

    public static FileOwner Responsibility(Guid id) => new(FileUserKind.Responsibility, id);

    public static FileOwner Board(Guid id) => new(FileUserKind.Board, id);
}

/// <summary>
/// Which files a page, a location or a responsibility uses (docs/locations-knowledge-plan.md §5.5): every save records the
/// files its Markdown references (<c>MarkdownText.Read(…).FileIds</c>). A use is never removed while its owner exists, since
/// their revisions and history keep the reference; it goes with them.
/// </summary>
public static class FileUses
{
    /// <summary>
    /// Adds the uses that are missing, for <paramref name="owner"/>, and returns the files newly used (a revision's <i>files
    /// added</i>). Unknown ids are ignored. A file marked deleted is still on disk (FileCleanupTask deletes its row first), so
    /// one referenced again is live again: at once, by a conditional update, so the cleanup either sees it live or has deleted
    /// its row already, in which case it is unknown here too. Doesn't save the uses: the caller's SaveChanges writes them with
    /// the page, value or description.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> RecordAsync(PnmtdDbContext db, IEnumerable<Guid> fileIds, FileOwner owner, CancellationToken ct)
    {
        var ids = fileIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }
        var files = await db.Files.AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        var ownerId = owner.Id;
        var used = owner.Kind switch
        {
            FileUserKind.Page => await db.FileUses.Where(u => u.PageId == ownerId && ids.Contains(u.FileId)).Select(u => u.FileId).ToListAsync(ct),
            FileUserKind.Location => await db.FileUses.Where(u => u.LocationId == ownerId && ids.Contains(u.FileId)).Select(u => u.FileId).ToListAsync(ct),
            FileUserKind.Board => await db.FileUses.Where(u => u.BoardId == ownerId && ids.Contains(u.FileId)).Select(u => u.FileId).ToListAsync(ct),
            _ => await db.FileUses.Where(u => u.ResponsibilityId == ownerId && ids.Contains(u.FileId)).Select(u => u.FileId).ToListAsync(ct),
        };
        var (pageId, locationId, responsibilityId, boardId) = owner.Kind switch
        {
            FileUserKind.Page => ((Guid?)ownerId, (Guid?)null, (Guid?)null, (Guid?)null),
            FileUserKind.Location => (null, ownerId, null, null),
            FileUserKind.Board => (null, null, null, ownerId),
            _ => (null, null, ownerId, null),
        };
        // Uses added earlier in the same unit of work, not saved yet.
        used.AddRange(db.FileUses.Local.Where(u => u.PageId == pageId && u.LocationId == locationId && u.ResponsibilityId == responsibilityId
            && u.BoardId == boardId).Select(u => u.FileId));
        var now = Clock.UtcNow;
        var added = new List<Guid>();
        foreach (var file in ids.Select(id => files.FirstOrDefault(f => f.Id == id)).OfType<FileEntity>())
        {
            if (file.DeletedAt is not null)
            {
                // One live file per content (§5.5): revived unless another live row holds the same bytes, which it then stays.
                var fileId = file.Id;
                await db.Files.Where(f => f.Id == fileId && f.DeletedAt != null && !db.Files.Any(o => o.Sha256 == f.Sha256 && o.Id != f.Id && o.DeletedAt == null))
                    .ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, (DateTime?)null), ct);
                if (!await db.Files.AnyAsync(f => f.Id == fileId, ct))
                {
                    // The cleanup took it in the meantime.
                    continue;
                }
            }
            if (!used.Contains(file.Id))
            {
                db.FileUses.Add(new FileUseEntity
                {
                    Id = Guid.NewGuid(), FileId = file.Id, PageId = pageId, LocationId = locationId, ResponsibilityId = responsibilityId,
                    BoardId = boardId, FirstUsedAt = now,
                });
                used.Add(file.Id);
                added.Add(file.Id);
            }
        }
        return added;
    }

    /// <summary>
    /// A board's uses made to match the images its layout names (docs/board-messages-plan.md §9.4): the missing ones added (reviving a
    /// file marked deleted), the others removed, so that a file no board or page uses any more goes the usual way. Doesn't save.
    /// </summary>
    public static async Task SyncBoardAsync(PnmtdDbContext db, Guid boardId, IEnumerable<Guid> fileIds, CancellationToken ct)
    {
        var ids = fileIds.Distinct().ToList();
        var stale = await db.FileUses.Where(u => u.BoardId == boardId && !ids.Contains(u.FileId)).ToListAsync(ct);
        db.FileUses.RemoveRange(stale);
        await RecordAsync(db, ids, FileOwner.Board(boardId), ct);
    }
}
