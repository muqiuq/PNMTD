using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Files;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Files;

/// <summary>
/// Files on disk (docs/locations-knowledge-plan.md §5.5, §12): written through <c>.tmp/</c> into the folder, cut off over
/// their kind's limit, blocked types refused, attachments always downloads, ranges, paths that never leave the folder,
/// symbolic links not followed, the Admin's delete at once, and the uploads per hour.
/// </summary>
[TestClass]
public sealed class FileStoreTests
{
    private static TestApp app = null!;
    private static string root = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        root = Directory.CreateTempSubdirectory("pnmtd-files-").FullName;
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["KnowledgeBase:FilesPath"] = root,
            ["KnowledgeBase:MaxAttachmentBytes"] = "300000",
        });
    }

    [ClassCleanup]
    public static void Stop()
    {
        app.Dispose();
        Directory.Delete(root, recursive: true);
    }

    [TestInitialize]
    public void Reset() => app.Services.GetRequiredService<UploadGuard>().Reset();

    internal static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string kind, string name, byte[] body, string type = "application/octet-stream")
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return await client.PostAsync($"/api/v1/files?kind={kind}&name={Uri.EscapeDataString(name)}", content);
    }

    private static string[] Leftovers() =>
        Directory.Exists(Path.Combine(root, FileStore.TempFolder)) ? Directory.GetFiles(Path.Combine(root, FileStore.TempFolder)) : [];

    [TestMethod]
    public async Task An_image_is_stripped_moved_into_place_and_served_inline_as_its_detected_type()
    {
        var user = app.Client(Roles.User);
        var (withMetadata, clean) = TestImages.JpegPair();
        using var response = await UploadAsync(user, "image", "Cage B (rack 4).gif", withMetadata, "image/gif");
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        var item = await response.ReadAsync<FileItem>();
        Assert.AreEqual(FileKind.Image, item.Kind);
        Assert.AreEqual("image/jpeg", item.ContentType, "the bytes decide, not the name or the type sent");
        Assert.AreEqual((8, 8), (item.Width, item.Height));
        Assert.AreEqual(clean.Length, item.Size);
        Assert.AreEqual($"/api/v1/files/{item.Id}/cage-b-rack-4.gif", item.Url);
        Assert.AreEqual(0, Leftovers().Length, "nothing left in .tmp/");

        await using var db = app.NewDb();
        var row = await db.Files.SingleAsync(f => f.Id == item.Id);
        StringAssert.Matches(row.Path, new System.Text.RegularExpressions.Regex($@"^\d{{4}}/\d{{2}}/{item.Id}-cage-b-rack-4\.jpg$"));
        var onDisk = Path.Combine(root, row.Path);
        CollectionAssert.AreEqual(clean, await File.ReadAllBytesAsync(onDisk));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(onDisk));
        }

        using var served = await app.Client(Roles.Viewer).GetAsync(item.Url);
        Assert.AreEqual(HttpStatusCode.OK, served.StatusCode);
        Assert.AreEqual("image/jpeg", served.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("inline", served.Content.Headers.ContentDisposition!.DispositionType);
        Assert.AreEqual("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.AreEqual($"\"{row.Sha256}\"", served.Headers.ETag!.Tag);
        StringAssert.Contains(served.Headers.CacheControl!.ToString(), "immutable");
        CollectionAssert.AreEqual(clean, await served.Content.ReadAsByteArrayAsync());

        using var again = await UploadAsync(user, "image", "the same.jpg", withMetadata);
        Assert.AreEqual(item.Id, (await again.ReadAsync<FileItem>()).Id, "the same bytes give the same file");
    }

    [TestMethod]
    public async Task An_attachment_is_always_a_download_and_answers_a_range()
    {
        var html = "<html><script>alert(document.cookie)</script></html>"u8.ToArray();
        using var response = await UploadAsync(app.Client(Roles.User), "attachment", "Grundriss Büro.html", html, "text/html");
        var item = await response.ReadAsync<FileItem>();
        Assert.AreEqual("text/html", item.ContentType, "named for its chip");

        using var served = await app.Client(Roles.Viewer).GetAsync(item.Url);
        Assert.AreEqual("application/octet-stream", served.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("attachment", served.Content.Headers.ContentDisposition!.DispositionType);
        Assert.AreEqual("Grundriss Büro.html", served.Content.Headers.ContentDisposition.FileNameStar, "RFC 5987 for non-ASCII names");

        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        request.Headers.Range = new RangeHeaderValue(6, 13);
        using var partial = await app.Client(Roles.Viewer).SendAsync(request);
        Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.AreEqual("<script>", await partial.Content.ReadAsStringAsync());
        Assert.AreEqual(html.Length, partial.Content.Headers.ContentRange!.Length);

        using var cached = new HttpRequestMessage(HttpMethod.Get, item.Url);
        cached.Headers.IfNoneMatch.Add(served.Headers.ETag!);
        Assert.AreEqual(HttpStatusCode.NotModified, (await app.Client(Roles.Viewer).SendAsync(cached)).StatusCode);
    }

    [TestMethod]
    public async Task Uploads_over_their_kinds_limit_are_cut_off_and_leave_nothing_behind()
    {
        var user = app.Client(Roles.User);
        await using var db = app.NewDb();
        var before = await db.Files.CountAsync();
        var (png, _) = TestImages.PngPair();
        byte[] large = [.. png, .. new byte[2 * 1024 * 1024]];
        using var image = await UploadAsync(user, "image", "large.png", large);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, image.StatusCode);
        StringAssert.Contains(await image.Content.ReadAsStringAsync(), FileErrors.TooLarge);
        using var attachment = await UploadAsync(user, "attachment", "large.zip", new byte[300_001]);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, attachment.StatusCode);
        using var pixels = await UploadAsync(user, "image", "wide.png", TestImages.Png(10_001, 1));
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, pixels.StatusCode);
        using var svg = await UploadAsync(user, "image", "logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray());
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, svg.StatusCode);
        StringAssert.Contains(await svg.Content.ReadAsStringAsync(), FileErrors.NotAnImage);
        Assert.AreEqual(0, Leftovers().Length);
        Assert.AreEqual(before, await db.Files.CountAsync());
    }

    [TestMethod]
    [DataRow("setup.exe")]
    [DataRow("report.pdf.exe")]
    [DataRow("Report.PDF.EXE.")]
    [DataRow("macro.js")]
    [DataRow("installer.msi")]
    public async Task Blocked_extensions_are_refused_as_attachments(string name)
    {
        using var response = await UploadAsync(app.Client(Roles.User), "attachment", name, "MZ"u8.ToArray());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode, name);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), FileErrors.BlockedType);
    }

    [TestMethod]
    public async Task A_crafted_name_never_leaves_the_folder_and_a_symbolic_link_is_not_followed()
    {
        using var response = await UploadAsync(app.Client(Roles.User), "attachment", "../../../etc/passwd", "root:x:0:0"u8.ToArray());
        var item = await response.ReadAsync<FileItem>();
        Assert.AreEqual("passwd", item.Name, "only the last part of a path is a name");
        await using var db = app.NewDb();
        var row = await db.Files.SingleAsync(f => f.Id == item.Id);
        Assert.IsTrue(Path.GetFullPath(Path.Combine(root, row.Path)).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.IsFalse(row.Path.Contains("..", StringComparison.Ordinal));

        // The host replaces the file with a link to a file outside the folder: PNMTD doesn't follow it.
        var outside = Path.Combine(Path.GetTempPath(), $"pnmtd-outside-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(outside, "outside");
        try
        {
            var onDisk = Path.Combine(root, row.Path);
            File.Delete(onDisk);
            File.CreateSymbolicLink(onDisk, outside);
            Assert.AreEqual(HttpStatusCode.NotFound, (await app.Client(Roles.Viewer).GetAsync(item.Url)).StatusCode);
            Assert.IsNull(app.Services.GetRequiredService<FileStore>().Locate("../" + Path.GetFileName(outside)));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [TestMethod]
    public async Task An_admins_delete_removes_the_file_at_once_and_writes_the_auth_log()
    {
        using var response = await UploadAsync(app.Client(Roles.User), "attachment", "secret-photo.pdf", "%PDF-1.7 the door code"u8.ToArray());
        var item = await response.ReadAsync<FileItem>();
        await using var db = app.NewDb();
        var row = await db.Files.AsNoTracking().SingleAsync(f => f.Id == item.Id);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.User).DeleteAsync($"/api/v1/files/{item.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await app.Client(Roles.Admin).DeleteAsync($"/api/v1/files/{item.Id}")).StatusCode);
        Assert.IsFalse(File.Exists(Path.Combine(root, row.Path)));
        Assert.IsFalse(await db.Files.AnyAsync(f => f.Id == item.Id));
        Assert.AreEqual(HttpStatusCode.NotFound, (await app.Client(Roles.Viewer).GetAsync(item.Url)).StatusCode);
        var log = await db.AuthLog.Where(l => l.EventType == AuthEvents.FileDeleted).OrderByDescending(l => l.CreatedAt).FirstAsync();
        StringAssert.Contains(log.Detail, item.Id.ToString());
        Assert.AreEqual(TestUsers.IdOf(Roles.Admin), log.UserId);
        Assert.AreEqual(HttpStatusCode.NotFound, (await app.Client(Roles.Admin).DeleteAsync($"/api/v1/files/{item.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task The_overview_shows_totals_uses_and_unknown_files()
    {
        using var response = await UploadAsync(app.Client(Roles.User), "attachment", "unused.txt", "nobody uses me"u8.ToArray(), "text/plain");
        var item = await response.ReadAsync<FileItem>();
        var stray = Path.Combine(root, "put-here-by-hand.txt");
        await File.WriteAllTextAsync(stray, "by hand");
        try
        {
            var overview = await (await app.Client(Roles.Admin).GetAsync("/api/v1/files?unused=true&unknown=true&pageSize=500")).ReadAsync<FilesOverview>();
            Assert.IsTrue(overview.Storage.Writable, overview.Storage.Problem);
            Assert.AreEqual(2, overview.Totals.Count);
            Assert.IsTrue(overview.Totals.Single(t => t.Kind == FileKind.Attachment).Count >= 1);
            var listed = overview.Files.Items.Single(f => f.File.Id == item.Id);
            Assert.AreEqual(0, listed.UsedBy.Count);
            Assert.IsTrue(overview.Unknown!.Any(u => u.Path == "put-here-by-hand.txt"));
            Assert.IsFalse(overview.Unknown!.Any(u => u.Path.StartsWith(".tmp", StringComparison.Ordinal)));
            Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.User).GetAsync("/api/v1/files")).StatusCode);
        }
        finally
        {
            File.Delete(stray);
        }
    }

    [TestMethod]
    public async Task Uploads_per_hour_are_limited_per_user()
    {
        using var own = new TestApp(settings: new Dictionary<string, string?> { ["KnowledgeBase:FilesPath"] = root, ["KnowledgeBase:UploadsPerHour"] = "2" });
        var user = own.Client(Roles.User);
        for (var i = 0; i < 2; i++)
        {
            Assert.AreEqual(HttpStatusCode.Created, (await UploadAsync(user, "attachment", $"n{i}.txt", [(byte)i, 1, 2])).StatusCode);
        }
        using var third = await UploadAsync(user, "attachment", "n3.txt", [3, 1, 2]);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.IsNotNull(third.Headers.RetryAfter);
        Assert.AreEqual(HttpStatusCode.Created, (await UploadAsync(own.Client(Roles.Admin), "attachment", "admin.txt", [4, 1, 2])).StatusCode,
            "per user");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await UploadAsync(own.Client(Roles.Admin), "video", "x.mp4", [5])).StatusCode);
    }
}
