using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Locations;

/// <summary>
/// <i>Admin → Location fields</i> (docs/locations-knowledge-plan.md §4.2–§4.4): keys, the safe type changes, the choices'
/// rules, the order, archiving, and deleting archived fields only.
/// </summary>
[TestClass]
public sealed class LocationFieldsApiTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static HttpClient user = null!;
    private static HttpClient viewer = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        admin = app.Client(Roles.Admin);
        user = app.Client(Roles.User);
        viewer = app.Client(Roles.Viewer);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<LocationFieldItem> CreateAsync(object body) =>
        await (await admin.PostJsonAsync("/api/v1/location-fields", body)).ReadAsync<LocationFieldItem>();

    private static async Task SetAsync(Guid location, Guid field, object? value, int revision = 0)
    {
        using var response = await user.PutJsonAsync($"/api/v1/locations/{location}/values/{field}", new { value, revision });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<LocationValueItem> ValueAsync(Guid location, Guid field) =>
        (await (await viewer.GetAsync($"/api/v1/locations/{location}")).ReadAsync<LocationDetail>()).Values.Single(v => v.FieldId == field);

    [TestMethod]
    public void SlugsSpellUmlautsOutAndDropTheRest()
    {
        Assert.AreEqual("on-site-contact", LocationFieldService.Slug("On-site contact"));
        Assert.AreEqual("oeffnungszeiten-buero", LocationFieldService.Slug(" Öffnungszeiten / Büro "));
        Assert.AreEqual("cafe-1", LocationFieldService.Slug("Café #1"));
        Assert.AreEqual("field", LocationFieldService.Slug("!!!"));
    }

    [TestMethod]
    public async Task EveryRoleReadsOnlyAdminsDefineAndTheOverviewFieldIsSeeded()
    {
        var list = await (await viewer.GetAsync("/api/v1/location-fields")).ReadAsync<LocationFieldList>();
        var overview = list.Items.Single(f => f.Id == LocationFieldEntity.OverviewId);
        Assert.AreEqual(LocationFieldType.Markdown, overview.Type);
        Assert.IsTrue(overview.Overview);
        Assert.IsFalse(overview.Inherited);
        Assert.AreEqual(10, list.Typical.Count);
        Assert.AreEqual("on-site-contact", list.Typical.Single(t => t.Label == "On-site contact").Key);

        // The users a User field can name, for those who fill fields only.
        Assert.IsNull(list.Users);
        var forUsers = await (await user.GetAsync("/api/v1/location-fields")).ReadAsync<LocationFieldList>();
        Assert.AreEqual("Test Viewer", forUsers.Users!.Single(u => u.Id == TestUsers.Viewer).Name);

        using var forbidden = await user.PostJsonAsync("/api/v1/location-fields", new { label = "x", type = "Text" });
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var invalid = await admin.PostJsonAsync("/api/v1/location-fields", new { label = "", type = "Choice", options = new { choices = new[] { "a", "A" } } });
        var errors = await V1.ErrorsOf(invalid);
        Assert.IsTrue(errors.TryGetProperty("label", out _));
        Assert.IsTrue(errors.TryGetProperty("options.choices", out _));
    }

    [TestMethod]
    public async Task KeysComeFromTheFirstLabelAndNeverChange()
    {
        var label = V1.Unique("Uplink ID");
        var first = await CreateAsync(new { label, type = "Text", group = "Network", help = "The circuit", overview = true, sensitive = true });
        var second = await CreateAsync(new { label, type = "Text" });
        Assert.AreEqual(LocationFieldService.Slug(label), first.Key);
        Assert.AreEqual(first.Key + "-2", second.Key);
        Assert.AreEqual("Network", first.Group);
        Assert.IsTrue(first.Sensitive);

        var renamed = await (await admin.PutJsonAsync($"/api/v1/location-fields/{first.Id}", new { label = "Circuit", type = "Text" })).ReadAsync<LocationFieldItem>();
        Assert.AreEqual(first.Key, renamed.Key);
        Assert.AreEqual("Circuit", renamed.Label);
        Assert.IsNull(renamed.Group);
    }

    [TestMethod]
    public async Task TypesChangeOnlyAlongSafePathsOnceValuesExist()
    {
        var site = await LocationsApiTests.CreateAsync(user);
        var text = await CreateAsync(new { label = V1.Unique("Notes"), type = "Text" });
        var number = await CreateAsync(new { label = V1.Unique("Power"), type = "Number", options = new { unit = "kW", min = 0, max = 50, decimals = 1 } });
        Assert.AreEqual("kW", number.Options!.Unit);

        // Without values, anything goes.
        var empty = await CreateAsync(new { label = V1.Unique("Empty"), type = "Date" });
        Assert.AreEqual(HttpStatusCode.OK, (await admin.PutJsonAsync($"/api/v1/location-fields/{empty.Id}", new { label = empty.Label, type = "YesNo" })).StatusCode);

        await SetAsync(site.Id, text.Id, "Rack 4");
        await SetAsync(site.Id, number.Id, 12.5);
        using var unsafeChange = await admin.PutJsonAsync($"/api/v1/location-fields/{number.Id}", new { label = number.Label, type = "Text" });
        Assert.AreEqual(HttpStatusCode.Conflict, unsafeChange.StatusCode);
        Assert.AreEqual("type_change", await V1.CodeOf(unsafeChange));

        var markdown = await (await admin.PutJsonAsync($"/api/v1/location-fields/{text.Id}", new { label = text.Label, type = "Markdown" })).ReadAsync<LocationFieldItem>();
        Assert.AreEqual(LocationFieldType.Markdown, markdown.Type);
        Assert.AreEqual("Rack 4", (await ValueAsync(site.Id, text.Id)).Value!.Value.GetString());
    }

    [TestMethod]
    public async Task AChoiceInUseStaysAndARenameReachesEveryValue()
    {
        var site = await LocationsApiTests.CreateAsync(user);
        var other = await LocationsApiTests.CreateAsync(user);
        var kind = await CreateAsync(new { label = V1.Unique("Kind"), type = "Choice", options = new { choices = new[] { "Colo", "Office", "Store" } } });
        await SetAsync(site.Id, kind.Id, "Colo");
        await SetAsync(other.Id, kind.Id, "office");

        using var removing = await admin.PutJsonAsync($"/api/v1/location-fields/{kind.Id}",
            new { label = kind.Label, type = "Choice", options = new { choices = new[] { "Colo", "Store" } } });
        Assert.AreEqual(HttpStatusCode.Conflict, removing.StatusCode);
        var problem = await removing.ProblemAsync();
        Assert.AreEqual("choice_in_use", problem.GetProperty("code").GetString());
        StringAssert.Contains(problem.GetProperty("detail").GetString(), other.Name);
        Assert.AreEqual(other.Id, problem.GetProperty("locations")[0].GetProperty("id").GetGuid());

        // An unused one goes; a renamed one is renamed in the values; then Choice → Choices keeps them as lists.
        await (await admin.PutJsonAsync($"/api/v1/location-fields/{kind.Id}", new
        {
            label = kind.Label, type = "Choice", options = new { choices = new[] { "Colocation", "Office" } },
            renames = new[] { new { from = "Colo", to = "Colocation" } },
        })).ReadAsync<LocationFieldItem>();
        var renamed = await ValueAsync(site.Id, kind.Id);
        Assert.AreEqual("Colocation", renamed.Value!.Value.GetString());
        Assert.AreEqual(2, renamed.Revision);

        await (await admin.PutJsonAsync($"/api/v1/location-fields/{kind.Id}", new
        {
            label = kind.Label, type = "Choices", options = new { choices = new[] { "Colocation", "Office" } },
        })).ReadAsync<LocationFieldItem>();
        var list = await ValueAsync(other.Id, kind.Id);
        Assert.AreEqual("Office", list.Value!.Value[0].GetString());
    }

    [TestMethod]
    public async Task ArchivingHidesDeletingNeedsItAndIsAudited()
    {
        var site = await LocationsApiTests.CreateAsync(user);
        var field = await CreateAsync(new { label = V1.Unique("Doomed"), type = "Text" });
        await SetAsync(site.Id, field.Id, "a value");

        using var notArchived = await admin.DeleteAsync($"/api/v1/location-fields/{field.Id}");
        Assert.AreEqual(HttpStatusCode.Conflict, notArchived.StatusCode);
        Assert.AreEqual("not_archived", await V1.CodeOf(notArchived));

        var archived = await (await admin.PostJsonAsync($"/api/v1/location-fields/{field.Id}/archive", new { })).ReadAsync<LocationFieldItem>();
        Assert.IsNotNull(archived.ArchivedAt);
        Assert.AreEqual(1, archived.Filled);
        Assert.IsFalse((await (await viewer.GetAsync($"/api/v1/locations/{site.Id}")).ReadAsync<LocationDetail>()).Values.Any(v => v.FieldId == field.Id));
        using var hidden = await user.PutJsonAsync($"/api/v1/locations/{site.Id}/values/{field.Id}", new { value = "x", revision = 1 });
        Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);

        var restored = await (await admin.PostJsonAsync($"/api/v1/location-fields/{field.Id}/restore", new { })).ReadAsync<LocationFieldItem>();
        Assert.IsNull(restored.ArchivedAt);
        Assert.AreEqual("a value", (await ValueAsync(site.Id, field.Id)).Value!.Value.GetString());

        await admin.PostJsonAsync($"/api/v1/location-fields/{field.Id}/archive", new { });
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.DeleteAsync($"/api/v1/location-fields/{field.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/location-fields/{field.Id}")).StatusCode);

        using var db = app.NewDb();
        Assert.IsFalse(await db.LocationValues.AnyAsync(v => v.FieldId == field.Id));
        Assert.IsFalse(await db.LocationHistory.AnyAsync(h => h.FieldId == field.Id), "its history rows go with it");
        var logged = await db.AuditLog.AsNoTracking().SingleAsync(l => l.EventType == AuditEvents.LocationFieldDeleted && l.Detail!.Contains(field.Key));
        StringAssert.Contains(logged.Detail, "1 value deleted");
    }

    [TestMethod]
    public async Task TheOrderIsTheOneGiven()
    {
        var a = await CreateAsync(new { label = V1.Unique("A"), type = "Text" });
        var b = await CreateAsync(new { label = V1.Unique("B"), type = "Text" });
        var ordered = await (await admin.PutJsonAsync("/api/v1/location-fields/order", new { ids = new[] { b.Id, a.Id } })).ReadAsync<LocationFieldList>();
        Assert.AreEqual(b.Id, ordered.Items[0].Id);
        Assert.AreEqual(a.Id, ordered.Items[1].Id);
        CollectionAssert.AreEqual(Enumerable.Range(0, ordered.Items.Count).ToArray(), ordered.Items.Select(i => i.Position).ToArray());

        using var unknown = await admin.PutJsonAsync("/api/v1/location-fields/order", new { ids = new[] { Guid.NewGuid() } });
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
    }
}
