using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Responsibilities;

/// <summary>
/// A responsibility's colour (docs/responsibilities-extensions-plan.md §4.2): #rrggbb or none, on the form and on its own endpoint,
/// with a history row and the audit; shown in the list, the page, the panels and the Redmine rows.
/// </summary>
[TestClass]
public sealed class ResponsibilityColourTests
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

    private static async Task<ResponsibilityDetail> CreateAsync(string? color)
    {
        using var response = await editor.PostJsonAsync("/api/v1/responsibilities", new { name = V1.Unique("Network"), color });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ResponsibilityDetail>();
    }

    [TestMethod]
    public async Task AColourIsStoredLowerCaseAndShownWhereverItIsNamed()
    {
        var r = await CreateAsync(" #2F8FD8 ");
        Assert.AreEqual("#2f8fd8", r.Color);
        var list = await (await reader.GetAsync("/api/v1/responsibilities")).ReadAsync<ResponsibilityList>();
        Assert.AreEqual("#2f8fd8", list.Items.Single(i => i.Id == r.Id).Color);

        var host = await V1.CreateHostAsync(admin, V1.Unique("sw-core"));
        using (var linked = await editor.PostJsonAsync($"/api/v1/responsibilities/{r.Id}/links", new { kind = "Host", id = host.Id }))
        {
            Assert.AreEqual(HttpStatusCode.OK, linked.StatusCode);
        }
        var panel = await (await reader.GetAsync($"/api/v1/responsibilities/linked?host={host.Id}")).ReadAsync<List<ResponsibilityRef>>();
        Assert.AreEqual("#2f8fd8", panel.Single().Color);

        Assert.IsNull((await CreateAsync(null)).Color, "a colour is optional");
    }

    [TestMethod]
    public async Task OnlyHexColoursAreTaken()
    {
        foreach (var wrong in new[] { "blue", "#12345", "#1234567", "#gggggg", "2f8fd8" })
        {
            using var response = await editor.PostJsonAsync("/api/v1/responsibilities", new { name = V1.Unique("Wrong"), color = wrong });
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, wrong);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "#rrggbb", wrong);
        }
        var r = await CreateAsync("#2e9d5b");
        using var colour = await editor.PutJsonAsync($"/api/v1/responsibilities/{r.Id}/color", new { color = "red" });
        Assert.AreEqual(HttpStatusCode.BadRequest, colour.StatusCode);
    }

    [TestMethod]
    public async Task TheFormAndItsOwnEndpointChangeItWithAHistoryRowAndAnAuditEntry()
    {
        var r = await CreateAsync(null);
        using (var form = await editor.PutJsonAsync($"/api/v1/responsibilities/{r.Id}", new { name = r.Name, color = "#d94f4f" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, form.StatusCode, await form.Content.ReadAsStringAsync());
            Assert.AreEqual("#d94f4f", (await form.ReadAsync<ResponsibilityDetail>()).Color);
        }
        using (var forbidden = await reader.PutJsonAsync($"/api/v1/responsibilities/{r.Id}/color", new { color = "#2e9d5b" }))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode, "View only reads");
        }
        using (var alone = await editor.PutJsonAsync($"/api/v1/responsibilities/{r.Id}/color", new { color = "#2E9D5B" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, alone.StatusCode, await alone.Content.ReadAsStringAsync());
            Assert.AreEqual("#2e9d5b", (await alone.ReadAsync<ResponsibilityDetail>()).Color);
        }
        using (var same = await editor.PutJsonAsync($"/api/v1/responsibilities/{r.Id}/color", new { color = "#2e9d5b" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, same.StatusCode, "the same colour again changes nothing");
        }
        using (var cleared = await editor.PutJsonAsync($"/api/v1/responsibilities/{r.Id}/color", new { color = (string?)null }))
        {
            Assert.IsNull((await cleared.ReadAsync<ResponsibilityDetail>()).Color);
        }

        var history = await (await reader.GetAsync($"/api/v1/responsibilities/{r.Id}/history")).ReadAsync<Paged<ResponsibilityHistoryItem>>();
        var colours = history.Items.Where(h => h.Kind == ResponsibilityHistoryKind.ColorChanged).Select(h => h.Detail).ToList();
        CollectionAssert.AreEquivalent(new[] { "none → #d94f4f", "#d94f4f → #2e9d5b", "#2e9d5b → none" }, colours);

        await using var db = app.NewDb();
        var entries = await db.AuditLog.AsNoTracking()
            .Where(a => a.EventType == AuditEvents.ResponsibilityChanged && a.TargetId == r.Id.ToString() && a.Outcome == AuditOutcome.Succeeded)
            .Select(a => a.Detail).ToListAsync();
        Assert.IsTrue(entries.Any(d => d!.Contains("colour none → #d94f4f")), string.Join(" | ", entries));
        Assert.IsTrue(entries.Any(d => d!.Contains("colour #d94f4f → #2e9d5b")), string.Join(" | ", entries));
    }

    [TestMethod]
    public async Task AFormWithoutAColourClearsIt()
    {
        // The whole form, as the page sends it: no colour means none.
        var r = await CreateAsync("#8e5cd9");
        using var form = await editor.PutJsonAsync($"/api/v1/responsibilities/{r.Id}", new { name = r.Name });
        Assert.IsNull((await form.ReadAsync<ResponsibilityDetail>()).Color);
    }
}
