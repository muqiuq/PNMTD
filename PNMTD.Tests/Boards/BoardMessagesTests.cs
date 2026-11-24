using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Api.V1.Contracts;
using PNMTD.Boards;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Boards;

/// <summary>
/// Board messages (docs/board-messages-plan.md §4, §6, §7, §12): sending with its rules and rights, "all boards", 10 open per board, the
/// board's poll with its stamp and 304, the time left from the server's clock, withdrawing, the cleanup, the audit, and the rate limit per
/// address and board.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BoardMessagesTests
{
    private const int PerMinute = 30;
    private static TestApp app = null!;
    private static HttpClient admin = null!;

    /// <summary>A sender of its own per test: the Debug page's limit is 10 sends a minute per user.</summary>
    private static HttpClient sender = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?> { ["Boards:RequestsPerMinute"] = PerMinute.ToString() });
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public async Task Reset()
    {
        Clock.Provider = TimeProvider.System;
        sender = app.ClientWithRights("Boards:Message");
        app.Services.GetRequiredService<BoardGuard>().Reset();
        app.Services.GetRequiredService<BoardRuntime>().Reset();
        // Other runs' and tests' messages would count against "10 open" and "all boards".
        await using var db = app.NewDb();
        await db.BoardMessages.ExecuteDeleteAsync();
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    private sealed record Board(BoardDetail Detail, string Token);

    private static async Task<Board> CreateAsync(bool messages = true, bool enabled = true, BoardPeople people = BoardPeople.Names)
    {
        var created = await (await admin.PostJsonAsync("/api/v1/boards", new { name = V1.Unique("msg"), columns = 1, from = "empty" })).ReadAsync<BoardDetail>();
        var layout = created.Layout with { Look = created.Layout.Look with { Messages = messages, People = people } };
        using var saved = await admin.PutJsonAsync($"/api/v1/boards/{created.Id}", new
        {
            name = created.Name, description = (string?)null, enabled, networks = Array.Empty<object>(), layout, revision = created.Revision,
        });
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        var url = await (await admin.GetAsync($"/api/v1/boards/{created.Id}/url")).ReadAsync<BoardUrl>();
        return new Board(await saved.ReadAsync<BoardDetail>(), url.Url[(url.Url.LastIndexOf('/') + 1)..]);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, object body) => client.PostJsonAsync("/api/v1/boards/messages", body);

    private static async Task<BoardMessageItem> SentAsync(object body)
    {
        using var response = await SendAsync(sender, body);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<BoardMessageItem>();
    }

    private static async Task<HttpResponseMessage> PollAsync(Board board, string? etag = null, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/pnmt-board/api/messages");
        request.Headers.TryAddWithoutValidation("Authorization", $"Board {token ?? board.Token}");
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }
        return await app.Client().SendAsync(request);
    }

    private static async Task<IReadOnlyList<BoardMessageView>> ShownAsync(Board board) =>
        (await (await PollAsync(board)).ReadAsync<BoardMessages>()).Messages;

    [TestMethod]
    public async Task AMessageGoesToItsBoardsWithinItsRules()
    {
        var noc = await CreateAsync();
        var helpdesk = await CreateAsync(people: BoardPeople.Initials);
        var quiet = await CreateAsync(messages: false);
        var off = await CreateAsync(enabled: false);

        // The fields' rules.
        foreach (var bad in new object[]
        {
            new { boardIds = new[] { noc.Detail.Id }, title = "", seconds = 60 },
            new { boardIds = new[] { noc.Detail.Id }, title = new string('t', 101), seconds = 60 },
            new { boardIds = new[] { noc.Detail.Id }, title = "Two\nlines", seconds = 60 },
            new { boardIds = new[] { noc.Detail.Id }, title = "Long", text = new string('x', 1001), seconds = 60 },
            new { boardIds = new[] { noc.Detail.Id }, title = "Short", seconds = 5 },
            new { boardIds = new[] { noc.Detail.Id }, title = "Too long", seconds = 3601 },
            new { boardIds = Array.Empty<Guid>(), title = "Nowhere", seconds = 60 },
        })
        {
            using var response = await SendAsync(app.ClientWithRights("Boards:Message"), bad);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        foreach (var (target, code) in new[] { (quiet, "board_no_messages"), (off, "board_disabled") })
        {
            using var response = await SendAsync(sender, new { boardIds = new[] { target.Detail.Id }, title = "No", seconds = 60 });
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), code);
        }

        // Rights: View reads, Message sends.
        using (var viewer = await SendAsync(app.ClientWithRights("Boards:View"), new { boardIds = new[] { noc.Detail.Id }, title = "No", seconds = 60 }))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, viewer.StatusCode);
        }
        using (var messenger = await SendAsync(app.ClientWithRights("Boards:Message"),
            new { boardIds = new[] { noc.Detail.Id, helpdesk.Detail.Id }, kind = "Warning", title = "Mail delays", text = "About **20 min**.", seconds = 60 }))
        {
            Assert.AreEqual(HttpStatusCode.Created, messenger.StatusCode, await messenger.Content.ReadAsStringAsync());
            var item = await messenger.ReadAsync<BoardMessageItem>();
            CollectionAssert.AreEquivalent(new[] { noc.Detail.Name, helpdesk.Detail.Name }, item.Boards.Select(b => b.Name).ToArray());
            Assert.IsTrue(item.Open);
        }
        var listed = await (await app.ClientWithRights("Boards:View").GetAsync("/api/v1/boards/messages?open=true")).ReadAsync<List<BoardMessageItem>>();
        Assert.AreEqual("Mail delays", listed.Single().Title);

        // On the boards: who sent it as each names people; not on the others.
        var shown = (await ShownAsync(noc)).Single();
        Assert.AreEqual((BoardMessageKind.Warning, "Mail delays", "About **20 min**."), (shown.Kind, shown.Title, shown.Text));
        var initials = (await ShownAsync(helpdesk)).Single().From;
        Assert.IsTrue(initials is { Length: >= 1 and <= 3 } && initials == initials.ToUpperInvariant(), initials);
        Assert.AreNotEqual(shown.From, initials);
        Assert.AreEqual(0, (await ShownAsync(quiet)).Count, "messages off: none");

        // The most important first, then the newest.
        await SentAsync(new { boardIds = new[] { noc.Detail.Id }, kind = "Information", title = "Lunch", seconds = 60 });
        await SentAsync(new { boardIds = new[] { noc.Detail.Id }, kind = "Alert", title = "Core switch down", seconds = 60 });
        CollectionAssert.AreEqual(new[] { "Core switch down", "Mail delays", "Lunch" }, (await ShownAsync(noc)).Select(m => m.Title).ToArray());

        await using var db = app.NewDb();
        var audit = await db.AuditLog.Where(l => l.EventType == AuditEvents.BoardMessageSent).OrderByDescending(l => l.CreatedAt).Take(3).ToListAsync();
        Assert.IsTrue(audit.Any(l => l.Detail!.Contains("Warning \"Mail delays\"") && l.Detail.Contains(noc.Detail.Name) && l.Detail.Contains("for 1 min")),
            string.Join(" | ", audit.Select(l => l.Detail)));
    }

    [TestMethod]
    public async Task AllBoardsAreTheOnesThatShowMessagesWhenSent()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        var quiet = await CreateAsync(messages: false);
        var sent = await SentAsync(new { allBoards = true, kind = "Maintenance", title = "Patching tonight", seconds = 120 });
        var ids = sent.Boards.Select(x => x.Id).ToList();
        CollectionAssert.IsSubsetOf(new[] { a.Detail.Id, b.Detail.Id }, ids);
        CollectionAssert.DoesNotContain(ids, quiet.Detail.Id);
        var later = await CreateAsync();
        Assert.AreEqual(0, (await ShownAsync(later)).Count, "a board added later doesn't get it");
    }

    [TestMethod]
    public async Task TenOpenPerBoardThenWithdrawOne()
    {
        var board = await CreateAsync();
        var first = await SentAsync(new { boardIds = new[] { board.Detail.Id }, title = "1", seconds = 600 });
        for (var i = 2; i <= BoardMessageService.MaxOpenPerBoard; i++)
        {
            await SentAsync(new { boardIds = new[] { board.Detail.Id }, title = i.ToString(), seconds = 600 });
        }
        using (var eleventh = await SendAsync(app.ClientWithRights("Boards:Manage"), new { boardIds = new[] { board.Detail.Id }, title = "11", seconds = 60 }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, eleventh.StatusCode);
            StringAssert.Contains(await eleventh.Content.ReadAsStringAsync(), "too_many_open");
        }
        using (var withdrawn = await admin.PostAsync($"/api/v1/boards/messages/{first.Id}/withdraw", null))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, withdrawn.StatusCode);
        }
        using (var again = await admin.PostAsync($"/api/v1/boards/messages/{first.Id}/withdraw", null))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        }
        CollectionAssert.DoesNotContain((await ShownAsync(board)).Select(m => m.Title).ToArray(), "1");
        var recent = await (await admin.GetAsync("/api/v1/boards/messages?limit=20")).ReadAsync<List<BoardMessageItem>>();
        Assert.IsNotNull(recent.Single(m => m.Id == first.Id).WithdrawnByName);
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.BoardMessageWithdrawn && l.Detail!.Contains("\"1\"")));
    }

    [TestMethod]
    public async Task ThePollAnswers304UntilTheStampMoves()
    {
        var board = await CreateAsync();
        using var first = await PollAsync(board);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag!.Tag;
        StringAssert.Contains(first.Headers.CacheControl?.ToString(), "no-store");

        // A row written behind the service's back: the stamp didn't move, so the poll doesn't read it.
        await using (var db = app.NewDb())
        {
            var now = Clock.UtcNow;
            db.BoardMessages.Add(new BoardMessageEntity
            {
                Id = Guid.NewGuid(), BoardIds = [board.Detail.Id], Kind = BoardMessageKind.Information, Title = "Unseen", Seconds = 60, SentAt = now,
                EndsAt = now.AddSeconds(60), Source = BoardMessageSource.Person, SentByName = "Test",
            });
            await db.SaveChangesAsync();
        }
        using (var unchanged = await PollAsync(board, etag))
        {
            Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode, "no database read");
        }

        // A send moves it.
        var sent = await SentAsync(new { boardIds = new[] { board.Detail.Id }, title = "Seen", seconds = 60 });
        using var changed = await PollAsync(board, etag);
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
        var after = changed.Headers.ETag!.Tag;
        Assert.AreNotEqual(etag, after);
        Assert.AreEqual(2, (await changed.ReadAsync<BoardMessages>()).Messages.Count);

        // So do a withdrawal and a save.
        await admin.PostAsync($"/api/v1/boards/messages/{sent.Id}/withdraw", null);
        using (var withdrawn = await PollAsync(board, after))
        {
            Assert.AreEqual(HttpStatusCode.OK, withdrawn.StatusCode);
            after = withdrawn.Headers.ETag!.Tag;
        }
        var detail = await (await admin.GetAsync($"/api/v1/boards/{board.Detail.Id}")).ReadAsync<BoardDetail>();
        using (await admin.PutJsonAsync($"/api/v1/boards/{board.Detail.Id}", new
        {
            name = detail.Name, description = (string?)null, enabled = true, networks = Array.Empty<object>(),
            layout = detail.Layout with { Look = detail.Layout.Look with { Messages = false } }, revision = detail.Revision,
        }))
        {
        }
        using var off = await PollAsync(board, after);
        Assert.AreEqual(HttpStatusCode.OK, off.StatusCode, "the Messages setting changed");
        Assert.AreEqual(0, (await off.ReadAsync<BoardMessages>()).Messages.Count);

        // Another process: another start id, so no 304 for what the last one gave.
        Assert.AreNotEqual(app.Services.GetRequiredService<BoardRuntime>().StartId, new BoardRuntime().StartId);
    }

    [TestMethod]
    public async Task TheTimeLeftIsTheServersAndTheHistoryIsCleaned()
    {
        var board = await CreateAsync();
        var start = new DateTimeOffset(Clock.UtcNow.Ticks - Clock.UtcNow.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        Clock.Provider = time;
        await SentAsync(new { boardIds = new[] { board.Detail.Id }, title = "A minute", seconds = 60 });
        time.Advance(TimeSpan.FromSeconds(20));
        app.Services.GetRequiredService<BoardRuntime>().Bump(board.Detail.Id);
        var shown = (await ShownAsync(board)).Single();
        Assert.AreEqual((60, 40), (shown.Seconds, shown.RemainingSeconds));
        time.Advance(TimeSpan.FromSeconds(41));
        app.Services.GetRequiredService<BoardRuntime>().Bump(board.Detail.Id);
        Assert.AreEqual(0, (await ShownAsync(board)).Count, "its time is up");

        // Kept 30 days after its end; cleaned when the next is sent.
        await using (var db = app.NewDb())
        {
            var old = Clock.UtcNow.AddDays(-31);
            db.BoardMessages.Add(new BoardMessageEntity
            {
                Id = Guid.NewGuid(), BoardIds = [board.Detail.Id], Kind = BoardMessageKind.Success, Title = "Old", Seconds = 60, SentAt = old,
                EndsAt = old.AddMinutes(1), Source = BoardMessageSource.Person, SentByName = "Test",
            });
            await db.SaveChangesAsync();
        }
        app.Services.GetRequiredService<BoardRuntime>().Reset();
        await SentAsync(new { boardIds = new[] { board.Detail.Id }, title = "New", seconds = 60 });
        await using var check = app.NewDb();
        Assert.IsFalse(await check.BoardMessages.AnyAsync(m => m.Title == "Old"));
        Assert.IsTrue(await check.BoardMessages.AnyAsync(m => m.Title == "A minute"), "ended, but within the 30 days");
    }

    [TestMethod]
    public async Task TheRateLimitCountsPerAddressAndBoard()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        for (var i = 0; i < PerMinute; i++)
        {
            using var ok = await PollAsync(a);
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode, $"request {i + 1}");
        }
        using (var over = await PollAsync(a))
        {
            Assert.AreEqual(HttpStatusCode.TooManyRequests, over.StatusCode);
        }
        using (var other = await PollAsync(b))
        {
            Assert.AreEqual(HttpStatusCode.OK, other.StatusCode, "another board from the same address has a budget of its own");
        }

        // Unknown tokens still block the address after ten.
        for (var i = 0; i < 10; i++)
        {
            using var _ = await PollAsync(a, token: "pnmb_" + new string((char)('A' + i), 43));
        }
        using var blocked = await PollAsync(b);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        StringAssert.Contains(await blocked.Content.ReadAsStringAsync(), "blocked");
    }
}
