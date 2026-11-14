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
    /// still on disk (FileCleanupTask deletes its row with it), so one referenced again is no longer deleted. Doesn't
    /// save: the caller's SaveChanges writes the uses with the page or value.
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
        var files = await db.Files.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        var used = pageId is { } page
            ? await db.FileUses.Where(u => u.PageId == page && ids.Contains(u.FileId)).Select(u => u.FileId).ToListAsync(ct)
            : await db.FileUses.Where(u => u.LocationId == locationId && ids.Contains(u.FileId)).Select(u => u.FileId).ToListAsync(ct);
        // Uses added earlier in the same unit of work, not saved yet.
        used.AddRange(db.FileUses.Local.Where(u => u.PageId == pageId && u.LocationId == locationId).Select(u => u.FileId));
        var now = Clock.UtcNow;
        var added = new List<Guid>();
        foreach (var file in ids.Select(id => files.FirstOrDefault(f => f.Id == id)).OfType<FileEntity>())
        {
            if (file.DeletedAt is not null && !await db.Files.AnyAsync(f => f.Sha256 == file.Sha256 && f.Id != file.Id && f.DeletedAt == null, ct))
            {
                // One live file per content (§5.5); FileStore revives a deleted one on the same upload, so this is the rule.
                file.DeletedAt = null;
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
