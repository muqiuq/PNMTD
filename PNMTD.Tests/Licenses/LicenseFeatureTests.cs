using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Notifications;
using PNMTD.Tests.Api.V1;
using PNMTD.Tools;

namespace PNMTD.Tests.Licenses;

/// <summary>
/// The switch (docs/licences-plan.md §13): off by default, and then nothing of the feature is usable or visible; turning it off removes
/// the item sensors and keeps everything else, turning it on again makes them from the assignments.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LicenseFeatureTests
{
    private static readonly Dictionary<string, string?> On = new() { ["Licenses:Enabled"] = "true" };

    [TestMethod]
    public async Task OffByDefaultNothingIsUsableOrVisible()
    {
        using var app = new TestApp();
        var admin = app.Client(Roles.Admin);
        foreach (var path in new[] { "/api/v1/licenses", "/api/v1/licenses/overview", $"/api/v1/licenses/{Guid.NewGuid()}", "/api/v1/license-templates",
                     "/api/v1/license-categories" })
        {
            using var response = await admin.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, path);
            Assert.AreEqual("licenses_disabled", await V1.CodeOf(response), path);
        }
        using (var post = await admin.PostJsonAsync("/api/v1/licenses", new { name = "x" }))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, post.StatusCode, "for an admin too, before the right");
        }
        using (var anonymous = await app.Client().GetAsync("/api/v1/licenses"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, anonymous.StatusCode, "before sign-in is asked for");
        }
        var info = JsonDocument.Parse(await (await admin.GetAsync("/api/v1/system/info")).Content.ReadAsStringAsync()).RootElement;
        Assert.IsFalse(info.GetProperty("licenses").GetBoolean());
        var catalog = await (await admin.GetAsync("/api/v1/rights")).ReadAsync<List<RightsAreaItem>>();
        Assert.IsFalse(catalog.Any(a => a.Area == Area.Licenses), "the area isn't listed");
        var types = await (await admin.GetAsync("/api/v1/system/sensor-types")).ReadAsync<List<SensorTypeInfo>>();
        Assert.IsFalse(types.Any(t => t.Type == SensorType.LICENSE));
        var settings = await (await admin.GetAsync("/api/v1/account/notifications")).ReadAsync<UserNotificationSettings>();
        Assert.IsFalse(settings.Kinds.Any(k => k.Kind.StartsWith("license.", StringComparison.Ordinal)), "the kinds aren't offered");
        var tasks = await (await admin.GetAsync("/api/v1/system/tasks")).ReadAsync<List<PNMTD.Api.V1.Contracts.TaskStatus>>();
        Assert.AreEqual("off", tasks.Single(t => t.Name == "Licence checks").State);
        Assert.IsFalse(LicenseToolsAvailable(app), "no tools");
    }

    [TestMethod]
    public async Task OnEverythingIsThere()
    {
        using var app = new TestApp(settings: On);
        var admin = app.Client(Roles.Admin);
        (await admin.GetAsync("/api/v1/licenses")).EnsureSuccessStatusCode();
        var info = JsonDocument.Parse(await (await admin.GetAsync("/api/v1/system/info")).Content.ReadAsStringAsync()).RootElement;
        Assert.IsTrue(info.GetProperty("licenses").GetBoolean());
        var catalog = await (await admin.GetAsync("/api/v1/rights")).ReadAsync<List<RightsAreaItem>>();
        Assert.AreEqual(26, catalog.Count);
        CollectionAssert.AreEqual(new[] { Level.View, Level.Details, Level.Edit, Level.Manage },
            catalog.Single(a => a.Area == Area.Licenses).Levels.Select(l => l.Level).ToArray());
        var types = await (await admin.GetAsync("/api/v1/system/sensor-types")).ReadAsync<List<SensorTypeInfo>>();
        Assert.AreEqual(Enum.GetValues<SensorType>().Length, types.Count);
        Assert.IsFalse(types.Single(t => t.Type == SensorType.LICENSE).CanCreate);
        var codes = await (await admin.GetAsync("/api/v1/system/status-codes")).ReadAsync<List<StatusCodeInfo>>();
        CollectionAssert.IsSubsetOf(new[] { 245, 246, 445, 446, 447, 448, 449 }, codes.Select(c => c.Code).ToArray());
        CollectionAssert.AreEquivalent(new[] { 245, 246 }, codes.Where(c => c.Name.StartsWith("LICENSE_") && c.Success).Select(c => c.Code).ToArray());
        var settings = await (await admin.GetAsync("/api/v1/account/notifications")).ReadAsync<UserNotificationSettings>();
        CollectionAssert.IsSubsetOf(new[] { UserNotificationKinds.LicenseWarning, UserNotificationKinds.LicenseReview, UserNotificationKinds.LicenseUnowned },
            settings.Kinds.Select(k => k.Kind).ToList());
        Assert.IsFalse(settings.Kinds.Single(k => k.Kind == UserNotificationKinds.LicenseUnowned).Channels[0].Default, "off by default");
        Assert.IsTrue(LicenseToolsAvailable(app));
        var categories = await (await admin.GetAsync("/api/v1/license-categories")).ReadAsync<List<LicenseCategoryItem>>();
        CollectionAssert.IsSubsetOf(new[] { "Software", "Cloud & SaaS", "Connectivity", "Hardware", "Other" }, categories.Select(c => c.Name).ToList());
        Assert.AreEqual("Software", categories.Single(c => c.DefaultFor == LicenseType.License).Name);
    }

    [TestMethod]
    public async Task AGroupKeepsItsGrantWhileTheAreaIsHidden()
    {
        Guid group;
        using (var on = new TestApp(settings: On))
        {
            var created = await (await on.Client(Roles.Admin).PostJsonAsync("/api/v1/groups", new { name = V1.Unique("lic-group"), rights = new { Licenses = "Edit", Mail = "Read" } }))
                .ReadAsync<GroupDetail>();
            group = created.Id;
            Assert.AreEqual(Level.Edit, created.Rights[Area.Licenses]);
        }
        using (var off = new TestApp())
        {
            var admin = off.Client(Roles.Admin);
            var shown = await (await admin.GetAsync($"/api/v1/groups/{group}")).ReadAsync<GroupDetail>();
            Assert.IsFalse(shown.Rights.ContainsKey(Area.Licenses), "hidden while off");
            (await admin.PutJsonAsync($"/api/v1/groups/{group}", new { name = shown.Name, rights = new { Mail = "Read" } })).EnsureSuccessStatusCode();
            await using var db = off.NewDb();
            Assert.IsTrue(await db.GroupRights.AnyAsync(r => r.GroupId == group && r.Area == Area.Licenses && r.Level == Level.Edit), "the stored grant stays");
        }
        using (var again = new TestApp(settings: On))
        {
            var back = await (await again.Client(Roles.Admin).GetAsync($"/api/v1/groups/{group}")).ReadAsync<GroupDetail>();
            Assert.AreEqual(Level.Edit, back.Rights[Area.Licenses], "turned on again, the group has it back");
            (await again.Client(Roles.Admin).DeleteAsync($"/api/v1/groups/{group}")).EnsureSuccessStatusCode();
        }
    }

    [TestMethod]
    public async Task OffRemovesTheItemSensorsAndKeepsEverythingElseOnMakesThemAgain()
    {
        Guid item, host, sensor;
        using (var on = new TestApp(settings: On))
        {
            var admin = on.Client(Roles.Admin);
            host = (await V1.CreateHostAsync(admin, V1.Unique("sync-host"), enabled: false)).Id;
            var created = await (await admin.PostJsonAsync("/api/v1/licenses", new
            {
                type = "License", kind = "Subscription", name = V1.Unique("sync"), renewal = "Manual", expiresOn = Clock.Today.AddDays(5),
                billing = "Recurring", billingMonths = 12, termMonths = 12, priceAmount = 100m,
            })).ReadAsync<LicenseDetail>();
            item = created.Id;
            sensor = (await (await admin.PostJsonAsync($"/api/v1/licenses/{item}/assignments", new { hostIds = new[] { host } }))
                .ReadAsync<List<LicenseAssignmentItem>>()).Single().SensorId!.Value;
        }
        try
        {
            using (var off = new TestApp())
            {
                await using var db = off.NewDb();
                Assert.IsFalse(await db.Sensors.AnyAsync(s => s.Id == sensor), "the item sensor is gone");
                Assert.IsFalse(await db.Events.AnyAsync(e => e.SensorId == sensor), "with its events");
                var assignment = await db.LicenseAssignments.AsNoTracking().SingleAsync(a => a.LicenseId == item);
                Assert.IsNull(assignment.SensorId, "the assignment stays, without a sensor");
                Assert.IsTrue(await db.Licenses.AnyAsync(l => l.Id == item), "the item stays");
                Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.LicenseSensorsRemoved && l.Subjects.Contains(host)));
                var removedEntries = await db.AuditLog.CountAsync(l => l.EventType == AuditEvents.LicenseSensorsRemoved);
                using (var second = new TestApp())
                {
                    await using var db2 = second.NewDb();
                    Assert.AreEqual(removedEntries, await db2.AuditLog.CountAsync(l => l.EventType == AuditEvents.LicenseSensorsRemoved),
                        "a start that changes nothing writes nothing");
                }
            }
            using (var again = new TestApp(settings: On))
            {
                await using var db = again.NewDb();
                var assignment = await db.LicenseAssignments.AsNoTracking().SingleAsync(a => a.LicenseId == item);
                Assert.IsNotNull(assignment.SensorId, "made again from the assignment");
                var made = await db.Sensors.AsNoTracking().SingleAsync(s => s.Id == assignment.SensorId);
                Assert.AreEqual((SensorType.LICENSE, host), (made.Type, made.ParentId));
                Assert.AreEqual(445, (await db.Events.AsNoTracking().Where(e => e.SensorId == made.Id).OrderByDescending(e => e.Created).FirstAsync()).Code,
                    "with its first event at once");
                Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.LicenseSensorsMade && l.Subjects.Contains(host)));
            }
        }
        finally
        {
            using var on = new TestApp(settings: On);
            await using var db = on.NewDb();
            var sensors = await db.LicenseAssignments.Where(a => a.LicenseId == item && a.SensorId != null).Select(a => a.SensorId!.Value).ToListAsync();
            await db.Licenses.Where(l => l.Id == item).ExecuteDeleteAsync();
            await db.Sensors.Where(s => sensors.Contains(s.Id)).ExecuteDeleteAsync();
            await db.Hosts.Where(h => h.Id == host).ExecuteDeleteAsync();
        }
    }

    private static bool LicenseToolsAvailable(TestApp app)
    {
        using var scope = app.Services.CreateScope();
        return ToolCatalog.All.Where(t => t.Name.StartsWith("license", StringComparison.Ordinal))
            .All(t => t.Available?.Invoke(scope.ServiceProvider) ?? true);
    }
}
