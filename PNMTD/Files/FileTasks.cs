using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Files;

/// <summary>
/// Daily (docs/locations-knowledge-plan.md §5.5): an upload no page or location used for KnowledgeBase:UnusedFileHours (a
/// paste that was never saved) is marked deleted; a file marked deleted KnowledgeBase:DeletedFileDays ago leaves the
/// database and the disk; leftovers in <c>.tmp/</c> go.
/// <para>
/// Saves and uploads revive files at any moment, so every step decides in the database, row by row: a file leaves only
/// if its row could still be deleted as due and unused, and only then its bytes. A file marked deleted while a save
/// started to use it is live again at the next run.
/// </para>
/// </summary>
public sealed class FileCleanupTask(IDbContextFactory<PnmtdDbContext> dbs, FileStore store, IOptionsMonitor<KnowledgeBaseOptions> options,
    ILogger<FileCleanupTask> logger) : BackgroundService
{
    /// <summary>A temporary file this old is no upload in progress.</summary>
    public static readonly TimeSpan TempAge = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "The files' cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<(int Marked, int Revived, int Removed, int Temporary)> RunAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        // Marked while a save began to use it: in use, so live again (unless another live row holds the same bytes).
        var revived = await db.Files
            .Where(f => f.DeletedAt != null && db.FileUses.Any(u => u.FileId == f.Id)
                && !db.Files.Any(o => o.Sha256 == f.Sha256 && o.Id != f.Id && o.DeletedAt == null))
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, (DateTime?)null), ct);
        var unusedSince = now.AddHours(-Math.Max(1, settings.UnusedFileHours));
        var marked = await db.Files.Where(f => f.DeletedAt == null && f.CreatedAt < unusedSince && !db.FileUses.Any(u => u.FileId == f.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, now), ct);
        var deletedBefore = now.AddDays(-Math.Max(0, settings.DeletedFileDays));
        var due = await db.Files.AsNoTracking().Where(f => f.DeletedAt != null && f.DeletedAt < deletedBefore).ToListAsync(ct);
        var removed = 0;
        foreach (var file in due)
        {
            if (await RemoveIfStillDueAsync(db, file, deletedBefore, ct))
            {
                removed++;
            }
        }
        var temporary = 0;
        var temp = Path.Combine(store.Root, FileStore.TempFolder);
        if (Directory.Exists(temp))
        {
            foreach (var leftover in new DirectoryInfo(temp).EnumerateFiles().Where(f => now - f.LastWriteTimeUtc > TempAge))
            {
                try
                {
                    leftover.Delete();
                    temporary++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Could not remove {Path}: {Error}", leftover.FullName, ex.Message);
                }
            }
        }
        if (marked + revived + removed + temporary > 0)
        {
            logger.LogInformation(
                "Files' cleanup: {Marked} unused uploads marked deleted, {Revived} in use again, {Removed} files left the disk, {Temporary} temporary files removed",
                marked, revived, removed, temporary);
        }
        return (marked, revived, removed, temporary);
    }

    /// <summary>
    /// The row first, and only if it is still due: marked deleted before <paramref name="deletedBefore"/> and used by nothing,
    /// in one statement; a revive or a new use since the list was read keeps the file. Then the bytes. Should they fail to
    /// go, they are an unknown file, never a lost one.
    /// </summary>
    internal async Task<bool> RemoveIfStillDueAsync(PnmtdDbContext db, FileEntity file, DateTime deletedBefore, CancellationToken ct)
    {
        var deleted = await db.Files
            .Where(f => f.Id == file.Id && f.DeletedAt != null && f.DeletedAt < deletedBefore && !db.FileUses.Any(u => u.FileId == f.Id))
            .ExecuteDeleteAsync(ct);
        if (deleted != 1)
        {
            return false;
        }
        try
        {
            store.RemoveFromDisk(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("File {Id} ({Path}) left the database but not the disk: {Error}", file.Id, file.Path, ex.Message);
        }
        return true;
    }
}

/// <summary>The last check's report, for the Admin's overview (in memory: the next check after a restart fills it).</summary>
public sealed class FileCheckState
{
    public FileCheckReport? Last { get; set; }
}

