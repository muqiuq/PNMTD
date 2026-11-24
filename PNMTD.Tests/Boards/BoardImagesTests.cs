using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Boards;
using PNMTD.Files;
using PNMTD.Tests.Api.V1;
using PNMTD.Tests.Files;

namespace PNMTD.Tests.Boards;

/// <summary>
/// A board's image elements (docs/board-messages-plan.md §9.4): the upload (checked by its bytes, animations kept, the board's own limit
/// rather than a page's), the use recorded at once and matched on save, copied on duplicate and dropped with the board; the editor's and
/// the board's image endpoints only for the board's own images; the CSP that lets the board show them from memory.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BoardImagesTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static string root = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        root = Directory.CreateTempSubdirectory("pnmtd-board-images-").FullName;
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["KnowledgeBase:FilesPath"] = root,
            // A page's images are tiny here: a board's take Boards:MaxImageBytes instead.
            ["KnowledgeBase:MaxImageBytes"] = "60",
            ["Boards:MaxImageBytes"] = "5000",
        });
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop()
    {
        app.Dispose();
        Directory.Delete(root, recursive: true);
    }

    [TestInitialize]
    public void Reset()
    {
        app.Services.GetRequiredService<UploadGuard>().Reset();
        app.Services.GetRequiredService<BoardGuard>().Reset();
        app.Services.GetRequiredService<BoardRuntime>().Reset();
    }

    private static async Task<BoardDetail> CreateAsync()
    {
        using var response = await admin.PostJsonAsync("/api/v1/boards", new { name = V1.Unique("images"), columns = 1, from = "empty" });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<BoardDetail>();
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid board, string name, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await client.PostAsync($"/api/v1/boards/{board}/images?name={Uri.EscapeDataString(name)}", content);
    }

    private static Task<HttpResponseMessage> SaveAsync(BoardDetail board, params BoardElement[] elements) =>
        admin.PutJsonAsync($"/api/v1/boards/{board.Id}", new
        {
            name = board.Name, description = board.Description, enabled = true, networks = Array.Empty<object>(),
            layout = board.Layout with { Columns = [new BoardColumn(1, elements)] }, revision = board.Revision,
        });

    private static BoardElement Image(Guid file) => BoardLayouts.NewElement(BoardElementType.Image) with
    {
        Settings = BoardLayouts.Normalize(BoardElementType.Image, new BoardElementSettings(FileId: file, Caption: "The office")),
    };

    private static async Task<HttpResponseMessage> BoardGetAsync(BoardDetail board, string path)
    {
        var url = await (await admin.GetAsync($"/api/v1/boards/{board.Id}/url")).ReadAsync<BoardUrl>();
        var token = url.Url[(url.Url.LastIndexOf('/') + 1)..];
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Board {token}");
        return await app.Client().SendAsync(request);
    }

    private static async Task<int> UsesAsync(Guid board)
    {
        await using var db = app.NewDb();
        return await db.FileUses.CountAsync(u => u.BoardId == board);
    }

    [TestMethod]
    public async Task AnImageIsUploadedForABoardAndUsedAsItsLayoutSays()
    {
        var board = await CreateAsync();
        var (_, gif) = TestImages.GifPair();
        using var uploaded = await UploadAsync(admin, board.Id, "party.gif", gif);
        Assert.AreEqual(HttpStatusCode.Created, uploaded.StatusCode, await uploaded.Content.ReadAsStringAsync());
        var animated = await uploaded.ReadAsync<BoardImageItem>();
        Assert.AreEqual("image/gif", animated.ContentType);
        var png = await (await UploadAsync(admin, board.Id, "logo.png", TestImages.Png(4, 4))).ReadAsync<BoardImageItem>();
        Assert.IsGreaterThan(60L, png.Size, "larger than a page's limit here, within a board's");
        Assert.AreEqual(2, await UsesAsync(board.Id), "recorded at once, before the board is saved");

        using (var text = await UploadAsync(admin, board.Id, "notes.png", "not an image"u8.ToArray()))
        {
            Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        }
        using (var large = await UploadAsync(admin, board.Id, "large.png", TestImages.Png(4, 4, ("tEXt", new byte[6000]))))
        {
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, large.StatusCode, "Boards:MaxImageBytes");
        }
        using (var viewer = await UploadAsync(app.ClientWithRights("Boards:View"), board.Id, "x.png", TestImages.Png(2, 2)))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, viewer.StatusCode);
        }

        var list = await (await app.ClientWithRights("Boards:View").GetAsync($"/api/v1/boards/{board.Id}/images")).ReadAsync<List<BoardImageItem>>();
        CollectionAssert.AreEquivalent(new[] { animated.Id, png.Id }, list.Select(i => i.Id).ToArray());
        using (var preview = await admin.GetAsync($"/api/v1/boards/{board.Id}/images/{animated.Id}"))
        {
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
            Assert.AreEqual("image/gif", preview.Content.Headers.ContentType?.MediaType);
            StringAssert.Contains(preview.Headers.CacheControl?.ToString(), "immutable");
            var bytes = Encoding.Latin1.GetString(await preview.Content.ReadAsByteArrayAsync());
            StringAssert.Contains(bytes, "NETSCAPE2.0", "the loop is kept");
            Assert.AreEqual(2, bytes.Split("!ù").Length - 1, "and both frames");
        }

        // Saved with the GIF only: the PNG's use goes, and the cleanup may take it.
        using (var saved = await SaveAsync(board, Image(animated.Id)))
        {
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            board = await saved.ReadAsync<BoardDetail>();
        }
        await using (var db = app.NewDb())
        {
            CollectionAssert.AreEqual(new[] { animated.Id }, await db.FileUses.Where(u => u.BoardId == board.Id).Select(u => u.FileId).ToArrayAsync());
            Assert.IsFalse(await db.FileUses.AnyAsync(u => u.FileId == png.Id));
        }

        // A file that isn't the board's: refused.
        var page = await (await FileStoreTests.UploadAsync(admin, "attachment", "page.txt", "a page's attachment"u8.ToArray())).ReadAsync<FileItem>();
        using (var foreign = await SaveAsync(board, Image(animated.Id), Image(page.Id)))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, foreign.StatusCode);
            StringAssert.Contains(await foreign.Content.ReadAsStringAsync(), "upload it for this board");
        }

        // The board shows it, and only it.
        var view = await (await BoardGetAsync(board, "/pnmt-board/api/view")).ReadAsync<BoardView>();
        var data = view.Elements.Single().Image!;
        Assert.AreEqual((animated.Id, "image/gif"), (data.FileId, data.ContentType));
        using (var shown = await BoardGetAsync(board, $"/pnmt-board/api/images/{animated.Id}"))
        {
            Assert.AreEqual(HttpStatusCode.OK, shown.StatusCode);
            StringAssert.Contains(shown.Headers.CacheControl?.ToString(), "max-age=31536000");
        }
        using (var other = await BoardGetAsync(board, $"/pnmt-board/api/images/{png.Id}"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode, "not in the layout");
        }
        using (var notUsed = await admin.GetAsync($"/api/v1/boards/{board.Id}/images/{png.Id}"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, notUsed.StatusCode, "the editor's preview: the board's own images only");
        }
        using (var none = await app.Client().GetAsync($"/pnmt-board/api/images/{animated.Id}"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, none.StatusCode, "no token");
        }

        // A copy uses the image too; deleting the original keeps it.
        var copy = await (await admin.PostAsync($"/api/v1/boards/{board.Id}/duplicate", null)).ReadAsync<BoardDetail>();
        Assert.AreEqual(1, await UsesAsync(copy.Id));
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/boards/{board.Id}")).StatusCode);
        Assert.AreEqual(0, await UsesAsync(board.Id), "dropped with the board");
        Assert.AreEqual(1, await UsesAsync(copy.Id));
        await admin.DeleteAsync($"/api/v1/boards/{copy.Id}");
    }

    [TestMethod]
    public async Task TheBoardsPageMayShowImagesFromMemory()
    {
        using var page = await app.Client().GetAsync("/pnmt-board/pnmb_" + new string('A', 43));
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        StringAssert.Contains(csp, "img-src 'self' data: blob:");
        StringAssert.Contains(csp, "default-src 'self'");
    }
}
