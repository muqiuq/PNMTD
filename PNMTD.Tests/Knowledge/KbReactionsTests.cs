using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Api.V1.Contracts;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Notifications;

namespace PNMTD.Tests.Knowledge;

/// <summary>
/// Likes and <i>Used to solve a problem</i> (docs/knowledge-base-extensions-plan.md §9, §16): a like toggles once per person and
/// counts two when two like at once; a use is an event, again after 12 hours, undone within 10 minutes, with a note within a day;
/// archived pages take neither; each tells the page's authors and its undoing cancels that; the e-mails, the orders, and redacting a
/// note. Viewers like pages and record uses too.
/// </summary>
[TestClass]
public sealed class KbReactionsTests
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

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    /// <summary>Created by the user, edited by the admin: both are its authors.</summary>
    private static async Task<KbPageDetail> PageAsync(string title, string tag)
    {
        var created = (await (await user.PostJsonAsync("/api/v1/kb/pages", KbPageTests.Request(title, "text", tag))).ReadAsync<KbSaveResult>()).Page;
        var request = KbPageTests.From(created);
        request.Content = "text, edited";
        return (await (await admin.PutJsonAsync($"/api/v1/kb/pages/{created.Id}", request)).ReadAsync<KbSaveResult>()).Page;
    }

    private static Task<HttpResponseMessage> LikeAsync(HttpClient client, KbPageDetail page, bool liked) =>
        client.PutJsonAsync($"/api/v1/kb/pages/{page.Id}/like", new KbLikeRequest { Liked = liked });

    private static Task<HttpResponseMessage> UseAsync(HttpClient client, KbPageDetail page, string? note = null) =>
        client.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/uses", new KbUseRequest { Note = note });

    private static async Task<List<UserNotificationEntity>> NoticesAsync(KbPageDetail page)
    {
        await using var db = app.NewDb();
        return await db.UserNotifications.AsNoTracking().Where(n => n.SubjectId == page.Id).OrderBy(n => n.Id).ToListAsync();
    }

    private static FakeTimeProvider Freeze()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero));
        Clock.Provider = time;
        return time;
    }

    [TestMethod]
    public async Task A_like_toggles_once_per_person_and_tells_the_authors_and_unliking_cancels_that()
    {
        var page = await PageAsync("Liked page", "likes");
        var liked = await (await LikeAsync(viewer, page, true)).ReadAsync<KbReactions>();
        Assert.AreEqual(1, liked.Likes);
        Assert.IsTrue(liked.LikedByMe);
        Assert.AreEqual(1, (await (await LikeAsync(viewer, page, true)).ReadAsync<KbReactions>()).Likes, "a second like changes nothing");
        var notices = await NoticesAsync(page);
        CollectionAssert.AreEquivalent(new[] { TestUsers.User, TestUsers.Admin }, notices.Select(n => n.UserId).ToArray(), "its creator and its editor");
        Assert.IsTrue(notices.All(n => n.Kind == UserNotificationKinds.KbLiked && n.ActorName == "test-viewer" && n.State == UserNotificationState.Pending));

        // The author's own like tells only the other author.
        await (await LikeAsync(user, page, true)).ReadAsync<KbReactions>();
        Assert.AreEqual(3, (await NoticesAsync(page)).Count);
        var read = await (await admin.GetAsync($"/api/v1/kb/pages/{page.Number}")).ReadAsync<KbPageDetail>();
        Assert.AreEqual(2, read.Reactions.Likes);
        Assert.IsFalse(read.Reactions.LikedByMe);
        Assert.AreEqual(page.Revision, read.Revision, "a like is no revision");
        Assert.AreEqual(page.UpdatedAt, read.UpdatedAt);
        var likes = await (await admin.GetAsync($"/api/v1/kb/pages/{page.Id}/likes")).ReadAsync<List<KbLikeItem>>();
        CollectionAssert.AreEquivalent(new[] { "test-viewer", "test-user" }, likes.Select(l => l.ByName).ToArray());

        var unliked = await (await LikeAsync(viewer, page, false)).ReadAsync<KbReactions>();
        Assert.AreEqual(1, unliked.Likes);
        Assert.IsFalse(unliked.LikedByMe);
        Assert.AreEqual(2, (await NoticesAsync(page)).Count(n => n.ActorName == "test-viewer" && n.State == UserNotificationState.Cancelled));
        Assert.AreEqual(1, (await (await LikeAsync(viewer, page, false)).ReadAsync<KbReactions>()).Likes, "unliking again changes nothing");
    }

    [TestMethod]
    public async Task Two_people_liking_at_once_count_two()
    {
        var page = await PageAsync("Liked at once", "likes");
        var both = await Task.WhenAll(LikeAsync(viewer, page, true), LikeAsync(admin, page, true), LikeAsync(viewer, page, true));
        Assert.IsTrue(both.All(r => r.StatusCode == HttpStatusCode.OK));
        await using var db = app.NewDb();
        Assert.AreEqual(2, (await db.KbPages.SingleAsync(p => p.Id == page.Id)).Likes);
        Assert.AreEqual(2, await db.KbPageLikes.CountAsync(l => l.PageId == page.Id));
    }

    [TestMethod]
    public async Task A_use_is_an_event_again_after_twelve_hours_undone_within_ten_minutes_with_a_note_within_a_day()
    {
        var time = Freeze();
        var page = await PageAsync("Used page", "uses");
        var first = await UseAsync(viewer, page, "  printer on the 2nd floor  ");
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        var used = await first.ReadAsync<KbReactions>();
        Assert.AreEqual(1, used.Uses);
        Assert.AreEqual(1, used.People);
        Assert.AreEqual("printer on the 2nd floor", used.MyLastUse!.Note);
        Assert.IsTrue(used.MyLastUse.CanUndo);
        Assert.AreEqual(Clock.UtcNow.AddHours(12), used.CanUseAgainAt);
        var notice = (await NoticesAsync(page)).First(n => n.Kind == UserNotificationKinds.KbUsed);
        Assert.AreEqual("printer on the 2nd floor", notice.Detail);

        var again = await UseAsync(viewer, page);
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        Assert.AreEqual(KbReactionService.UsedRecently, (await again.ProblemAsync()).GetProperty("code").GetString());

        // A note added later reaches the notification that hasn't gone out.
        time.Advance(TimeSpan.FromHours(2));
        var noted = await (await viewer.PutJsonAsync($"/api/v1/kb/pages/{page.Id}/uses/{used.MyLastUse.Id}", new KbUseRequest { Note = "the queue was stuck" }))
            .ReadAsync<KbReactions>();
        Assert.AreEqual("the queue was stuck", noted.MyLastUse!.Note);
        Assert.IsTrue((await NoticesAsync(page)).Where(n => n.Kind == UserNotificationKinds.KbUsed).All(n => n.Detail == "the queue was stuck"));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await admin.PutJsonAsync($"/api/v1/kb/pages/{page.Id}/uses/{used.MyLastUse.Id}", new KbUseRequest { Note = "x" })).StatusCode,
            "only one's own");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/v1/kb/pages/{page.Id}/uses/{used.MyLastUse.Id}")).StatusCode, "too late to undo");

        time.Advance(TimeSpan.FromHours(10));
        var second = await (await UseAsync(viewer, page)).ReadAsync<KbReactions>();
        Assert.AreEqual(2, second.Uses);
        Assert.AreEqual(1, second.People);
        var other = await (await UseAsync(admin, page, "by another")).ReadAsync<KbReactions>();
        Assert.AreEqual(3, other.Uses);
        Assert.AreEqual(2, other.People);

        // The second use undone in its ten minutes: the counter, the last use and its notifications follow.
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await admin.DeleteAsync($"/api/v1/kb/pages/{page.Id}/uses/{second.MyLastUse!.Id}")).StatusCode, "not one's own");
        var undone = await (await viewer.DeleteAsync($"/api/v1/kb/pages/{page.Id}/uses/{second.MyLastUse.Id}")).ReadAsync<KbReactions>();
        Assert.AreEqual(2, undone.Uses);
        Assert.AreEqual(used.MyLastUse.Id, undone.MyLastUse!.Id);
        Assert.AreEqual(other.LastUsedAt, undone.LastUsedAt);
        Assert.IsTrue((await NoticesAsync(page)).Where(n => n.SourceId == second.MyLastUse.Id).All(n => n.State == UserNotificationState.Cancelled));
        var uses = await (await user.GetAsync($"/api/v1/kb/pages/{page.Id}/uses")).ReadAsync<Paged<KbUseItem>>();
        CollectionAssert.AreEqual(new[] { "test-admin", "test-viewer" }, uses.Items.Select(u => u.ByName).ToArray());
    }

    [TestMethod]
    public async Task An_archived_page_takes_neither_and_the_list_orders_by_likes_and_uses()
    {
        var tag = $"r{Guid.NewGuid():N}"[..10];
        var few = await PageAsync("Liked once", tag);
        var many = await PageAsync("Liked twice", tag);
        var none = await PageAsync("Not liked", tag);
        await (await LikeAsync(viewer, few, true)).ReadAsync<KbReactions>();
        await (await LikeAsync(viewer, many, true)).ReadAsync<KbReactions>();
        await (await LikeAsync(admin, many, true)).ReadAsync<KbReactions>();
        await (await UseAsync(viewer, none)).ReadAsync<KbReactions>();
        var byLikes = await (await viewer.GetAsync($"/api/v1/kb/pages?tags={tag}&sort=likes&period=all")).ReadAsync<Paged<KbPageListItem>>();
        CollectionAssert.AreEqual(new[] { many.Number, few.Number, none.Number }, byLikes.Items.Select(i => i.Number).ToArray());
        Assert.AreEqual(2, byLikes.Items[0].Likes);
        Assert.AreEqual(2, byLikes.Items[0].RecentLikes);
        var byUses = await (await viewer.GetAsync($"/api/v1/kb/pages?tags={tag}&sort=uses")).ReadAsync<Paged<KbPageListItem>>();
        Assert.AreEqual(none.Number, byUses.Items[0].Number);
        Assert.AreEqual(1, byUses.Items[0].RecentUses);

        await (await user.PostAsync($"/api/v1/kb/pages/{few.Id}/archive", null)).ReadAsync<KbPageDetail>();
        Assert.AreEqual(HttpStatusCode.Conflict, (await LikeAsync(admin, few, true)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await UseAsync(admin, few)).StatusCode);
    }

    [TestMethod]
    public async Task The_emails_name_who_and_a_sensitive_page_only_by_its_number()
    {
        var page = await PageAsync("Printer queue", "uses");
        var rows = new List<UserNotificationEntity>
        {
            new() { ActorName = "Anna", Detail = "printer on the 2nd floor", CreatedAt = DateTime.UtcNow, Kind = UserNotificationKinds.KbUsed },
            new() { ActorName = "Ben", CreatedAt = DateTime.UtcNow, Kind = UserNotificationKinds.KbUsed },
        };
        var used = UserNotificationKinds.Find(UserNotificationKinds.KbUsed)!;
        await using var db = app.NewDb();
        var mail = (await new KbUsedNotice().RenderAsync(db, new UserNotificationBatch(used, TestUsers.User, "Test User", page.Id, rows), "https://pnmt.example.com",
            CancellationToken.None))!;
        Assert.AreEqual($"PNMT: Anna and Ben used KB-{page.Number} “Printer queue” to solve a problem (2 times)", mail.Subject);
        StringAssert.Contains(mail.Body, "- Anna, ");
        StringAssert.Contains(mail.Body, ": printer on the 2nd floor");
        StringAssert.EndsWith(mail.Body, $"https://pnmt.example.com/pnmt-admin/kb/{page.Number}");
        Assert.AreEqual($"you wrote or edited KB-{page.Number}", mail.Because);

        await (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}/sensitive", new KbSensitiveRequest { Sensitive = true })).ReadAsync<KbPageDetail>();
        var hidden = (await new KbUsedNotice().RenderAsync(db, new UserNotificationBatch(used, TestUsers.User, "Test User", page.Id, rows), null, CancellationToken.None))!;
        Assert.IsFalse(hidden.Subject.Contains("Printer queue") || hidden.Body.Contains("Printer queue") || hidden.Body.Contains("2nd floor"),
            hidden.Subject + "\n" + hidden.Body);
        StringAssert.Contains(hidden.Subject, $"KB-{page.Number}");
        Assert.AreEqual("Anna, Ben and 2 others", KbReactionNotice.Names(["Anna", "Ben", "Anna", "Eve", "Max"]));
    }

    [TestMethod]
    public async Task Redacting_reaches_the_notes_of_the_uses()
    {
        var page = await PageAsync("A note with a secret", "uses");
        var secret = $"Hunter{Guid.NewGuid():N}"[..14];
        await (await UseAsync(viewer, page, $"the password is {secret}")).ReadAsync<KbReactions>();
        var result = await (await admin.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/redact", new KbRedactRequest { Text = secret })).ReadAsync<KbRedactResult>();
        Assert.AreEqual(0, result.Revisions);
        await using var db = app.NewDb();
        Assert.AreEqual("the password is [redacted]", (await db.KbPageUses.SingleAsync(u => u.PageId == page.Id)).Note);
        Assert.IsFalse(await db.UserNotifications.AnyAsync(n => n.SubjectId == page.Id && n.Detail!.Contains(secret)));
    }
}
