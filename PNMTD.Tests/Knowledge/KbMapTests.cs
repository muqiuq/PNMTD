using System.Net;
using PNMTD.Api.V1.Contracts;
using PNMTD.Tests.Api.V1;
using PNMTD.Tests.Locations;

namespace PNMTD.Tests.Knowledge;

/// <summary>
/// The knowledge map (docs/knowledge-base-extensions-plan.md §8, §16): the steps around a focus over every kind of edge; the
/// structure upwards always, downwards only on the way to pages; the whole knowledge base with its filters; the node limit; the
/// states only with Monitoring: View; and the refusals of a bad focus or depth. The limit is 10 here.
/// </summary>
[TestClass]
public sealed class KbMapTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?> { ["KnowledgeBase:MapMaxNodes"] = "10" });
        user = app.Client(Roles.User);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<KbPageDetail> PageAsync(string title, string content = "text", string? tag = null, params KbLinkRequest[] links)
    {
        var request = KbPageTests.Request(title, content, tag is null ? [] : [tag]);
        request.Links = [.. links];
        return (await (await user.PostJsonAsync("/api/v1/kb/pages", request)).ReadAsync<KbSaveResult>()).Page;
    }

    private static KbLinkRequest To(KbLinkKind kind, Guid id) => new() { Kind = kind, Id = id };

    private static async Task<KbGraph> MapAsync(string query, HttpClient? client = null) =>
        await (await (client ?? user).GetAsync($"/api/v1/kb/map?{query}")).ReadAsync<KbGraph>();

    private static string[] Ids(KbGraph g) => [.. g.Nodes.Select(n => n.Id)];

    [TestMethod]
    public async Task Around_a_page_the_steps_follow_page_links_and_links_in_the_texts()
    {
        var d = await PageAsync("Map D");
        var c = await PageAsync("Map C", links: To(KbLinkKind.Page, d.Id));
        var b = await PageAsync("Map B", $"See [C](/pnmt-admin/kb/{c.Number}).");
        var a = await PageAsync("Map A", $"Also in the text: [B](/pnmt-admin/kb/{b.Number}).", links: To(KbLinkKind.Page, b.Id));
        string P(KbPageDetail p) => $"page:{p.Id}";

        var one = await MapAsync($"focus=page:{a.Number}&depth=1");
        Assert.AreEqual(P(a), one.Focus);
        CollectionAssert.AreEquivalent(new[] { P(a), P(b) }, Ids(one));
        var ab = one.Edges.Single();
        Assert.AreEqual(KbGraphEdgeKind.PageLink, ab.Kind);
        Assert.IsTrue(ab.AlsoInText, "the page link and the link in the text are one edge");

        var two = await MapAsync($"focus=page:{a.Number}&depth=2");
        CollectionAssert.AreEquivalent(new[] { P(a), P(b), P(c) }, Ids(two));
        var bc = two.Edges.Single(e => e.Kind == KbGraphEdgeKind.TextLink);
        Assert.AreEqual((P(b), P(c)), (bc.Source, bc.Target), "from the page whose text has it");
        CollectionAssert.AreEquivalent(new[] { P(a), P(b), P(c), P(d) }, Ids(await MapAsync($"focus=page:{a.Number}&depth=3")));
        var node = two.Nodes.Single(n => n.Id == P(a));
        Assert.AreEqual((KbGraphNodeKind.Page, a.Number, "Map A", $"KB-{a.Number}"), (node.Kind, node.Number, node.Name, node.Detail));
    }

    [TestMethod]
    public async Task The_structure_goes_up_always_and_down_only_on_the_way_to_pages()
    {
        var site = await LocationsApiTests.CreateAsync(user);
        var room = await LocationsApiTests.CreateAsync(user, parentId: site.Id);
        var host = await HostAsync(room.Id);
        var quiet = await HostAsync(room.Id);
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        var page = await PageAsync("About the sensor", links: To(KbLinkKind.Sensor, sensor.Id));

        var around = await MapAsync($"focus=page:{page.Number}&depth=1");
        CollectionAssert.AreEquivalent(new[] { $"page:{page.Id}", $"sensor:{sensor.Id}", $"host:{host.Id}", $"location:{room.Id}", $"location:{site.Id}" },
            Ids(around), "the sensor brings its host, room and site");
        CollectionAssert.AreEquivalent(new[] { ($"sensor:{sensor.Id}", $"host:{host.Id}"), ($"host:{host.Id}", $"location:{room.Id}"), ($"location:{room.Id}", $"location:{site.Id}") },
            around.Edges.Where(e => e.Kind == KbGraphEdgeKind.Structure).Select(e => (e.Source, e.Target)).ToArray());
        Assert.IsTrue(around.Nodes.Where(n => n.Kind != KbGraphNodeKind.Page).All(n => n.Group == site.Id.ToString()), "all in the site's circle");
        Assert.AreEqual(site.Name, around.Groups.Single().Name);
        Assert.AreEqual(host.Name, around.Nodes.Single(n => n.Kind == KbGraphNodeKind.Sensor).Detail);

        // From the site down: on the way to the page; the quiet host isn't.
        var down = await MapAsync($"focus=location:{site.Id}&depth=4");
        CollectionAssert.Contains(Ids(down), $"page:{page.Id}");
        CollectionAssert.DoesNotContain(Ids(down), $"host:{quiet.Id}");
        // A host without pages, as the focus, still has its place.
        CollectionAssert.AreEquivalent(new[] { $"host:{quiet.Id}", $"location:{room.Id}", $"location:{site.Id}" }, Ids(await MapAsync($"focus=host:{quiet.Id}&depth=1")));

        // States with Monitoring: View only.
        Assert.IsNotNull(around.Nodes.Single(n => n.Kind == KbGraphNodeKind.Host).State);
        var reader = app.ClientWithRights("KnowledgeBase:View");
        Assert.IsTrue((await MapAsync($"focus=page:{page.Number}&depth=1", reader)).Nodes.All(n => n.State is null));
    }

    [TestMethod]
    public async Task The_whole_knowledge_base_takes_the_filters_and_the_limit()
    {
        var tag = $"m{Guid.NewGuid():N}"[..10];
        var site = await LocationsApiTests.CreateAsync(user);
        var lonely = await PageAsync("Map lonely", tag: tag);
        var other = await PageAsync("Map other", tag: $"{tag}x");
        var linked = await PageAsync("Map linked", tag: tag, links: [To(KbLinkKind.Page, other.Id), To(KbLinkKind.Location, site.Id)]);

        var tagged = await MapAsync($"tags={tag}");
        CollectionAssert.AreEquivalent(new[] { $"page:{linked.Id}", $"page:{other.Id}", $"location:{site.Id}" }, Ids(tagged),
            "the linked page and what it links to; the lonely page has no link");
        CollectionAssert.Contains(Ids(await MapAsync($"tags={tag}&unlinked=true")), $"page:{lonely.Id}");
        CollectionAssert.Contains(Ids(await MapAsync($"location={site.Id}")), $"page:{linked.Id}");

        // Twelve linked pages: ten nodes at most, and it says so.
        var hub = await PageAsync("Map hub", tag: $"{tag}hub");
        for (var i = 0; i < 12; i++)
        {
            await PageAsync($"Map spoke {i}", tag: $"{tag}hub", links: To(KbLinkKind.Page, hub.Id));
        }
        var limited = await MapAsync($"tags={tag}hub");
        Assert.HasCount(10, limited.Nodes);
        Assert.IsTrue(limited.Truncated);
        Assert.AreEqual(13, limited.Total);
        Assert.AreEqual($"page:{hub.Id}", limited.Nodes[0].Id, "the most linked first");
        Assert.IsTrue(limited.Edges.All(e => Ids(limited).Contains(e.Source) && Ids(limited).Contains(e.Target)));
    }

    [TestMethod]
    public async Task A_bad_focus_or_depth_is_refused()
    {
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync("/api/v1/kb/map?focus=page:999999")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync($"/api/v1/kb/map?focus=host:{Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.GetAsync("/api/v1/kb/map?focus=nonsense")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.GetAsync("/api/v1/kb/map?depth=0")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.GetAsync("/api/v1/kb/map?depth=5")).StatusCode);
    }

    private static async Task<HostDetail> HostAsync(Guid locationId)
    {
        using var response = await user.PostJsonAsync("/api/v1/hosts", new { name = V1.Unique("map-host"), locationId });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<HostDetail>();
    }
}