/// <summary>
/// The check of the files folder (docs/locations-knowledge-plan.md §5.5), on the system sensor <i>File storage</i> (key
/// <c>files</c>): at start, then daily, every file the database names must be there with its size, and weekly (and the
/// first daily run after a start) with its SHA-256; one marked deleted may be gone already. 428 FILE_STORAGE_PROBLEM when
/// the folder is missing or not writable, its disk has less than KnowledgeBase:MinFreeMb free, or files are missing or
/// changed; 220 once all is well again.
/// Files on disk that no row names are listed and counted, never deleted, and alone are no problem: someone may have
/// put them there on purpose.
/// </summary>
public sealed class FileCheckTask(IDbContextFactory<PnmtdDbContext> dbs, FileStore store, FileAdmin admin, FileCheckState state, SystemSensors sensors,
    ILogger<FileCheckTask> logger) : BackgroundService
{
    public static readonly TimeSpan HashInterval = TimeSpan.FromDays(7);

    /// <summary>Findings named in the sensor's message at most; the overview lists them all.</summary>
    private const int Named = 5;

    private DateTime lastHashed = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // At start: the folder and its files, quickly; their contents with the next run.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        await SafelyAsync(false, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SafelyAsync(Clock.UtcNow - lastHashed >= HashInterval, stoppingToken);
        }
    }

    private async Task SafelyAsync(bool hashes, CancellationToken ct)
    {
        try
        {
            await RunAsync(hashes, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "The files' check failed");
        }
    }

    public async Task<FileCheckReport> RunAsync(bool hashes, CancellationToken ct)
    {
        var storage = admin.Storage();
        var problems = new List<string>();
        var missing = new List<FileCheckFinding>();
        var changed = new List<FileCheckFinding>();
        var unknown = 0;
        var count = 0;
        if (storage.Problem is { } folder)
        {
            problems.Add(folder);
        }
        else
        {
            if (storage.FreeBytes is { } free && free < storage.MinFreeBytes)
            {
                problems.Add($"Only {Size(free)} free on the files folder's disk (KnowledgeBase:MinFreeMb {storage.MinFreeBytes / (1024 * 1024)}).");
            }
            await using var db = await dbs.CreateDbContextAsync(ct);
            var files = await db.Files.AsNoTracking().OrderBy(f => f.CreatedAt).ToListAsync(ct);
            count = files.Count;
            foreach (var file in files)
            {
                var path = store.Locate(file.Path);
                if (path is null && file.DeletedAt is not null && new FileInfo(Path.Combine(store.Root, file.Path)).LinkTarget is null)
                {
                    // Marked deleted and gone already: after a restore (the backup's tar may lack it), or an Admin's delete in
                    // between. The cleanup removes the row in its time.
                    continue;
                }
                if (path is null)
                {
                    // Not there, or not to be read: a symbolic link on its way, which PNMTD doesn't follow.
                    var link = new FileInfo(Path.Combine(store.Root, file.Path)).LinkTarget is not null;
                    (link ? changed : missing).Add(new FileCheckFinding(file.Id, file.Name, file.Path, link ? "replaced by a symbolic link" : "missing"));
                    continue;
                }
                var length = new FileInfo(path).Length;
                if (length != file.Size)
                {
                    changed.Add(new FileCheckFinding(file.Id, file.Name, file.Path, $"{length} bytes instead of {file.Size}"));
                }
                else if (hashes && await HashAsync(path, ct) != file.Sha256)
                {
                    changed.Add(new FileCheckFinding(file.Id, file.Name, file.Path, "its content changed"));
                }
            }
            unknown = (await admin.UnknownAsync(db, ct)).Count;
            if (hashes)
            {
                lastHashed = Clock.UtcNow;
            }
        }
        if (missing.Count > 0)
        {
            problems.Add($"{missing.Count} {Files(missing.Count)} missing ({Names(missing)}).");
        }
        if (changed.Count > 0)
        {
            problems.Add($"{changed.Count} {Files(changed.Count)} changed ({Names(changed)}).");
        }
        var report = new FileCheckReport(Clock.UtcNow, hashes, problems.Count > 0, missing, changed, unknown);
        state.Last = report;
        var space = storage.FreeBytes is { } bytes ? $" {Size(bytes)} free." : "";
        var stray = unknown > 0 ? $" {unknown} unknown {Files(unknown)} in the folder." : "";
        var (code, message) = problems.Count > 0
            ? (PNMTStatusCodes.FILE_STORAGE_PROBLEM, $"File storage: {string.Join(" ", problems)}{(storage.Problem is null ? space : "")}{stray}")
            : (PNMTStatusCodes.SYSTEM_OK, $"File storage fine: {count} {Files(count)}{(hashes ? ", contents checked" : "")}.{space}{stray}");
        await sensors.WriteAsync(SystemSensorKeys.Files, code, message, ct);
        if (problems.Count > 0)
        {
            logger.LogWarning("{Message}", message);
        }
        return report;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static string Files(int n) => n == 1 ? "file" : "files";

    private static string Names(List<FileCheckFinding> findings) =>
        string.Join(", ", findings.Take(Named).Select(f => f.Name)) + (findings.Count > Named ? $" and {findings.Count - Named} more" : "");

    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1L << 20):0} MB";
}
