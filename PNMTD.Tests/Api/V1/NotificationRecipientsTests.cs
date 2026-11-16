using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Notifications;
using PNMTD.Tasks;

namespace PNMTD.Tests.Api.V1;

/// <summary>
/// Rules to a user, a group or the responsible (docs/notification-recipients-plan.md): whom they reach by the channels and the
/// plan, with the stand-in; what the API refuses; the task sending one message per delivery and recording whom; a rule that
/// reaches nobody in the coverage. Against the fake Pushover; e-mail isn't set up, so an e-mail delivery fails without harm.
/// </summary>
[TestClass]
public sealed class NotificationRecipientsTests
{
    private static readonly DebugTests.FakePushover pushover = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static HttpClient user = null!;
    private static HttpClient viewer = null!;
    private static UserEntity anna = null!;
    private static UserEntity ben = null!;
    private static UserEntity carl = null!;
    private static UserEntity dora = null!;
    private static GroupEntity storage = null!;
    private static readonly List<Guid> created = [];

    private const string AnnaKey = "uQiRzpo4DXghDmr9QzzfQu27cmVRsG";
    private const string CarlKey = "cQiRzpo4DXghDmr9QzzfQu27cmVRsC";

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        var settings = new Dictionary<string, string?>(DebugTests.Settings) { ["Development:DoNotSendNotifications"] = "false" };
        app = new TestApp(settings: settings, services: s => pushover.Install(s));
        admin = app.Client(Roles.Admin);
        user = app.Client(Roles.User);
        viewer = app.Client(Roles.Viewer);
        await using var db = app.NewDb();
        UserEntity User(string name, string code) => new()
        {
            Id = Guid.NewGuid(), Username = V1.Unique(name), NormalizedUsername = V1.Unique(name).ToLowerInvariant(), DisplayName = name,
            PasswordHash = "x", SecurityStamp = Guid.NewGuid().ToString(), InPlan = true, ShortCode = code,
        };
        anna = User("Anna Muster", "NRA");
        anna.Email = "anna@example.com";
        anna.PushoverKey = AnnaKey;
        ben = User("Ben Ott", "NRB");
        ben.Email = "ben@example.com";
        carl = User("Carl Inactive", "NRC");
        carl.PushoverKey = CarlKey;
        carl.IsActive = false;
        dora = User("Dora Nowhere", "NRD");
        db.Users.AddRange(anna, ben, carl, dora);
        storage = new GroupEntity { Id = Guid.NewGuid(), Name = V1.Unique("Storage"), NormalizedName = V1.Unique("storage") };
        db.Groups.Add(storage);
        await db.SaveChangesAsync();
        db.UserGroups.AddRange(new[] { anna, ben, carl, dora }.Select(u => new UserGroupEntity { UserId = u.Id, GroupId = storage.Id }));
        await db.SaveChangesAsync();
        // The default week: Monday to Friday 08:00–17:00, the weekend closed, so that the plan has closed days and nights.
        await V1.AvailabilityHoursAsync(admin, null);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        foreach (var id in created)
        {
            await user.DeleteAsync($"/api/v1/notification-rules/{id}");
        }
        app.Dispose();
    }

    private static NotificationRecipients Recipients => app.Services.GetRequiredService<NotificationRecipients>();

    private static async Task<Resolved> ResolveAsync(NotificationRuleEntity rule, DateTime? utcNow = null)
    {
        await using var db = app.NewDb();
        return await Recipients.ResolveAsync(db, rule, utcNow ?? Clock.UtcNow, CancellationToken.None);
    }

    private static NotificationRuleEntity Rule(NotificationRecipientKind kind, Guid? userId = null, Guid? groupId = null, bool byEmail = true,
        bool byPushover = true, NotificationHours hours = NotificationHours.Always, string? recipient = null) => new()
    {
        Id = Guid.NewGuid(), Name = "test", RecipientKind = kind, UserId = userId, GroupId = groupId, ByEmail = byEmail, ByPushover = byPushover,
        Hours = hours, Recipient = recipient, Enabled = true, Type = NotificationRuleType.ALWAYS,
    };

    /// <summary>A Monday three weeks ahead: nobody else's test touches it, and the plan is kept filled far beyond it.</summary>
    private static DateOnly Monday
    {
        get
        {
            var day = DateOnly.FromDateTime(Clock.LocalNow).AddDays(21);
            while (day.DayOfWeek != DayOfWeek.Monday)
            {
                day = day.AddDays(1);
            }
            return day;
        }
    }

    private static DateTime At(DateOnly day, int hour) => Clock.ToUtc(day.ToDateTime(new TimeOnly(hour, 0)));

    private static async Task<NotificationRuleDetail> CreateAsync(object request)
    {
        using var response = await user.PostJsonAsync("/api/v1/notification-rules", request);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rule = await response.ReadAsync<NotificationRuleDetail>();
        created.Add(rule.Id);
        return rule;
    }

    [TestMethod]
    public async Task An_address_rule_is_one_delivery_to_the_provider_it_matches()
    {
        var resolved = await ResolveAsync(Rule(NotificationRecipientKind.Address, recipient: "pushover://" + AnnaKey));
        Assert.AreEqual(1, resolved.Deliveries.Count);
        Assert.AreEqual(("pushover://" + AnnaKey, "", "pushover"), (resolved.Deliveries[0].Recipient, resolved.Deliveries[0].Person, resolved.Deliveries[0].Channel));
        Assert.AreEqual("pushover", resolved.Who, "LastSentTo never holds the address");
        Assert.IsNull(resolved.Because);
    }

    [TestMethod]
    public async Task A_user_is_reached_by_the_channels_the_rule_allows_and_an_inactive_or_unreachable_one_by_none()
    {
        var both = await ResolveAsync(Rule(NotificationRecipientKind.User, anna.Id));
        CollectionAssert.AreEqual(new[] { "Pushover", "e-mail" }, both.Deliveries.Select(d => d.Channel).ToArray());
        Assert.AreEqual("Anna Muster: Pushover, e-mail", both.Who);
        Assert.AreEqual("to you", both.Because);

        var mailOnly = await ResolveAsync(Rule(NotificationRecipientKind.User, anna.Id, byPushover: false));
        Assert.AreEqual(1, mailOnly.Deliveries.Count);
        Assert.AreEqual("anna@example.com", mailOnly.Deliveries[0].Recipient);

        var inactive = await ResolveAsync(Rule(NotificationRecipientKind.User, carl.Id));
        Assert.AreEqual(0, inactive.Deliveries.Count);
        Assert.AreEqual("Carl Inactive is deactivated.", inactive.Why);

        var nowhere = await ResolveAsync(Rule(NotificationRecipientKind.User, dora.Id));
        Assert.AreEqual("Dora Nowhere has no e-mail address or Pushover key.", nowhere.Why);

        var removed = await ResolveAsync(Rule(NotificationRecipientKind.User, null));
        Assert.AreEqual("The user was removed.", removed.Why);
    }

    [TestMethod]
    public async Task A_group_reaches_every_active_member_with_contact_details()
    {
        var resolved = await ResolveAsync(Rule(NotificationRecipientKind.Group, groupId: storage.Id));
        Assert.AreEqual(3, resolved.Deliveries.Count, "Anna twice, Ben once; Carl is inactive, Dora has nothing");
        Assert.AreEqual(2, resolved.People);
        Assert.IsFalse(resolved.Deliveries.Any(d => d.Recipient.Contains(CarlKey)), "an inactive member is skipped");
        Assert.AreEqual("Anna Muster: Pushover, e-mail; Ben Ott: e-mail; Dora Nowhere (no e-mail address or Pushover key)", resolved.Who);
        Assert.AreEqual($"to you as a member of {storage.Name}", resolved.Because);

        var pushoverOnly = await ResolveAsync(Rule(NotificationRecipientKind.Group, groupId: storage.Id, byEmail: false));
        Assert.AreEqual(1, pushoverOnly.Deliveries.Count, "only Anna has a key");

        Assert.AreEqual("The group was removed.", (await ResolveAsync(Rule(NotificationRecipientKind.Group, groupId: Guid.NewGuid()))).Why);
    }

    [TestMethod]
    public async Task The_responsible_follows_the_plan_and_the_stand_in_takes_the_closed_days_and_nights()
    {
        var monday = Monday;
        var saturday = monday.AddDays(5);
        using (var rows = await admin.PutAsJsonAsync("/api/v1/availability/days",
            new AvailabilityDaysRequest([new AvailabilityDayChange(monday, anna.Id, ben.Id, null)])))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, rows.StatusCode, await rows.Content.ReadAsStringAsync());
        }

        // At any hour: the day's responsible, at 10:00 and at 03:00 alike.
        var rule = Rule(NotificationRecipientKind.Responsible, groupId: storage.Id);
        var day = await ResolveAsync(rule, At(monday, 10));
        Assert.AreEqual("Anna Muster (responsible): Pushover, e-mail", day.Who);
        Assert.AreEqual("to you as the responsible today", day.Because);
        Assert.AreEqual("Anna Muster (responsible): Pushover, e-mail", (await ResolveAsync(rule, At(monday, 3))).Who);

        // In working hours only: the night is the stand-in's.
        var office = Rule(NotificationRecipientKind.Responsible, userId: ben.Id, hours: NotificationHours.WorkingHours);
        Assert.AreEqual("Anna Muster (responsible): Pushover, e-mail", (await ResolveAsync(office, At(monday, 10))).Who);
        var night = await ResolveAsync(office, At(monday, 3));
        Assert.AreEqual("Ben Ott (stand-in): e-mail", night.Who);
        Assert.AreEqual("to you as the stand-in for the responsible", night.Because);

        // Anna on vacation: the fallback.
        var types = await (await admin.GetAsync("/api/v1/availability/settings")).ReadAsync<AvailabilitySettingsItem>();
        var vacation = types.Types.Single(t => t.Code == "VACA").Id;
        using (var cells = await admin.PutAsJsonAsync("/api/v1/availability/entries",
            new AvailabilityCellsRequest([new AvailabilityCellChange(anna.Id, monday, vacation)])))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, cells.StatusCode, await cells.Content.ReadAsStringAsync());
        }
        var fallback = await ResolveAsync(rule, At(monday, 10));
        Assert.AreEqual("Ben Ott (fallback): e-mail", fallback.Who);
        Assert.AreEqual("to you as the fallback today", fallback.Because);

        // A closed day: the stand-in group, whether or not its members work.
        var closed = await ResolveAsync(rule, At(saturday, 10));
        Assert.AreEqual("Anna Muster (stand-in): Pushover, e-mail; Ben Ott (stand-in): e-mail; Dora Nowhere (no e-mail address or Pushover key)", closed.Who);
        Assert.AreEqual("to you as the stand-in for the responsible", closed.Because);

        // Nobody, and why.
        var alone = await ResolveAsync(Rule(NotificationRecipientKind.Responsible), At(saturday, 10));
        Assert.AreEqual(0, alone.Deliveries.Count);
        Assert.AreEqual("A closed day: nobody is responsible, and the rule has no stand-in.", alone.Why);
        var afterHours = await ResolveAsync(Rule(NotificationRecipientKind.Responsible, hours: NotificationHours.WorkingHours), At(monday, 22));
        Assert.AreEqual("Outside working hours, and the rule has no stand-in.", afterHours.Why);
        var unreachable = await ResolveAsync(Rule(NotificationRecipientKind.Responsible, userId: dora.Id), At(saturday, 10));
        Assert.AreEqual("A closed day: nobody is responsible, and the stand-in Dora Nowhere has no e-mail address or Pushover key.", unreachable.Why);
    }

    [TestMethod]
    public async Task The_api_refuses_what_cannot_be_reached_and_keeps_the_old_shape()
    {
        using (var nowhere = await user.PostJsonAsync("/api/v1/notification-rules",
            new { name = V1.Unique("rule"), recipientKind = "User", userId = dora.Id }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, nowhere.StatusCode);
            StringAssert.Contains((await V1.ErrorsOf(nowhere)).GetProperty("userId")[0].GetString(), "no e-mail address or Pushover key");
        }
        using (var inactive = await user.PostJsonAsync("/api/v1/notification-rules",
            new { name = V1.Unique("rule"), recipientKind = "User", userId = carl.Id }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, inactive.StatusCode);
            StringAssert.Contains((await V1.ErrorsOf(inactive)).GetProperty("userId")[0].GetString(), "deactivated");
        }
        using (var both = await user.PostJsonAsync("/api/v1/notification-rules",
            new { name = V1.Unique("rule"), recipientKind = "Responsible", userId = ben.Id, groupId = storage.Id }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, both.StatusCode);
            Assert.IsTrue((await V1.ErrorsOf(both)).TryGetProperty("groupId", out _));
        }
        using (var silent = await user.PostJsonAsync("/api/v1/notification-rules",
            new { name = V1.Unique("rule"), recipientKind = "Group", groupId = storage.Id, byEmail = false, byPushover = false }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, silent.StatusCode);
            Assert.IsTrue((await V1.ErrorsOf(silent)).TryGetProperty("byEmail", out _));
        }
        using (var noGroup = await user.PostJsonAsync("/api/v1/notification-rules", new { name = V1.Unique("rule"), recipientKind = "Group" }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, noGroup.StatusCode);
            Assert.IsTrue((await V1.ErrorsOf(noGroup)).TryGetProperty("groupId", out _));
        }

        // The request of 1.11 still creates an address rule, indistinguishable from before.
        var address = await CreateAsync(new { name = V1.Unique("rule"), recipient = "ops@example.com", type = "ONLY_FAILURES" });
        Assert.AreEqual((NotificationRecipientKind.Address, "email", "ops@example.com", true, true), (address.RecipientKind, address.Provider, address.Recipient,
            address.ByEmail, address.ByPushover));
        Assert.AreEqual((1, 1, "email"), (address.Reach!.People, address.Reach.Deliveries, address.Reach.Who));
    }

    [TestMethod]
    public async Task A_group_rule_shows_its_label_its_reach_and_is_found_by_the_groups_name()
    {
        var rule = await CreateAsync(new
        {
            name = V1.Unique("rule"), recipientKind = "Group", groupId = storage.Id, byPushover = false, type = "ONLY_FAILURES_ON_CHANGE",
        });
        Assert.AreEqual(("group", storage.Name, NotificationRecipientKind.Group, true, false), (rule.Provider, rule.Recipient, rule.RecipientKind, rule.ByEmail,
            rule.ByPushover));
        Assert.AreEqual(storage.Id, rule.Group!.Id);
        Assert.IsNull(rule.User);
        Assert.AreEqual((2, 2), (rule.Reach!.People, rule.Reach.Deliveries), "Anna and Ben by e-mail");
        Assert.IsNull(rule.Reach.Why);
        Assert.IsNull(rule.LastSentAt);

        var seen = await (await viewer.GetAsync($"/api/v1/notification-rules/{rule.Id}")).ReadAsync<NotificationRuleDetail>();
        Assert.AreEqual(storage.Name, seen.Recipient, "a name is no secret, as an address never was");
        var found = await (await viewer.GetAsync($"/api/v1/notification-rules?search={storage.Name}")).ReadAsync<Paged<NotificationRuleItem>>();
        Assert.IsTrue(found.Items.Any(r => r.Id == rule.Id && r.Provider == "group" && r.RecipientKind == NotificationRecipientKind.Group));

        // Changed to the responsible with the group as the stand-in: the group stays, the kind changes, the audit names it.
        var changed = await (await user.PutJsonAsync($"/api/v1/notification-rules/{rule.Id}", new
        {
            name = rule.Name, recipientKind = "Responsible", groupId = storage.Id, hours = "WorkingHours", type = rule.Type.ToString(), enabled = true,
        })).ReadAsync<NotificationRuleDetail>();
        Assert.AreEqual(("responsible", NotificationRecipients.ResponsibleLabel, NotificationHours.WorkingHours), (changed.Provider, changed.Recipient, changed.Hours));
        Assert.AreEqual(storage.Id, changed.Group!.Id);
        await using var db = app.NewDb();
        var audit = await db.AuditLog.AsNoTracking().Where(l => l.EventType == "NotificationRuleUpdated").OrderByDescending(l => l.CreatedAt).FirstAsync();
        StringAssert.Contains(audit.Detail, $"stand-in {storage.Name}");
        StringAssert.Contains(audit.Detail, "in working hours only");
    }

    [TestMethod]
    public async Task The_pickers_name_people_and_groups_without_their_details_for_the_editor_only()
    {
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/notification-rules/people")).StatusCode);
        var people = await (await user.GetAsync("/api/v1/notification-rules/people")).ReadAsync<NotificationPeople>();
        var a = people.Users.Single(u => u.Id == anna.Id);
        Assert.AreEqual((true, true, true), (a.IsActive, a.HasEmail, a.HasPushover));
        var d = people.Users.Single(u => u.Id == dora.Id);
        Assert.AreEqual((false, false), (d.HasEmail, d.HasPushover));
        Assert.IsFalse(people.Users.Single(u => u.Id == carl.Id).IsActive);
        Assert.IsTrue(people.Users.TakeWhile(u => u.IsActive).Any(u => u.Id == anna.Id), "active users first");
        Assert.AreEqual(4, people.Groups.Single(g => g.Id == storage.Id).Members);
    }

    [TestMethod]
    public async Task The_task_sends_one_message_per_delivery_records_whom_and_a_rule_to_nobody_as_no_action()
    {
        var host = await V1.CreateHostAsync(user);
        var told = await V1.CreateSensorAsync(user, host.Id);
        var untold = await V1.CreateSensorAsync(user, host.Id);
        var team = await CreateAsync(new { name = V1.Unique("team"), recipientKind = "Group", groupId = storage.Id });
        Guid erin;
        await using (var db = app.NewDb())
        {
            var e = new UserEntity
            {
                Id = Guid.NewGuid(), Username = V1.Unique("erin"), NormalizedUsername = V1.Unique("erin").ToLowerInvariant(), DisplayName = "Erin Gone",
                PasswordHash = "x", SecurityStamp = Guid.NewGuid().ToString(), Email = "erin@example.com",
            };
            db.Users.Add(e);
            await db.SaveChangesAsync();
            erin = e.Id;
        }
        var gone = await CreateAsync(new { name = V1.Unique("gone"), recipientKind = "User", userId = erin });
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/users/{erin}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await user.PutAsync($"/api/v1/notification-rules/{team.Id}/sensors/{told.Id}", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await user.PutAsync($"/api/v1/notification-rules/{gone.Id}/sensors/{untold.Id}", null)).StatusCode);
        await using (var db = app.NewDb())
        {
            foreach (var id in new[] { told.Id, untold.Id })
            {
                db.Events.Add(new EventEntity { Id = Guid.NewGuid(), SensorId = id, Code = 500, Message = "down", Created = Clock.UtcNow, Source = "test" });
            }
            await db.SaveChangesAsync();
        }

        var before = pushover.Sent.Count(m => m.GetProperty("user").GetString() == AnnaKey);
        ActivatorUtilities.CreateInstance<NotificationTask>(app.Services).RunOnce();

        Assert.AreEqual(before + 1, pushover.Sent.Count(m => m.GetProperty("user").GetString() == AnnaKey), "Anna's Pushover, once for the event");
        var annas = pushover.Sent.Last(m => m.GetProperty("user").GetString() == AnnaKey);
        Assert.AreEqual($"Alert ({told.Name})", annas.GetProperty("title").GetString());
        await using var check = app.NewDb();
        var sent = await check.NotificationRules.AsNoTracking().SingleAsync(r => r.Id == team.Id);
        Assert.IsNotNull(sent.LastSentAt);
        Assert.AreEqual("Anna Muster: Pushover, e-mail; Ben Ott: e-mail; Dora Nowhere (no e-mail address or Pushover key)", sent.LastSentTo);
        Assert.IsFalse(await check.NotificationRuleEvents.AnyAsync(n => n.NotificationRule.Id == team.Id && n.Event.SensorId == told.Id && n.NoAction));

        var nobody = await check.NotificationRules.AsNoTracking().SingleAsync(r => r.Id == gone.Id);
        Assert.IsNull(nobody.UserId, "ON DELETE SET NULL");
        Assert.IsNull(nobody.LastSentAt);
        Assert.IsTrue(await check.NotificationRuleEvents.AnyAsync(n => n.NotificationRule.Id == gone.Id && n.Event.SensorId == untold.Id && n.NoAction),
            "fired, nobody to tell: recorded as no action");

        // The rule shows "removed", the coverage lists it, and its only sensor counts as uncovered.
        var detail = await (await viewer.GetAsync($"/api/v1/notification-rules/{gone.Id}")).ReadAsync<NotificationRuleDetail>();
        Assert.AreEqual(("Removed user", null, "The user was removed."), (detail.Recipient, detail.User, detail.Reach!.Why));
        var coverage = await (await viewer.GetAsync("/api/v1/notifications/coverage")).ReadAsync<NotificationCoverage>();
        Assert.IsTrue(coverage.RulesReachingNobody.Any(r => r.Rule.Id == gone.Id && r.Why == "The user was removed."));
        Assert.IsFalse(coverage.RulesReachingNobody.Any(r => r.Rule.Id == team.Id));
        Assert.IsFalse(coverage.Sensors.Any(s => s.SensorId == told.Id));
        var matrix = await (await viewer.GetAsync($"/api/v1/notifications/matrix?hostId={host.Id}&uncovered=true")).ReadAsync<NotificationMatrix>();
        CollectionAssert.AreEqual(new[] { untold.Id }, matrix.Rows.Select(r => r.SensorId).ToArray(), "covered only by a rule that reaches nobody");
    }
}
