using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Availability;
using PNMTD.Cookie;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Availability;

/// <summary>
/// The availability plan (docs/cookie-plan.md §8.1, §8.2, §20.6): the checkboxes decide who works; the responsible, the
/// fallback, the global fallback; the fill and <i>Fill again</i>; the counts; one's own column from today on; the contact
/// details on the user. The viewer and the user of the tests are in the plan as VIE and USR, on days far ahead.
/// </summary>
[TestClass]
public sealed class AvailabilityTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static HttpClient viewer = null!;
    private static AvailabilitySettingsItem defaults = null!;
    private static readonly DateOnly Monday = NextMonday(AvailabilityPlan.Today.AddDays(200));

    private static DateOnly NextMonday(DateOnly day)
    {
        while (day.DayOfWeek != DayOfWeek.Monday)
        {
            day = day.AddDays(1);
        }
        return day;
    }

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp();
        admin = app.Client(Roles.Admin);
        viewer = app.Client(Roles.Viewer);
        defaults = await (await admin.GetAsync("/api/v1/availability/settings")).ReadAsync<AvailabilitySettingsItem>();
        await PersonAsync(TestUsers.Viewer, "VIE", inPlan: true);
        await PersonAsync(TestUsers.User, "USR", inPlan: true);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await PersonAsync(TestUsers.Viewer, null, inPlan: false);
        await PersonAsync(TestUsers.User, null, inPlan: false);
        await using (var db = app.NewDb())
        {
            await db.AvailabilityEntries.Where(e => e.UserId == TestUsers.Viewer || e.UserId == TestUsers.User).ExecuteDeleteAsync();
            await db.AvailabilityDays.Where(d => d.Date >= Monday).ExecuteDeleteAsync();
        }
        await SaveSettingsAsync(defaults.Settings);
        app.Dispose();
    }

    private static Guid Type(string code) => defaults.Types.Single(t => t.Code == code).Id;

    private static async Task PersonAsync(Guid id, string? code, bool inPlan, List<DefaultDay>? week = null)
    {
        using var response = await admin.PutAsJsonAsync($"/api/v1/availability/people/{id}",
            new AvailabilityPersonRequest(inPlan, code, "#a0c4ff", week, inPlan ? 0 : 100), People.Json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task SaveSettingsAsync(AvailabilitySettings settings)
    {
        var types = defaults.Types.Where(t => !t.Retired).Select(t => new AvailabilityTypeInput(t.Id, t.Name, t.Code, t.Color, t.Flags, t.IsDefault, t.IsDayOff)).ToList();
        using var response = await admin.PutAsJsonAsync("/api/v1/availability/settings", new AvailabilitySettingsRequest(types, settings), AvailabilitySettings.Json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpStatusCode> CellsAsync(HttpClient client, params AvailabilityCellChange[] cells)
    {
        using var response = await client.PutAsJsonAsync("/api/v1/availability/entries", new AvailabilityCellsRequest(cells));
        return response.StatusCode;
    }

    private static PlanSnapshot Read(DateOnly day) =>
        app.Services.GetRequiredService<AvailabilityPlan>().DayAsync(day, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void The_types_of_the_Excel_plan_come_with_the_migration()
    {
        CollectionAssert.IsSubsetOf(new[] { "ANWE", "HOME", "EXTN", "VACA", "KOMP", "UNAV" }, defaults.Types.Select(t => t.Code).ToList());
        Assert.IsTrue(defaults.Types.Single(t => t.Code == "ANWE").IsDefault);
        Assert.IsTrue(defaults.Types.Single(t => t.Code == "UNAV").IsDayOff);
        Assert.IsTrue(defaults.Types.Single(t => t.Code == "HOME").Works, "home office works");
        Assert.IsFalse(defaults.Types.Single(t => t.Code == "VACA").Works, "vacation doesn't");
        Assert.IsFalse(defaults.Types.Single(t => t.Code == "UNAV").Works, "no checkbox: not working");
    }

    [TestMethod]
    public void The_checkboxes_decide_who_works_never_the_name()
    {
        var both = new AvailabilityTypeEntity { Name = "Present", OnSite = true, Vacation = true };
        Assert.IsFalse(both.Works, "on site with vacation: away");
        Assert.IsTrue(new AvailabilityTypeEntity { Name = "Vacation", WorkTravel = true }.Works, "a name means nothing");
        Assert.IsFalse(new AvailabilityTypeEntity { HomeOffice = true, Compensation = true }.Works);
    }

    [TestMethod]
    public async Task Settings_are_checked()
    {
        var types = defaults.Types.Where(t => !t.Retired).Select(t => new AvailabilityTypeInput(t.Id, t.Name, t.Code, t.Color, t.Flags, true, t.IsDayOff)).ToList();
        using var response = await admin.PutAsJsonAsync("/api/v1/availability/settings", new AvailabilitySettingsRequest(types, defaults.Settings),
            AvailabilitySettings.Json);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, "every type the default");
        using var forbidden = await viewer.GetAsync("/api/v1/availability/settings");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [TestMethod]
    public async Task Everyone_in_the_plan_edits_their_own_column_from_today_on()
    {
        var day = Monday.AddDays(1);
        Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(viewer, new AvailabilityCellChange(TestUsers.Viewer, day, Type("HOME"))));
        Assert.AreEqual(HttpStatusCode.Forbidden, await CellsAsync(viewer, new AvailabilityCellChange(TestUsers.User, day, Type("HOME"))), "another's column");
        Assert.AreEqual(HttpStatusCode.Forbidden,
            await CellsAsync(viewer, new AvailabilityCellChange(TestUsers.Viewer, AvailabilityPlan.Today.AddDays(-1), Type("HOME"))), "the past");
        Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(admin, new AvailabilityCellChange(TestUsers.User, day, Type("VACA"))), "Edit: everything");

        var plan = await (await viewer.GetAsync($"/api/v1/availability/plan?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}")).ReadAsync<AvailabilityPlanItem>();
        Assert.IsTrue(plan.InPlan);
        Assert.IsFalse(plan.CanEditAll);
        Assert.AreEqual("Tu", plan.Days[0].Weekday);
        Assert.AreEqual(Type("HOME"), plan.Days[0].Cells.Single(c => c.UserId == TestUsers.Viewer).TypeId);

        var outsider = await (await app.ClientWithRights("Monitoring:View").GetAsync("/api/v1/availability/plan")).ReadAsync<AvailabilityPlanItem>();
        Assert.IsTrue(outsider.InPlan, "the test's viewer is in the plan whatever the rights");
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == "AvailabilityChanged" && l.Detail!.Contains("VIE")));
    }

    [TestMethod]
    public async Task The_responsible_then_the_fallback_then_the_global_fallback()
    {
        var day = Monday.AddDays(7);
        using (var rows = await admin.PutAsJsonAsync("/api/v1/availability/days",
            new AvailabilityDaysRequest([new AvailabilityDayChange(day, TestUsers.Viewer, TestUsers.User, "Server room work, VIE on site")])))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, rows.StatusCode, await rows.Content.ReadAsStringAsync());
        }
        Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(admin,
            new AvailabilityCellChange(TestUsers.Viewer, day, Type("ANWE")), new AvailabilityCellChange(TestUsers.User, day, Type("HOME"))));
        Assert.AreEqual("responsible", Read(day).ResponsibleOn(day).As);
        CollectionAssert.AreEqual(new[] { TestUsers.Viewer }, Read(day).ResponsibleOn(day).UserIds.ToArray());

        Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(admin, new AvailabilityCellChange(TestUsers.Viewer, day, Type("VACA"))));
        Assert.AreEqual("fallback", Read(day).ResponsibleOn(day).As, "the responsible is on vacation");

        Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(admin, new AvailabilityCellChange(TestUsers.User, day, Type("KOMP"))));
        Assert.AreEqual(0, Read(day).ResponsibleOn(day).UserIds.Count, "both away, no global fallback");
        await SaveSettingsAsync(defaults.Settings with { GlobalFallbackUserId = TestUsers.Admin });
        Assert.AreEqual("global fallback", Read(day).ResponsibleOn(day).As);
        await SaveSettingsAsync(defaults.Settings);

        var weekend = day.AddDays(5);
        Assert.IsTrue(Read(weekend).Closed(weekend), "Saturday: closed by the default hours");
        Assert.AreEqual(0, Read(weekend).ResponsibleOn(weekend).UserIds.Count);
    }

    [TestMethod]
    public async Task The_fill_follows_the_default_week_and_never_overwrites_a_person()
    {
        var friday = Monday.AddDays(18);
        Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(viewer, new AvailabilityCellChange(TestUsers.Viewer, friday.AddDays(-1), Type("VACA"))));
        using (var fill = await viewer.PostAsJsonAsync("/api/v1/availability/fill/mine", new AvailabilityFillRequest(false)))
        {
            Assert.AreEqual(HttpStatusCode.OK, fill.StatusCode, await fill.Content.ReadAsStringAsync());
        }
        var plan = Read(friday);
        Assert.AreEqual(Type("ANWE"), plan.Cell(TestUsers.Viewer, friday)!.TypeId, "the default type");
        Assert.IsTrue(plan.Cell(TestUsers.Viewer, friday)!.Filled);
        Assert.IsNull(plan.Cell(TestUsers.Viewer, friday.AddDays(1)), "Saturday is closed: nothing to fill");
        Assert.IsNull(plan.Cell(TestUsers.User, friday), "only one's own column");

        // HOME on Fridays now: Fill again replaces what the fill wrote, and keeps what the person set.
        var week = People.StandardWeek.Select(d => d.Day == DayOfWeek.Friday ? d with { TypeId = Type("HOME") } : d).ToList();
        await PersonAsync(TestUsers.Viewer, "VIE", inPlan: true, week);
        using (var again = await admin.PostAsJsonAsync("/api/v1/availability/fill", new AvailabilityFillRequest(true)))
        {
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, await again.Content.ReadAsStringAsync());
            Assert.IsGreaterThan(0, (await again.ReadAsync<AvailabilityFillResult>()).Replaced);
        }
        var after = await app.Services.GetRequiredService<AvailabilityPlan>().ReadAsync(friday.AddDays(-1), friday, CancellationToken.None);
        Assert.AreEqual(Type("HOME"), after.Cell(TestUsers.Viewer, friday)!.TypeId);
        Assert.AreEqual(Type("VACA"), after.Cell(TestUsers.Viewer, friday.AddDays(-1))!.TypeId, "a person's entry stays");
        Assert.IsNotNull(after.Cell(TestUsers.User, friday), "everyone's columns");
        await PersonAsync(TestUsers.Viewer, "VIE", inPlan: true, [.. People.StandardWeek]);
    }

    [TestMethod]
    public async Task Fill_again_takes_its_days_its_people_and_the_types_it_may_replace()
    {
        var monday = Monday.AddDays(35);
        var tuesday = monday.AddDays(1);
        var wednesday = monday.AddDays(2);
        using (var fill = await admin.PostAsJsonAsync("/api/v1/availability/fill", new AvailabilityFillRequest(false)))
        {
            Assert.AreEqual(HttpStatusCode.OK, fill.StatusCode, await fill.Content.ReadAsStringAsync());
        }
        Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(viewer, new AvailabilityCellChange(TestUsers.Viewer, tuesday, Type("VACA"))));
        var home = People.StandardWeek.Select(d => d with { TypeId = Type("HOME") }).ToList();
        await PersonAsync(TestUsers.Viewer, "VIE", inPlan: true, home);
        await PersonAsync(TestUsers.User, "USR", inPlan: true, home);
        try
        {
            // Monday and Tuesday, the viewer only, office days only: Tuesday's vacation is the viewer's own, and stays.
            using (var again = await admin.PostAsJsonAsync("/api/v1/availability/fill",
                new AvailabilityFillRequest(true, monday, tuesday, [TestUsers.Viewer], [Type("ANWE")])))
            {
                Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, await again.Content.ReadAsStringAsync());
            }
            var plan = await app.Services.GetRequiredService<AvailabilityPlan>().ReadAsync(monday, wednesday, CancellationToken.None);
            Assert.AreEqual(Type("HOME"), plan.Cell(TestUsers.Viewer, monday)!.TypeId, "in the range, the person, the type");
            Assert.AreEqual(Type("VACA"), plan.Cell(TestUsers.Viewer, tuesday)!.TypeId, "a person's own entry");
            Assert.AreEqual(Type("ANWE"), plan.Cell(TestUsers.Viewer, wednesday)!.TypeId, "after the range");
            Assert.AreEqual(Type("ANWE"), plan.Cell(TestUsers.User, monday)!.TypeId, "another person");

            // With the person's own entries too, vacations only.
            using (var own = await admin.PostAsJsonAsync("/api/v1/availability/fill",
                new AvailabilityFillRequest(true, tuesday, tuesday, [TestUsers.Viewer], [Type("VACA")], OverwriteOwn: true)))
            {
                Assert.AreEqual(1, (await own.ReadAsync<AvailabilityFillResult>()).Replaced);
            }
            plan = await app.Services.GetRequiredService<AvailabilityPlan>().ReadAsync(tuesday, tuesday, CancellationToken.None);
            Assert.AreEqual(Type("HOME"), plan.Cell(TestUsers.Viewer, tuesday)!.TypeId);
            Assert.IsTrue(plan.Cell(TestUsers.Viewer, tuesday)!.Filled, "the fill's now");

            using var past = await admin.PostAsJsonAsync("/api/v1/availability/fill",
                new AvailabilityFillRequest(true, AvailabilityPlan.Today.AddDays(-1), AvailabilityPlan.Today));
            Assert.AreEqual(HttpStatusCode.BadRequest, past.StatusCode, "the future only");
        }
        finally
        {
            await PersonAsync(TestUsers.Viewer, "VIE", inPlan: true, [.. People.StandardWeek]);
            await PersonAsync(TestUsers.User, "USR", inPlan: true, [.. People.StandardWeek]);
        }
    }

    [TestMethod]
    public async Task The_counts_are_by_checkboxes_and_red_below_their_minimum()
    {
        var day = Monday.AddDays(28);
        Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(admin,
            new AvailabilityCellChange(TestUsers.Viewer, day, Type("ANWE")), new AvailabilityCellChange(TestUsers.User, day, Type("EXTN"))));
        var plan = await (await admin.GetAsync($"/api/v1/availability/plan?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}")).ReadAsync<AvailabilityPlanItem>();
        var onSite = plan.Columns.ToList().FindIndex(c => c.Name == "On site");
        var remote = plan.Columns.ToList().FindIndex(c => c.Name == "Remote");
        Assert.IsGreaterThanOrEqualTo(1, plan.Days[0].Counts[onSite]);
        Assert.IsGreaterThanOrEqualTo(1, plan.Days[0].Counts[remote], "work travel counts as remote");
        var column = defaults.Settings.CountColumns.Single(c => c.Name == "On site") with { Minimum = 50 };
        Assert.IsLessThan(column.Minimum!.Value, Read(day).Count(column, day), "below its minimum");
    }

    [TestMethod]
    public async Task The_contact_details_are_on_the_user()
    {
        using (var bad = await viewer.PutAsJsonAsync("/api/v1/account/contact", new AccountContactRequest("not an address", "short")))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        using (var test = await viewer.PostAsJsonAsync("/api/v1/account/contact/test", new ContactTestRequest("pushover")))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, test.StatusCode, "no key yet");
        }
        using (var ok = await viewer.PutAsJsonAsync("/api/v1/account/contact", new AccountContactRequest(" viewer@example.com ", null)))
        {
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode, await ok.Content.ReadAsStringAsync());
            Assert.AreEqual("viewer@example.com", (await ok.ReadAsync<AccountContactItem>()).Email);
        }
        using var taken = await admin.PutAsJsonAsync($"/api/v1/users/{TestUsers.Admin}/profile",
            new UserProfileRequest(null, null, null, true, "VIE", "#ffffff", null, 0), People.Json);
        Assert.AreEqual(HttpStatusCode.BadRequest, taken.StatusCode, "VIE is taken");
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == "ContactChanged"));
    }
}
