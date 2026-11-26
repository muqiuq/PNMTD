using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Models.Db;
using PNMTD.Responsibilities;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Responsibilities;

/// <summary>
/// The escalation order (docs/responsibilities-mcp-and-map-plan.md §3): responsible, deputy, global responsible, global deputy, as
/// one queue; whoever isn't set or is inactive is skipped and the next moves up, and a person counts once. On the list, a
/// responsibility's page and the map.
/// </summary>
[TestClass]
public sealed class EscalationOrderTests
{
    private static readonly ResponsibilityPerson Anna = new(Guid.NewGuid(), "Anna", true);
    private static readonly ResponsibilityPerson Ben = new(Guid.NewGuid(), "Ben", true);
    private static readonly ResponsibilityPerson Carla = new(Guid.NewGuid(), "Carla", true);
    private static readonly ResponsibilityPerson Dan = new(Guid.NewGuid(), "Dan", true);

    private static string Order(ResponsibilityPerson? r, ResponsibilityPerson? d, ResponsibilityPerson? gr, ResponsibilityPerson? gd) =>
        string.Join(" · ", EscalationOrder.Of(r, d, gr, gd).Select(s => $"{s.Step} {s.Person.Name} ({s.Role})"));

    [TestMethod]
    public void TheQueueSkipsWhoIsMissingAndTheNextMovesUp()
    {
        Assert.AreEqual("1 Anna (Responsible) · 2 Ben (Deputy) · 3 Carla (GlobalResponsible) · 4 Dan (GlobalDeputy)", Order(Anna, Ben, Carla, Dan));
        Assert.AreEqual("1 Ben (Deputy) · 2 Carla (GlobalResponsible) · 3 Dan (GlobalDeputy)", Order(null, Ben, Carla, Dan));
        Assert.AreEqual("1 Ben (Deputy) · 2 Carla (GlobalResponsible)", Order(Anna with { Active = false }, Ben, Carla, null));
        Assert.AreEqual("1 Carla (GlobalResponsible) · 2 Dan (GlobalDeputy)", Order(null, null, Carla, Dan));
        Assert.AreEqual("1 Anna (Responsible) · 2 Dan (GlobalDeputy)", Order(Anna, null, Carla with { Active = false }, Dan));
        Assert.AreEqual("", Order(null, null, null, null));
    }

    [TestMethod]
    public void APersonCountsOnceAtTheirFirstPlace()
    {
        Assert.AreEqual("1 Carla (Responsible) · 2 Ben (Deputy) · 3 Dan (GlobalDeputy)", Order(Carla, Ben, Carla, Dan));
        Assert.AreEqual("1 Anna (Responsible) · 2 Ben (Deputy)", Order(Anna, Ben, Ben, Anna));
    }

    [TestMethod]
    public void TheGlobalPeopleStandInExactlyWhenNoneOfItsOwnIsInTheQueue()
    {
        ResponsibilityPerson?[] Variants(ResponsibilityPerson p) => [null, p, p with { Active = false }];
        var count = 0;
        foreach (var r in Variants(Anna))
        foreach (var d in Variants(Ben))
        foreach (var gr in Variants(Carla))
        foreach (var gd in Variants(Dan))
        {
            var steps = EscalationOrder.Of(r, d, gr, gd);
            Assert.AreEqual(ResponsibilityQueries.StandsIn(r, d),
                steps.All(s => s.Role is ResponsibilityRole.GlobalResponsible or ResponsibilityRole.GlobalDeputy), $"{r} {d} {gr} {gd}");
            Assert.IsTrue(steps.All(s => s.Person.Active), "only active people");
            CollectionAssert.AreEqual(Enumerable.Range(1, steps.Count).ToList(), steps.Select(s => s.Step).ToList());
            count++;
        }
        Assert.AreEqual(81, count);
    }

    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<Guid> UserAsync(string name)
    {
        await using var db = app.NewDb();
        var username = V1.Unique("person").ToLowerInvariant();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(), Username = username, NormalizedUsername = username, DisplayName = name, PasswordHash = "x", SecurityStamp = "x",
            IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [TestMethod]
    public async Task TheListThePageAndTheMapCarryIt()
    {
        var admin = app.Client(Roles.Admin);
        var reader = app.ClientWithRights("Responsibilities:View");
        var anna = await UserAsync("Anna Queue");
        var ben = await UserAsync("Ben Queue");
        var carla = await UserAsync("Carla Queue");
        using var before = await admin.GetAsync("/api/v1/responsibilities");
        var previous = (await before.ReadAsync<ResponsibilityList>()).Global;
        try
        {
            using (var set = await admin.PutJsonAsync("/api/v1/responsibilities/global", new { responsibleId = carla, deputyId = (Guid?)null }))
            {
                Assert.AreEqual(HttpStatusCode.OK, set.StatusCode, await set.Content.ReadAsStringAsync());
            }
            using var created = await admin.PostJsonAsync("/api/v1/responsibilities", new { name = V1.Unique("Queue"), responsibleId = anna, deputyId = ben });
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
            var id = (await created.ReadAsync<ResponsibilityDetail>()).Id;

            string Names(IEnumerable<ResponsibilityStep> steps) => string.Join(" → ", steps.Select(s => $"{s.Person.Name}:{s.Role}"));
            async Task<(string List, string Page, string Map)> ReadAsync()
            {
                using var list = await reader.GetAsync("/api/v1/responsibilities");
                using var page = await reader.GetAsync($"/api/v1/responsibilities/{id}");
                using var map = await reader.GetAsync("/api/v1/responsibilities/map");
                return (Names((await list.ReadAsync<ResponsibilityList>()).Items.Single(i => i.Id == id).Escalation),
                    Names((await page.ReadAsync<ResponsibilityDetail>()).Escalation),
                    Names((await map.ReadAsync<ResponsibilityMap>()).Responsibilities.Single(n => n.Id == id).Escalation));
            }

            var all = "Anna Queue:Responsible → Ben Queue:Deputy → Carla Queue:GlobalResponsible";
            Assert.AreEqual((all, all, all), await ReadAsync());

            // Anna leaves: Ben is asked first, Carla second.
            await using (var db = app.NewDb())
            {
                await db.Users.Where(u => u.Id == anna).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
            }
            var moved = "Ben Queue:Deputy → Carla Queue:GlobalResponsible";
            Assert.AreEqual((moved, moved, moved), await ReadAsync());
        }
        finally
        {
            using var back = await admin.PutJsonAsync("/api/v1/responsibilities/global",
                new { responsibleId = previous.Responsible?.Id, deputyId = previous.Deputy?.Id });
        }
    }
}
