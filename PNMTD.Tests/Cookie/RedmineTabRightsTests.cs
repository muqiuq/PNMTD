using System.Net;
using System.Net.Http.Json;
using PNMTD.Api.V1.Contracts;
using PNMTD.Cookie;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// Admin → Cookie → Redmine has a right of its own (docs/cookie-redmine-tab-plan.md §3): <i>Redmine: Manage</i> reaches the
/// tab's endpoints and the pickers, and nothing else of Admin → Cookie; <i>Cookie: Manage</i> alone no longer reaches the tab.
/// </summary>
[TestClass]
public sealed class RedmineTabRightsTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public async Task Redmine_Manage_alone_reaches_the_Redmine_tab_and_its_history_but_not_the_rest_of_Admin_Cookie()
    {
        var manager = app.ClientWithRights("Redmine:Manage");
        var section = await (await manager.GetAsync("/api/v1/cookie/admin/redmine/settings")).ReadAsync<CookieRedmineAdminSettings>();
        Assert.IsNotNull(section.Redmine);
        Assert.IsFalse(section.Duties.FirstAnswerMissed, "the duties' switches, read only: off until switched on under Actions");

        var watched = new CookieRedmineSettings { Projects = [WatchedProject.Default(1) with { UpdateDays = 21, UpdateReminderDays = 20 }] };
        using var save = await manager.PutAsJsonAsync("/api/v1/cookie/admin/redmine/settings", watched, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.OK, save.StatusCode, await save.Content.ReadAsStringAsync());
        var saved = await save.ReadAsync<CookieSaved>();
        var versions = await (await manager.GetAsync("/api/v1/cookie/admin/redmine/settings/versions")).ReadAsync<List<CookieSettingsVersion>>();
        Assert.AreEqual(saved.Version, versions[0].Version);
        using var version = await manager.GetAsync($"/api/v1/cookie/admin/redmine/settings/versions/{saved.Version}");
        StringAssert.Contains(await version.Content.ReadAsStringAsync(), "\"updateDays\":21");
        using var rollback = await manager.PostAsync($"/api/v1/cookie/admin/redmine/settings/rollback/{versions[^1].Version}", null);
        Assert.AreEqual(HttpStatusCode.OK, rollback.StatusCode, await rollback.Content.ReadAsStringAsync());
        Assert.AreEqual(14, (await manager.GetFromJsonAsync<WatchedProject>("/api/v1/cookie/admin/redmine/defaults"))!.UpdateDays);

        // The pickers serve the tab: the groups for the escalation group, Redmine's projects.
        Assert.AreEqual(HttpStatusCode.OK, (await manager.GetAsync("/api/v1/cookie/admin/people")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await manager.GetAsync("/api/v1/cookie/admin/redmine")).StatusCode);
        // The rest of Admin → Cookie stays Cookie: Manage's.
        foreach (var path in new[] { "settings", "overview", "defaults", "settings/actions/versions" })
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/cookie/admin/{path}")).StatusCode, path);
        }
    }

    [TestMethod]
    public async Task Cookie_Manage_alone_no_longer_reaches_the_Redmine_section()
    {
        var cookie = app.ClientWithRights("Cookie:Manage");
        foreach (var path in new[] { "redmine/settings", "redmine/settings/versions", "redmine/defaults" })
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, (await cookie.GetAsync($"/api/v1/cookie/admin/{path}")).StatusCode, path);
        }
        using var put = await cookie.PutAsJsonAsync("/api/v1/cookie/admin/redmine/settings", new CookieRedmineSettings(), CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.Forbidden, put.StatusCode);

        // GET settings leaves the section out; the generic history refuses it; the old PUT is gone.
        var all = await (await cookie.GetAsync("/api/v1/cookie/admin/settings")).ReadAsync<CookieAllSettings>();
        Assert.IsNull(all.Redmine);
        Assert.IsNotNull(all.Actions);
        Assert.AreEqual(HttpStatusCode.NotFound, (await cookie.GetAsync("/api/v1/cookie/admin/settings/redmine/versions")).StatusCode);
        using var rollback = await cookie.PostAsync("/api/v1/cookie/admin/settings/redmine/rollback/1", null);
        Assert.AreEqual(HttpStatusCode.NotFound, rollback.StatusCode);
        using var old = await cookie.PutAsJsonAsync("/api/v1/cookie/admin/settings/redmine", new CookieRedmineSettings(), CookieSettings.Json);
        Assert.IsTrue(old.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, old.StatusCode.ToString());
        Assert.AreEqual(HttpStatusCode.OK, (await cookie.GetAsync("/api/v1/cookie/admin/people")).StatusCode, "the pickers serve Cookie: Manage too");

        // An admin holds every right: the section is in GET settings.
        var admin = app.Client(Roles.Admin);
        Assert.IsNotNull((await (await admin.GetAsync("/api/v1/cookie/admin/settings")).ReadAsync<CookieAllSettings>()).Redmine);
    }

    [TestMethod]
    public void Redmine_Manage_includes_Link_and_no_MCP_token_gets_it()
    {
        var redmine = PNMTD.Auth.Rights.RightsCatalog.Areas.Single(a => a.Area == PNMTD.Auth.Rights.Area.Redmine);
        CollectionAssert.AreEqual(new[] { PNMTD.Auth.Rights.Level.View, PNMTD.Auth.Rights.Level.Link, PNMTD.Auth.Rights.Level.Manage },
            redmine.Levels.Select(l => l.Level).ToArray());
        Assert.AreEqual(PNMTD.Auth.Rights.Level.Link, PNMTD.Mcp.McpRights.Ceiling[PNMTD.Auth.Rights.Area.Redmine]);
    }
}
