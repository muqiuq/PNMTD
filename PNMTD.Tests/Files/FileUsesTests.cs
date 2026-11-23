using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Files;

/// <summary>Which files a page or a location uses (docs/locations-knowledge-plan.md §5.5), and the names files are served under.</summary>
[TestClass]
public sealed class FileUsesTests
{
    private static PnmtdDbContext NewDb() => PnmtdDbContext.Create(DatabaseSettings.ConnectionString(TestDefaults.Configuration));

    private static FileEntity NewFile(string name, DateTime? deletedAt = null) => new()
    {
        Id = Guid.NewGuid(), Kind = FileKind.Attachment, Name = name, ContentType = "application/pdf", Size = 3,
        Sha256 = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()), Path = $"2026/11/{Guid.NewGuid()}-x.pdf", CreatedAt = Clock.UtcNow,
        DeletedAt = deletedAt,
    };

    [TestMethod]
    public async Task Uses_are_added_once_never_removed_and_unknown_ids_are_ignored()
    {
        await using var db = NewDb();
        var page = new KbPageEntity { Id = Guid.NewGuid(), Title = "Uses", CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow };
        var location = new LocationEntity { Id = Guid.NewGuid(), Name = "Uses", NormalizedName = $"uses {Guid.NewGuid()}", CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow };
        var a = NewFile("a.pdf");
        var b = NewFile("b.pdf");
        var removed = NewFile("removed.pdf", deletedAt: Clock.UtcNow.AddDays(-2));
        db.AddRange(page, location, a, b, removed);
        await db.SaveChangesAsync();

        var added = await FileUses.RecordAsync(db, [a.Id, a.Id, Guid.NewGuid(), removed.Id], FileOwner.Page(page.Id), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { a.Id, removed.Id }, added.ToArray());
        // Twice in one unit of work, before saving.
        Assert.AreEqual(0, (await FileUses.RecordAsync(db, [a.Id], FileOwner.Page(page.Id), CancellationToken.None)).Count);
        await db.SaveChangesAsync();

        // The next save references b only: a stays used, since old revisions keep it.
        CollectionAssert.AreEqual(new[] { b.Id }, (await FileUses.RecordAsync(db, [b.Id, removed.Id], FileOwner.Page(page.Id), CancellationToken.None)).ToArray());
        CollectionAssert.AreEqual(new[] { a.Id }, (await FileUses.RecordAsync(db, [a.Id], FileOwner.Location(location.Id), CancellationToken.None)).ToArray());
        await db.SaveChangesAsync();

        await using var check = NewDb();
        var uses = await check.FileUses.Where(u => u.PageId == page.Id || u.LocationId == location.Id).ToListAsync();
        Assert.AreEqual(4, uses.Count);
        Assert.AreEqual(3, uses.Count(u => u.PageId == page.Id));
        Assert.IsNull((await check.Files.SingleAsync(f => f.Id == removed.Id)).DeletedAt, "referenced again while still on disk");

        // A responsibility's description is an owner of its own (docs/responsibilities-plan.md §10.4).
        var responsibility = new ResponsibilityEntity { Id = Guid.NewGuid(), Name = "Uses", NormalizedName = $"USES {Guid.NewGuid()}", CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow };
        check.Responsibilities.Add(responsibility);
        await check.SaveChangesAsync();
        CollectionAssert.AreEqual(new[] { a.Id }, (await FileUses.RecordAsync(check, [a.Id], FileOwner.Responsibility(responsibility.Id), CancellationToken.None)).ToArray());
        await check.SaveChangesAsync();
        Assert.AreEqual(3, await check.FileUses.CountAsync(u => u.FileId == a.Id));
        await check.Responsibilities.Where(r => r.Id == responsibility.Id).ExecuteDeleteAsync();

        // A use goes with its page.
        await check.KbPages.Where(p => p.Id == page.Id).ExecuteDeleteAsync();
        Assert.AreEqual(1, await check.FileUses.CountAsync(u => u.FileId == a.Id));
    }

    [TestMethod]
    [DataRow("Cage B.png", "cage-b.png")]
    [DataRow("UPS manual.pdf", "ups-manual.pdf")]
    [DataRow("Grundriss Büro, 3. OG.PNG", "grundriss-buro-3-og.png")]
    [DataRow("Straße.txt", "strasse.txt")]
    [DataRow("../../etc/passwd", "etc-passwd")]
    [DataRow("..", "file")]
    [DataRow("", "file")]
    [DataRow("报告.pdf", "file.pdf")]
    [DataRow("report.pdf.exe", "report-pdf.exe")]
    [DataRow(".bashrc", "file.bashrc")]
    [DataRow("archive.tar.gz", "archive-tar.gz")]
    public void A_slug_is_safe_to_read_and_to_put_in_a_path(string name, string slug)
    {
        Assert.AreEqual(slug, FileNames.Slug(name));
    }

    [TestMethod]
    [DataRow("invoice.cmd", "jpg", "invoice.jpg")]
    [DataRow("photo.JPEG", "jpg", "photo.JPEG")]
    [DataRow("Screenshot", "png", "Screenshot.png")]
    [DataRow("Floor plan v2.1", "webp", "Floor plan v2.1.webp")]
    [DataRow("animation.png", "gif", "animation.gif")]
    public void An_images_name_ends_in_its_types_extension(string name, string extension, string expected)
    {
        Assert.AreEqual(expected, FileNames.ImageName(name, extension));
        Assert.AreEqual(FileNames.MaxName, FileNames.ImageName(new string('a', 255), "webp").Length);
    }

    [TestMethod]
    public void A_long_name_keeps_its_extension_and_the_url_names_the_file()
    {
        var slug = FileNames.Slug(new string('a', 200) + ".docx");
        Assert.AreEqual(FileNames.MaxSlug, slug.Length);
        Assert.IsTrue(slug.EndsWith(".docx", StringComparison.Ordinal));
        var id = Guid.NewGuid();
        Assert.AreEqual($"/api/v1/files/{id}/cage-b.png", FileNames.Url(id, "Cage B.png"));
    }
}
