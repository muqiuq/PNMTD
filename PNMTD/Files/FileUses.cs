using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Files;

/// <summary>
/// Which files a page or a location uses (docs/locations-knowledge-plan.md §5.5): every save records the files its Markdown
/// references (<c>MarkdownText.Read(…).FileIds</c>). A use is never removed while its page or location exists, since their
/// revisions and history keep the reference; it goes with them.
/// </summary>
public static class FileUses
{
    /// <summary>
    /// Adds the uses that are missing, for exactly one of <paramref name="pageId"/> and <paramref name="locationId"/>, and
    /// returns the files newly used (a revision's <i>files added</i>). Unknown ids are ignored. A file marked deleted is
    /// still on disk (FileCleanupTask deletes its row first), so one referenced again is live again: at once, by a
    /// conditional update, so the cleanup either sees it live or has deleted its row already, in which case it is
    /// unknown here too. Doesn't save the uses: the caller's SaveChanges writes them with the page or value.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> RecordAsync(PnmtdDbContext db, IEnumerable<Guid> fileIds, Guid? pageId, Guid? locationId,
        CancellationToken ct)
    {
        if ((pageId is null) == (locationId is null))
        {
            throw new ArgumentException("A use belongs to exactly one page or location.");
        }
        var ids = fileIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }
        var files = await db.Files.AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        var used = pageId is { } page
            ? await db.FileUses.Where(u => u.PageId == page && ids.Contains(u.FileId)).Select(u => u.FileId).ToListAsync(ct)
            : await db.FileUses.Where(u => u.LocationId == locationId && ids.Contains(u.FileId)).Select(u => u.FileId).ToListAsync(ct);
        // Uses added earlier in the same unit of work, not saved yet.
        used.AddRange(db.FileUses.Local.Where(u => u.PageId == pageId && u.LocationId == locationId).Select(u => u.FileId));
        var now = Clock.UtcNow;
        var added = new List<Guid>();
        foreach (var file in ids.Select(id => files.FirstOrDefault(f => f.Id == id)).OfType<FileEntity>())
        {
            if (file.DeletedAt is not null)
            {
                // One live file per content (§5.5): revived unless another live row holds the same bytes, which it then stays.
                var id = file.Id;
                await db.Files.Where(f => f.Id == id && f.DeletedAt != null && !db.Files.Any(o => o.Sha256 == f.Sha256 && o.Id != f.Id && o.DeletedAt == null))
                    .ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, (DateTime?)null), ct);
                if (!await db.Files.AnyAsync(f => f.Id == id, ct))
                {
                    // The cleanup took it in the meantime.
                    continue;
                }
            }
            if (!used.Contains(file.Id))
            {
                db.FileUses.Add(new FileUseEntity { Id = Guid.NewGuid(), FileId = file.Id, PageId = pageId, LocationId = locationId, FirstUsedAt = now });
                used.Add(file.Id);
                added.Add(file.Id);
            }
        }
        return added;
    }
}
