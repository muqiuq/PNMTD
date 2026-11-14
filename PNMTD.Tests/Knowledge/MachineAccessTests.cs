using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Knowledge;

/// <summary>
/// What machines may read (docs/locations-knowledge-plan.md §7.4, §12): a sensitive page, a sensitive field's value
/// (inherited too), an old revision and a file used only by a sensitive page are each invisible through MachineAccess.
/// </summary>
[TestClass]
public sealed class MachineAccessTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<KbPageDetail> CreateAsync(string title, string content, bool sensitive = false) =>
        (await (await user.PostJsonAsync("/api/v1/kb/pages", new KbPageRequest { Title = title, Content = content, MarkSensitive = sensitive, AcknowledgeCredentials = true }))
            .ReadAsync<KbSaveResult>()).Page;

    [TestMethod]
    public async Task A_sensitive_page_and_an_old_revision_are_invisible()
    {
        var old = "old text " + Guid.NewGuid().ToString("N");
        var open = await CreateAsync("Readable page", old);
        var request = KbPageTests.From(open);
        request.Content = "new text";
        await (await user.PutJsonAsync($"/api/v1/kb/pages/{open.Id}", request)).ReadAsync<KbSaveResult>();
        var sensitive = await CreateAsync("Sensitive page", "secret text", sensitive: true);
        await using var db = app.NewDb();
        Assert.IsTrue(await MachineAccess.Pages(db).AnyAsync(p => p.Id == open.Id));
        Assert.IsFalse(await MachineAccess.Pages(db).AnyAsync(p => p.Id == sensitive.Id));
        Assert.IsFalse(await MachineAccess.Pages(db).AnyAsync(p => p.Content.Contains(old) || p.PlainText.Contains(old)), "only the current revision");
        Assert.IsTrue(await db.KbRevisions.AnyAsync(r => r.Content.Contains(old)), "the history has it, and machines don't read the history");
    }

    [TestMethod]
    public async Task A_file_is_readable_only_through_readable_current_content()
    {
        var onlySensitive = await KbPageTests.FileAsync(FileKind.Image, app);
        var shared = await KbPageTests.FileAsync(FileKind.Image, app);
        var onlyOld = await KbPageTests.FileAsync(FileKind.Attachment, app);
        var readable = await KbPageTests.FileAsync(FileKind.Attachment, app);
        await CreateAsync("Sensitive with files", $"![a](/api/v1/files/{onlySensitive}/a.png) ![b](/api/v1/files/{shared}/b.png)", sensitive: true);
        await CreateAsync("Readable with a file", $"![b](/api/v1/files/{shared}/b.png) [c](/api/v1/files/{readable}/c.pdf)");
        var edited = await CreateAsync("Had a file", $"[d](/api/v1/files/{onlyOld}/d.pdf)");
        var request = KbPageTests.From(edited);
        request.Content = "The file is gone from the text.";
        await (await user.PutJsonAsync($"/api/v1/kb/pages/{edited.Id}", request)).ReadAsync<KbSaveResult>();
        await using var db = app.NewDb();
        var ids = new[] { onlySensitive, shared, onlyOld, readable };
        var visible = await MachineAccess.Files(db).Where(f => ids.Contains(f.Id)).Select(f => f.Id).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { shared, readable }, visible);
        Assert.IsTrue(await db.FileUses.AnyAsync(u => u.FileId == onlyOld), "the use stays with the page's history");
    }

    [TestMethod]
    public async Task A_sensitive_fields_value_is_invisible_inherited_too_and_so_are_its_files()
    {
        var (site, room) = await KbPageTests.LocationsAsync(app);
        var now = Clock.UtcNow;
        LocationFieldEntity Field(string label, bool sensitive) => new()
        {
            Id = Guid.NewGuid(), Key = $"kb-{Guid.NewGuid():N}"[..20], Label = label, Type = LocationFieldType.Markdown, Inherited = true,
            Sensitive = sensitive, Position = 100, CreatedAt = now,
        };
        var address = Field("Address", false);
        var access = Field("Access", true);
        var inValue = await KbPageTests.FileAsync(FileKind.Image, app);
        var inSensitive = await KbPageTests.FileAsync(FileKind.Image, app);
        await using (var write = app.NewDb())
        {
            write.LocationFields.AddRange(address, access);
            write.LocationValues.AddRange(
                new LocationValueEntity { LocationId = site, FieldId = address.Id, Value = $"\"Bahnhofstrasse 1 ![map](/api/v1/files/{inValue}/map.png)\"", Revision = 1, UpdatedAt = now },
                new LocationValueEntity { LocationId = site, FieldId = access.Id, Value = $"\"Door code 4711 ![door](/api/v1/files/{inSensitive}/door.png)\"", Revision = 1, UpdatedAt = now });
            write.FileUses.AddRange(
                new FileUseEntity { Id = Guid.NewGuid(), FileId = inValue, LocationId = site, FirstUsedAt = now },
                new FileUseEntity { Id = Guid.NewGuid(), FileId = inSensitive, LocationId = site, FirstUsedAt = now });
            await write.SaveChangesAsync();
        }
        await using var db = app.NewDb();
        var tree = await app.Services.GetRequiredService<LocationTree>().GetAsync();
        var values = await MachineAccess.LocationValuesAsync(db, tree, room, CancellationToken.None);
        var inherited = values.Single(v => v.FieldId == address.Id);
        Assert.AreEqual(site, inherited.InheritedFrom);
        Assert.IsFalse(values.Any(v => v.FieldId == access.Id), "not even inherited");
        Assert.IsFalse((await MachineAccess.LocationValuesAsync(db, tree, site, CancellationToken.None)).Any(v => v.FieldId == access.Id));
        Assert.IsFalse(await MachineAccess.LocationValues(db).AnyAsync(v => v.FieldId == access.Id));
        var files = await MachineAccess.Files(db).Where(f => f.Id == inValue || f.Id == inSensitive).Select(f => f.Id).ToListAsync();
        CollectionAssert.AreEqual(new[] { inValue }, files);
    }
}
