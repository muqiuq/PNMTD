using Microsoft.Extensions.Time.Testing;
using PNMTD.Api.V1.Contracts;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Knowledge;

/// <summary>
/// The recent changes on top of the knowledge base (docs/knowledge-base-extensions-plan.md §6, §16): what counts as a change,
/// one row per page with its newest change, newest first, archived pages left out, at most 10, and the last 14 days. The tests
/// run one after another, so the pages a test has just changed are the newest in the shared database.
/// </summary>
[TestClass]
public sealed class KbChangesTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient viewer = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
        viewer = app.Client(Roles.Viewer);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<KbPageDetail> CreateAsync(string title) =>
        (await (await user.PostJsonAsync("/api/v1/kb/pages", KbPageTests.Request(title, "one\ntwo"))).ReadAsync<KbSaveResult>()).Page;

    private static async Task<KbPageDetail> SaveAsync(KbPageDetail page, Action<KbPageRequest> change)
    {
        var request = KbPageTests.From(page);
        change(request);
        return (await (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", request)).ReadAsync<KbSaveResult>()).Page;
    }

    private static async Task<List<KbRecentChange>> ChangesAsync()
    {
        var changes = await (await viewer.GetAsync("/api/v1/kb/changes")).ReadAsync<KbRecentChanges>();
        Assert.AreEqual(14, changes.Days);
        return [.. changes.Items];
    }

    [TestMethod]
    public async Task Each_page_shows_once_with_its_newest_change_and_housekeeping_doesnt_count()
    {
        var a = await CreateAsync("Changed text");
        var b = await CreateAsync("Changed status");
        a = await SaveAsync(a, r => { r.Content = "one\n2\nthree"; r.Note = "the second line"; });
        b = await SaveAsync(b, r => r.Status = KbPageStatus.Draft);
        // Housekeeping after that: a tag on A, the sensitive mark and Written with AI on B.
        a = await SaveAsync(a, r => r.Tags = ["housekeeping"]);
        b = await SaveAsync(b, r => { r.Sensitive = true; r.WrittenWithAi = true; });

        var changes = await ChangesAsync();
        CollectionAssert.AreEqual(new[] { b.Number, a.Number }, changes.Take(2).Select(c => c.Number).ToArray());
        var status = changes[0];
        Assert.AreEqual(KbChanges.Status, status.Kind);
        Assert.AreEqual("Published → Draft", status.Detail);
        Assert.AreEqual(2, status.Revision);
        Assert.IsTrue(status.Sensitive);
        Assert.IsTrue(status.WrittenWithAi);
        Assert.AreEqual(KbPageStatus.Draft, status.Status);
        var edited = changes[1];
        Assert.AreEqual(KbChanges.Edited, edited.Kind);
        Assert.AreEqual(2, edited.Revision);
        Assert.AreEqual("the second line", edited.Note);
        Assert.AreEqual(2, edited.Added);
        Assert.AreEqual(1, edited.Removed);
        Assert.AreEqual(2, edited.Changes);
        Assert.AreEqual(1, edited.People);
        Assert.AreEqual(1, changes.Count(c => c.Number == a.Number));

        var restored = await (await user.PostAsync($"/api/v1/kb/pages/{a.Id}/revisions/1/restore", null)).ReadAsync<KbPageDetail>();
        var created = await CreateAsync("Created last");
        await (await user.PostAsync($"/api/v1/kb/pages/{b.Id}/archive", null)).ReadAsync<KbPageDetail>();
        changes = await ChangesAsync();
        Assert.AreEqual(created.Number, changes[0].Number);
        Assert.AreEqual(KbChanges.Created, changes[0].Kind);
        Assert.AreEqual(a.Number, changes[1].Number);
        Assert.AreEqual(KbChanges.Restored, changes[1].Kind);
        Assert.AreEqual("from 1", changes[1].Detail);
        Assert.AreEqual(restored.Revision, changes[1].Revision);
        Assert.IsFalse(changes.Any(c => c.Number == b.Number), "archived pages are left out");
    }

    [TestMethod]
    public async Task At_most_ten_pages_from_the_last_fourteen_days()
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero) + TimeSpan.FromDays(60);
        var time = new FakeTimeProvider(start);
        try
        {
            Clock.Provider = time;
            Assert.AreEqual(0, (await ChangesAsync()).Count, "everything else is older than 14 days");
            var pages = new List<KbPageDetail>();
            for (var i = 0; i < 11; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                pages.Add(await CreateAsync($"Page {i} of eleven"));
            }
            var changes = await ChangesAsync();
            CollectionAssert.AreEqual(pages.Skip(1).Reverse().Select(p => p.Number).ToArray(), changes.Select(c => c.Number).ToArray());

            // The first page's creation is at start + 1 s: still in at exactly 14 days, out a second later.
            time.SetUtcNow(start + TimeSpan.FromSeconds(1) + TimeSpan.FromDays(14));
            Assert.AreEqual(10, (await ChangesAsync()).Count);
            // The last page's creation is at start + 11 s: only it is left then.
            time.SetUtcNow(start + TimeSpan.FromSeconds(11) + TimeSpan.FromDays(14));
            CollectionAssert.AreEqual(new[] { pages[^1].Number }, (await ChangesAsync()).Select(c => c.Number).ToArray());
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, (await ChangesAsync()).Count);
        }
        finally
        {
            Clock.Provider = TimeProvider.System;
        }
    }
}
