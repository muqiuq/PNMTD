using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Boards;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Tests.Api.V1;
using PNMTD.Tests.Connectors;

namespace PNMTD.Tests.Boards;

/// <summary>
/// Boards (docs/boards-plan.md): the admin's API with its rights and audit (§12.1, §13), a board's URL and the access check of its data
/// (§5.2), the board app's hosting (§4.2), and the data and status bar a view carries (§7, §8). Client addresses come from the connectors'
/// test header (<see cref="ConnectorHarness.AddressHeader"/>); without it, 10.20.0.5.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BoardsTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(services: ConnectorHarness.Install);
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public void Reset()
    {
        app.Services.GetRequiredService<BoardGuard>().Reset();
        app.Services.GetRequiredService<BoardRuntime>().Reset();
    }

    private static async Task<BoardDetail> CreateAsync(HttpClient client, string? name = null, string from = "start", int columns = 3)
    {
        using var response = await client.PostJsonAsync("/api/v1/boards", new { name = name ?? V1.Unique("board"), columns, from });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<BoardDetail>();
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, BoardDetail board, BoardLayout? layout = null, object? networks = null,
        bool enabled = true, int? revision = null) =>
        client.PutJsonAsync($"/api/v1/boards/{board.Id}", new
        {
            name = board.Name, description = board.Description, enabled, networks = networks ?? Array.Empty<object>(), layout = layout ?? board.Layout,
            revision = revision ?? board.Revision,
        });

    private static async Task<string> TokenAsync(BoardDetail board)
    {
        var url = await (await admin.GetAsync($"/api/v1/boards/{board.Id}/url")).ReadAsync<BoardUrl>();
        StringAssert.StartsWith(url.Url, "http://localhost/pnmt-board/pnmb_");
        return url.Url[(url.Url.LastIndexOf('/') + 1)..];
    }

    /// <summary>The board app's request: signed out unless <paramref name="role"/>, from <paramref name="from"/>.</summary>
    private static async Task<HttpResponseMessage> ViewAsync(string? token, string from = ConnectorHarness.Office, string? role = null, string path = "view")
    {
        using var client = app.Client(role);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/pnmt-board/api/{path}");
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Board {token}");
        }
        request.Headers.Add(ConnectorHarness.AddressHeader, from);
        return await client.SendAsync(request);
    }

    private static BoardLayout With(BoardLayout layout, params BoardElement[] elements) =>
        layout with { Columns = [new BoardColumn(1, elements), .. layout.Columns.Skip(1).Select(c => c with { Elements = [] })] };

    private static BoardElement Element(BoardElementType type, BoardElementSettings? settings = null) =>
        BoardLayouts.NewElement(type) with { Settings = BoardLayouts.Normalize(type, settings ?? new BoardElementSettings()) };

    // --- the admin's API ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ABoardIsCreatedSavedAndDeletedWithItsRightsAndItsAudit()
    {
        var board = await CreateAsync(admin);
        Assert.AreEqual(3, board.Layout.Columns.Count);
        Assert.AreEqual(3, BoardLayouts.Elements(board.Layout).Count(), "the starting layout: summary, failing sensors, clock");
        Assert.IsTrue(board.Anywhere, "no networks: anywhere");
        Assert.AreEqual("default", board.Layout.Look.Theme);
        Assert.IsTrue(board.Layout.Bar.Rules.Count >= 3, "the default rules");

        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.PostJsonAsync("/api/v1/boards", new { name = board.Name.ToUpperInvariant() })).StatusCode,
            "names are unique, ignoring case");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.Viewer).GetAsync("/api/v1/boards")).StatusCode, "a new area, granted to nobody");
        var viewer = app.ClientWithRights("Boards:View");
        var seen = await (await viewer.GetAsync($"/api/v1/boards/{board.Id}")).ReadAsync<BoardDetail>();
        Assert.IsNull(seen.TokenHint, "View sees no URL");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/boards/{board.Id}/url")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await SaveAsync(viewer, board)).StatusCode);

        // A save: from the revision it was loaded at, validated with the field's path.
        var layout = board.Layout with { Look = board.Layout.Look with { Theme = "terminal" } };
        var saved = await (await SaveAsync(admin, board, layout, new[] { new { cidr = "10.20.0.0/24", label = "office" } })).ReadAsync<BoardDetail>();
        Assert.AreEqual(board.Revision + 1, saved.Revision);
        Assert.AreEqual("terminal", saved.Layout.Look.Theme);
        Assert.AreEqual("10.20.0.0/24", saved.Networks.Single().Cidr);
        var stale = await SaveAsync(admin, board);
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.AreEqual("edit_conflict", (await stale.ProblemAsync()).GetProperty("code").GetString());
        var bad = await SaveAsync(admin, saved, saved.Layout with { RefreshSeconds = 5, Look = saved.Layout.Look with { Theme = "nope" } });
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.ProblemAsync()).GetProperty("errors");
        Assert.IsTrue(errors.TryGetProperty("layout.refreshSeconds", out _) && errors.TryGetProperty("layout.look.theme", out _), errors.ToString());
        var badNetwork = await SaveAsync(admin, saved, networks: new[] { new { cidr = "10.20.0.5/24", label = (string?)null } });
        Assert.AreEqual(HttpStatusCode.BadRequest, badNetwork.StatusCode, "host bits are refused, as for connectors");

        // The URL: shown and renewed with Manage, both audited; duplicate and delete.
        var token = await TokenAsync(saved);
        var renewed = await (await admin.PostAsync($"/api/v1/boards/{board.Id}/url/renew", null)).ReadAsync<BoardUrl>();
        Assert.IsFalse(renewed.Url.EndsWith(token, StringComparison.Ordinal));
        var copy = await (await admin.PostAsync($"/api/v1/boards/{board.Id}/duplicate", null)).ReadAsync<BoardDetail>();
        Assert.AreEqual($"{board.Name} (copy)", copy.Name);
        Assert.AreEqual(0, copy.Networks.Count, "a copy has no networks");
        Assert.IsTrue(BoardLayouts.Elements(copy.Layout).Select(e => e.Id).Intersect(BoardLayouts.Elements(saved.Layout).Select(e => e.Id)).Count() == 0,
            "its elements have ids of their own");
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/boards/{copy.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/boards/{board.Id}")).StatusCode);

        await using var db = app.NewDb();
        var entries = await db.AuditLog.AsNoTracking().Where(a => a.TargetId == board.Id.ToString()).OrderBy(a => a.CreatedAt).ToListAsync();
        CollectionAssert.IsSubsetOf(new[] { AuditEvents.BoardCreated, AuditEvents.BoardChanged, AuditEvents.BoardUrlShown, AuditEvents.BoardUrlRenewed,
            AuditEvents.BoardDeleted }, entries.Select(e => e.EventType).ToList());
        var changed = entries.First(e => e.EventType == AuditEvents.BoardChanged && e.Outcome == AuditOutcome.Succeeded);
        StringAssert.Contains(changed.Detail, "theme: default → terminal");
        StringAssert.Contains(changed.Detail, "networks: 10.20.0.0/24");
    }

    [TestMethod]
    public async Task AddingAnElementNeedsItsAreasRightButKeepingOneDoesnt()
    {
        var board = await CreateAsync(admin);
        var redmine = Element(BoardElementType.RedmineNewest);
        var withRedmine = await (await SaveAsync(admin, board, With(board.Layout, redmine))).ReadAsync<BoardDetail>();

        var editor = app.ClientWithRights("Boards:Manage,Monitoring:View");
        var refused = await SaveAsync(editor, withRedmine, With(withRedmine.Layout, BoardLayouts.Elements(withRedmine.Layout).Single(),
            Element(BoardElementType.RedmineReceived)));
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await refused.ProblemAsync();
        Assert.AreEqual("element_right", problem.GetProperty("code").GetString());
        Assert.AreEqual("Redmine", problem.GetProperty("area").GetString());

        var kept = await SaveAsync(editor, withRedmine, With(withRedmine.Layout, BoardLayouts.Elements(withRedmine.Layout).Single(),
            Element(BoardElementType.Clock)));
        Assert.AreEqual(HttpStatusCode.OK, kept.StatusCode, "what someone else added stays, unchanged: " + await kept.Content.ReadAsStringAsync());
        var changedSettings = (await kept.ReadAsync<BoardDetail>()) is var k ? k : null;
        var redmineNow = BoardLayouts.Elements(changedSettings!.Layout).Single(e => e.Type == BoardElementType.RedmineNewest);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await SaveAsync(editor, changedSettings, With(changedSettings.Layout,
            redmineNow with { Settings = redmineNow.Settings with { Rows = 3 } }))).StatusCode, "changing its settings needs the right");

        // The preview withholds what its editor may not see; the board itself shows it.
        var preview = await (await app.ClientWithRights("Boards:View,Monitoring:View").PostJsonAsync("/api/v1/boards/preview",
            new { layout = changedSettings.Layout })).ReadAsync<BoardView>();
        Assert.AreEqual(BoardDataState.Withheld, preview.Elements.Single(e => e.Type == BoardElementType.RedmineNewest).State);
        var catalog = await (await editor.GetAsync("/api/v1/boards/catalog")).ReadAsync<BoardCatalog>();
        Assert.IsFalse(catalog.Types.Single(t => t.Type == BoardElementType.RedmineNewest).Available);
        Assert.IsTrue(catalog.Types.Single(t => t.Type == BoardElementType.Lounge).Available, "the lounge, read-only, needs no right of its own");
        CollectionAssert.AreEqual(BoardThemes.Ids.ToArray(), catalog.Themes.ToArray());
    }

    // --- a board's URL and its access check (§5.2) --------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheTokenTheNetworksAndASessionDecideWhoSeesABoard()
    {
        var board = await CreateAsync(admin);
        var token = await TokenAsync(board);
        var open = await ViewAsync(token, "203.0.113.7");
        Assert.AreEqual(HttpStatusCode.OK, open.StatusCode, "no networks: anywhere");
        var view = await open.ReadAsync<BoardView>();
        Assert.AreEqual(board.Name, view.Name);
        Assert.AreEqual(3, view.Elements.Count);
        Assert.AreEqual("no-store", open.Headers.CacheControl?.ToString());
        Assert.IsTrue(open.Headers.TryGetValues("X-Robots-Tag", out var robots) && robots.Single().Contains("noindex"));

        var saved = await (await SaveAsync(admin, board, networks: new[] { new { cidr = "10.20.0.0/24", label = "office" },
            new { cidr = "2001:db8::/48", label = "v6" } })).ReadAsync<BoardDetail>();
        Assert.AreEqual(HttpStatusCode.OK, (await ViewAsync(token, "10.20.0.9")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await ViewAsync(token, "::ffff:10.20.0.9")).StatusCode, "IPv4 in IPv6 counts as IPv4");
        Assert.AreEqual(HttpStatusCode.OK, (await ViewAsync(token, "2001:db8::17")).StatusCode);
        var outside = await ViewAsync(token, "203.0.113.7");
        Assert.AreEqual(HttpStatusCode.Forbidden, outside.StatusCode);
        var problem = await outside.ProblemAsync();
        Assert.AreEqual("board_network", problem.GetProperty("code").GetString());
        Assert.AreEqual("203.0.113.7", problem.GetProperty("address").GetString());
        Assert.AreEqual(HttpStatusCode.Forbidden, (await ViewAsync(token, "203.0.113.7")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await ViewAsync(token, "203.0.113.7", Roles.Viewer)).StatusCode,
            "a signed-in user, with no right at all, opens it from anywhere");
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(1, await db.AuditLog.CountAsync(a => a.EventType == AuditEvents.BoardRefused && a.TargetId == board.Id.ToString()),
                "a refusal by network, at most once an hour per address");
        }

        // Switched off; then the token renewed: the old one stops at once, despite the cache.
        await SaveAsync(admin, saved, enabled: false);
        var off = await ViewAsync(token, "10.20.0.9");
        Assert.AreEqual("board_disabled", (await off.ProblemAsync()).GetProperty("code").GetString());
        var current = await (await admin.GetAsync($"/api/v1/boards/{board.Id}")).ReadAsync<BoardDetail>();
        await SaveAsync(admin, current, enabled: true, networks: Array.Empty<object>());
        Assert.AreEqual(HttpStatusCode.OK, (await ViewAsync(token)).StatusCode);
        await admin.PostAsync($"/api/v1/boards/{board.Id}/url/renew", null);
        var gone = await ViewAsync(token);
        Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.AreEqual("board_not_found", (await gone.ProblemAsync()).GetProperty("code").GetString());

        // Seen: the list counts the screen.
        var listed = (await (await admin.GetAsync("/api/v1/boards")).ReadAsync<List<BoardItem>>()).Single(b => b.Id == board.Id);
        Assert.IsTrue(listed.ScreensNow >= 1);
        Assert.IsNotNull(listed.LastSeenAt);
    }

    [TestMethod]
    public async Task UnknownTokensBlockAnAddress()
    {
        for (var i = 0; i < BoardGuard.FailedTokens; i++)
        {
            Assert.AreEqual(HttpStatusCode.NotFound, (await ViewAsync(BoardTokens.New(), "198.51.100.66")).StatusCode);
        }
        var blocked = await ViewAsync(BoardTokens.New(), "198.51.100.66");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.IsNotNull(blocked.Headers.RetryAfter);
        Assert.AreEqual(HttpStatusCode.NotFound, (await ViewAsync(null, "198.51.100.67")).StatusCode, "another address isn't");
    }

    [TestMethod]
    public void ABoardsRequestsNeverRenewASession()
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var scheme = new Microsoft.AspNetCore.Authentication.AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null,
            typeof(CookieAuthenticationHandler));
        foreach (var (path, renews) in new[] { ("/pnmt-board/api/view", false), ("/api/v1/hosts", true) })
        {
            var http = new DefaultHttpContext { RequestServices = app.Services };
            http.Request.Path = path;
            var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(), CookieAuthenticationDefaults.AuthenticationScheme);
            var context = new CookieSlidingExpirationContext(http, scheme, options, ticket, TimeSpan.FromDays(4), TimeSpan.FromDays(3)) { ShouldRenew = true };
            options.Events.OnCheckSlidingExpiration(context).GetAwaiter().GetResult();
            Assert.AreEqual(renews, context.ShouldRenew, path);
        }
    }

    // --- the board app's hosting (§4.2) -------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheBoardAppIsServedUnderItsOwnPath()
    {
        var client = app.Client();
        var bare = await client.GetAsync("/pnmt-board");
        Assert.AreEqual(HttpStatusCode.MovedPermanently, bare.StatusCode);
        Assert.AreEqual("/pnmt-board/", bare.Headers.Location?.ToString());
        var shell = await client.GetAsync($"/pnmt-board/{BoardTokens.New()}");
        Assert.AreEqual(HttpStatusCode.OK, shell.StatusCode, "the shell, anonymous: it holds no data");
        Assert.AreEqual(TestDefaults.BoardIndexHtml, await shell.Content.ReadAsStringAsync());
        StringAssert.Contains(shell.Headers.GetValues("Content-Security-Policy").Single(), "frame-ancestors 'none'");
        Assert.IsTrue(shell.Headers.TryGetValues("X-Robots-Tag", out _));
        var asset = await client.GetAsync("/pnmt-board/assets/board-7c1e.js");
        Assert.AreEqual("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
        var unknown = await client.GetAsync("/pnmt-board/api/nothing");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
        foreach (var escape in new[] { "/pnmt-board/../api/v1/hosts", "/pnmt-board/%2e%2e/api/v1/hosts", "/pnmt-board/api/../../api/v1/hosts" })
        {
            var response = await client.GetAsync(escape);
            Assert.IsFalse(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("\"items\""), escape);
        }
    }

    // --- data and the status bar (§7, §8) -----------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AViewShowsItsElementsDataAndItsBarsVerdict()
    {
        var location = await (await admin.PostJsonAsync("/api/v1/locations", new { name = V1.Unique("boardloc") })).ReadAsync<LocationDetail>();
        var important = await (await admin.PostJsonAsync("/api/v1/hosts", new { name = V1.Unique("bd-core"), locationId = location.Id, importance = "High" }))
            .ReadAsync<HostDetail>();
        var plain = await (await admin.PostJsonAsync("/api/v1/hosts", new { name = V1.Unique("bd-printer"), locationId = location.Id })).ReadAsync<HostDetail>();
        var down = await V1.CreateSensorAsync(admin, important.Id, SensorType.SIMPLE, name: "uplink");
        var paper = await V1.CreateSensorAsync(admin, plain.Id, SensorType.SIMPLE, name: "paper");
        var fine = await V1.CreateSensorAsync(admin, plain.Id, SensorType.SIMPLE, name: "ping");
        var now = Clock.UtcNow;
        V1.AddEvents(app, down.Id, (now.AddMinutes(-30), 500, "login failed for backup-admin"));
        V1.AddEvents(app, paper.Id, (now.AddMinutes(-20), 500, "no paper"));
        V1.AddEvents(app, fine.Id, (now.AddMinutes(-10), 200, "ok"));
        app.ClearDashboardCache();
        try
        {
            var board = await CreateAsync(admin, columns: 1, from: "empty");
            var at = new BoardElementSettings(LocationId: location.Id);
            var failing = Element(BoardElementType.FailingSensors, at);
            var withMessages = Element(BoardElementType.FailingSensors, at with { Messages = true, Importance = Importance.High });
            var summary = Element(BoardElementType.SensorSummary, at);
            var tiles = Element(BoardElementType.StatusTiles, new BoardElementSettings(Targets: [new BoardTarget(BoardTargetKind.Host, plain.Id),
                new BoardTarget(BoardTargetKind.Sensor, fine.Id), new BoardTarget(BoardTargetKind.Host, Guid.NewGuid())]));
            var note = Element(BoardElementType.Note, new BoardElementSettings(Text: "Call **044 555**"));
            var layout = With(board.Layout, failing, withMessages, summary, tiles, note) with
            {
                Bar = board.Layout.Bar with
                {
                    Rules =
                    [
                        new BoardRule(BoardMetric.SensorsFailing, BoardColour.Red, 1, "important sensors failing",
                            new BoardRuleFilter(Importance: Importance.High, LocationId: location.Id)),
                        new BoardRule(BoardMetric.SensorsFailing, BoardColour.Yellow, 1, "sensors failing", new BoardRuleFilter(LocationId: location.Id)),
                    ],
                },
            };
            var saved = await (await SaveAsync(admin, board, layout)).ReadAsync<BoardDetail>();
            var view = await (await ViewAsync(await TokenAsync(saved))).ReadAsync<BoardView>();
            BoardElementData Of(BoardElement e) => view.Elements.Single(x => x.Id == e.Id);

            CollectionAssert.AreEqual(new[] { "uplink", "paper" }, Of(failing).Sensors!.Select(s => s.Sensor).ToArray(), "the most important first");
            Assert.IsTrue(Of(failing).Sensors!.All(s => s.Message is null), "messages are off by default");
            Assert.AreEqual("login failed for backup-admin", Of(withMessages).Sensors!.Single().Message);
            Assert.AreEqual((1, 2), (Of(summary).Counts!.Ok, Of(summary).Counts!.Failing));
            CollectionAssert.AreEqual(new[] { BoardTileState.Failing, BoardTileState.Ok, BoardTileState.Missing }, Of(tiles).Tiles!.Select(t => t.State).ToArray());
            Assert.AreEqual("Call **044 555**", Of(note).Note);

            Assert.AreEqual(BoardColour.Red, view.Bar.Colour, view.Bar.Text);
            StringAssert.StartsWith(view.Bar.Text, "1 important sensors failing");
            StringAssert.Contains(view.Bar.Text, "2 sensors failing");
            Assert.IsNotNull(view.Bar.Since);

            // Green when nothing matches: the location's hosts fine again.
            V1.AddEvents(app, down.Id, (Clock.UtcNow.AddSeconds(-2), 200, "ok"));
            V1.AddEvents(app, paper.Id, (Clock.UtcNow.AddSeconds(-1), 200, "ok"));
            app.ClearDashboardCache();
            app.Services.GetRequiredService<BoardRuntime>().Reset();
            var green = await (await ViewAsync(await TokenAsync(saved))).ReadAsync<BoardView>();
            Assert.AreEqual((BoardColour.Green, "All clear"), (green.Bar.Colour, green.Bar.Text));
        }
        finally
        {
            await admin.DeleteAsync($"/api/v1/hosts/{important.Id}");
            await admin.DeleteAsync($"/api/v1/hosts/{plain.Id}");
        }
    }

    /// <summary>
    /// docs/board-messages-plan.md §9.3: a sensor's last message, as plain text cut at 2000 characters; no message, no data, a deleted
    /// sensor, none chosen; one sensor only, and Monitoring: View to add it.
    /// </summary>
    [TestMethod]
    public async Task ASensorMessageShowsItsSensorsLastMessage()
    {
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new { name = V1.Unique("bd-queue") })).ReadAsync<HostDetail>();
        var queue = await V1.CreateSensorAsync(admin, host.Id, SensorType.SIMPLE, name: "queue");
        var quiet = await V1.CreateSensorAsync(admin, host.Id, SensorType.SIMPLE, name: "quiet");
        var chatty = await V1.CreateSensorAsync(admin, host.Id, SensorType.SIMPLE, name: "chatty");
        var now = Clock.UtcNow;
        V1.AddEvents(app, queue.Id, (now.AddMinutes(-12), 500, "Queue: 12 mails waiting,\noldest 3 min"));
        V1.AddEvents(app, chatty.Id, (now.AddMinutes(-1), 200, new string('x', 2500)));
        app.ClearDashboardCache();
        try
        {
            BoardElement For(Guid? sensor) => Element(BoardElementType.SensorMessage, new BoardElementSettings(
                Targets: sensor is { } id ? [new BoardTarget(BoardTargetKind.Host, host.Id), new BoardTarget(BoardTargetKind.Sensor, id),
                    new BoardTarget(BoardTargetKind.Sensor, quiet.Id)] : null, TextSize: BoardTextSize.Fit));
            var shown = For(queue.Id);
            var none = For(quiet.Id);
            var cut = For(chatty.Id);
            var gone = For(Guid.NewGuid());
            var unset = For(null);
            Assert.AreEqual(1, shown.Settings.Targets!.Count, "one sensor, never a host");
            Assert.AreEqual((BoardTextSize.Fit, 6, BoardStateColour.Bar, true), (shown.Settings.TextSize, shown.Settings.Rows, shown.Settings.StateColour,
                shown.Settings.Age));
            Assert.AreEqual((Area.Monitoring, Level.View), BoardLayouts.Of(BoardElementType.SensorMessage).Right);

            var board = await CreateAsync(admin, columns: 1, from: "empty");
            var saved = await (await SaveAsync(admin, board, With(board.Layout, shown, none, cut, gone, unset))).ReadAsync<BoardDetail>();
            var view = await (await ViewAsync(await TokenAsync(saved))).ReadAsync<BoardView>();
            BoardElementData Of(BoardElement e) => view.Elements.Single(x => x.Id == e.Id);

            var message = Of(shown).SensorMessage!;
            Assert.AreEqual((host.Name, "queue", BoardTileState.Failing, "Queue: 12 mails waiting,\noldest 3 min"),
                (message.Host, message.Name, message.State, message.Message));
            Assert.IsNotNull(message.At);
            Assert.AreEqual((BoardTileState.NoData, null), (Of(none).SensorMessage!.State, Of(none).SensorMessage!.Message));
            Assert.AreEqual(BoardData.MaxSensorMessage, Of(cut).SensorMessage!.Message!.Length);
            StringAssert.EndsWith(Of(cut).SensorMessage!.Message, "…");
            Assert.AreEqual((BoardTileState.Missing, null), (Of(gone).SensorMessage!.State, Of(gone).SensorMessage!.Host));
            Assert.AreEqual(BoardDataState.NotConfigured, Of(unset).State);
        }
        finally
        {
            await admin.DeleteAsync($"/api/v1/hosts/{host.Id}");
        }
    }

    /// <summary>§9.3: the server validates a save against the same themes as the board app draws.</summary>
    [TestMethod]
    public void TheServerKnowsTheBoardAppsThemes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PNMTD.slnx")))
        {
            root = root.Parent;
        }
        var registry = File.ReadAllText(Path.Combine(root!.FullName, "PNMTD.WebUI", "src", "boards", "themes", "registry.ts"));
        var list = registry[registry.IndexOf("export const themes", StringComparison.Ordinal)..registry.IndexOf("export const themeName", StringComparison.Ordinal)];
        var ids = System.Text.RegularExpressions.Regex.Matches(list, @"\bid: '([a-z0-9-]+)'").Select(m => m.Groups[1].Value).ToArray();
        CollectionAssert.AreEqual(BoardThemes.Ids.ToArray(), ids);
    }

    [TestMethod]
    public void PeopleAreNamedAsTheBoardSays() =>
        CollectionAssert.AreEqual(new[] { "AM", "AMK", "CO" }, new[] { BoardData.Initials("Anna Muster"), BoardData.Initials("Anna-Maria Keller"),
            BoardData.Initials("cookie") });

    [TestMethod]
    public void TheLayoutKeepsOnlyWhatATypeReads()
    {
        var v = new global::PNMTD.Api.ApiValidator();
        var layout = BoardLayouts.New(1, false, false) with
        {
            Columns = [new BoardColumn(1, [BoardLayouts.NewElement(BoardElementType.Clock) with
            {
                Settings = new BoardElementSettings(Rows: 7, Text: "not a clock's", Zones: [new BoardZone("Europe/London", "London")]),
            }])],
        };
        var clean = BoardLayouts.Validate(layout, v);
        Assert.IsFalse(v.HasErrors);
        var settings = BoardLayouts.Elements(clean).Single().Settings;
        Assert.AreEqual((null, null), (settings.Rows, settings.Text));
        Assert.AreEqual("London", settings.Zones!.Single().Label);
        var invalid = layout with
        {
            Columns = [new BoardColumn(1, [BoardLayouts.NewElement(BoardElementType.Clock) with
            {
                Settings = new BoardElementSettings(Zones: [new BoardZone("Mars/Olympus", "Mars")]),
            }])],
        };
        BoardLayouts.Validate(invalid, v);
        Assert.IsTrue(v.HasErrors);
    }

    /// <summary>
    /// docs/board-messages-plan.md §9.1, §9.2, §14: a layout from 1.23 reads 24-hour (decision 6), its old date format and the browser's
    /// names; a new board names days in English; a clock keeps its own formats, and a header can't ask for the browser's hours.
    /// </summary>
    [TestMethod]
    public void TimesAndDatesHaveTheirDefaultsAndOverrides()
    {
        const string old = """
            {"schemaVersion":1,"refreshSeconds":30,"look":{"theme":"dark","fit":true,"textScale":100,"motion":true,"people":"Names"},
             "header":{"showTitle":true,"title":null,"clock":true,"date":true,"seconds":false},
             "bar":{"show":true,"position":"Top","size":"Medium","greenText":null,"flash":true,"rules":[]},
             "columns":[{"width":1,"elements":[{"id":"abcd1234","type":"Clock","title":null,"showTitle":true,"height":null,
               "settings":{"seconds":true,"date":true,"zones":[]}}]}]}
            """;
        var layout = BoardLayouts.Read(old);
        Assert.AreEqual("dark", layout.Look.Theme);
        Assert.AreEqual((BoardTimeFormat.Hours24, BoardDateFormat.WeekdayDayMonth, false, BoardDateLanguage.Browser),
            (layout.Look.TimeFormat, layout.Look.DateFormat, layout.Look.WeekNumber, layout.Look.DateLanguage));
        Assert.IsNull(layout.Header.TimeFormat);
        var clock = BoardLayouts.Elements(layout).Single().Settings;
        Assert.AreEqual((null, null, null), (clock.TimeFormat, clock.DateFormat, clock.WeekNumber));
        Assert.AreEqual(BoardDateLanguage.English, BoardLayouts.New(1, true, false).Look.DateLanguage);

        var v = new global::PNMTD.Api.ApiValidator();
        var own = layout with
        {
            Look = layout.Look with { TimeFormat = BoardTimeFormat.Hours12, DateFormat = BoardDateFormat.Dotted, WeekNumber = true },
            Header = layout.Header with { TimeFormat = BoardTimeFormat.Hours24 },
            Columns = [new BoardColumn(1, [BoardLayouts.Elements(layout).Single() with
            {
                Settings = clock with { TimeFormat = BoardTimeFormat.Hours12, DateFormat = BoardDateFormat.Iso, WeekNumber = false },
            }])],
        };
        var clean = BoardLayouts.Read(BoardLayouts.Write(BoardLayouts.Validate(own, v)));
        Assert.IsFalse(v.HasErrors);
        Assert.AreEqual((BoardTimeFormat.Hours12, BoardDateFormat.Dotted, true), (clean.Look.TimeFormat, clean.Look.DateFormat, clean.Look.WeekNumber));
        Assert.AreEqual(BoardTimeFormat.Hours24, clean.Header.TimeFormat);
        var kept = BoardLayouts.Elements(clean).Single().Settings;
        Assert.AreEqual((BoardTimeFormat.Hours12, BoardDateFormat.Iso, false), (kept.TimeFormat, kept.DateFormat, kept.WeekNumber));

        BoardLayouts.Validate(own with { Header = own.Header with { TimeFormat = BoardTimeFormat.Browser } }, v);
        Assert.IsTrue(v.HasErrors);
        // A note doesn't read a clock's formats: dropped on save.
        var note = BoardLayouts.Normalize(BoardElementType.Note, new BoardElementSettings(TimeFormat: BoardTimeFormat.Hours12, WeekNumber: true));
        Assert.AreEqual((null, null), (note.TimeFormat, note.WeekNumber));
    }
}
