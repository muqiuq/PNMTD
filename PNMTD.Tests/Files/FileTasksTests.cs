using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Files;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Tests.Files;

/// <summary>
/// The files' cleanup and check (docs/locations-knowledge-plan.md §5.5, §12), on a database and a folder of their own, since
/// the check looks at every file the database names: unused uploads are marked deleted, deleted files stay 7 days, and a
/// missing, changed and unknown file give 428 on <i>File storage</i>, then 220 once fixed.
/// </summary>
[TestClass]
public sealed class FileTasksTests
{
    private const string Database = "pnmtd_test_files";
    private static TestApp app = null!;
    private static string root = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        TestDatabases.Fresh(Database).Dispose();
        root = Directory.CreateTempSubdirectory("pnmtd-files-tasks-").FullName;
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = TestDatabases.ConnectionString(Database),
            ["KnowledgeBase:FilesPath"] = root,
        });
    }

    [ClassCleanup]
    public static void Stop()
    {
        app.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private static async Task<FileItem> UploadAsync(string name, byte[] body)
    {
        using var response = await FileStoreTests.UploadAsync(app.Client(Roles.User), "attachment", name, body);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<FileItem>();
    }

    private static async Task<string> PathOfAsync(Guid id)
    {
        await using var db = app.NewDb();
        return Path.Combine(root, (await db.Files.SingleAsync(f => f.Id == id)).Path);
    }

    [TestMethod]
    public async Task Unused_uploads_are_marked_deleted_and_deleted_files_leave_the_disk_after_seven_days()
    {
        var unused = await UploadAsync("never-saved.txt", "a paste that was never saved"u8.ToArray());
        var used = await UploadAsync("used.txt", "on a location"u8.ToArray());
        var fresh = await UploadAsync("fresh.txt", "uploaded a minute ago"u8.ToArray());
        var sixDays = await UploadAsync("six-days.txt", "deleted six days ago"u8.ToArray());
        var eightDays = await UploadAsync("eight-days.txt", "deleted eight days ago"u8.ToArray());
        await using (var db = app.NewDb())
        {
            var location = new LocationEntity { Id = Guid.NewGuid(), Name = "Zurich", NormalizedName = "zurich", CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow };
            db.Locations.Add(location);
            await FileUses.RecordAsync(db, [used.Id], FileOwner.Location(location.Id), CancellationToken.None);
            await db.SaveChangesAsync();
            var old = Clock.UtcNow.AddHours(-25);
            await db.Files.Where(f => f.Id == unused.Id || f.Id == used.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.CreatedAt, old));
            await db.Files.Where(f => f.Id == sixDays.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.CreatedAt, old.AddDays(-7)).SetProperty(f => f.DeletedAt, Clock.UtcNow.AddDays(-6)));
            await db.Files.Where(f => f.Id == eightDays.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.CreatedAt, old.AddDays(-9)).SetProperty(f => f.DeletedAt, Clock.UtcNow.AddDays(-8)));
        }
        var tmp = Path.Combine(root, FileStore.TempFolder);
        var leftover = Path.Combine(tmp, "crashed.upload");
        var inProgress = Path.Combine(tmp, "in-progress.upload");
        await File.WriteAllTextAsync(leftover, "x");
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-2));
        await File.WriteAllTextAsync(inProgress, "x");
        var sixDaysPath = await PathOfAsync(sixDays.Id);
        var eightDaysPath = await PathOfAsync(eightDays.Id);

        var (marked, revived, removed, temporary) = await app.Services.GetRequiredService<FileCleanupTask>().RunAsync(CancellationToken.None);

        Assert.AreEqual((1, 0, 1, 1), (marked, revived, removed, temporary));
        await using var check = app.NewDb();
        Assert.IsNotNull((await check.Files.SingleAsync(f => f.Id == unused.Id)).DeletedAt, "unused for 24 hours");
        Assert.IsNull((await check.Files.SingleAsync(f => f.Id == used.Id)).DeletedAt);
        Assert.IsNull((await check.Files.SingleAsync(f => f.Id == fresh.Id)).DeletedAt);
        Assert.IsTrue(File.Exists(sixDaysPath), "a file marked deleted stays 7 days");
        Assert.IsFalse(File.Exists(eightDaysPath));
        Assert.IsFalse(await check.Files.AnyAsync(f => f.Id == eightDays.Id));
        Assert.IsFalse(File.Exists(leftover));
        Assert.IsTrue(File.Exists(inProgress), "an upload in progress");
        File.Delete(inProgress);

        // Uploaded again while still on disk: the same file, live again.
        Assert.AreEqual(sixDays.Id, (await UploadAsync("again.txt", "deleted six days ago"u8.ToArray())).Id);
        Assert.IsNull((await check.Files.AsNoTracking().SingleAsync(f => f.Id == sixDays.Id)).DeletedAt);
    }

    private static async Task<(FileItem File, LocationEntity Location)> DeletedLongAgoAsync(string name)
    {
        var file = await UploadAsync(name, System.Text.Encoding.UTF8.GetBytes($"{name}, deleted eight days ago"));
        await using var db = app.NewDb();
        var location = new LocationEntity { Id = Guid.NewGuid(), Name = name, NormalizedName = name, CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow };
        db.Locations.Add(location);
        await db.SaveChangesAsync();
        await db.Files.Where(f => f.Id == file.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, Clock.UtcNow.AddDays(-8)));
        return (file, location);
    }

    [TestMethod]
    public async Task A_file_revived_after_the_cleanup_read_its_list_keeps_its_bytes_and_uses()
    {
        var (file, location) = await DeletedLongAgoAsync("revived-in-between");
        var cleanup = app.Services.GetRequiredService<FileCleanupTask>();
        await using var db = app.NewDb();
        // The cleanup's view: due when it read the list.
        var stale = await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id);
        var path = await PathOfAsync(file.Id);
        // Then a save uses it again.
        await using (var save = app.NewDb())
        {
            CollectionAssert.AreEqual(new[] { file.Id }, (await FileUses.RecordAsync(save, [file.Id], FileOwner.Location(location.Id), CancellationToken.None)).ToArray());
            await save.SaveChangesAsync();
        }
        Assert.IsFalse(await cleanup.RemoveIfStillDueAsync(db, stale, Clock.UtcNow.AddDays(-7), CancellationToken.None));
        Assert.IsTrue(File.Exists(path));
        Assert.IsNull((await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id)).DeletedAt);
        Assert.AreEqual(1, await db.FileUses.CountAsync(u => u.FileId == file.Id));
    }

    [TestMethod]
    public async Task A_file_the_cleanup_removed_is_not_used_again_and_the_save_still_succeeds()
    {
        var (file, location) = await DeletedLongAgoAsync("removed-first");
        var path = await PathOfAsync(file.Id);
        await using var db = app.NewDb();
        var stale = await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id);
        Assert.IsTrue(await app.Services.GetRequiredService<FileCleanupTask>().RemoveIfStillDueAsync(db, stale, Clock.UtcNow.AddDays(-7), CancellationToken.None));
        Assert.IsFalse(File.Exists(path));
        await using var save = app.NewDb();
        Assert.AreEqual(0, (await FileUses.RecordAsync(save, [file.Id], FileOwner.Location(location.Id), CancellationToken.None)).Count);
        await save.SaveChangesAsync();
        Assert.IsFalse(await save.FileUses.AnyAsync(u => u.FileId == file.Id));
    }

    [TestMethod]
    public async Task A_file_marked_deleted_while_a_save_began_to_use_it_is_live_again_and_a_gone_one_is_no_finding()
    {
        var used = await UploadAsync("used-meanwhile.txt", "marked, then used"u8.ToArray());
        var gone = await UploadAsync("gone-after-restore.txt", "the backup's tar lacked it"u8.ToArray());
        await using (var db = app.NewDb())
        {
            var location = new LocationEntity { Id = Guid.NewGuid(), Name = "Race", NormalizedName = "race", CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow };
            db.Locations.Add(location);
            db.FileUses.Add(new FileUseEntity { Id = Guid.NewGuid(), FileId = used.Id, LocationId = location.Id, FirstUsedAt = Clock.UtcNow });
            await db.SaveChangesAsync();
            await db.Files.Where(f => f.Id == used.Id || f.Id == gone.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, Clock.UtcNow));
        }
        File.Delete(await PathOfAsync(gone.Id));

        var (_, revived, _, _) = await app.Services.GetRequiredService<FileCleanupTask>().RunAsync(CancellationToken.None);
        Assert.IsTrue(revived >= 1);
        await using (var db = app.NewDb())
        {
            Assert.IsNull((await db.Files.SingleAsync(f => f.Id == used.Id)).DeletedAt);
        }
        var report = await app.Services.GetRequiredService<FileCheckTask>().RunAsync(hashes: false, CancellationToken.None);
        Assert.IsFalse(report.Missing.Any(f => f.Id == gone.Id), "marked deleted and gone: expected, the cleanup removes the row");
    }

    [TestMethod]
    public async Task A_missing_changed_and_unknown_file_give_428_and_220_follows_when_fixed()
    {
        var missing = await UploadAsync("ups-manual.pdf", "%PDF the UPS manual"u8.ToArray());
        var resized = await UploadAsync("floor-plan.pdf", "%PDF the floor plan"u8.ToArray());
        var edited = await UploadAsync("contacts.txt", "Anna 044 123 45 67"u8.ToArray());
        var missingPath = await PathOfAsync(missing.Id);
        var resizedPath = await PathOfAsync(resized.Id);
        var editedPath = await PathOfAsync(edited.Id);
        File.Move(missingPath, missingPath + ".bak");
        await File.AppendAllTextAsync(resizedPath, " and more");
        await File.WriteAllTextAsync(editedPath, "Anna 044 765 43 21");
        var unknown = Path.Combine(root, "2026", "copied-by-hand.png");
        Directory.CreateDirectory(Path.GetDirectoryName(unknown)!);
        await File.WriteAllTextAsync(unknown, "by hand");
        var task = app.Services.GetRequiredService<FileCheckTask>();

        var daily = await task.RunAsync(hashes: false, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { missing.Id }, daily.Missing.Select(f => f.Id).ToArray());
        CollectionAssert.AreEqual(new[] { resized.Id }, daily.Changed.Select(f => f.Id).ToArray(), "the same size: only the weekly check sees it");
        var weekly = await task.RunAsync(hashes: true, CancellationToken.None);
        Assert.IsTrue(weekly.Problem);
        CollectionAssert.AreEquivalent(new[] { resized.Id, edited.Id }, weekly.Changed.Select(f => f.Id).ToArray());
        Assert.IsTrue(weekly.UnknownCount >= 2, "the hand-copied file and the moved one");
        var last = await LastEventAsync();
        Assert.AreEqual(PNMTStatusCodes.FILE_STORAGE_PROBLEM, last.Code);
        StringAssert.Contains(last.Message, "1 file missing (ups-manual.pdf)");
        StringAssert.Contains(last.Message, "2 files changed");
        var overview = await (await app.Client(Roles.Admin).GetAsync("/api/v1/files")).ReadAsync<FilesOverview>();
        Assert.AreEqual(1, overview.Check!.Missing.Count);

        File.Move(missingPath + ".bak", missingPath);
        await File.WriteAllTextAsync(resizedPath, "%PDF the floor plan");
        await File.WriteAllTextAsync(editedPath, "Anna 044 123 45 67");
        var fixedReport = await task.RunAsync(hashes: true, CancellationToken.None);
        Assert.IsFalse(fixedReport.Problem);
        last = await LastEventAsync();
        Assert.AreEqual(PNMTStatusCodes.SYSTEM_OK, last.Code, last.Message);
        StringAssert.Contains(last.Message, "1 unknown file in the folder", "listed, never a problem by itself");
        File.Delete(unknown);
    }

    [TestMethod]
    public async Task A_missing_folder_gives_428()
    {
        var gone = Path.Combine(root, "not-mounted");
        using var own = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = TestDatabases.ConnectionString(Database),
            ["KnowledgeBase:FilesPath"] = gone,
        });
        var report = await own.Services.GetRequiredService<FileCheckTask>().RunAsync(hashes: false, CancellationToken.None);
        Assert.IsTrue(report.Problem);
        var last = await LastEventAsync();
        Assert.AreEqual(PNMTStatusCodes.FILE_STORAGE_PROBLEM, last.Code);
        StringAssert.Contains(last.Message, "doesn't exist");
        using var upload = await FileStoreTests.UploadAsync(own.Client(Roles.User), "attachment", "x.txt", [1, 2, 3]);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, upload.StatusCode);
        StringAssert.Contains(await upload.Content.ReadAsStringAsync(), FileErrors.StorageUnavailable);
    }

    private static async Task<EventEntity> LastEventAsync()
    {
        var id = await app.Services.GetRequiredService<SystemSensors>().IdAsync(SystemSensorKeys.Files, CancellationToken.None);
        await using var db = app.NewDb();
        return await db.Events.Where(e => e.SensorId == id).OrderByDescending(e => e.Created).FirstAsync();
    }
}
