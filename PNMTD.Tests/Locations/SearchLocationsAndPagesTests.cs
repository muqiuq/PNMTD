using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Locations;

/// <summary>
/// The global search's locations and pages (docs/locations-knowledge-plan.md §6.7): locations by name with their paths;
/// pages by number, title, summary and tag, <c>#tag</c> by tags only, never archived ones.
/// </summary>
[TestClass]
public sealed class SearchLocationsAndPagesTests
{
    private static TestApp app = null!;
    private static HttpClient viewer = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        viewer = app.Client(Roles.Viewer);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static Task<SearchResult> SearchAsync(string q) =>
        viewer.GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}").ContinueWith(t => t.Result.ReadAsync<SearchResult>()).Unwrap();

    /// <summary>A page straight in the database, as the knowledge base would have stored it.</summary>
    private static KbPageEntity Page(string title, string? summary = null, bool archived = false, params string[] tags)
    {
        using var db = app.NewDb();
        var now = Clock.UtcNow;
        var page = new KbPageEntity
        {
            Id = Guid.NewGuid(), Title = title, Summary = summary, Content = "", CreatedAt = now, UpdatedAt = now, Revision = 1,
            ArchivedAt = archived ? now : null,
        };
        db.KbPages.Add(page);
        foreach (var name in tags)
        {
            var tag = db.KbTags.SingleOrDefault(t => t.NormalizedName == name)
                ?? db.KbTags.Add(new KbTagEntity { Id = Guid.NewGuid(), Name = name, NormalizedName = name, CreatedAt = now }).Entity;
            db.KbPageTags.Add(new KbPageTagEntity { PageId = page.Id, TagId = tag.Id, AddedAt = now });
        }
        db.SaveChanges();
        return page;
    }

    [TestMethod]
    public async Task LocationsAreFoundByNameWithTheirPaths()
    {
        var user = app.Client(Roles.User);
        var site = await LocationsApiTests.CreateAsync(user, description: "Main site");
        var name = V1.Unique("Cage");
        var cage = await LocationsApiTests.CreateAsync(user, name, site.Id);

        var result = await SearchAsync(name[..10].ToUpperInvariant());
        var found = result.Locations.Single(l => l.Id == cage.Id);
        Assert.AreEqual($"{site.Name} › {name}", found.Path);
        Assert.AreEqual("Main site", (await SearchAsync(site.Name)).Locations.Single(l => l.Id == site.Id).Description);
    }

    [TestMethod]
    public async Task PagesByNumberTitleSummaryAndTag()
    {
        var word = V1.Unique("printer");
        var tag = V1.Unique("bk").ToLowerInvariant();
        var byTitle = Page($"Setting up the {word}", tags: [tag, "zrh"]);
        var bySummary = Page("Drivers", summary: $"For the {word} on the 2nd floor");
        var archived = Page($"Old {word}", archived: true, tags: [tag]);
        var number = byTitle.Number;
        Assert.IsTrue(number > 0);

        var result = await SearchAsync(word);
        CollectionAssert.AreEquivalent(new[] { byTitle.Id, bySummary.Id }, result.Pages.Select(p => p.Id).ToArray(), "archived pages are left out");
        CollectionAssert.AreEqual(new[] { tag, "zrh" }, result.Pages.Single(p => p.Id == byTitle.Id).Tags.ToArray());

        foreach (var q in new[] { $"KB-{number}", $"kb{number}", $"{number}" })
        {
            Assert.AreEqual(byTitle.Id, (await SearchAsync(q)).Pages.First().Id, q);
        }

        var tagged = await SearchAsync($"#{tag[..5]}");
        Assert.AreEqual(byTitle.Id, tagged.Pages.Single(p => p.Id == byTitle.Id || p.Id == archived.Id).Id);
        Assert.AreEqual(0, tagged.Hosts.Count + tagged.Sensors.Count + tagged.Locations.Count, "#tag searches pages only");
        Assert.AreEqual(byTitle.Id, (await SearchAsync(tag)).Pages.Single().Id, "a tag without # too");
    }
}
