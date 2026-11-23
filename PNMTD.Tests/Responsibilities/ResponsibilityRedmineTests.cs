using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Dev;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Responsibilities;

/// <summary>
/// Redmine combinations (docs/responsibilities-plan.md §6): a project PNMT reads with a tracker or every tracker, on a
/// responsibility, with the user who handles it; once across all responsibilities; names from the lookups; a removed user
/// leaves nobody. Against the fake Redmine: IT Support with its subproject Printers, and Network; Other isn't PNMT's.
/// </summary>
[TestClass]
public sealed class ResponsibilityRedmineTests
{
    private const int ItSupport = 1;
    private const int Printers = 2;
    private const int Other = 4;
    private const int Bug = 1;
    private const int Support = 3;

    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient manager = null!;
    private static HttpClient reader = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: FakeRedmine.Settings, services: redmine.Install);
        manager = app.ClientWithRights("Responsibilities:Manage");
        reader = app.ClientWithRights("Responsibilities:View");
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<ResponsibilityDetail> CreateAsync()
    {
        using var response = await manager.PostJsonAsync("/api/v1/responsibilities", new { name = V1.Unique("Workplaces") });
        return await response.ReadAsync<ResponsibilityDetail>();
    }

    private static async Task<Guid> UserAsync(string name, bool redmineLinked = false)
    {
        await using var db = app.NewDb();
        var username = V1.Unique("handler").ToLowerInvariant();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(), Username = username, NormalizedUsername = username, DisplayName = name, PasswordHash = "x", SecurityStamp = "x",
            IsActive = true, CreatedAt = DateTime.UtcNow, RedmineUserId = redmineLinked ? Random.Shared.Next(100_000, 900_000) : null,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Every test uses projects and trackers of its own where it can; the shared table keeps each pair once.</summary>
    private static async Task ClearAsync(int project, int? tracker)
    {
        await using var db = app.NewDb();
        await db.ResponsibilityRedmine.Where(c => c.ProjectId == project && c.TrackerId == tracker).ExecuteDeleteAsync();
    }

    [TestMethod]
    public async Task TheDialogPicksProjectsPnmtReadsWithTheirPathsAndRedminesTrackers()
    {
        var lookups = await (await manager.GetAsync("/api/v1/responsibilities/redmine")).ReadAsync<ResponsibilityRedmineLookups>();
        Assert.IsTrue(lookups.Configured);
        Assert.IsNull(lookups.Error);
        CollectionAssert.AreEquivalent(new[] { "IT Support", "IT Support › Printers", "Network" }, lookups.Projects.Select(p => p.Path).ToArray());
        Assert.AreEqual(1, lookups.Projects.Single(p => p.Id == Printers).Depth);
        Assert.IsTrue(lookups.Trackers.Any(t => t.Id == Bug && t.Name == "Bug"));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/responsibilities/redmine")).StatusCode);
    }

    [TestMethod]
    public async Task ACombinationIsOnOneResponsibilityWithAUserAndExistsOnce()
    {
        await ClearAsync(ItSupport, Bug);
        await ClearAsync(ItSupport, null);
        var dan = await UserAsync("Dan Weber", redmineLinked: true);
        var carla = await UserAsync("Carla Rossi");
        var workplaces = await CreateAsync();
        var other = await CreateAsync();

        using var added = await manager.PostJsonAsync($"/api/v1/responsibilities/{workplaces.Id}/redmine",
            new { projectId = ItSupport, trackerId = Bug, userId = dan, note = "Desk only" });
        Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync());
        var detail = await added.ReadAsync<ResponsibilityDetail>();
        var combination = detail.Redmine.Single();
        Assert.AreEqual("IT Support", combination.ProjectPath);
        Assert.AreEqual("Bug", combination.TrackerName);
        Assert.AreEqual("Dan Weber", combination.User!.Name);
        Assert.IsTrue(combination.UserRedmineLinked);

        // Every tracker beside a single tracker of the same project.
        using var every = await manager.PostJsonAsync($"/api/v1/responsibilities/{workplaces.Id}/redmine", new { projectId = ItSupport, userId = carla });
        Assert.AreEqual(HttpStatusCode.OK, every.StatusCode, await every.Content.ReadAsStringAsync());
        var both = (await every.ReadAsync<ResponsibilityDetail>()).Redmine;
        Assert.IsNull(both[0].TrackerId, "every tracker first");
        Assert.IsFalse(both[0].UserRedmineLinked, "no Redmine link needed");

        // Once across all responsibilities, every tracker (null) included.
        using var twice = await manager.PostJsonAsync($"/api/v1/responsibilities/{other.Id}/redmine", new { projectId = ItSupport, trackerId = Bug, userId = carla });
        Assert.AreEqual(ResponsibilityErrors.DuplicateCombination, await V1.CodeOf(twice));
        StringAssert.Contains((await twice.ProblemAsync()).GetProperty("detail").GetString(), workplaces.Name);
        using var everyTwice = await manager.PostJsonAsync($"/api/v1/responsibilities/{other.Id}/redmine", new { projectId = ItSupport, userId = carla });
        Assert.AreEqual(ResponsibilityErrors.DuplicateCombination, await V1.CodeOf(everyTwice));

        using var notPnmts = await manager.PostJsonAsync($"/api/v1/responsibilities/{other.Id}/redmine", new { projectId = Other, userId = carla });
        Assert.IsTrue((await V1.ErrorsOf(notPnmts)).TryGetProperty("projectId", out _));
        using var noTracker = await manager.PostJsonAsync($"/api/v1/responsibilities/{other.Id}/redmine", new { projectId = Printers, trackerId = 999, userId = carla });
        Assert.IsTrue((await V1.ErrorsOf(noTracker)).TryGetProperty("trackerId", out _));
        using var nobody = await manager.PostJsonAsync($"/api/v1/responsibilities/{other.Id}/redmine", new { projectId = Printers });
        Assert.IsTrue((await V1.ErrorsOf(nobody)).TryGetProperty("userId", out _));
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await reader.PostJsonAsync($"/api/v1/responsibilities/{other.Id}/redmine", new { projectId = Printers, userId = carla })).StatusCode);

        // The overview lists them with the responsibility, and the list counts them.
        var all = await (await reader.GetAsync($"/api/v1/responsibilities/redmine/combinations?responsibility={workplaces.Id}"))
            .ReadAsync<RedmineCombinationList>();
        Assert.AreEqual(2, all.Items.Count);
        Assert.IsTrue(all.Items.All(i => i.ResponsibilityName == workplaces.Name));
        var list = await (await reader.GetAsync("/api/v1/responsibilities")).ReadAsync<ResponsibilityList>();
        Assert.IsTrue(list.RedmineConfigured);
        Assert.AreEqual(2, list.Items.Single(i => i.Id == workplaces.Id).Combinations);
    }

    [TestMethod]
    public async Task AChangeMovesItAndTheHistoryAndAuditSaySo()
    {
        await ClearAsync(Printers, Support);
        var anna = await UserAsync("Anna Moves");
        var ben = await UserAsync("Ben Moves");
        var from = await CreateAsync();
        var to = await CreateAsync();
        var added = await (await manager.PostJsonAsync($"/api/v1/responsibilities/{from.Id}/redmine",
            new { projectId = Printers, trackerId = Support, userId = anna })).ReadAsync<ResponsibilityDetail>();
        var combination = added.Redmine.Single();
        Assert.AreEqual("IT Support › Printers", combination.ProjectPath);

        using var moved = await manager.PutJsonAsync($"/api/v1/responsibilities/{from.Id}/redmine/{combination.Id}",
            new { projectId = Printers, trackerId = Support, userId = ben, responsibilityId = to.Id });
        Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode, await moved.Content.ReadAsStringAsync());
        Assert.AreEqual("Ben Moves", (await moved.ReadAsync<ResponsibilityDetail>()).Redmine.Single().User!.Name);
        var fromNow = await (await reader.GetAsync($"/api/v1/responsibilities/{from.Id}")).ReadAsync<ResponsibilityDetail>();
        Assert.AreEqual(0, fromNow.Redmine.Count);

        var history = await (await reader.GetAsync($"/api/v1/responsibilities/{from.Id}/history")).ReadAsync<Paged<ResponsibilityHistoryItem>>();
        StringAssert.Contains(history.Items.Single(h => h.Kind == ResponsibilityHistoryKind.RedmineRemoved).Detail, $"moved to {to.Name}");
        await using var db = app.NewDb();
        var entry = await db.AuditLog.AsNoTracking().Where(a => a.EventType == AuditEvents.ResponsibilityRedmineSaved && a.TargetId == to.Id.ToString())
            .OrderByDescending(a => a.CreatedAt).FirstAsync();
        Assert.IsTrue(entry.Subjects.Contains(from.Id), "both responsibilities' History show the move");

        using var removed = await manager.DeleteAsync($"/api/v1/responsibilities/{to.Id}/redmine/{combination.Id}");
        Assert.AreEqual(0, (await removed.ReadAsync<ResponsibilityDetail>()).Redmine.Count);
    }

    [TestMethod]
    public async Task ARemovedUserLeavesNobodyAndNeedsAttention()
    {
        await ClearAsync(Printers, Bug);
        var eve = await UserAsync("Eve Leaves");
        var r = await CreateAsync();
        await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/redmine", new { projectId = Printers, trackerId = Bug, userId = eve });
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == eve).ExecuteDeleteAsync();
        }
        var detail = await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}")).ReadAsync<ResponsibilityDetail>();
        Assert.IsNull(detail.Redmine.Single().User);
        Assert.IsTrue(detail.Attention);
        var attention = await (await reader.GetAsync("/api/v1/responsibilities?attention=true")).ReadAsync<ResponsibilityList>();
        Assert.IsTrue(attention.Items.Any(i => i.Id == r.Id));

        // Deleting the responsibility takes its combinations along.
        Assert.AreEqual(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/v1/responsibilities/{r.Id}")).StatusCode);
        await using var check = app.NewDb();
        Assert.IsFalse(await check.ResponsibilityRedmine.AnyAsync(c => c.ResponsibilityId == r.Id));
    }
}
