using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Responsibilities;

/// <summary>
/// The overview (docs/responsibilities-extensions-plan.md §6, §7.3): the map with its people, the layers' objects and the cut at 600;
/// places saved by hand for everyone, skipped when gone, checked, cleared and reset, each in the audit log.
/// </summary>
[TestClass]
public sealed class ResponsibilityMapTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static HttpClient editor = null!;
    private static HttpClient reader = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        admin = app.Client(Roles.Admin);
        editor = app.ClientWithRights("Responsibilities:Edit");
        reader = app.ClientWithRights("Responsibilities:View");
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<ResponsibilityDetail> CreateAsync(string name, Guid? responsibleId = null, Guid? deputyId = null)
    {
        using var response = await editor.PostJsonAsync("/api/v1/responsibilities", new { name = V1.Unique(name), responsibleId, deputyId, color = "#2f8fd8" });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ResponsibilityDetail>();
    }

    private static async Task<Guid> UserAsync(string name, string? code = null, string? color = null, bool active = true)
    {
        await using var db = app.NewDb();
        var username = V1.Unique("person").ToLowerInvariant();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(), Username = username, NormalizedUsername = username, DisplayName = name, PasswordHash = "x", SecurityStamp = "x",
            IsActive = active, CreatedAt = DateTime.UtcNow, ShortCode = code, Color = color,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<ResponsibilityMap> MapAsync(string query = "", HttpClient? client = null)
    {
        using var response = await (client ?? reader).GetAsync($"/api/v1/responsibilities/map{query}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ResponsibilityMap>();
    }

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, params (Guid Id, double X, double Y)[] places) =>
        await client.PutJsonAsync("/api/v1/responsibilities/map/positions", new { responsibilities = places.Select(p => new { id = p.Id, x = p.X, y = p.Y }) });

    [TestMethod]
    public async Task TheMapHasEveryResponsibilityTheirPeopleAndTheLayersObjects()
    {
        var anna = await UserAsync("Anna Map", "AMP", "#d94f4f");
        var ben = await UserAsync("Ben Map");
        var r = await CreateAsync("Network", anna, ben);
        await using (var db = app.NewDb())
        {
            // Deactivated since: he stays set (docs/responsibilities-plan.md §4.2).
            await db.Users.Where(u => u.Id == ben).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        }
        var host = await V1.CreateHostAsync(admin, V1.Unique("sw-map"));
        using (var linked = await editor.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/links", new { kind = "Host", id = host.Id }))
        {
            Assert.AreEqual(HttpStatusCode.OK, linked.StatusCode);
        }

        var map = await MapAsync();
        var node = map.Responsibilities.Single(n => n.Id == r.Id);
        Assert.AreEqual("#2f8fd8", node.Color);
        Assert.AreEqual((anna, ben), (node.ResponsibleId!.Value, node.DeputyId!.Value));
        Assert.IsNull(node.X, "placed automatically");
        Assert.IsTrue(node.Attention, "its deputy is inactive");
        var a = map.People.Single(p => p.Id == anna);
        Assert.AreEqual(("AMP", "#d94f4f", true), (a.Code, a.Color, a.Active));
        Assert.IsFalse(map.People.Single(p => p.Id == ben).Active);
        Assert.AreEqual(0, map.Objects.Count, "no layer of objects asked for");

        var hosts = await MapAsync("?hosts=true");
        var o = hosts.Objects.Single(x => x.Id == host.Id);
        Assert.AreEqual((KbLinkKind.Host, host.Name), (o.Kind, o.Name));
        Assert.AreEqual(r.Id, o.Links.Single().ResponsibilityId);
        Assert.IsTrue(hosts.Objects.All(x => x.Kind == KbLinkKind.Host));
        Assert.IsFalse((await MapAsync("?pages=true")).Objects.Any(x => x.Id == host.Id));

        using var forbidden = await app.Client(Roles.User).GetAsync("/api/v1/responsibilities/map");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [TestMethod]
    public async Task AtMost600ObjectsAreDrawnAndAllAreCounted()
    {
        var r = await CreateAsync("Many objects");
        await using (var db = app.NewDb())
        {
            for (var i = 0; i < 601; i++)
            {
                var host = new HostEntity { Id = Guid.NewGuid(), Name = V1.Unique("map-filler"), Location = "lab", Created = DateTime.UtcNow };
                db.Hosts.Add(host);
                db.ResponsibilityLinks.Add(new ResponsibilityLinkEntity { Id = Guid.NewGuid(), ResponsibilityId = r.Id, HostId = host.Id, CreatedAt = DateTime.UtcNow });
            }
            await db.SaveChangesAsync();
        }
        var map = await MapAsync("?hosts=true");
        Assert.AreEqual(600, map.Objects.Count);
        Assert.IsTrue(map.ObjectsTotal >= 601);
        Assert.IsTrue(map.Truncated);
    }

    [TestMethod]
    public async Task PlacesAreSavedForEveryoneAndSkippedWhenGone()
    {
        var one = await CreateAsync("Placed");
        var two = await CreateAsync("Placed too");
        using (var viewOnly = await SaveAsync(reader, (one.Id, 1, 2)))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, viewOnly.StatusCode);
        }
        using (var saved = await SaveAsync(editor, (one.Id, 120.5, -40.25), (two.Id, -300, 210), (Guid.NewGuid(), 0, 0)))
        {
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            var result = await saved.ReadAsync<ResponsibilityMapSaveResult>();
            Assert.AreEqual((2, 1), (result.Saved, result.Skipped));
        }
        var node = (await MapAsync()).Responsibilities.Single(n => n.Id == one.Id);
        Assert.AreEqual((120.5, -40.25), (node.X!.Value, node.Y!.Value));
        Assert.IsNotNull(node.Position);
        using (var again = await SaveAsync(editor, (one.Id, 10, 20)))
        {
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, "saving again updates");
        }
        Assert.AreEqual(10, (await MapAsync()).Responsibilities.Single(n => n.Id == one.Id).X);

        await using var db = app.NewDb();
        var entry = await db.AuditLog.AsNoTracking().Where(e => e.EventType == AuditEvents.ResponsibilityMapSaved && e.Subjects.Contains(two.Id))
            .SingleAsync();
        StringAssert.Contains(entry.Detail, "saved the places of 2 responsibilities");
    }

    [TestMethod]
    public async Task WrongPlacesAreRefused()
    {
        var r = await CreateAsync("Wrong place");
        foreach (var (x, y) in new[] { (double.NaN, 0d), (1e9, 0d), (0d, -100_001d) })
        {
            using var response = await editor.PutJsonAsync("/api/v1/responsibilities/map/positions",
                new { responsibilities = new[] { new { id = r.Id, x = double.IsNaN(x) ? (object)"NaN" : x, y } } });
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, $"{x}, {y}");
        }
        using (var twice = await SaveAsync(editor, (r.Id, 1, 1), (r.Id, 2, 2)))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, twice.StatusCode);
        }
        using var none = await editor.PutJsonAsync("/api/v1/responsibilities/map/positions", new { responsibilities = Array.Empty<object>() });
        Assert.AreEqual(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [TestMethod]
    public async Task BackToAutomaticForOneOrAllAndDeletingTakesThePlaceAlong()
    {
        var one = await CreateAsync("Automatic");
        var two = await CreateAsync("Automatic too");
        var three = await CreateAsync("Deleted");
        using (var saved = await SaveAsync(editor, (one.Id, 1, 1), (two.Id, 2, 2), (three.Id, 3, 3)))
        {
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
        }
        Assert.AreEqual(HttpStatusCode.NoContent, (await editor.DeleteAsync($"/api/v1/responsibilities/map/positions/{one.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await editor.DeleteAsync($"/api/v1/responsibilities/map/positions/{one.Id}")).StatusCode);
        Assert.IsNull((await MapAsync()).Responsibilities.Single(n => n.Id == one.Id).X);

        Assert.AreEqual(HttpStatusCode.NoContent, (await app.ClientWithRights("Responsibilities:Manage").DeleteAsync($"/api/v1/responsibilities/{three.Id}")).StatusCode);
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.ResponsibilityMapPositions.AnyAsync(p => p.ResponsibilityId == three.Id), "its place went with it");
        }

        Assert.AreEqual(HttpStatusCode.NoContent, (await editor.DeleteAsync("/api/v1/responsibilities/map/positions")).StatusCode);
        var map = await MapAsync();
        Assert.IsNull(map.Responsibilities.Single(n => n.Id == two.Id).X);
        Assert.AreEqual(0, map.SavedPositions);
        await using (var db = app.NewDb())
        {
            Assert.IsTrue(await db.AuditLog.AnyAsync(e => e.EventType == AuditEvents.ResponsibilityMapReset && e.Detail!.Contains("back to automatic")));
        }
    }
}
