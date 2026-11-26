using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Dev;
using PNMTD.Mcp;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;
using PNMTD.Tests.Locations;
using PNMTD.Tools;

namespace PNMTD.Tests.Responsibilities;

/// <summary>
/// responsibilities_list and responsibility_get for MCP and Cookie (docs/responsibilities-plan.md §7): read-only, within
/// Responsibilities: View, a sensitive page never named, the global people for what nothing links, the Redmine combinations.
/// </summary>
[TestClass]
public sealed class ResponsibilityToolsTests
{
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static HttpClient manager = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: FakeRedmine.Settings, services: redmine.Install);
        admin = app.Client(Roles.Admin);
        manager = app.ClientWithRights("Responsibilities:Manage");
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static ToolContext Reader(bool withRight = true) => new(app.Services,
        new UserRights(false, withRight ? new Dictionary<Area, Level> { [Area.Responsibilities] = Level.View } : new Dictionary<Area, Level>()),
        NoMasking.Instance);

    private static Task<string> RunAsync(string tool, string arguments, ToolContext? ctx = null) =>
        CookieTools.RunAsync(tool, arguments, ctx ?? Reader(), CancellationToken.None);

    private static async Task<int> PageAsync(string title, bool sensitive) =>
        (await (await admin.PostJsonAsync("/api/v1/kb/pages", new KbPageRequest
        {
            Title = title, Content = $"{title}.", MarkSensitive = sensitive, AcknowledgeCredentials = true,
        })).ReadAsync<KbSaveResult>()).Page.Number;

    [TestMethod]
    public async Task TheyReadWhatThePageShowsAndNeverASensitivePage()
    {
        var name = V1.Unique("Tool Network");
        var r = await (await manager.PostJsonAsync("/api/v1/responsibilities", new { name, summary = "Switches and routers" })).ReadAsync<ResponsibilityDetail>();
        await manager.PutJsonAsync($"/api/v1/responsibilities/{r.Id}/description", new { content = "The **core switches** in every building.", revision = r.Revision });
        var host = await V1.CreateHostAsync(admin, V1.Unique("sw-core"));
        var other = await V1.CreateHostAsync(admin, V1.Unique("printer"));
        var site = await LocationsApiTests.CreateAsync(admin, V1.Unique("Bern"));
        var open = await PageAsync(V1.Unique("Open WAN page"), false);
        var secret = await PageAsync(V1.Unique("Secret router page"), true);
        await using (var db = app.NewDb())
        {
            var pages = db.KbPages.Where(p => p.Number == open || p.Number == secret).Select(p => p.Id).ToList();
            foreach (var (kind, id) in new[] { ("Host", host.Id), ("Location", site.Id) }.Concat(pages.Select(p => ("Page", p))))
            {
                await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/links", new { kind, id });
            }
        }
        await using (var db = app.NewDb())
        {
            db.ResponsibilityRedmine.RemoveRange(db.ResponsibilityRedmine.Where(c => c.ProjectId == 3 && c.TrackerId == null));
            await db.SaveChangesAsync();
        }
        await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/redmine", new { projectId = 3, userId = TestUsers.User });

        var byHost = await RunAsync("responsibilities_list", $$"""{"hostId":"{{host.Id}}"}""");
        StringAssert.Contains(byHost, name);
        StringAssert.Contains(byHost, "Switches and routers");
        var unlinked = await RunAsync("responsibilities_list", $$"""{"hostId":"{{other.Id}}"}""");
        StringAssert.Contains(unlinked, "No responsibility links this host");
        Assert.IsFalse(unlinked.Contains(name), unlinked);
        StringAssert.Contains(await RunAsync("responsibilities_list", $$"""{"pageNumber":{{open}}}"""), name);
        StringAssert.Contains(await RunAsync("responsibilities_list", $$"""{"pageNumber":{{secret}}}"""), "No such page");
        StringAssert.Contains(await RunAsync("responsibilities_list", """{"redmineProject":3}"""), name);
        StringAssert.Contains(await RunAsync("responsibilities_list", $$"""{"hostId":"{{host.Id}}","pageNumber":{{open}}}"""), "At most one");

        var full = await RunAsync("responsibility_get", $$"""{"name":"{{name.ToLowerInvariant()}}"}""");
        StringAssert.Contains(full, "core switches");
        StringAssert.Contains(full, "Open WAN page");
        Assert.IsFalse(full.Contains("Secret router page"), "a sensitive page is never named");
        StringAssert.Contains(full, "1 linked page is not shown");
        StringAssert.Contains(full, site.Name);
        StringAssert.Contains(full, host.Name);
        StringAssert.Contains(full, "Network");
        StringAssert.Contains(full, "every tracker");
        Assert.IsFalse(full.Contains("@"), "never contact details");
        StringAssert.Contains(await RunAsync("responsibility_get", $$"""{"id":"{{Guid.NewGuid()}}"}"""), "No such responsibility");
    }

    [TestMethod]
    public void TheyNeedResponsibilitiesViewAndReachTokensAndCookieOnlyAsReads()
    {
        Assert.IsFalse(CookieTools.For(Reader(withRight: false).Rights).Any(t => t.Name.StartsWith("responsibilit", StringComparison.Ordinal)));
        var forCookie = CookieTools.For(Reader().Rights).Select(t => t.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "responsibilities_list", "responsibility_get" }, forCookie);

        var user = new UserRights(false, new Dictionary<Area, Level> { [Area.Responsibilities] = Level.Manage });
        foreach (var kind in new[] { McpTokenKind.Read, McpTokenKind.Write })
        {
            var capped = McpRights.For(user, kind);
            // docs/mcp-extensions-plan.md §8.1: the read & write token edits (never Manage: deleting, the global people); the read-only one reads.
            Assert.AreEqual(kind == McpTokenKind.Write ? Level.Edit : Level.View, capped.Levels[Area.Responsibilities], "Manage never reaches a token");
            var names = ToolCatalog.ForMcp(capped, kind).Select(t => t.Name).ToList();
            CollectionAssert.IsSubsetOf(new[] { "responsibilities_list", "responsibility_get" }, names);
            var writes = new[] { "responsibility_create", "responsibility_update", "responsibility_link", "responsibility_redmine", "responsibility_options" };
            Assert.AreEqual(kind == McpTokenKind.Write, writes.All(names.Contains), $"{kind}: the tools that change them");
        }
        Assert.IsFalse(CookieTools.For(new UserRights(true, new Dictionary<Area, Level>())).Any(t => t.Name is "responsibility_create" or "responsibility_update"),
            "Cookie still only reads them");
    }

    [TestMethod]
    public async Task WithoutTheRightCookieIsToldSo() =>
        StringAssert.Contains(await RunAsync("responsibilities_list", "{}", Reader(withRight: false)), "needs a right");

    /// <summary>A tool run as an MCP token with these rights (docs/responsibilities-mcp-and-map-plan.md §6.4).</summary>
    private static async Task<System.Text.Json.JsonElement> McpAsync(string tool, string arguments, Dictionary<Area, Level> rights)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var ctx = new ToolContext(scope.ServiceProvider, new UserRights(false, rights), NoMasking.Instance)
        {
            Mcp = new McpCallInfo(TestUsers.Admin, "test-admin", Guid.NewGuid(), McpTokenKind.Read, "https://pnmt.example", "test"),
        };
        var result = await ToolCatalog.All.Single(t => t.Name == tool)
            .Run(System.Text.Json.JsonDocument.Parse(arguments).RootElement, ctx, scope.ServiceProvider.GetRequiredService<Data.PnmtdDbContext>(), CancellationToken.None);
        return System.Text.Json.JsonSerializer.SerializeToElement(result, ToolResults.Json);
    }

    [TestMethod]
    public async Task TheLinksListNeverNamesASensitivePageAndTheUnlinkedViewsNeedTheirRight()
    {
        var name = V1.Unique("Tool Links");
        var r = await (await manager.PostJsonAsync("/api/v1/responsibilities", new { name })).ReadAsync<ResponsibilityDetail>();
        var open = await PageAsync(V1.Unique("Open links page"), false);
        var secret = await PageAsync(V1.Unique("Secret links page"), true);
        await using (var db = app.NewDb())
        {
            foreach (var page in db.KbPages.Where(p => p.Number == open || p.Number == secret).Select(p => p.Id).ToList())
            {
                await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/links", new { kind = "Page", id = page });
            }
        }
        var reader = new Dictionary<Area, Level> { [Area.Responsibilities] = Level.View };
        var rows = await McpAsync("responsibility_links_list", $$"""{"responsibilityIds":["{{r.Id}}"]}""", reader);
        var text = rows.GetRawText();
        StringAssert.Contains(text, "Open links page");
        Assert.IsFalse(text.Contains("Secret links page"), text);
        Assert.AreEqual("1 linked page is not shown", rows.GetProperty("pagesNotShown").GetString());

        var refused = await Assert.ThrowsExactlyAsync<ToolException>(() => McpAsync("responsibility_links_list", """{"view":"unlinkedHosts"}""", reader));
        Assert.AreEqual("missing_right", refused.Code);
        var locations = await Assert.ThrowsExactlyAsync<ToolException>(() => McpAsync("responsibility_links_list", """{"view":"unlinkedLocations"}""",
            new Dictionary<Area, Level> { [Area.Responsibilities] = Level.View, [Area.Monitoring] = Level.View }));
        Assert.AreEqual("missing_right", locations.Code);
        var hosts = await McpAsync("responsibility_links_list", """{"view":"unlinkedHosts","limit":1}""",
            new Dictionary<Area, Level> { [Area.Responsibilities] = Level.View, [Area.Monitoring] = Level.View });
        Assert.IsTrue(hosts.TryGetProperty("items", out _));

        // A sensitive page is no page to link either: refused before anything is written.
        var edit = new Dictionary<Area, Level> { [Area.Responsibilities] = Level.Edit };
        var missing = await Assert.ThrowsExactlyAsync<ToolException>(() =>
            McpAsync("responsibility_links_change", $$"""{"action":"link","responsibilityId":"{{r.Id}}","pageNumbers":[{{secret}}]}""", edit));
        Assert.AreEqual("not_found", missing.Code);
    }

    [TestMethod]
    public void TheServersPaletteIsTheWebUisOne()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PNMTD.slnx")))
        {
            root = root.Parent;
        }
        var colours = File.ReadAllText(Path.Combine(root!.FullName, "PNMTD.WebUI", "src", "components", "colours.ts"));
        var list = colours[colours.IndexOf("export const palette", StringComparison.Ordinal)..colours.IndexOf("export function colourName", StringComparison.Ordinal)];
        var swatches = System.Text.RegularExpressions.Regex.Matches(list, @"\{ name: '([A-Za-z]+)', hex: '(#[0-9a-f]{6})' \}")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.AreEqual(12, swatches.Count);
        CollectionAssert.AreEqual(global::PNMTD.Services.Domain.Colours.Palette.ToList(), swatches);
    }
}
