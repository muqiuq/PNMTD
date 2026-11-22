using System.Net;
using System.Text.Json;
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
/// The knowledge base's pages through the API (docs/locations-knowledge-plan.md §6, §12): revisions and their changes,
/// conflicts, restoring, archiving, deleting, redacting, tags, links and the panels, views, who is editing, and the
/// content search. Each test tags its pages with a tag of its own, so the shared database's other pages don't matter.
/// </summary>
[TestClass]
public sealed class KbPageTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient admin = null!;
    private static HttpClient viewer = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
        admin = app.Client(Roles.Admin);
        viewer = app.Client(Roles.Viewer);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    internal static KbPageRequest Request(string title, string content = "", params string[] tags) =>
        new() { Title = title, Content = content, Tags = [.. tags] };

    private static async Task<KbSaveResult> CreateAsync(KbPageRequest request) =>
        await (await user.PostJsonAsync("/api/v1/kb/pages", request)).ReadAsync<KbSaveResult>();

    private static async Task<KbSaveResult> SaveAsync(KbPageDetail page, Action<KbPageRequest> change, HttpClient? client = null)
    {
        var request = From(page);
        change(request);
        return await (await (client ?? user).PutJsonAsync($"/api/v1/kb/pages/{page.Id}", request)).ReadAsync<KbSaveResult>();
    }

    internal static KbPageRequest From(KbPageDetail page) => new()
    {
        Title = page.Title, Summary = page.Summary, Content = page.Content, Tags = page.Tags.Select(t => t.Name).ToList(), Status = page.Status,
        Links = page.Links.Select(l => new KbLinkRequest { Kind = l.Kind, Id = l.Id }).ToList(), Revision = page.Revision,
    };

    private static async Task<IReadOnlyList<KbRevisionItem>> RevisionsAsync(Guid id) =>
        await (await user.GetAsync($"/api/v1/kb/pages/{id}/revisions")).ReadAsync<List<KbRevisionItem>>();

    [TestMethod]
    public async Task A_new_page_is_published_numbered_and_its_first_revision()
    {
        var tag = Unique("t");
        var saved = await CreateAsync(Request("  Printer setup, 2nd floor ", "## Drivers\n\nInstall from \\\\print01.", "Printer", tag));
        var page = saved.Page;
        Assert.AreEqual("Printer setup, 2nd floor", page.Title);
        Assert.AreEqual(KbPageStatus.Published, page.Status);
        Assert.AreEqual(1, page.Revision);
        Assert.IsGreaterThan(0, page.Number);
        CollectionAssert.AreEquivalent(new[] { "printer", tag }, page.Tags.Select(t => t.Name).ToArray());
        Assert.IsTrue(page.Tags.All(t => t.Source == KbTagSource.Manual));
        Assert.AreEqual("test-user", page.CreatedByName);
        var read = await (await viewer.GetAsync($"/api/v1/kb/pages/{page.Number}")).ReadAsync<KbPageDetail>();
        Assert.AreEqual(page.Id, read.Id);
        var revision = (await RevisionsAsync(page.Id)).Single();
        Assert.AreEqual(1, revision.Number);
        Assert.AreEqual(3, revision.Added);
        Assert.IsTrue(revision.IsEdit);
        Assert.AreEqual(KbChangeKinds.Created, revision.Changes[0].Kind);
        await using var db = app.NewDb();
        var stored = await db.KbPages.SingleAsync(p => p.Id == page.Id);
        Assert.AreEqual("Drivers\nInstall from \\print01.", stored.PlainText);
        Assert.AreEqual(string.Join(" ", new[] { "printer", tag }.Order(StringComparer.Ordinal)), stored.TagsText);
    }

    [TestMethod]
    public async Task Invalid_pages_are_refused_with_the_fields_named()
    {
        var response = await user.PostJsonAsync("/api/v1/kb/pages", new KbPageRequest
        {
            Title = "ab", Summary = new string('s', 301), Tags = ["ok", "no spaces!", "x"], Note = new string('n', 201),
            Links = [new KbLinkRequest { Kind = KbLinkKind.Host, Id = Guid.NewGuid() }],
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ProblemAsync()).GetProperty("errors");
        foreach (var field in new[] { "title", "summary", "tags", "note" })
        {
            Assert.IsTrue(errors.TryGetProperty(field, out _), field);
        }
        var ghost = Request("A page with a ghost link");
        ghost.Links = [new KbLinkRequest { Kind = KbLinkKind.Sensor, Id = Guid.NewGuid() }];
        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.PostJsonAsync("/api/v1/kb/pages", ghost)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.PostJsonAsync("/api/v1/kb/pages", Request("A viewer's page"))).StatusCode);
    }

    [TestMethod]
    public async Task A_save_makes_a_revision_with_what_changed()
    {
        var tag = Unique("t");
        var page = (await CreateAsync(Request("Backups of the NAS", "one\ntwo\nthree", tag, "nas"))).Page;
        var saved = await SaveAsync(page, r =>
        {
            r.Title = "Backups of nas-01";
            r.Summary = "Nightly, to the cloud.";
            r.Content = "one\n2\nthree\nfour";
            r.Tags = [tag, "backup"];
            r.Status = KbPageStatus.NeedsReview;
            r.Note = "After the move";
        });
        Assert.AreEqual(2, saved.Page.Revision);
        Assert.IsFalse(saved.AskAiTags, "AI is off in this app");
        var revision = (await RevisionsAsync(page.Id))[0];
        Assert.AreEqual(2, revision.Number);
        Assert.AreEqual("After the move", revision.Note);
        Assert.AreEqual(2, revision.Added);
        Assert.AreEqual(1, revision.Removed);
        Assert.IsTrue(revision.IsEdit);
        var changes = revision.Changes.ToDictionary(c => c.Kind, c => c.Text);
        Assert.AreEqual("+2 −1", changes[KbChangeKinds.Content]);
        Assert.AreEqual("Backups of the NAS → Backups of nas-01", changes[KbChangeKinds.Title]);
        Assert.IsTrue(changes.ContainsKey(KbChangeKinds.Summary));
        Assert.AreEqual("+backup −nas", changes[KbChangeKinds.Tags]);
        Assert.AreEqual("Published → Needs review", changes[KbChangeKinds.Status]);

        // A save without a change makes no revision.
        var same = await SaveAsync(saved.Page, _ => { });
        Assert.AreEqual(2, same.Page.Revision);
        // Only the status: a revision, but not an edit (most edited counts edits).
        var status = await SaveAsync(same.Page, r => r.Status = KbPageStatus.Published);
        Assert.AreEqual(3, status.Page.Revision);
        Assert.IsFalse((await RevisionsAsync(page.Id))[0].IsEdit);
    }

    [TestMethod]
    public async Task A_save_from_an_older_revision_is_a_conflict_naming_theirs()
    {
        var page = (await CreateAsync(Request("Conflicting edits", "base"))).Page;
        var theirs = await SaveAsync(page, r => r.Content = "theirs", admin);
        var mine = From(page);
        mine.Content = "mine";
        var response = await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", mine);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.AreEqual("edit_conflict", problem.GetProperty("code").GetString());
        Assert.AreEqual(2, problem.GetProperty("revision").GetInt32());
        Assert.AreEqual("test-admin", problem.GetProperty("by").GetString());
        Assert.IsTrue(problem.TryGetProperty("at", out _));
        // Save mine anyway: on top of theirs, which stays in the history.
        mine.Revision = theirs.Page.Revision;
        var anyway = await (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", mine)).ReadAsync<KbSaveResult>();
        Assert.AreEqual("mine", anyway.Page.Content);
        Assert.AreEqual(3, anyway.Page.Revision);
        var missing = From(page);
        missing.Revision = null;
        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", missing)).StatusCode);
    }

    [TestMethod]
    public async Task Restoring_an_old_revision_brings_back_its_text_and_tags_but_not_its_status_or_links()
    {
        var tag = Unique("t");
        var host = await HostAsync();
        var page = (await CreateAsync(Request("Restore me", "first text", tag, "old-tag"))).Page;
        await SaveAsync(page, r =>
        {
            r.Content = "second text";
            r.Tags = [tag];
            r.Status = KbPageStatus.Draft;
            r.Links = [new KbLinkRequest { Kind = KbLinkKind.Host, Id = host }];
        });
        var restored = await (await user.PostAsync($"/api/v1/kb/pages/{page.Id}/revisions/1/restore", null)).ReadAsync<KbPageDetail>();
        Assert.AreEqual("first text", restored.Content);
        CollectionAssert.AreEquivalent(new[] { tag, "old-tag" }, restored.Tags.Select(t => t.Name).ToArray());
        Assert.AreEqual(KbPageStatus.Draft, restored.Status);
        Assert.AreEqual(host, restored.Links.Single().Id);
        var revision = (await RevisionsAsync(page.Id))[0];
        Assert.AreEqual(3, revision.Number);
        Assert.IsTrue(revision.Changes.Any(c => c.Kind == KbChangeKinds.Restored && c.Text == "from 1"));
        var diff = await (await viewer.GetAsync($"/api/v1/kb/pages/{page.Id}/diff?from=2&to=3")).ReadAsync<KbDiff>();
        Assert.AreEqual("second text", diff.FromContent);
        Assert.AreEqual("first text", diff.ToContent);
        Assert.AreEqual(1, diff.Added);
        Assert.AreEqual(1, diff.Removed);
        Assert.IsGreaterThan(0, diff.Hunks.Count);
        var old = await (await viewer.GetAsync($"/api/v1/kb/pages/{page.Id}/revisions/1")).ReadAsync<KbRevisionDetail>();
        Assert.AreEqual("first text", old.Content);
        Assert.AreEqual(KbPageStatus.Published, old.Status);
    }

    [TestMethod]
    public async Task An_archived_page_is_read_only_and_left_out_until_restored()
    {
        var tag = Unique("t");
        var page = (await CreateAsync(Request("Archive me", "text", tag))).Page;
        var archived = await (await user.PostAsync($"/api/v1/kb/pages/{page.Id}/archive", null)).ReadAsync<KbPageDetail>();
        Assert.IsNotNull(archived.ArchivedAt);
        Assert.AreEqual("test-user", archived.ArchivedByName);
        var edit = From(archived);
        edit.Content = "changed";
        var refused = await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", edit);
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.AreEqual("archived", (await refused.ProblemAsync()).GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.Conflict, (await user.PostAsync($"/api/v1/kb/pages/{page.Id}/revisions/1/restore", null)).StatusCode);
        Assert.AreEqual(0, (await ListAsync($"tags={tag}")).TotalCount);
        Assert.AreEqual(1, (await ListAsync($"tags={tag}&archived=true")).TotalCount);
        var back = await (await user.PostAsync($"/api/v1/kb/pages/{page.Id}/restore", null)).ReadAsync<KbPageDetail>();
        Assert.IsNull(back.ArchivedAt);
        Assert.AreEqual(1, (await ListAsync($"tags={tag}")).TotalCount);
        var changes = (await RevisionsAsync(page.Id)).Take(2).Select(r => r.Changes.Single(c => c.Kind == KbChangeKinds.Archived).Text).ToList();
        CollectionAssert.AreEqual(new[] { "restored", "archived" }, changes);
    }

    [TestMethod]
    public async Task Only_an_admin_deletes_and_only_an_archived_page()
    {
        var page = (await CreateAsync(Request("Delete me", "text"))).Page;
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.DeleteAsync($"/api/v1/kb/pages/{page.Id}")).StatusCode);
        var notArchived = await admin.DeleteAsync($"/api/v1/kb/pages/{page.Id}");
        Assert.AreEqual(HttpStatusCode.Conflict, notArchived.StatusCode);
        Assert.AreEqual("not_archived", (await notArchived.ProblemAsync()).GetProperty("code").GetString());
        var file = await FileAsync(FileKind.Attachment);
        var other = await FileAsync(FileKind.Attachment);
        var archived = await SaveAsync(page, r => r.Content = $"[a](/api/v1/files/{file}/a.pdf) [b](/api/v1/files/{other}/b.pdf)");
        var elsewhere = await CreateAsync(Request("Uses the other file", $"[b](/api/v1/files/{other}/b.pdf)"));
        await user.PostAsync($"/api/v1/kb/pages/{page.Id}/archive", null);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/kb/pages/{page.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/kb/pages/{page.Number}")).StatusCode);
        await using var db = app.NewDb();
        Assert.IsFalse(await db.KbRevisions.AnyAsync(r => r.PageId == page.Id));
        Assert.IsNotNull((await db.Files.SingleAsync(f => f.Id == file)).DeletedAt, "used by no other page");
        Assert.IsNull((await db.Files.SingleAsync(f => f.Id == other)).DeletedAt, "still used");
        var log = await db.AuditLog.Where(l => l.EventType == AuditEvents.KbPageDeleted).OrderByDescending(l => l.CreatedAt).FirstAsync();
        StringAssert.Contains(log.Detail, $"KB-{page.Number} “Delete me”");
        Assert.AreEqual(elsewhere.Page.Id, (await db.FileUses.SingleAsync(u => u.FileId == other)).PageId);
        _ = archived;
    }

    [TestMethod]
    public async Task Redacting_removes_the_text_from_every_revision_and_the_search_text()
    {
        var secret = "hunter2-" + Guid.NewGuid().ToString("N")[..6];
        var page = (await CreateAsync(Request("Router access", $"The password is {secret}."))).Page;
        page = (await SaveAsync(page, r => r.Content = $"The password is {secret}.\nUse the console.")).Page;
        page = (await SaveAsync(page, r => r.Content = "Use the console.")).Page;
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/redact", new KbRedactRequest { Text = secret })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/redact", new KbRedactRequest { Text = "abc" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await admin.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/redact", new KbRedactRequest { Text = "not in there" })).StatusCode);
        var result = await (await admin.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/redact", new KbRedactRequest { Text = secret })).ReadAsync<KbRedactResult>();
        Assert.AreEqual(2, result.Revisions);
        Assert.AreEqual(2, result.Occurrences);
        Assert.AreEqual(4, result.Page.Revision);
        await using var db = app.NewDb();
        var revisions = await db.KbRevisions.Where(r => r.PageId == page.Id).ToListAsync();
        Assert.IsFalse(revisions.Any(r => r.Content.Contains(secret)));
        Assert.AreEqual(2, revisions.Count(r => r.Content.Contains("[redacted]")));
        Assert.IsTrue(KbRevisions.Changes(revisions.Single(r => r.Number == 4).Changes).Any(c => c.Kind == KbChangeKinds.Redacted && c.Text == "1 text in 2 revisions"));
        Assert.AreEqual(0, (await ListAsync($"q={Uri.EscapeDataString(secret)}")).TotalCount);
        var log = await db.AuditLog.Where(l => l.EventType == AuditEvents.KbRevisionRedacted).OrderByDescending(l => l.CreatedAt).FirstAsync();
        Assert.IsFalse(log.Detail!.Contains(secret));
    }

    [TestMethod]
    public async Task Redacting_reaches_the_lists_of_what_changed_and_the_tags()
    {
        var secret = "PSK Sommer" + Guid.NewGuid().ToString("N")[..6];
        var tag = "geheim-" + Guid.NewGuid().ToString("N")[..6];
        var page = (await CreateAsync(Request($"VPN zrh {secret}", "Ask the network team.", tag))).Page;
        // A title's change names the old title; a tag's, the tag (§6.3).
        page = (await SaveAsync(page, r => r.Title = "VPN zrh")).Page;
        page = (await SaveAsync(page, r => r.Tags = [])).Page;
        await using (var before = app.NewDb())
        {
            var changes = (await before.KbRevisions.Where(r => r.PageId == page.Id).ToListAsync()).SelectMany(r => KbRevisions.Changes(r.Changes)).ToList();
            Assert.IsTrue(changes.Any(c => c.Text.Contains(secret)), "the title's change holds it");
            Assert.IsTrue(changes.Any(c => c.Text.Contains(tag)), "the tags' change holds it");
        }
        var result = await (await admin.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/redact", new KbRedactRequest { Text = secret })).ReadAsync<KbRedactResult>();
        Assert.AreEqual(2, result.Revisions, "the first by its title, the second by its list of changes");
        Assert.AreEqual(2, result.Occurrences);
        await (await admin.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/redact", new KbRedactRequest { Text = tag })).ReadAsync<KbRedactResult>();
        await using var db = app.NewDb();
        var revisions = await db.KbRevisions.Where(r => r.PageId == page.Id).OrderBy(r => r.Number).ToListAsync();
        foreach (var r in revisions)
        {
            Assert.IsFalse(r.Title.Contains(secret) || r.Changes.Contains(secret), $"revision {r.Number}");
            Assert.IsFalse(r.Changes.Contains(tag) || r.Tags.Contains(tag), $"revision {r.Number}");
        }
        Assert.AreEqual("VPN zrh [redacted] → VPN zrh", KbRevisions.Changes(revisions[1].Changes).Single(c => c.Kind == KbChangeKinds.Title).Text);
        var shown = await (await viewer.GetAsync($"/api/v1/kb/pages/{page.Id}/revisions")).Content.ReadAsStringAsync();
        Assert.IsFalse(shown.Contains(secret) || shown.Contains(tag));
    }

    [TestMethod]
    public async Task Tags_are_normalised_confirmed_renamed_merged_and_deleted()
    {
        var a = Unique("ta");
        var b = Unique("tb");
        var page = (await CreateAsync(Request("Tagged page", "", $"#{a.ToUpperInvariant()}", b))).Page;
        CollectionAssert.AreEquivalent(new[] { a, b }, page.Tags.Select(t => t.Name).ToArray());
        var other = (await CreateAsync(Request("Other tagged page", "", a))).Page;
        // An AI tag stays one until confirmed (§6.4).
        await using (var db = app.NewDb())
        {
            var tagId = await db.KbTags.Where(t => t.NormalizedName == b).Select(t => t.Id).SingleAsync();
            await db.KbPageTags.Where(t => t.PageId == page.Id && t.TagId == tagId).ExecuteUpdateAsync(u => u.SetProperty(t => t.Source, KbTagSource.Ai));
        }
        page = await (await user.GetAsync($"/api/v1/kb/pages/{page.Number}")).ReadAsync<KbPageDetail>();
        var kept = await SaveAsync(page, r => r.AiTags = [b]);
        Assert.AreEqual(page.Revision, kept.Page.Revision, "nothing changed");
        var confirmed = await SaveAsync(page, r => r.AiTags = []);
        Assert.IsTrue(confirmed.Page.Tags.All(t => t.Source == KbTagSource.Manual));
        Assert.AreEqual("✓" + b, (await RevisionsAsync(page.Id))[0].Changes.Single(c => c.Kind == KbChangeKinds.Tags).Text);

        var tags = await (await viewer.GetAsync($"/api/v1/kb/tags?search={a}")).ReadAsync<List<KbTagItem>>();
        Assert.AreEqual(2, tags.Single().Pages);
        var renamed = Unique("tc");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.PutJsonAsync($"/api/v1/kb/tags/{tags[0].Id}", new KbTagRequest { Name = renamed })).StatusCode);
        var rename = await (await admin.PutJsonAsync($"/api/v1/kb/tags/{tags[0].Id}", new KbTagRequest { Name = renamed.ToUpperInvariant() })).ReadAsync<KbTagChange>();
        Assert.IsFalse(rename.Merged);
        Assert.AreEqual(renamed, rename.Tag.Name);
        // Into b: page has both, other gets b.
        var bId = (await (await viewer.GetAsync($"/api/v1/kb/tags?search={b}")).ReadAsync<List<KbTagItem>>()).Single().Id;
        var merge = await (await admin.PutJsonAsync($"/api/v1/kb/tags/{tags[0].Id}", new KbTagRequest { Name = b })).ReadAsync<KbTagChange>();
        Assert.IsTrue(merge.Merged);
        Assert.AreEqual(bId, merge.Tag.Id);
        Assert.AreEqual(2, merge.Tag.Pages);
        Assert.AreEqual(b, (await (await user.GetAsync($"/api/v1/kb/pages/{other.Number}")).ReadAsync<KbPageDetail>()).Tags.Single().Name);
        Assert.AreEqual(2, (await ListAsync($"q=%23{b}")).TotalCount, "the search text follows");

        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/kb/tags/{bId}")).StatusCode);
        Assert.IsEmpty((await (await user.GetAsync($"/api/v1/kb/pages/{other.Number}")).ReadAsync<KbPageDetail>()).Tags);
        var deletion = (await RevisionsAsync(other.Id))[0];
        Assert.AreEqual($"−{b}", deletion.Changes.Single().Text);
        Assert.AreEqual("The tag was deleted.", deletion.Note);
        await using var check = app.NewDb();
        Assert.IsTrue(await check.AuditLog.AnyAsync(l => l.EventType == AuditEvents.KbTagDeleted && l.Detail!.Contains(b)));
    }

    [TestMethod]
    public async Task Links_show_on_the_panels_with_the_locations_ancestors_and_go_with_their_target()
    {
        var (site, room) = await LocationsAsync();
        var host = await HostAsync(room);
        var sensor = await SensorAsync(host);
        var tag = Unique("t");
        async Task<KbPageDetail> Linked(string title, KbLinkKind kind, Guid id)
        {
            var request = Request(title, "", tag);
            request.Links = [new KbLinkRequest { Kind = kind, Id = id }];
            return (await CreateAsync(request)).Page;
        }
        var onSite = await Linked("Site page", KbLinkKind.Location, site);
        var onRoom = await Linked("Room page", KbLinkKind.Location, room);
        var onHost = await Linked("Host page", KbLinkKind.Host, host);
        var onSensor = await Linked("Sensor page", KbLinkKind.Sensor, sensor);
        var archived = await Linked("Archived host page", KbLinkKind.Host, host);
        await user.PostAsync($"/api/v1/kb/pages/{archived.Id}/archive", null);

        var forSensor = await (await viewer.GetAsync($"/api/v1/kb/related?sensorId={sensor}")).ReadAsync<KbRelated>();
        Assert.AreEqual(onSensor.Id, forSensor.Pages.Single().Id);
        CollectionAssert.AreEqual(new[] { "Host page", "Room page", "Site page" },
            forSensor.Via.Select(g => g.Pages.Single().Title).ToArray());
        CollectionAssert.AreEqual(new[] { KbLinkKind.Host, KbLinkKind.Location, KbLinkKind.Location }, forSensor.Via.Select(g => g.Kind).ToArray());
        var forHost = await (await viewer.GetAsync($"/api/v1/kb/related?hostId={host}")).ReadAsync<KbRelated>();
        Assert.AreEqual(onHost.Id, forHost.Pages.Single().Id);
        CollectionAssert.AreEqual(new[] { room, site }, forHost.Via.Select(g => g.Id!.Value).ToArray());
        var forSite = await (await viewer.GetAsync($"/api/v1/kb/related?locationId={site}")).ReadAsync<KbRelated>();
        Assert.AreEqual(onSite.Id, forSite.Pages.Single().Id);
        Assert.AreEqual("its hosts", forSite.Via.Single().Name, "the room isn't an ancestor; its host is the site's");
        var forRoom = await (await viewer.GetAsync($"/api/v1/kb/related?locationId={room}")).ReadAsync<KbRelated>();
        CollectionAssert.AreEqual(new[] { "Site page", "Host page" }, forRoom.Via.Select(g => g.Pages.Single().Title).ToArray());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await viewer.GetAsync($"/api/v1/kb/related?hostId={host}&sensorId={sensor}")).StatusCode);

        // The list's filter by location includes its sub-locations.
        Assert.AreEqual(2, (await ListAsync($"tags={tag}&location={site}")).TotalCount);
        Assert.AreEqual(1, (await ListAsync($"tags={tag}&host={host}")).TotalCount);
        Assert.AreEqual(onRoom.Links.Single().Detail, (await LocationTreeAsync()).Path(room));

        // Deleting the sensor through ExecuteDelete takes its links (§6.5).
        await using var db = app.NewDb();
        await db.Sensors.Where(s => s.Id == sensor).ExecuteDeleteAsync();
        Assert.IsFalse(await db.KbLinks.AnyAsync(l => l.SensorId == sensor));
        Assert.IsEmpty((await (await user.GetAsync($"/api/v1/kb/pages/{onSensor.Number}")).ReadAsync<KbPageDetail>()).Links);
    }

    [TestMethod]
    public async Task Page_links_give_linked_from_and_a_missing_page()
    {
        var target = (await CreateAsync(Request("Link target", ""))).Page;
        var source = (await CreateAsync(Request("Link source", $"See [the target](/pnmt-admin/kb/{target.Number}) and [gone](/pnmt-admin/kb/999999).")))
            .Page;
        CollectionAssert.AreEqual(new[] { target.Number, 999999 }, source.LinksTo.Select(r => r.Number).ToArray());
        Assert.IsNull(source.LinksTo[1].Title, "a missing page");
        var linked = await (await viewer.GetAsync($"/api/v1/kb/pages/{target.Number}")).ReadAsync<KbPageDetail>();
        Assert.AreEqual(source.Number, linked.LinkedFrom.Single().Number);
        await SaveAsync(source, r => r.Content = "No links any more.");
        Assert.IsEmpty((await (await viewer.GetAsync($"/api/v1/kb/pages/{target.Number}")).ReadAsync<KbPageDetail>()).LinkedFrom);
    }

    [TestMethod]
    public async Task A_page_lists_the_files_its_content_uses()
    {
        var image = await FileAsync(FileKind.Image);
        var attachment = await FileAsync(FileKind.Attachment);
        var page = (await CreateAsync(Request("With files", $"![x](/api/v1/files/{image}/x.png)\n[manual.pdf](/api/v1/files/{attachment}/manual.pdf)"))).Page;
        CollectionAssert.AreEqual(new[] { image, attachment }, page.Files.Select(f => f.Id).ToArray());
        Assert.AreEqual($"/api/v1/files/{attachment}/manual.pdf", page.Files[1].Url);
        var revision = (await RevisionsAsync(page.Id))[0];
        Assert.AreEqual("+2", revision.Changes.Single(c => c.Kind == KbChangeKinds.Files).Text);
        var tagged = Unique("t");
        var withAttachment = (await CreateAsync(Request("Attachment row", $"[m](/api/v1/files/{attachment}/m.pdf)", tagged))).Page;
        Assert.IsTrue((await ListAsync($"tags={tagged}")).Items.Single().HasAttachments);
        _ = withAttachment;
    }

    [TestMethod]
    public async Task Views_count_once_per_person_and_half_hour_and_order_the_list()
    {
        var tag = Unique("t");
        var quiet = (await CreateAsync(Request("Quiet page", "", tag))).Page;
        var popular = (await CreateAsync(Request("Popular page", "", tag))).Page;
        foreach (var client in new[] { viewer, viewer, user, admin })
        {
            Assert.AreEqual(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/kb/pages/{popular.Id}/views", null)).StatusCode);
        }
        var read = await (await viewer.GetAsync($"/api/v1/kb/pages/{popular.Number}")).ReadAsync<KbPageDetail>();
        Assert.AreEqual(3, read.Views);
        var list = await ListAsync($"tags={tag}&sort=views");
        CollectionAssert.AreEqual(new[] { popular.Id, quiet.Id }, list.Items.Select(i => i.Id).ToArray());
        Assert.AreEqual(3, list.Items[0].RecentViews);
        Assert.AreEqual(3, (await ListAsync($"tags={tag}&sort=views&period=all")).Items[0].Views);
        await using var db = app.NewDb();
        Assert.AreEqual(3, await db.KbPageViews.Where(v => v.PageId == popular.Id).SumAsync(v => v.Views));
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.PostAsync($"/api/v1/kb/pages/{Guid.NewGuid()}/views", null)).StatusCode);
    }

    [TestMethod]
    public async Task The_lists_orders_and_filters()
    {
        var tag = Unique("t");
        var first = (await CreateAsync(Request("Bravo page", "", tag))).Page;
        var second = (await CreateAsync(Request("Alpha page", "", tag, "second"))).Page;
        await SaveAsync(first, r => { r.Content = "edit one"; r.Status = KbPageStatus.Draft; });
        first = await (await user.GetAsync($"/api/v1/kb/pages/{first.Number}")).ReadAsync<KbPageDetail>();
        await SaveAsync(first, r => r.Content = "edit two");
        async Task<Guid[]> Order(string query) => (await ListAsync($"tags={tag}&{query}")).Items.Select(i => i.Id).ToArray();
        CollectionAssert.AreEqual(new[] { second.Id, first.Id }, await Order("sort=newest"));
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, await Order("sort=updated"));
        CollectionAssert.AreEqual(new[] { second.Id, first.Id }, await Order("sort=title"));
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, await Order("sort=edits"));
        var edits = (await ListAsync($"tags={tag}&sort=edits&period=all")).Items[0];
        Assert.AreEqual(3, edits.Edits);
        Assert.AreEqual(3, edits.RecentEdits);
        CollectionAssert.AreEqual(new[] { first.Id }, await Order("status=Draft"));
        CollectionAssert.AreEqual(new[] { second.Id }, (await ListAsync($"tags={tag},second")).Items.Select(i => i.Id).ToArray(), "several tags narrow the list");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/kb/pages?sort=relevance")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/kb/pages?sort=random")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/kb/pages?period=week")).StatusCode);
    }

    [TestMethod]
    public async Task The_content_search_ranks_a_title_before_the_content_and_marks_its_snippets()
    {
        var word = "Druckerwarteschlange" + Guid.NewGuid().ToString("N")[..6];
        var inContent = (await CreateAsync(Request("About queues", $"Long text first. Then {word} stalls, and more text after it."))).Page;
        var inTitle = (await CreateAsync(Request($"The {word}", "Nothing here."))).Page;
        var found = await ListAsync($"q={Uri.EscapeDataString(word[..14])}");
        CollectionAssert.AreEqual(new[] { inTitle.Id, inContent.Id }, found.Items.Select(i => i.Id).ToArray(), "every word a prefix; the title first");
        var snippet = found.Items[1].Snippet!;
        StringAssert.Contains(snippet, $"{KbSearch.MarkStart}{word}{KbSearch.MarkEnd}");
        Assert.IsFalse(snippet.Contains('<'));
        Assert.IsNull(found.Items[0].Snippet, "a title match: the summary is shown instead");
        Assert.AreEqual(1, (await ListAsync($"q={Uri.EscapeDataString($"{word} -queues")}")).TotalCount);
        Assert.AreEqual(1, (await ListAsync($"q={Uri.EscapeDataString($"\"the {word}\"")}")).TotalCount);
        Assert.AreEqual(2, (await ListAsync($"q={Uri.EscapeDataString($"{word} or nothingatall")}")).TotalCount);
    }

    [TestMethod]
    public async Task Others_editing_a_page_are_shown_for_ninety_seconds()
    {
        var page = (await CreateAsync(Request("Edited by two", ""))).Page;
        Assert.IsEmpty(await (await user.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/editing", new KbEditingRequest())).ReadAsync<List<KbEditor>>());
        var others = await (await admin.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/editing", new KbEditingRequest())).ReadAsync<List<KbEditor>>();
        Assert.AreEqual("test-user", others.Single().Name);
        Assert.AreEqual("test-admin", (await (await user.GetAsync($"/api/v1/kb/pages/{page.Number}")).ReadAsync<KbPageDetail>()).Editing.Single().Name);
        await admin.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/editing", new KbEditingRequest { Stopped = true });
        Assert.IsEmpty((await (await user.GetAsync($"/api/v1/kb/pages/{page.Number}")).ReadAsync<KbPageDetail>()).Editing);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/editing", new KbEditingRequest())).StatusCode);
    }

    [TestMethod]
    public async Task New_credentials_in_a_save_are_warned_about_once()
    {
        var page = (await CreateAsync(Request("Password policy", "Passwords need 12 characters and a password manager."))).Page;
        var request = From(page);
        request.Content = page.Content + "\nThe old router: password: Sommer2024!x";
        var warned = await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", request);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, warned.StatusCode);
        var problem = await warned.ProblemAsync();
        Assert.AreEqual("looks_like_credential", problem.GetProperty("code").GetString());
        Assert.AreEqual(2, problem.GetProperty("lines")[0].GetInt32());
        Assert.IsFalse(problem.GetRawText().Contains("Sommer2024"));
        request.AcknowledgeCredentials = true;
        var saved = await (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", request)).ReadAsync<KbSaveResult>();
        // The same line again isn't new.
        var again = From(saved.Page);
        again.Content += "\nMore text.";
        Assert.AreEqual(HttpStatusCode.OK, (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", again)).StatusCode);
        // Mark as sensitive and save.
        var other = (await CreateAsync(Request("Another router", ""))).Page;
        var marking = From(other);
        marking.Content = "password: Winter2025!y";
        marking.MarkSensitive = true;
        var marked = await (await user.PutJsonAsync($"/api/v1/kb/pages/{other.Id}", marking)).ReadAsync<KbSaveResult>();
        Assert.IsTrue(marked.Page.Sensitive);
        Assert.IsTrue((await RevisionsAsync(other.Id))[0].Changes.Any(c => c.Kind == KbChangeKinds.Sensitive && c.Text == "marked"));
    }

    // --- the sensitive mark and Written with AI (docs/knowledge-base-extensions-plan.md §4, §5, §15) ---------------------

    [TestMethod]
    public async Task A_save_without_the_new_fields_keeps_the_sensitive_mark_and_written_with_ai()
    {
        var tag = Unique("t");
        var created = await CreateAsync(new KbPageRequest { Title = "Old callers", Content = "one", Tags = [tag], MarkSensitive = true, WrittenWithAi = true });
        Assert.IsTrue(created.Page.Sensitive);
        Assert.IsTrue(created.Page.WrittenWithAi);
        CollectionAssert.IsSubsetOf(new[] { KbChangeKinds.Sensitive, KbChangeKinds.WrittenWithAi },
            (await RevisionsAsync(created.Page.Id))[0].Changes.Select(c => c.Kind).ToArray());
        // As the page view's quick saves and MCP's kb_page_update send it: every old field, none of the new ones.
        var saved = await SaveAsync(created.Page, r => r.Content = "one\ntwo");
        Assert.AreEqual(2, saved.Page.Revision);
        Assert.IsTrue(saved.Page.Sensitive);
        Assert.IsTrue(saved.Page.WrittenWithAi);
        Assert.IsFalse((await RevisionsAsync(created.Page.Id))[0].Changes.Any(c => c.Kind is KbChangeKinds.Sensitive or KbChangeKinds.WrittenWithAi));
    }

    [TestMethod]
    public async Task The_editing_page_marks_and_unmarks_sensitive_with_the_save_and_the_statements_cover_the_saved_text()
    {
        var page = (await CreateAsync(Request("Switch in the details", "text", Unique("t")))).Page;
        var marked = await SaveAsync(page, r => r.Sensitive = true);
        Assert.IsTrue(marked.Page.Sensitive);
        Assert.AreEqual(2, marked.Page.Revision);
        Assert.IsFalse(marked.AskAiTags);

        var unmark = From(marked.Page);
        unmark.Content = "text, rewritten";
        unmark.Sensitive = false;
        var refused = await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", unmark);
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.IsTrue((await refused.ProblemAsync()).GetProperty("errors").TryGetProperty("statements", out _));
        unmark.Statements = new KbStatements { NoCredentials = true };
        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", unmark)).StatusCode);

        unmark.Statements = new KbStatements { NoCredentials = true, NoPersonalData = true };
        var unmarked = await (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}", unmark)).ReadAsync<KbSaveResult>();
        Assert.IsFalse(unmarked.Page.Sensitive);
        var revision = (await RevisionsAsync(page.Id))[0];
        Assert.AreEqual(3, revision.Number);
        Assert.IsTrue(revision.Changes.Any(c => c.Kind == KbChangeKinds.Sensitive && c.Text == "unmarked"));
        Assert.IsTrue(revision.Changes.Any(c => c.Kind == KbChangeKinds.Content));
        // The declaration is for the text this save wrote, revision 3, not the one before.
        Assert.AreEqual(3, revision.Declaration!.Revision);
        CollectionAssert.AreEquivalent(KbStatementNames.Both.ToArray(), revision.Declaration.Statements.ToArray());
        await using var db = app.NewDb();
        Assert.AreEqual(2, await db.AuditLog.CountAsync(l => l.EventType == AuditEvents.KbPageSensitivityChanged && l.Detail!.StartsWith($"KB-{page.Number} ")));
        // Unmarking a page that isn't sensitive changes nothing, and needs no statements.
        var again = await SaveAsync(unmarked.Page, r => r.Sensitive = false);
        Assert.AreEqual(3, again.Page.Revision);
    }

    [TestMethod]
    public async Task Written_with_ai_is_kept_in_the_revisions_and_the_list_filters_by_it_and_by_sensitive()
    {
        var tag = Unique("t");
        var ai = (await CreateAsync(new KbPageRequest { Title = "Mostly AI", Content = "a", Tags = [tag], WrittenWithAi = true })).Page;
        var mine = (await CreateAsync(new KbPageRequest { Title = "By hand", Content = "b", Tags = [tag], MarkSensitive = true })).Page;
        Assert.IsTrue(ai.WrittenWithAi);
        Assert.IsFalse(mine.WrittenWithAi);

        var unchecked_ = await SaveAsync(ai, r => r.WrittenWithAi = false);
        Assert.IsFalse(unchecked_.Page.WrittenWithAi);
        var revision = (await RevisionsAsync(ai.Id))[0];
        Assert.IsTrue(revision.Changes.Any(c => c.Kind == KbChangeKinds.WrittenWithAi && c.Text == "unmarked"));
        Assert.IsFalse(revision.IsEdit);
        var first = await (await viewer.GetAsync($"/api/v1/kb/pages/{ai.Id}/revisions/1")).ReadAsync<KbRevisionDetail>();
        Assert.IsTrue(first.WrittenWithAi);
        await SaveAsync(unchecked_.Page, r => r.WrittenWithAi = true);

        CollectionAssert.AreEqual(new[] { ai.Number }, (await ListAsync($"tags={tag}&ai=true")).Items.Select(i => i.Number).ToArray());
        CollectionAssert.AreEqual(new[] { mine.Number }, (await ListAsync($"tags={tag}&ai=false")).Items.Select(i => i.Number).ToArray());
        CollectionAssert.AreEqual(new[] { mine.Number }, (await ListAsync($"tags={tag}&sensitive=true")).Items.Select(i => i.Number).ToArray());
        var both = (await ListAsync($"tags={tag}&sensitive=false")).Items.Single();
        Assert.AreEqual(ai.Number, both.Number);
        Assert.IsTrue(both.WrittenWithAi);
    }

    [TestMethod]
    public async Task A_link_added_by_a_save_counts_once_in_its_revision()
    {
        var host = await HostAsync();
        var page = (await CreateAsync(Request("One link, counted once", "text", Unique("t")))).Page;
        var saved = await SaveAsync(page, r => r.Links = [new KbLinkRequest { Kind = KbLinkKind.Host, Id = host }]);
        Assert.HasCount(1, saved.Page.Links);
        var revision = (await RevisionsAsync(page.Id))[0];
        Assert.AreEqual("+1 −0", revision.Changes.Single(c => c.Kind == KbChangeKinds.Links).Text);
        var stored = await (await viewer.GetAsync($"/api/v1/kb/pages/{page.Id}/revisions/{revision.Number}")).ReadAsync<KbRevisionDetail>();
        Assert.HasCount(1, stored.Links, "the revision's snapshot holds the link once");
    }

    // --- fixtures ------------------------------------------------------------------------------------------------------

    private static async Task<Paged<KbPageListItem>> ListAsync(string query) =>
        await (await viewer.GetAsync($"/api/v1/kb/pages?{query}")).ReadAsync<Paged<KbPageListItem>>();

    internal static async Task<Guid> HostAsync(Guid? location = null, HttpClient? client = null)
    {
        var host = await (await (client ?? user).PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"kb-host-{Guid.NewGuid():N}"[..20] }))
            .ReadAsync<HostDetail>();
        if (location is { } l)
        {
            await using var db = app.NewDb();
            await db.Hosts.Where(h => h.Id == host.Id).ExecuteUpdateAsync(u => u.SetProperty(h => h.LocationId, l));
        }
        return host.Id;
    }

    private static async Task<Guid> SensorAsync(Guid host)
    {
        var sensor = await (await user.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = host, Type = Lib.Models.Enum.SensorType.SIMPLE, Name = "kb sensor",
        })).ReadAsync<SensorDetail>();
        return sensor.Id;
    }

    /// <summary>A site and a room in a building in it, written directly; the tree is read again.</summary>
    internal static async Task<(Guid Site, Guid Room)> LocationsAsync(TestApp? on = null)
    {
        on ??= app;
        var now = Clock.UtcNow;
        LocationEntity Location(string name, Guid? parent) => new()
        {
            Id = Guid.NewGuid(), Name = name, NormalizedName = name.ToLowerInvariant(), ParentId = parent, CreatedAt = now, UpdatedAt = now,
        };
        var site = Location($"Site {Guid.NewGuid():N}"[..20], null);
        var building = Location("Building A", site.Id);
        var room = Location("Room 2.13", building.Id);
        await using var db = on.NewDb();
        db.Locations.AddRange(site, building, room);
        await db.SaveChangesAsync();
        on.Services.GetRequiredService<LocationTree>().Invalidate();
        return (site.Id, room.Id);
    }

    private static async Task<LocationTreeSnapshot> LocationTreeAsync() => await app.Services.GetRequiredService<LocationTree>().GetAsync();

    /// <summary>A file's row only: the pages need its metadata, not its bytes.</summary>
    internal static async Task<Guid> FileAsync(FileKind kind, TestApp? on = null)
    {
        var id = Guid.NewGuid();
        await using var db = (on ?? app).NewDb();
        db.Files.Add(new FileEntity
        {
            Id = id, Kind = kind, Name = kind == FileKind.Image ? "x.png" : "manual.pdf", ContentType = kind == FileKind.Image ? "image/png" : "application/pdf",
            Size = 1234, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(id.ToByteArray())),
            Path = $"2026/11/{id}-x", CreatedAt = Clock.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }
}
