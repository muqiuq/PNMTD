using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Cookie;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// The output limits' ceilings in .env (docs/cookie-redmine-tab-plan.md §13): the tab offers them, the save refuses a value above
/// them, and every reader counts a saved value above them (a ceiling lowered later, a rollback) as the ceiling.
/// </summary>
[TestClass]
public sealed class CeilingsTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Cookie:Ceilings:EmailPerPersonDay"] = "3",
            ["Cookie:Ceilings:TicketsPerDay"] = "0",
        });
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await Settings.SaveAsync(new ActionSettings(), null, "test", null, CancellationToken.None);
        app.Dispose();
    }

    private static CookieSettings Settings => app.Services.GetRequiredService<CookieSettings>();

    [TestMethod]
    public async Task The_tab_offers_the_ceilings_from_env_and_the_save_keeps_to_them()
    {
        var defaults = await (await admin.GetAsync("/api/v1/cookie/admin/defaults")).ReadAsync<CookieDefaults>();
        Assert.AreEqual(3, defaults.Ceilings.EmailPerPersonDay);
        Assert.AreEqual(0, defaults.Ceilings.TicketsPerDay);
        Assert.AreEqual(OutputLimits.DefaultCeilings.MessagesPerDay, defaults.Ceilings.MessagesPerDay, "a key not in .env keeps the old ceiling");

        using var above = await admin.PutAsJsonAsync("/api/v1/cookie/admin/settings/actions",
            new ActionSettings { Limits = new OutputLimits(EmailPerPersonDay: 4, TicketsPerDay: 0) }, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.BadRequest, above.StatusCode);
        var problem = await above.Content.ReadAsStringAsync();
        StringAssert.Contains(problem, "limits.emailPerPersonDay");
        StringAssert.Contains(problem, "0 to 3.");

        using var within = await admin.PutAsJsonAsync("/api/v1/cookie/admin/settings/actions",
            new ActionSettings { Limits = new OutputLimits(EmailPerPersonDay: 3, TicketsPerDay: 0) }, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.OK, within.StatusCode, await within.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task A_value_saved_above_its_ceiling_counts_as_the_ceiling()
    {
        // As a ceiling lowered in .env after the save, or a rollback to an older version, leaves it: the save's check doesn't see it.
        await Settings.SaveAsync(new ActionSettings { Limits = new OutputLimits(EmailPerPersonDay: 10, TicketsPerDay: 5) }, null, "test", null,
            CancellationToken.None);
        var limits = await Settings.LimitsAsync(CancellationToken.None);
        Assert.AreEqual(3, limits.EmailPerPersonDay);
        Assert.AreEqual(0, limits.TicketsPerDay);
        Assert.AreEqual(new OutputLimits().MessagesPerDay, limits.MessagesPerDay, "a limit below its ceiling stays as saved");

        await using var db = app.NewDb();
        var outbox = app.Services.GetRequiredService<Outbox>();
        Assert.AreEqual("new tickets: 0 a day",
            await outbox.LimitAsync(db, new OutboxRequest(CookieChannel.RedmineTicket, "A ticket", "Its text"), CancellationToken.None));
    }
}
