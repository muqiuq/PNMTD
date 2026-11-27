using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Files;

/// <summary>
/// What the Admin's <i>Knowledge base → Files</i> shows (docs/locations-knowledge-plan.md §10.6): totals per kind, the
/// folder and its disk, the largest files with where they are used, unused uploads and files waiting to leave the disk,
/// and the files on disk that no row names. Also the folder's inventory for the check (FileCheckTask).
/// </summary>
public sealed class FileAdmin(IDbContextFactory<PnmtdDbContext> dbs, FileStore store, FileCheckState check, IOptionsMonitor<KnowledgeBaseOptions> options,
    IHttpContextAccessor accessor, IConfiguration configuration)
{
    /// <summary>Unknown files listed at most.</summary>
    public const int MaxUnknown = 500;

    public async Task<FilesOverview> OverviewAsync(FileQuery query, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var totals = await db.Files.AsNoTracking().Where(f => f.DeletedAt == null).GroupBy(f => f.Kind)
            .Select(g => new FileKindTotal(g.Key, g.Count(), g.Sum(f => f.Size))).ToListAsync(ct);
        foreach (var kind in Enum.GetValues<FileKind>().Where(k => totals.All(t => t.Kind != k)))
        {
            totals.Add(new FileKindTotal(kind, 0, 0));
        }

        var files = db.Files.AsNoTracking();
        if (query.Kind is { } only)
        {
            files = files.Where(f => f.Kind == only);
        }
        if (query.Unused)
        {
            files = files.Where(f => !db.FileUses.Any(u => u.FileId == f.Id));
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            files = files.Where(f => EF.Functions.ILike(f.Name, pattern, @"\"));
        }
        var (field, descending) = Paging.Sort(query.Sort, "-size", "size", "created", "name");
        files = (field, descending) switch
        {
            ("size", true) => files.OrderByDescending(f => f.Size).ThenBy(f => f.Id),
            ("size", false) => files.OrderBy(f => f.Size).ThenBy(f => f.Id),
            ("created", true) => files.OrderByDescending(f => f.CreatedAt).ThenBy(f => f.Id),
            ("created", false) => files.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id),
            (_, true) => files.OrderByDescending(f => f.Name).ThenBy(f => f.Id),
            _ => files.OrderBy(f => f.Name).ThenBy(f => f.Id),
        };
        var page = await Paging.PageAsync(files, query, ct);
        var ids = page.Items.Select(f => f.Id).ToList();
        var pages = await (from u in db.FileUses where ids.Contains(u.FileId) && u.PageId != null
                           join p in db.KbPages on u.PageId equals p.Id
                           select new { u.FileId, Use = new FileUseRef(FileUserKind.Page, p.Id, p.Title, p.Number) }).ToListAsync(ct);
        var locations = await (from u in db.FileUses where ids.Contains(u.FileId) && u.LocationId != null
                               join l in db.Locations on u.LocationId equals l.Id
                               select new { u.FileId, Use = new FileUseRef(FileUserKind.Location, l.Id, l.Name, null) }).ToListAsync(ct);
        var responsibilities = await (from u in db.FileUses where ids.Contains(u.FileId) && u.ResponsibilityId != null
                                      join r in db.Responsibilities on u.ResponsibilityId equals r.Id
                                      select new { u.FileId, Use = new FileUseRef(FileUserKind.Responsibility, r.Id, r.Name, null) }).ToListAsync(ct);
        var boards = await (from u in db.FileUses where ids.Contains(u.FileId) && u.BoardId != null
                            join b in db.Boards on u.BoardId equals b.Id
                            select new { u.FileId, Use = new FileUseRef(FileUserKind.Board, b.Id, b.Name, null) }).ToListAsync(ct);
        // A licence's or service's document (docs/licences-plan.md §7.3): its number for whoever may read the item, while the feature is on.
        var named = Licenses.LicenseSetup.Enabled(configuration)
            && accessor.HttpContext?.User is { } user && Auth.Rights.RightsExtensions.Has(user, Auth.Rights.Area.Licenses, Auth.Rights.Level.View);
        var off = !Licenses.LicenseSetup.Enabled(configuration);
        var licenses = (await (from u in db.FileUses where ids.Contains(u.FileId) && u.LicenseId != null
                               join l in db.Licenses on u.LicenseId equals l.Id
                               select new { u.FileId, l.Id, l.Type, l.Number }).ToListAsync(ct))
            .Select(x => new { x.FileId, Use = new FileUseRef(FileUserKind.License, x.Id,
                off ? "Licences & services (turned off)" : named ? PNMTD.Licenses.LicenseWords.Number(x.Type, x.Number) : "a licence or service",
                named ? x.Number : null) }).ToList();
        var uses = pages.Concat(locations).Concat(responsibilities).Concat(boards).Concat(licenses).ToLookup(x => x.FileId, x => x.Use);
        var days = options.CurrentValue.DeletedFileDays;
        var items = page.Items.Select(f => new FileAdminItem(FileNames.Item(f), Clock.AsUtc(f.CreatedAt), f.CreatedByName, f.DeletedAt,
            f.DeletedAt?.AddDays(days), uses[f.Id].OrderBy(u => u.Kind).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList())).ToList();

        var unknown = query.Unknown ? (await UnknownAsync(db, ct)).Take(MaxUnknown).ToList() : null;
        return new FilesOverview(totals.OrderBy(t => t.Kind).ToList(), Storage(), check.Last, new Paged<FileAdminItem>(items, page.Page, page.PageSize, page.TotalCount), unknown);
    }

    public FileStorageInfo Storage()
    {
        var root = store.Root;
        var problem = store.Problem();
        long? free = null, total = null;
        if (Directory.Exists(root))
        {
            try
            {
                var drive = new DriveInfo(root);
                free = drive.AvailableFreeSpace;
                total = drive.TotalSize;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Some systems don't say; the sensor then checks nothing about space.
            }
        }
        return new FileStorageInfo(root, problem is null, problem, free, total, options.CurrentValue.MinFreeMb * 1024 * 1024);
    }

    /// <summary>Every file in the folder, relative with <c>/</c>, but <c>.tmp/</c>'s. Symbolic links are listed, never followed.</summary>
    public IEnumerable<(string Path, FileInfo Info)> OnDisk()
    {
        var root = store.Root.TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            yield break;
        }
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = folder.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');
                if (entry is DirectoryInfo directory && entry.LinkTarget is null)
                {
                    if (!(folder.FullName == root && entry.Name == FileStore.TempFolder))
                    {
                        pending.Push(directory);
                    }
                }
                else if (entry is FileInfo file)
                {
                    yield return (relative, file);
                }
            }
        }
    }

    /// <summary>The files on disk that no row names, newest first.</summary>
    public async Task<List<UnknownFileItem>> UnknownAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var known = (await db.Files.AsNoTracking().Select(f => f.Path).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return OnDisk().Where(f => !known.Contains(f.Path))
            .Select(f => new UnknownFileItem(f.Path, f.Info.LinkTarget is null ? f.Info.Length : 0, f.Info.LastWriteTimeUtc))
            .OrderByDescending(f => f.ModifiedAt).ToList();
    }
}
