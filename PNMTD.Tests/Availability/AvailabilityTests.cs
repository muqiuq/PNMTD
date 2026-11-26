using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Availability;
using PNMTD.Boards;
using PNMTD.Lib.Helper;
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
            await db.AvailabilityNotes.Where(n => n.Date >= Monday).ExecuteDeleteAsync();
        }
        await SaveSettingsAsync(defaults.Settings);
        app.Dispose();
    }

    private static Guid Type(string code) => defaults.Types.Single(t => t.Code == code).Id;

    private static async Task PersonAsync(Guid id, string? code, bool inPlan, List<DefaultDay>? week = null, int? share = null)
    {
        using var response = await admin.PutAsJsonAsync($"/api/v1/availability/people/{id}",
            new AvailabilityPersonRequest(inPlan, code, "#a0c4ff", week, inPlan ? 0 : 100, share), People.Json);
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

    private static async Task DayAsync(DateOnly day, Guid? responsible, Guid? fallback, string? comment = null)
    {
        using var rows = await admin.PutAsJsonAsync("/api/v1/availability/days",
            new AvailabilityDaysRequest([new AvailabilityDayChange(day, responsible, fallback, comment)]));
        Assert.AreEqual(HttpStatusCode.NoContent, rows.StatusCode, await rows.Content.ReadAsStringAsync());
    }

    /// <summary>docs/board-messages-plan.md §9.5: the chain in order, those who work, each person once; its head is ResponsibleOn's.</summary>
    [TestMethod]
    public async Task The_chain_gives_the_deputies_in_order_and_each_person_once()
    {
        var day = Monday.AddDays(56);
        await DayAsync(day, TestUsers.Viewer, TestUsers.User);
        Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(admin,
            new AvailabilityCellChange(TestUsers.Viewer, day, Type("ANWE")), new AvailabilityCellChange(TestUsers.User, day, Type("HOME"))));
        await SaveSettingsAsync(defaults.Settings with { GlobalFallbackUserId = TestUsers.User });
        try
        {
            var chain = Read(day).ChainOn(day);
            CollectionAssert.AreEqual(new[] { "responsible", "fallback" }, chain.Select(c => c.As).ToArray(), "the global fallback is the fallback: once");
            CollectionAssert.AreEqual(new[] { TestUsers.Viewer, TestUsers.User }, chain.SelectMany(c => c.UserIds).ToArray());

            await SaveSettingsAsync(defaults.Settings with { GlobalFallbackUserId = TestUsers.Admin });
            Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(admin, new AvailabilityCellChange(TestUsers.Viewer, day, Type("VACA"))));
            var plan = Read(day);
            chain = plan.ChainOn(day);
            CollectionAssert.AreEqual(new[] { "fallback", "global fallback" }, chain.Select(c => c.As).ToArray(), "the responsible is away");
            Assert.AreEqual(plan.ResponsibleOn(day).As, chain[0].As);
            CollectionAssert.AreEqual(plan.ResponsibleOn(day).UserIds.ToArray(), chain[0].UserIds.ToArray());

            var saturday = day.AddDays(5);
            Assert.AreEqual(0, Read(saturday).ChainOn(saturday).Count, "a closed day");
        }
        finally
        {
            await SaveSettingsAsync(defaults.Settings);
        }
    }

    /// <summary>
    /// docs/board-messages-plan.md §9.5: <i>On duty today</i> on a board: the head of the chain, the deputies the element shows, where each
    /// works, the day's comment and the next open day; 0 deputies for a layout from 1.23; labels only when the board hides names.
    /// </summary>
    [TestMethod]
    public async Task On_duty_today_on_a_board_shows_the_deputies_and_the_options()
    {
        var day = Monday.AddDays(63);
        await DayAsync(day, TestUsers.Viewer, TestUsers.User, "Server room closed from 16:00");
        Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(admin,
            new AvailabilityCellChange(TestUsers.Viewer, day, Type("ANWE")), new AvailabilityCellChange(TestUsers.User, day, Type("HOME"))));
        await SaveSettingsAsync(defaults.Settings with { GlobalFallbackUserId = TestUsers.Admin });
        string[] names;
        await using (var db = app.NewDb())
        {
            var users = await db.Users.Where(u => u.Id == TestUsers.Viewer || u.Id == TestUsers.User || u.Id == TestUsers.Admin)
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName);
            names = [users[TestUsers.Viewer], users[TestUsers.User], users[TestUsers.Admin]];
        }
        Clock.Provider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Clock.ToUtc(day.ToDateTime(new TimeOnly(10, 0))));
        try
        {
            async Task<BoardOnDuty> OnDutyAsync(BoardElementSettings settings, BoardPeople people = BoardPeople.Names)
            {
                using var scope = app.Services.CreateScope();
                var element = BoardLayouts.NewElement(BoardElementType.OnDuty) with { Settings = BoardLayouts.Normalize(BoardElementType.OnDuty, settings) };
                var layout = BoardLayouts.New(1, false, false) is var l ? l with { Look = l.Look with { People = people }, Columns = [new BoardColumn(1, [element])] } : null!;
                var view = await scope.ServiceProvider.GetRequiredService<BoardData>().BuildAsync("t", layout, null, null, CancellationToken.None);
                return view.Elements.Single().OnDuty!;
            }

            var full = await OnDutyAsync(new BoardElementSettings(Deputies: 2, Where: true, DayComment: true, Tomorrow: true));
            Assert.AreEqual("responsible", full.As);
            Assert.IsFalse(full.Closed);
            CollectionAssert.AreEqual(new[]
            {
                new BoardDutyPerson(names[0], false, "responsible", "on site"),
                new BoardDutyPerson(names[1], true, "fallback", "home office"),
                new BoardDutyPerson(names[2], true, "global fallback", "on site"),
            }, full.People.ToArray());
            Assert.AreEqual("Server room closed from 16:00", full.Comment);
            Assert.AreEqual(day.AddDays(1), full.Next!.Day);
            CollectionAssert.AreEqual(new[] { names[2] }, full.Next.People.ToArray(), "Tuesday has no row: the global fallback");

            var old = await OnDutyAsync(new BoardElementSettings());
            Assert.AreEqual(1, old.People.Count, "1.23's element: no deputies");
            Assert.AreEqual((null, null), (old.Comment, old.Next), "and no comment or next day unless asked");
            Assert.IsNull(old.People[0].Where);

            var hidden = await OnDutyAsync(new BoardElementSettings(Deputies: 1, Tomorrow: true), BoardPeople.Hidden);
            CollectionAssert.AreEqual(new string?[] { null, null }, hidden.People.Select(p => p.Name).ToArray(), "labels only");
            Assert.IsNull(hidden.Next, "nobody to name for the next day");

            Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(admin, new AvailabilityCellChange(TestUsers.Viewer, day, Type("VACA"))));
            var away = await OnDutyAsync(new BoardElementSettings(Deputies: 5));
            CollectionAssert.AreEqual(new[] { names[1], names[2] }, away.People.Select(p => p.Name).ToArray(), "the fallback on duty, one deputy left");
            Assert.AreEqual("fallback", away.As);

            Clock.Provider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Clock.ToUtc(day.AddDays(5).ToDateTime(new TimeOnly(10, 0))));
            var saturday = await OnDutyAsync(new BoardElementSettings(Deputies: 1));
            Assert.IsTrue(saturday.Closed);
            Assert.AreEqual(0, saturday.People.Count);
        }
        finally
        {
            Clock.Provider = TimeProvider.System;
            await SaveSettingsAsync(defaults.Settings);
        }
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

    /// <summary>
    /// docs/availability-extensions-plan.md §3.1, §3.2: periods that don't overlap save, and the plan shows them as entered, in
    /// PNMT's zone. Before 1.28 the text was converted once more: 08:00 showed as 09:00 in Zurich's winter.
    /// </summary>
    [TestMethod]
    public async Task The_hours_show_as_entered_and_periods_with_a_gap_save()
    {
        var holiday = Monday.AddDays(37);
        var week = defaults.Settings.Week.Select(d => d.Day == DayOfWeek.Monday
            ? new DayHours(d.Day, [new HoursPeriod("08:00", "11:30"), new HoursPeriod("13:00", "17:00")]) : d).ToList();
        try
        {
            await SaveSettingsAsync(defaults.Settings with
            {
                Week = week,
                Holidays = [.. defaults.Settings.Holidays, new Holiday(holiday, "Late shift", [new HoursPeriod("13:00", "24:00")])],
            });
            var plan = await (await admin.GetAsync($"/api/v1/availability/plan?from={Monday:yyyy-MM-dd}&to={holiday:yyyy-MM-dd}")).ReadAsync<AvailabilityPlanItem>();
            Assert.AreEqual("08:00–11:30, 13:00–17:00", plan.Days.Single(d => d.Date == Monday).Hours, $"in {Clock.TimeZone.Id}");
            Assert.AreEqual("13:00–24:00", plan.Days.Single(d => d.Date == holiday).Hours);
            Assert.AreEqual("Late shift", plan.Days.Single(d => d.Date == holiday).Holiday);
            Assert.AreEqual(Clock.TimeZone.Id, plan.TimeZone);

            var overlapping = week.Select(d => d.Day == DayOfWeek.Tuesday
                ? new DayHours(d.Day, [new HoursPeriod("08:00", "12:00"), new HoursPeriod("11:30", "17:00")]) : d).ToList();
            var types = defaults.Types.Where(t => !t.Retired).Select(t => new AvailabilityTypeInput(t.Id, t.Name, t.Code, t.Color, t.Flags, t.IsDefault, t.IsDayOff)).ToList();
            using var bad = await admin.PutAsJsonAsync("/api/v1/availability/settings",
                new AvailabilitySettingsRequest(types, defaults.Settings with { Week = overlapping }), AvailabilitySettings.Json);
            Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
            var body = await bad.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "week.Tuesday");
            Assert.IsFalse(body.Contains("week.Monday"), "08:00–11:30 and 13:00–17:00 don't overlap: " + body);
        }
        finally
        {
            await SaveSettingsAsync(defaults.Settings);
        }
    }

    /// <summary>docs/availability-extensions-plan.md §7: the responsible's name is a setting, trimmed, 1 to 30 characters.</summary>
    [TestMethod]
    public async Task The_responsibles_name_is_a_setting()
    {
        try
        {
            await SaveSettingsAsync(defaults.Settings with { ResponsibleLabel = "  Hero " });
            var plan = await (await viewer.GetAsync($"/api/v1/availability/plan?from={Monday:yyyy-MM-dd}&to={Monday:yyyy-MM-dd}")).ReadAsync<AvailabilityPlanItem>();
            Assert.AreEqual("Hero", plan.ResponsibleLabel);
            var stats = await (await viewer.GetAsync("/api/v1/availability/stats")).ReadAsync<AvailabilityStatsItem>();
            Assert.AreEqual("Hero", stats.ResponsibleLabel);

            var types = defaults.Types.Where(t => !t.Retired).Select(t => new AvailabilityTypeInput(t.Id, t.Name, t.Code, t.Color, t.Flags, t.IsDefault, t.IsDayOff)).ToList();
            foreach (var bad in new[] { " ", new string('x', 31) })
            {
                using var response = await admin.PutAsJsonAsync("/api/v1/availability/settings",
                    new AvailabilitySettingsRequest(types, defaults.Settings with { ResponsibleLabel = bad }), AvailabilitySettings.Json);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                StringAssert.Contains(await response.Content.ReadAsStringAsync(), "responsibleLabel");
            }
        }
        finally
        {
            await SaveSettingsAsync(defaults.Settings);
        }
        var back = await (await viewer.GetAsync($"/api/v1/availability/plan?from={Monday:yyyy-MM-dd}&to={Monday:yyyy-MM-dd}")).ReadAsync<AvailabilityPlanItem>();
        Assert.AreEqual(AvailabilitySettings.DefaultResponsibleLabel, back.ResponsibleLabel);
    }

    /// <summary>§8: a share of 0 to 500 on the person, left as it is when a request doesn't name it.</summary>
    [TestMethod]
    public async Task The_share_as_responsible_is_on_the_person()
    {
        try
        {
            using (var bad = await admin.PutAsJsonAsync($"/api/v1/availability/people/{TestUsers.User}",
                new AvailabilityPersonRequest(true, "USR", "#a0c4ff", null, 0, 501), People.Json))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
                StringAssert.Contains(await bad.Content.ReadAsStringAsync(), "responsibleShare");
            }
            await PersonAsync(TestUsers.User, "USR", inPlan: true, share: 50);
            await PersonAsync(TestUsers.User, "USR", inPlan: true);
            var people = await (await admin.GetAsync("/api/v1/availability/people")).ReadAsync<List<AvailabilityPeopleItem>>();
            Assert.AreEqual(50, people.Single(p => p.Id == TestUsers.User).ResponsibleShare, "left as it was");
            using var profile = await admin.PutAsJsonAsync($"/api/v1/users/{TestUsers.User}/profile",
                new UserProfileRequest(null, null, null, true, "USR", "#a0c4ff", null, 0, ResponsibleShare: 0), People.Json);
            Assert.AreEqual(HttpStatusCode.OK, profile.StatusCode, await profile.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await profile.ReadAsync<PersonProfile>()).ResponsibleShare, "Admin → Users sets it too");
        }
        finally
        {
            await PersonAsync(TestUsers.User, "USR", inPlan: true, share: 100);
        }
    }

    /// <summary>
    /// §6: everyone in the plan writes notes; only the author changes one; the author and Availability: Edit delete it. One line of
    /// 1 to 300 characters, at most 20 a day.
    /// </summary>
    [TestMethod]
    public async Task Notes_are_written_by_the_plan_and_carry_their_authors_name()
    {
        var day = Monday.AddDays(44);
        var user = app.Client(Roles.User);
        using var added = await viewer.PostAsJsonAsync("/api/v1/availability/notes", new AvailabilityNoteRequest(day, " Dentist\nuntil 10 "));
        Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync());
        var note = await added.ReadAsync<AvailabilityNoteItem>();
        Assert.AreEqual("Dentist until 10", note.Text, "one line, trimmed");
        Assert.IsTrue(note.Mine);
        await using (var db = app.NewDb())
        {
            var name = await db.Users.Where(u => u.Id == TestUsers.Viewer).Select(u => u.DisplayName).SingleAsync();
            Assert.AreEqual(name, note.Author);
        }

        var plan = await (await admin.GetAsync($"/api/v1/availability/plan?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}")).ReadAsync<AvailabilityPlanItem>();
        var shown = plan.Days.Single().Notes.Single(n => n.Id == note.Id);
        Assert.IsFalse(shown.Mine, "the admin's view");
        Assert.AreEqual(TestUsers.Viewer, shown.UserId);

        using (var other = await user.PutAsJsonAsync($"/api/v1/availability/notes/{note.Id}", new AvailabilityNoteChange("mine now")))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, other.StatusCode, "only the author changes it");
        }
        using (var changed = await viewer.PutAsJsonAsync($"/api/v1/availability/notes/{note.Id}", new AvailabilityNoteChange("Dentist until 11")))
        {
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
            Assert.AreEqual("Dentist until 11", (await changed.ReadAsync<AvailabilityNoteItem>()).Text);
        }
        foreach (var bad in new[] { " \n ", new string('x', 301) })
        {
            using var response = await viewer.PostAsJsonAsync("/api/v1/availability/notes", new AvailabilityNoteRequest(day, bad));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var notTheirs = await user.DeleteAsync($"/api/v1/availability/notes/{note.Id}"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, notTheirs.StatusCode, "anyone's needs Edit");
        }
        using (var removed = await admin.DeleteAsync($"/api/v1/availability/notes/{note.Id}"))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode, "Edit removes anyone's");
        }

        var full = Monday.AddDays(45);
        for (var i = 0; i < 20; i++)
        {
            using var response = await viewer.PostAsJsonAsync("/api/v1/availability/notes", new AvailabilityNoteRequest(full, $"Note {i}"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        using (var tooMany = await viewer.PostAsJsonAsync("/api/v1/availability/notes", new AvailabilityNoteRequest(full, "one more")))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, tooMany.StatusCode, "at most 20 a day");
        }

        await PersonAsync(TestUsers.User, null, inPlan: false);
        try
        {
            using var outside = await user.PostAsJsonAsync("/api/v1/availability/notes", new AvailabilityNoteRequest(day, "hello"));
            Assert.AreEqual(HttpStatusCode.Forbidden, outside.StatusCode, "not in the plan, no Edit");
        }
        finally
        {
            await PersonAsync(TestUsers.User, "USR", inPlan: true);
        }
        await using var check = app.NewDb();
        Assert.IsTrue(await check.AuditLog.AnyAsync(l => l.EventType == "AvailabilityChanged" && l.Detail!.Contains("a note on")));
    }

    /// <summary>
    /// §9, §11: the Status tab counts open days only; a day as responsible is the day as planned, the fallback's apart; the share is a
    /// weight. A month of its own, far ahead: everything in it is planned.
    /// </summary>
    [TestMethod]
    public async Task The_stats_count_the_planned_responsible_on_open_days_against_the_share()
    {
        // After every other test's days (the latest is Monday + 68).
        var start = Monday.AddDays(105);
        var month = new DateOnly(start.Year, start.Month, 1);
        var open = Enumerable.Range(0, 28).Select(month.AddDays).Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Take(4).ToList();
        var saturday = Enumerable.Range(0, 7).Select(month.AddDays).First(d => d.DayOfWeek == DayOfWeek.Saturday);
        await PersonAsync(TestUsers.Viewer, "VIE", inPlan: true, share: 100);
        await PersonAsync(TestUsers.User, "USR", inPlan: true, share: 50);
        try
        {
            using (var rows = await admin.PutAsJsonAsync("/api/v1/availability/days", new AvailabilityDaysRequest([
                new AvailabilityDayChange(open[0], TestUsers.Viewer, null, null),
                new AvailabilityDayChange(open[1], TestUsers.Viewer, null, null),
                new AvailabilityDayChange(open[2], TestUsers.Viewer, null, null),
                new AvailabilityDayChange(open[3], TestUsers.User, TestUsers.Viewer, null),
                new AvailabilityDayChange(saturday, TestUsers.Viewer, null, null),
            ])))
            {
                Assert.AreEqual(HttpStatusCode.NoContent, rows.StatusCode, await rows.Content.ReadAsStringAsync());
            }
            // Away on two of their days as responsible: they still count, as planned.
            Assert.AreEqual(HttpStatusCode.NoContent, await CellsAsync(admin,
                new AvailabilityCellChange(TestUsers.Viewer, open[0], Type("VACA")), new AvailabilityCellChange(TestUsers.Viewer, open[1], Type("VACA")),
                new AvailabilityCellChange(TestUsers.Viewer, open[2], Type("HOME")), new AvailabilityCellChange(TestUsers.Viewer, saturday, Type("VACA"))));

            using (var tooLate = await viewer.GetAsync($"/api/v1/availability/stats?year={AvailabilityPlan.Today.Year + 2}"))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, tooLate.StatusCode);
            }
            var stats = await (await viewer.GetAsync($"/api/v1/availability/stats?year={month.Year}")).ReadAsync<AvailabilityStatsItem>();
            Assert.AreEqual(month.Year, stats.Year);
            Assert.AreEqual(AvailabilityPlan.Today, stats.Today);
            Assert.AreEqual(Clock.TimeZone.Id, stats.TimeZone);
            var vie = stats.Months.Single(m => m.UserId == TestUsers.Viewer && m.Month == month.Month);
            var usr = stats.Months.Single(m => m.UserId == TestUsers.User && m.Month == month.Month);
            Assert.AreEqual(new AvailabilityTally(0, 3), vie.Responsible, "the Saturday is closed: it doesn't count");
            Assert.AreEqual(new AvailabilityTally(0, 1), vie.Fallback);
            Assert.AreEqual(new AvailabilityTally(0, 1), usr.Responsible);
            Assert.AreEqual(2, vie.Types.Single(t => t.TypeId == Type("VACA")).Planned, "the Saturday's vacation doesn't count");
            Assert.IsGreaterThanOrEqualTo(1, vie.Types.Single(t => t.TypeId == Type("HOME")).Planned);

            Assert.IsTrue(stats.Days.Any(d => d.Date == open[0] && d.ResponsibleId == TestUsers.Viewer));
            Assert.IsFalse(stats.Days.Any(d => d.Date == saturday), "open days only");
            Assert.AreEqual(stats.Columns.Count, stats.Days[0].Counts.Count);

            var vieShare = stats.People.Single(p => p.Id == TestUsers.Viewer);
            var usrShare = stats.People.Single(p => p.Id == TestUsers.User);
            Assert.AreEqual(2.0, vieShare.Target / usrShare.Target, 0.01, "100 against 50");
            Assert.AreEqual(1.0, stats.People.Sum(p => p.Target), 0.01);
            CollectionAssert.AreEquivalent(new[] { "yearToDate", "year", "last365", "last30" }, stats.Windows.Select(w => w.Window).ToList());
            var year = stats.Windows.Single(w => w.Window == "year");
            Assert.AreEqual(year.Total, year.People.Sum(p => p.Days));
            foreach (var row in year.People)
            {
                Assert.AreEqual(row.Target * year.Total, row.Expected, 0.02);
                Assert.AreEqual(row.Days - row.Expected, row.Deviation, 0.02);
            }
            Assert.IsGreaterThanOrEqualTo(3, year.People.Single(p => p.UserId == TestUsers.Viewer).Days);
            Assert.AreEqual(AvailabilityPlan.Today, stats.Ahead!.From);
            Assert.AreEqual(AvailabilityPlan.Today.AddDays(AvailabilityService.AheadDays - 1), stats.Ahead.To);
        }
        finally
        {
            await PersonAsync(TestUsers.Viewer, "VIE", inPlan: true, share: 100);
            await PersonAsync(TestUsers.User, "USR", inPlan: true, share: 100);
        }
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
