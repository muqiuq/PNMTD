using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Knowledge;

/// <summary>
/// Mutual links between pages (docs/knowledge-base-extensions-plan.md §7, §16): made on either page and shown on both, removed
/// from either, two mirrored rows after every operation, a links-only revision on the other page that no revision check counts,
/// and the refusals: the page itself, an archived page, a page with 50 links.
/// </summary>
[TestClass]
public sealed class KbPageLinksTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<KbPageDetail> CreateAsync(string title, params Guid[] linkedTo)
    {
        var request = KbPageTests.Request(title, "text");
        request.Links = [.. linkedTo.Select(id => new KbLinkRequest { Kind = KbLinkKind.Page, Id = id })];
        return (await (await user.PostJsonAsync("/api/v1/kb/pages", request)).ReadAsync<KbSaveResult>()).Page;
    }

    private static Task<HttpResponseMessage> PutAsync(KbPageDetail page, Action<KbPageRequest> change)
    {
        var request = KbPageTests.From(page);
        change(request);
        return user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", request);
    }

    private static async Task<KbPageDetail> SaveAsync(KbPageDetail page, Action<KbPageRequest> change) =>
        (await (await PutAsync(page, change)).ReadAsync<KbSaveResult>()).Page;

    private static void Link(KbPageRequest r, Guid other) => r.Links.Add(new KbLinkRequest { Kind = KbLinkKind.Page, Id = other });

    private static void Unlink(KbPageRequest r, Guid other) => r.Links.RemoveAll(l => l.Kind == KbLinkKind.Page && l.Id == other);

    private static async Task<KbPageDetail> GetAsync(KbPageDetail page) =>
        await (await user.GetAsync($"/api/v1/kb/pages/{page.Number}")).ReadAsync<KbPageDetail>();

    private static async Task<List<KbRevisionItem>> RevisionsAsync(KbPageDetail page) =>
        await (await user.GetAsync($"/api/v1/kb/pages/{page.Id}/revisions")).ReadAsync<List<KbRevisionItem>>();

    private static Guid[] Linked(KbPageDetail page) => [.. page.Links.Where(l => l.Kind == KbLinkKind.Page).Select(l => l.Id)];

    /// <summary>Every page link is two rows, one on each page (§7.2).</summary>
    private static async Task AssertMirroredAsync()
    {
        await using var db = app.NewDb();
        var rows = await db.KbLinks.AsNoTracking().Where(l => l.TargetPageId != null).Select(l => new { l.PageId, Target = l.TargetPageId!.Value }).ToListAsync();
        var lonely = rows.Where(r => !rows.Any(m => m.PageId == r.Target && m.Target == r.PageId)).ToList();
        Assert.IsEmpty(lonely, string.Join(", ", lonely.Select(l => $"{l.PageId} → {l.Target}")));
    }

    [TestMethod]
    public async Task A_link_made_on_one_page_shows_on_both_and_either_page_removes_it()
    {
        var a = await CreateAsync("Linking page");
        var b = await CreateAsync("Linked page");
        a = await SaveAsync(a, r => Link(r, b.Id));
        CollectionAssert.AreEqual(new[] { b.Id }, Linked(a));
        var link = a.Links.Single(l => l.Kind == KbLinkKind.Page);
        Assert.AreEqual(b.Number, link.Number);
        Assert.AreEqual($"KB-{b.Number}", link.Detail);
        Assert.AreEqual("Linked page", link.Name);
        Assert.AreEqual("+1 −0", (await RevisionsAsync(a))[0].Changes.Single(c => c.Kind == KbChangeKinds.Links).Text);

        // The other page: the link, in a links-only revision of its own, which isn't an edit.
        var other = await GetAsync(b);
        CollectionAssert.AreEqual(new[] { a.Id }, Linked(other));
        Assert.AreEqual(2, other.Revision);
        Assert.AreEqual(b.UpdatedAt, other.UpdatedAt, "nobody edited it");
        var linkedOnly = (await RevisionsAsync(b))[0];
        Assert.IsTrue(linkedOnly.LinksOnly);
        Assert.IsFalse(linkedOnly.IsEdit);
        Assert.AreEqual(new KbChange(KbChangeKinds.Linked, $"KB-{a.Number}"), linkedOnly.Changes.Single());
        await AssertMirroredAsync();

        // Removed on the other page: gone from both, and the first page gets its links-only revision.
        other = await SaveAsync(other, r => Unlink(r, a.Id));
        Assert.IsEmpty(Linked(other));
        var first = await GetAsync(a);
        Assert.IsEmpty(Linked(first));
        Assert.AreEqual(new KbChange(KbChangeKinds.Unlinked, $"KB-{b.Number}"), (await RevisionsAsync(a))[0].Changes.Single());
        await AssertMirroredAsync();

        // A new page links at once: the other page's revision names the new number.
        var c = await CreateAsync("Born linked", b.Id);
        CollectionAssert.AreEqual(new[] { b.Id }, Linked(c));
        Assert.AreEqual(new KbChange(KbChangeKinds.Linked, $"KB-{c.Number}"), (await RevisionsAsync(b))[0].Changes.Single());
        await AssertMirroredAsync();
    }

    [TestMethod]
    public async Task Someone_editing_the_other_page_meanwhile_gets_no_conflict_and_keeps_the_link()
    {
        var a = await CreateAsync("Links while B is open");
        var opened = await CreateAsync("Open in an editor");
        await SaveAsync(a, r => Link(r, opened.Id));

        // B's editor started from revision 1, before the links-only revision 2, and sends B's links as it loaded them.
        var saved = await SaveAsync(opened, r => r.Content = "text, edited meanwhile");
        Assert.AreEqual(3, saved.Revision);
        CollectionAssert.AreEqual(new[] { a.Id }, Linked(saved), "the link made meanwhile stays");
        CollectionAssert.AreEqual(new[] { opened.Id }, Linked(await GetAsync(a)));
        await AssertMirroredAsync();

        // A real edit in between is still a conflict.
        var stale = await PutAsync(opened, r => r.Content = "an older edit");
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);

        // Unmarking sensitive with statements for a revision before a links-only one.
        var marked = await (await user.PutJsonAsync($"/api/v1/kb/pages/{saved.Id}/sensitive", new KbSensitiveRequest { Sensitive = true }))
            .ReadAsync<KbPageDetail>();
        await CreateAsync("Links to the sensitive page", saved.Id);
        var unmark = new KbSensitiveRequest
        {
            Sensitive = false, Revision = marked.Revision, Statements = new KbStatements { NoCredentials = true, NoPersonalData = true },
        };
        var unmarked = await (await user.PutJsonAsync($"/api/v1/kb/pages/{saved.Id}/sensitive", unmark)).ReadAsync<KbPageDetail>();
        Assert.IsFalse(unmarked.Sensitive);
    }

    [TestMethod]
    public async Task The_page_itself_an_archived_page_and_a_full_page_are_refused_and_an_archived_pages_link_is_kept()
    {
        var a = await CreateAsync("Refusals");
        var self = await PutAsync(a, r => Link(r, a.Id));
        Assert.AreEqual(HttpStatusCode.BadRequest, self.StatusCode);
        StringAssert.Contains((await self.ProblemAsync()).GetProperty("errors").GetProperty("links")[0].GetString(), "itself");

        var archived = await CreateAsync("Archived target");
        await (await user.PostAsync($"/api/v1/kb/pages/{archived.Id}/archive", null)).ReadAsync<KbPageDetail>();
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PutAsync(a, r => Link(r, archived.Id))).StatusCode);

        // A link to a page that is archived afterwards stays, whatever the other page's save sends.
        var later = await CreateAsync("Archived later");
        a = await SaveAsync(a, r => Link(r, later.Id));
        await (await user.PostAsync($"/api/v1/kb/pages/{later.Id}/archive", null)).ReadAsync<KbPageDetail>();
        a = await GetAsync(a);
        Assert.IsTrue(a.Links.Single(l => l.Id == later.Id).Archived);
        a = await SaveAsync(a, r => { Unlink(r, later.Id); r.Content = "an edit that drops the archived link"; });
        CollectionAssert.AreEqual(new[] { later.Id }, Linked(a));
        await AssertMirroredAsync();

        // Deleting it (archived, by an admin) removes both rows.
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/kb/pages/{later.Id}")).StatusCode);
        a = await GetAsync(a);
        Assert.IsEmpty(Linked(a));
        await AssertMirroredAsync();

        // A page with 50 links takes no more, from either side.
        var full = await CreateAsync("Fifty links");
        await using (var db = app.NewDb())
        {
            for (var i = 0; i < KbPageService.MaxLinks; i++)
            {
                var location = new LocationEntity { Id = Guid.NewGuid(), Name = $"full-{Guid.NewGuid():N}", CreatedAt = Clock.UtcNow };
                location.NormalizedName = location.Name;
                db.Locations.Add(location);
                db.KbLinks.Add(new KbLinkEntity { Id = Guid.NewGuid(), PageId = full.Id, LocationId = location.Id, CreatedAt = Clock.UtcNow });
            }
            await db.SaveChangesAsync();
        }
        var refused = await PutAsync(a, r => Link(r, full.Id));
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        StringAssert.Contains((await refused.ProblemAsync()).GetProperty("errors").GetProperty("links")[0].GetString(), "50 links already");
    }

    [TestMethod]
    public async Task Two_people_linking_the_same_two_pages_at_once_make_one_link()
    {
        for (var round = 0; round < 5; round++)
        {
            var a = await CreateAsync($"Both sides A {round}");
            var b = await CreateAsync($"Both sides B {round}");
            var both = await Task.WhenAll(PutAsync(a, r => Link(r, b.Id)), PutAsync(b, r => Link(r, a.Id)));
            foreach (var response in both)
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            await using var db = app.NewDb();
            Assert.AreEqual(2, await db.KbLinks.CountAsync(l => (l.PageId == a.Id && l.TargetPageId == b.Id) || (l.PageId == b.Id && l.TargetPageId == a.Id)));
        }
        await AssertMirroredAsync();
    }

    [TestMethod]
    public async Task A_page_link_to_a_sensitive_page_is_invisible_to_machines()
    {
        var open = await CreateAsync("Readable, linked");
        var secret = await CreateAsync("Sensitive, linked", open.Id);
        var readable = await CreateAsync("Readable too", open.Id);
        await (await user.PutJsonAsync($"/api/v1/kb/pages/{secret.Id}/sensitive", new KbSensitiveRequest { Sensitive = true })).ReadAsync<KbPageDetail>();
        await using var db = app.NewDb();
        var seen = await MachineAccess.PageLinks(db).Where(l => l.PageId == open.Id || l.TargetPageId == open.Id).ToListAsync();
        Assert.HasCount(2, seen, "only the readable pair, both rows");
        Assert.IsTrue(seen.All(l => l.PageId != secret.Id && l.TargetPageId != secret.Id));
        Assert.IsTrue(seen.Any(l => l.PageId == open.Id && l.TargetPageId == readable.Id));
    }
}
