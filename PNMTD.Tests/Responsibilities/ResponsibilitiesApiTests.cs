using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;
using PNMTD.Tests.Files;
using PNMTD.Tests.Locations;

namespace PNMTD.Tests.Responsibilities;

/// <summary>
/// Responsibilities (docs/responsibilities-plan.md §4, §5, §11): the rights, names and people, who stays set when deactivated,
/// the global people, links, the history and the audit, and deleting.
/// </summary>
[TestClass]
public sealed class ResponsibilitiesApiTests
{
    private static TestApp app = null!;
    private static string files = null!;
    private static HttpClient admin = null!;
    private static HttpClient manager = null!;
    private static HttpClient reader = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        files = Directory.CreateTempSubdirectory("pnmtd-responsibility-files-").FullName;
        app = new TestApp(settings: new Dictionary<string, string?> { ["KnowledgeBase:FilesPath"] = files });
        admin = app.Client(Roles.Admin);
        manager = app.ClientWithRights("Responsibilities:Manage");
        reader = app.ClientWithRights("Responsibilities:View");
    }

    [ClassCleanup]
    public static void Stop()
    {
        app.Dispose();
        Directory.Delete(files, recursive: true);
    }

    private static async Task<ResponsibilityDetail> CreateAsync(string? name = null, Guid? responsibleId = null, Guid? deputyId = null,
        string? summary = null)
    {
        using var response = await manager.PostJsonAsync("/api/v1/responsibilities", new { name = name ?? V1.Unique("Network"), summary, responsibleId, deputyId });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ResponsibilityDetail>();
    }

    private static async Task<ResponsibilityDetail> UpdateAsync(ResponsibilityDetail r, string? name = null, Guid? responsibleId = null,
        Guid? deputyId = null, bool keepPeople = true)
    {
        using var response = await manager.PutJsonAsync($"/api/v1/responsibilities/{r.Id}", new
        {
            name = name ?? r.Name, summary = r.Summary,
            responsibleId = keepPeople ? responsibleId ?? r.Responsible?.Id : responsibleId,
            deputyId = keepPeople ? deputyId ?? r.Deputy?.Id : deputyId,
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ResponsibilityDetail>();
    }

    private static async Task<Guid> UserAsync(string name, bool active = true)
    {
        await using var db = app.NewDb();
        var username = V1.Unique("person").ToLowerInvariant();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(), Username = username, NormalizedUsername = username, DisplayName = name, PasswordHash = "x", SecurityStamp = "x",
            IsActive = active, CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task SetActiveAsync(Guid user, bool active)
    {
        await using var db = app.NewDb();
        await db.Users.Where(u => u.Id == user).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, active));
    }

    [TestMethod]
    public async Task ViewReadsManageChangesAndOthersSeeNothing()
    {
        var r = await CreateAsync(summary: "Switches, routers, the firewall");
        var list = await (await reader.GetAsync("/api/v1/responsibilities")).ReadAsync<ResponsibilityList>();
        Assert.IsTrue(list.Items.Any(i => i.Id == r.Id && i.Summary == "Switches, routers, the firewall"));
        Assert.AreEqual(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}")).StatusCode);

        using var write = await reader.PostJsonAsync("/api/v1/responsibilities", new { name = V1.Unique("x") });
        Assert.AreEqual(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/responsibilities/people")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.User).GetAsync("/api/v1/responsibilities")).StatusCode,
            "no former role holds the new area");

        // Without Redmine configured, its parts aren't there (docs/responsibilities-plan.md §6.1).
        Assert.IsFalse(list.RedmineConfigured);
        Assert.AreEqual(0, (await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}")).ReadAsync<ResponsibilityDetail>()).Redmine.Count);
        using var combinations = await reader.GetAsync("/api/v1/responsibilities/redmine/combinations");
        Assert.AreEqual(ResponsibilityErrors.RedmineNotConfigured, await V1.CodeOf(combinations));
        using var add = await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/redmine", new { projectId = 1, userId = TestUsers.Viewer });
        Assert.AreEqual(HttpStatusCode.NotFound, add.StatusCode);
        Assert.IsFalse((await (await manager.GetAsync("/api/v1/responsibilities/redmine")).ReadAsync<ResponsibilityRedmineLookups>()).Configured);
    }

    /// <summary>docs/mcp-extensions-plan.md §8.1: Edit creates and changes; deleting and the global people stay Manage's.</summary>
    [TestMethod]
    public async Task EditChangesButOnlyManageDeletesAndSetsTheGlobalPeople()
    {
        var editor = app.ClientWithRights("Responsibilities:Edit");
        using var created = await editor.PostJsonAsync("/api/v1/responsibilities", new { name = V1.Unique("Printers") });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var r = await created.ReadAsync<ResponsibilityDetail>();
        using (var changed = await editor.PutJsonAsync($"/api/v1/responsibilities/{r.Id}", new { name = r.Name, summary = "Every printer" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
        }
        using (var described = await editor.PutJsonAsync($"/api/v1/responsibilities/{r.Id}/description", new { content = "# Printers\n\nThe toner.", revision = r.Revision }))
        {
            Assert.AreEqual(HttpStatusCode.OK, described.StatusCode, await described.Content.ReadAsStringAsync());
        }
        Assert.AreEqual(HttpStatusCode.OK, (await editor.GetAsync("/api/v1/responsibilities/people")).StatusCode);

        using var global = await editor.PutJsonAsync("/api/v1/responsibilities/global", new { responsibleId = (Guid?)null, deputyId = (Guid?)null });
        Assert.AreEqual(HttpStatusCode.Forbidden, global.StatusCode);
        using var delete = await editor.DeleteAsync($"/api/v1/responsibilities/{r.Id}");
        Assert.AreEqual(HttpStatusCode.Forbidden, delete.StatusCode);
        using var deleted = await manager.DeleteAsync($"/api/v1/responsibilities/{r.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, "Manage includes Edit and deletes");
    }

    [TestMethod]
    public async Task NamesAreUniqueIgnoringCaseAndTheDeputyIsSomeoneElse()
    {
        var anna = await UserAsync("Anna Muster");
        var name = V1.Unique("Backup");
        var r = await CreateAsync(name, anna);
        Assert.AreEqual("Anna Muster", r.Responsible!.Name);
        Assert.IsNull(r.Deputy);
        Assert.IsFalse(r.GlobalStandsIn);

        using var duplicate = await manager.PostJsonAsync("/api/v1/responsibilities", new { name = $"  {name.ToUpperInvariant()} " });
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.AreEqual(ResponsibilityErrors.DuplicateName, await V1.CodeOf(duplicate));

        using var same = await manager.PutJsonAsync($"/api/v1/responsibilities/{r.Id}", new { name, responsibleId = anna, deputyId = anna });
        Assert.IsTrue((await V1.ErrorsOf(same)).TryGetProperty("deputyId", out _));

        using var invalid = await manager.PostJsonAsync("/api/v1/responsibilities", new { name = "", summary = new string('x', 301) });
        var errors = await V1.ErrorsOf(invalid);
        Assert.IsTrue(errors.TryGetProperty("name", out _));
        Assert.IsTrue(errors.TryGetProperty("summary", out _));

        var unknown = Guid.NewGuid();
        using var nobody = await manager.PostJsonAsync("/api/v1/responsibilities", new { name = V1.Unique("x"), deputyId = unknown });
        Assert.IsTrue((await V1.ErrorsOf(nobody)).TryGetProperty("deputyId", out _));

        // A deputy without a responsible.
        var ben = await UserAsync("Ben Keller");
        var alone = await CreateAsync(deputyId: ben);
        Assert.IsNull(alone.Responsible);
        Assert.AreEqual(ben, alone.Deputy!.Id);
        Assert.IsFalse(alone.GlobalStandsIn);
    }

    [TestMethod]
    public async Task ADeactivatedPersonStaysSetAndTheGlobalPeopleStandIn()
    {
        var carla = await UserAsync("Carla Rossi");
        var r = await CreateAsync(responsibleId: carla);
        await SetActiveAsync(carla, false);

        var changed = await UpdateAsync(r, name: r.Name + "-2");
        Assert.AreEqual(carla, changed.Responsible!.Id, "who stays chosen may be inactive");
        Assert.IsFalse(changed.Responsible.Active);
        Assert.IsTrue(changed.GlobalStandsIn);
        Assert.IsTrue(changed.Attention);

        var other = await CreateAsync();
        using var refused = await manager.PutJsonAsync($"/api/v1/responsibilities/{other.Id}", new { name = other.Name, responsibleId = carla });
        Assert.IsTrue((await V1.ErrorsOf(refused)).TryGetProperty("responsibleId", out _), "a newly chosen person is active");

        var attention = await (await reader.GetAsync("/api/v1/responsibilities?attention=true")).ReadAsync<ResponsibilityList>();
        Assert.IsTrue(attention.Items.Any(i => i.Id == r.Id));
        Assert.IsTrue(attention.Items.Any(i => i.Id == other.Id), "nobody set needs attention too");
        Assert.IsTrue(attention.Items.All(i => i.Attention));

        // A deleted user's place becomes empty.
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == carla).ExecuteDeleteAsync();
        }
        var after = await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}")).ReadAsync<ResponsibilityDetail>();
        Assert.IsNull(after.Responsible);
    }

    [TestMethod]
    public async Task TheGlobalPeopleAreTwoDifferentUsersAndTheChangeIsAudited()
    {
        var anna = await UserAsync("Anna Global");
        var ben = await UserAsync("Ben Global");
        using var same = await manager.PutJsonAsync("/api/v1/responsibilities/global", new { responsibleId = anna, deputyId = anna });
        Assert.IsTrue((await V1.ErrorsOf(same)).TryGetProperty("deputyId", out _));

        using var set = await manager.PutJsonAsync("/api/v1/responsibilities/global", new { responsibleId = anna, deputyId = ben });
        var global = await set.ReadAsync<ResponsibilityGlobal>();
        Assert.AreEqual("Anna Global", global.Responsible!.Name);
        Assert.AreEqual("Ben Global", global.Deputy!.Name);
        var list = await (await reader.GetAsync("/api/v1/responsibilities")).ReadAsync<ResponsibilityList>();
        Assert.AreEqual(anna, list.Global.Responsible!.Id);

        await using var db = app.NewDb();
        var entry = await db.AuditLog.AsNoTracking().Where(a => a.EventType == AuditEvents.GlobalResponsibleChanged)
            .OrderByDescending(a => a.CreatedAt).FirstAsync();
        StringAssert.Contains(entry.Detail, "Anna Global");
        StringAssert.Contains(entry.Detail, "Ben Global");
    }

    [TestMethod]
    public async Task LinksArePagesLocationsAndHostsOnceEachAndGoWithTheirObject()
    {
        var r = await CreateAsync();
        var host = await V1.CreateHostAsync(admin, V1.Unique("sw-core"));
        var site = await LocationsApiTests.CreateAsync(admin, V1.Unique("Zurich"));
        var page = (await (await admin.PostJsonAsync("/api/v1/kb/pages", new KbPageRequest { Title = V1.Unique("WAN providers"), Content = "x" }))
            .ReadAsync<KbSaveResult>()).Page;
        var sensor = await V1.CreateSensorAsync(admin, host.Id);

        foreach (var (kind, id) in new[] { ("Host", host.Id), ("Location", site.Id), ("Page", page.Id) })
        {
            using var linked = await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/links", new { kind, id });
            Assert.AreEqual(HttpStatusCode.OK, linked.StatusCode, await linked.Content.ReadAsStringAsync());
        }
        using var twice = await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/links", new { kind = "Host", id = host.Id });
        Assert.AreEqual(ResponsibilityErrors.AlreadyLinked, await V1.CodeOf(twice));
        using var aSensor = await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/links", new { kind = "Sensor", id = sensor.Id });
        Assert.AreEqual(HttpStatusCode.BadRequest, aSensor.StatusCode);
        using var missing = await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/links", new { kind = "Host", id = Guid.NewGuid() });
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        var detail = await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}")).ReadAsync<ResponsibilityDetail>();
        Assert.AreEqual(3, detail.Links.Count);
        Assert.AreEqual($"KB-{page.Number}", detail.Links.Single(l => l.Kind == KbLinkKind.Page).Detail);
        Assert.AreEqual(site.Name, detail.Links.Single(l => l.Kind == KbLinkKind.Location).Name);
        Assert.AreEqual(1, (await (await reader.GetAsync("/api/v1/responsibilities")).ReadAsync<ResponsibilityList>()).Items
            .Count(i => i.Id == r.Id && i.Links == 3));

        // The object's own History shows the link.
        await using (var db = app.NewDb())
        {
            var entry = await db.AuditLog.AsNoTracking().Where(a => a.EventType == AuditEvents.ResponsibilityLinked && a.TargetId == r.Id.ToString())
                .ToListAsync();
            Assert.IsTrue(entry.Any(e => e.Subjects.Contains(host.Id)));
        }

        var link = detail.Links.Single(l => l.Kind == KbLinkKind.Location);
        using var unlinked = await manager.DeleteAsync($"/api/v1/responsibilities/{r.Id}/links/{link.LinkId}");
        Assert.AreEqual(2, (await unlinked.ReadAsync<ResponsibilityDetail>()).Links.Count);

        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/hosts/{host.Id}")).StatusCode);
        var after = await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}")).ReadAsync<ResponsibilityDetail>();
        Assert.AreEqual(KbLinkKind.Page, after.Links.Single().Kind, "deleting the host removed its link");
    }

    [TestMethod]
    public async Task OnlyALinkedObjectShowsItsResponsibilitiesNothingIsInherited()
    {
        var anna = await UserAsync("Anna Linked");
        var network = await CreateAsync(V1.Unique("A-Network"), anna);
        var backup = await CreateAsync(V1.Unique("B-Backup"));
        var site = await LocationsApiTests.CreateAsync(admin, V1.Unique("Basel"));
        var inSite = await V1.CreateHostAsync(admin, V1.Unique("fw"));
        await admin.PutJsonAsync("/api/v1/hosts/location", new { ids = new[] { inSite.Id }, locationId = site.Id });
        await manager.PostJsonAsync($"/api/v1/responsibilities/{network.Id}/links", new { kind = "Location", id = site.Id });
        await manager.PostJsonAsync($"/api/v1/responsibilities/{backup.Id}/links", new { kind = "Location", id = site.Id });

        var onSite = await (await reader.GetAsync($"/api/v1/responsibilities/linked?location={site.Id}")).ReadAsync<List<ResponsibilityRef>>();
        CollectionAssert.AreEqual(new[] { network.Id, backup.Id }, onSite.Select(r => r.Id).ToArray(), "by name");
        Assert.AreEqual("Anna Linked", onSite[0].Responsible!.Name);
        Assert.IsTrue(onSite[1].GlobalStandsIn);

        var onHost = await (await reader.GetAsync($"/api/v1/responsibilities/linked?host={inSite.Id}")).ReadAsync<List<ResponsibilityRef>>();
        Assert.AreEqual(0, onHost.Count, "a host in a linked location isn't linked");
        using var two = await reader.GetAsync($"/api/v1/responsibilities/linked?host={inSite.Id}&location={site.Id}");
        Assert.AreEqual(HttpStatusCode.BadRequest, two.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.Viewer).GetAsync($"/api/v1/responsibilities/linked?host={inSite.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task EveryChangeWritesAHistoryRowAndAnAuditEntry()
    {
        var anna = await UserAsync("Anna History");
        var ben = await UserAsync("Ben History");
        var r = await CreateAsync(responsibleId: anna);
        var renamed = await UpdateAsync(r, name: r.Name + "-renamed", deputyId: ben);
        Assert.AreEqual(ben, renamed.Deputy!.Id);

        var history = await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}/history")).ReadAsync<Paged<ResponsibilityHistoryItem>>();
        CollectionAssert.AreEquivalent(new[] { ResponsibilityHistoryKind.Created, ResponsibilityHistoryKind.Renamed, ResponsibilityHistoryKind.PeopleChanged },
            history.Items.Select(h => h.Kind).ToArray());
        StringAssert.Contains(history.Items.Single(h => h.Kind == ResponsibilityHistoryKind.PeopleChanged).Detail, "deputy nobody → Ben History");
        var row = await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}/history/{history.Items[0].Id}"))
            .ReadAsync<ResponsibilityHistoryDetail>();
        Assert.AreEqual(history.Items[0].Kind, row.Kind);

        await using var db = app.NewDb();
        var entries = await db.AuditLog.AsNoTracking().Where(a => a.TargetId == r.Id.ToString()).ToListAsync();
        Assert.IsTrue(entries.Any(e => e.EventType == AuditEvents.ResponsibilityCreated && e.Detail!.Contains("Anna History")));
        Assert.IsTrue(entries.Any(e => e.EventType == AuditEvents.ResponsibilityChanged && e.Detail!.Contains("Ben History")));
        Assert.IsTrue(entries.All(e => e.Category == AuditCategory.Responsibilities));
    }

    [TestMethod]
    public async Task DeletingRemovesItsLinksAndHistory()
    {
        var r = await CreateAsync();
        var host = await V1.CreateHostAsync(admin, V1.Unique("nas"));
        await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/links", new { kind = "Host", id = host.Id });
        Assert.AreEqual(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/v1/responsibilities/{r.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/v1/responsibilities/{r.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}")).StatusCode);

        await using var db = app.NewDb();
        Assert.IsFalse(await db.ResponsibilityLinks.AnyAsync(l => l.ResponsibilityId == r.Id));
        Assert.IsFalse(await db.ResponsibilityHistory.AnyAsync(h => h.ResponsibilityId == r.Id));
        var entry = await db.AuditLog.AsNoTracking().SingleAsync(a => a.EventType == AuditEvents.ResponsibilityDeleted && a.TargetId == r.Id.ToString() && a.Outcome == AuditOutcome.Succeeded);
        StringAssert.Contains(entry.Detail, "1 link");
    }

    private static Task<HttpResponseMessage> DescribeAsync(Guid id, string content, int revision, bool acknowledge = false) =>
        manager.PutJsonAsync($"/api/v1/responsibilities/{id}/description", new { content, revision, acknowledgeCredentials = acknowledge });

    [TestMethod]
    public async Task TheDescriptionIsSavedAsAPagesTextIs()
    {
        var r = await CreateAsync();
        using var first = await DescribeAsync(r.Id, "## What it covers\n\nThe **core switches** in every building.", r.Revision);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        var saved = await first.ReadAsync<ResponsibilityDetail>();
        Assert.AreEqual(r.Revision + 1, saved.Revision);

        using var stale = await DescribeAsync(r.Id, "Someone else's text", r.Revision);
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.AreEqual("edit_conflict", await V1.CodeOf(stale));

        var withPassword = saved.Description + "\nThe old router: password: Sommer2024!x";
        using var warned = await DescribeAsync(r.Id, withPassword, saved.Revision);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, warned.StatusCode);
        Assert.AreEqual("looks_like_credential", await V1.CodeOf(warned));
        using var acknowledged = await DescribeAsync(r.Id, withPassword, saved.Revision, acknowledge: true);
        Assert.AreEqual(HttpStatusCode.OK, acknowledged.StatusCode);
        var current = await acknowledged.ReadAsync<ResponsibilityDetail>();

        using var tooLarge = await DescribeAsync(r.Id, new string('x', 1024 * 1024 + 1), current.Revision);
        Assert.IsTrue((await V1.ErrorsOf(tooLarge)).TryGetProperty("content", out _));

        // The plain text is searched.
        var found = await (await reader.GetAsync("/api/v1/responsibilities?q=core%20switches")).ReadAsync<ResponsibilityList>();
        Assert.IsTrue(found.Items.Any(i => i.Id == r.Id));

        // Restoring the text from before the first change: a save of its own.
        var history = await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}/history")).ReadAsync<Paged<ResponsibilityHistoryItem>>();
        var changes = history.Items.Where(h => h.Kind == ResponsibilityHistoryKind.DescriptionChanged).ToList();
        Assert.AreEqual(2, changes.Count);
        StringAssert.Contains(changes[^1].Detail, "+3 −0 lines");
        var row = await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}/history/{changes[0].Id}")).ReadAsync<ResponsibilityHistoryDetail>();
        Assert.AreEqual(withPassword, row.NewValue);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await reader.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/history/{changes[0].Id}/restore", new { })).StatusCode);
        using var restored = await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/history/{changes[0].Id}/restore", new { });
        Assert.AreEqual(saved.Description, (await restored.ReadAsync<ResponsibilityDetail>()).Description);
        var created = history.Items.Single(h => h.Kind == ResponsibilityHistoryKind.Created);
        using var notText = await manager.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/history/{created.Id}/restore", new { });
        Assert.AreEqual(HttpStatusCode.BadRequest, notText.StatusCode);
        var after = await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}/history")).ReadAsync<Paged<ResponsibilityHistoryItem>>();
        StringAssert.Contains(after.Items[0].Detail, "restored from before");
    }

    [TestMethod]
    public async Task ItsFilesAreItsOwnUsesUploadedWithManageAndGoneWithIt()
    {
        using var upload = await FileStoreTests.UploadAsync(manager, "attachment", "contract.txt", Encoding.UTF8.GetBytes("The printers' service contract."));
        Assert.AreEqual(HttpStatusCode.Created, upload.StatusCode, "Responsibilities: Manage uploads");
        var file = await upload.ReadAsync<FileItem>();
        Assert.AreEqual(HttpStatusCode.OK, (await reader.GetAsync(file.Url)).StatusCode, "Responsibilities: View reads");

        var r = await CreateAsync();
        using var described = await DescribeAsync(r.Id, $"The contract: [contract.txt]({file.Url})", r.Revision);
        var detail = await described.ReadAsync<ResponsibilityDetail>();
        Assert.AreEqual(file.Id, detail.Files.Single().Id);

        await using var db = app.NewDb();
        Assert.IsTrue(await db.FileUses.AnyAsync(u => u.FileId == file.Id && u.ResponsibilityId == r.Id));
        var listed = await (await admin.GetAsync("/api/v1/files?search=contract")).ReadAsync<FilesOverview>();
        Assert.IsTrue(listed.Files.Items.Single(f => f.File.Id == file.Id).UsedBy.Any(u => u.Kind == FileUserKind.Responsibility && u.Id == r.Id));

        Assert.AreEqual(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/v1/responsibilities/{r.Id}")).StatusCode);
        Assert.IsFalse(await db.FileUses.AnyAsync(u => u.FileId == file.Id), "unused again, for the cleanup");
    }

    [TestMethod]
    public async Task TheSearchFindsNamesDescriptionsAndPeopleAndThePickersList()
    {
        var person = await UserAsync(V1.Unique("Dora"));
        var r = await CreateAsync(summary: "Printers and their contracts", responsibleId: person);
        var byPerson = await (await reader.GetAsync($"/api/v1/responsibilities?q={Uri.EscapeDataString(await NameOfAsync(person))}"))
            .ReadAsync<ResponsibilityList>();
        Assert.AreEqual(r.Id, byPerson.Items.Single().Id);
        var bySummary = await (await reader.GetAsync("/api/v1/responsibilities?q=their%20contracts")).ReadAsync<ResponsibilityList>();
        Assert.IsTrue(bySummary.Items.Any(i => i.Id == r.Id));

        var people = await (await manager.GetAsync("/api/v1/responsibilities/people")).ReadAsync<ResponsibilityPeople>();
        Assert.IsTrue(people.Users.Any(u => u.Id == person));
        var host = await V1.CreateHostAsync(admin, V1.Unique("printer-hub"));
        var targets = await (await manager.GetAsync($"/api/v1/responsibilities/targets?q={host.Name}")).ReadAsync<SearchResult>();
        Assert.AreEqual(host.Id, targets.Hosts.Single().Id);
        Assert.AreEqual(0, targets.Sensors.Count);
    }

    private static async Task<string> NameOfAsync(Guid user)
    {
        await using var db = app.NewDb();
        return await db.Users.Where(u => u.Id == user).Select(u => u.DisplayName).SingleAsync();
    }
}
