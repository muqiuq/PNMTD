using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Cookie;
using PNMTD.Dev;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// Which Redmine user a PNMT user is (docs/cookie-redmine-tab-plan.md §4): the check's states, a link picked by id, every exact
/// match at once, the profile's name clearing the link, and the assignee found by id first.
/// </summary>
[TestClass]
public sealed class RedmineLinksTests
{
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient manager = null!;
    private static readonly string Tag = Guid.NewGuid().ToString("N")[..6];
    private static Guid linda, max, sam, nomi, unset, gone;

    private static string N(string name) => $"{name} {Tag}";

    /// <summary>Linda's name as an admin typed it: other case, spaces around.</summary>
    private static readonly string LindaName = $"  {N("Linda Link").ToUpperInvariant()} ";

    private static readonly int GoneId = 8000 + Random.Shared.Next(1, 100_000);

    private static JsonObject Member(int id, string name, int role) =>
        new() { ["user"] = new JsonObject { ["id"] = id, ["name"] = name }, ["roles"] = new JsonArray(new JsonObject { ["id"] = role, ["name"] = "Role" }) };

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        // IT Support's members: three to link, two with one name, a staff member nobody is, and a group.
        redmine.Members[1] =
        [
            Member(7001, N("Linda Link"), 3), Member(7002, N("Max Match"), 5), Member(7003, N("Sam Twice"), 5), Member(7004, N("Sam Twice"), 5),
            Member(7005, N("Dora Desk"), 4),
            new JsonObject { ["group"] = new JsonObject { ["id"] = 7100, ["name"] = "Desk group" }, ["roles"] = new JsonArray() },
        ];
        redmine.Members[2] = [];
        redmine.Members[3] = [];
        app = new TestApp(settings: FakeRedmine.Settings, services: redmine.Install);
        manager = app.ClientWithRights("Redmine:Manage");
        await using var db = app.NewDb();
        UserEntity User(string name, string? redmineName, int? redmineId = null) => new()
        {
            Id = Guid.NewGuid(), Username = V1.Unique(name), NormalizedUsername = V1.Unique(name).ToLowerInvariant(), DisplayName = N(name),
            PasswordHash = "x", SecurityStamp = Guid.NewGuid().ToString(), RedmineName = redmineName, RedmineUserId = redmineId,
        };
        var users = new[]
        {
            User("Linda", LindaName), User("Max", N("Max Match")), User("Sam", N("Sam Twice")), User("Nomi", N("Nobody")), User("Unset", null),
            User("Gone", "Gone Person", GoneId), User("Twin", N("Nobody")),
        };
        db.Users.AddRange(users);
        // An open watched ticket assigned to someone who is no member and no PNMT user.
        db.CookieCases.Add(new CookieCaseEntity
        {
            Id = Guid.NewGuid(), Kind = CookieCaseKind.Ticket, Key = $"ticket:{Tag}", Title = "A ticket", ProjectId = 1, AssigneeId = 7006,
            AssigneeName = N("Otto Open"), AuthorId = 7007, AuthorName = N("Rita Requester"), OpenedAt = DateTime.UtcNow, ChangedAt = DateTime.UtcNow,
            ProgressAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        (linda, max, sam, nomi, unset, gone) = (users[0].Id, users[1].Id, users[2].Id, users[3].Id, users[4].Id, users[5].Id);
        using var save = await manager.PutAsJsonAsync("/api/v1/cookie/admin/redmine/settings",
            new CookieRedmineSettings { Projects = [WatchedProject.Default(1) with { StaffRoleIds = [4] }] }, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.OK, save.StatusCode, await save.Content.ReadAsStringAsync());
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            await db.CookieCases.Where(c => c.Key == $"ticket:{Tag}").ExecuteDeleteAsync();
            await db.Users.Where(u => u.DisplayName.EndsWith(Tag)).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false).SetProperty(u => u.RedmineUserId, (int?)null));
        }
        using var reset = await manager.PutAsJsonAsync("/api/v1/cookie/admin/redmine/settings", new CookieRedmineSettings(), CookieSettings.Json);
        app.Dispose();
    }

    /// <summary>Each test starts from the users as seeded: nobody linked but Gone.</summary>
    [TestInitialize]
    public async Task Reset()
    {
        await using var db = app.NewDb();
        foreach (var (id, name, redmineId) in new[] { (linda, LindaName, (int?)null), (max, N("Max Match"), null), (gone, "Gone Person", GoneId) })
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.RedmineName, name).SetProperty(u => u.RedmineUserId, redmineId));
        }
    }

    private static async Task<RedmineLinksView> ViewAsync() => await (await manager.GetAsync("/api/v1/cookie/admin/redmine/links")).ReadAsync<RedmineLinksView>();

    private static RedmineLinkRow Row(RedmineLinksView view, Guid id) => view.Rows.Single(r => r.UserId == id);

    [TestMethod]
    public async Task The_check_names_each_users_state_and_whom_no_user_is()
    {
        var view = await ViewAsync();
        Assert.IsTrue(view.Configured);
        Assert.IsNotNull(view.ReadAt, view.Error);
        Assert.AreEqual(1, view.Groups, "the group is a member, but no one to link");
        Assert.AreEqual(RedmineLinkState.Matches, Row(view, linda).State, "the name, trimmed and ignoring case");
        Assert.AreEqual(7001, Row(view, linda).Candidates.Single().Id);
        Assert.AreEqual(RedmineLinkState.Matches, Row(view, max).State);
        Assert.AreEqual(RedmineLinkState.Several, Row(view, sam).State);
        CollectionAssert.AreEquivalent(new[] { 7003, 7004 }, Row(view, sam).Candidates.Select(c => c.Id).ToArray());
        Assert.AreEqual(RedmineLinkState.NoMatch, Row(view, nomi).State);
        Assert.IsTrue(Row(view, nomi).Shared, "Twin has the same name");
        await using (var db = app.NewDb())
        {
            Assert.IsNull(await Recipients.AssigneeAsync(db, null, N("Nobody"), CancellationToken.None), "two people by one name: neither by chance");
            Assert.AreEqual(sam, (await Recipients.AssigneeAsync(db, 7003, N("Sam Twice"), CancellationToken.None))?.Id, "by name while nobody is linked");
        }
        Assert.AreEqual(RedmineLinkState.NotSet, Row(view, unset).State);
        Assert.AreEqual(RedmineLinkState.LinkedUnknown, Row(view, gone).State, "linked to an id no member or ticket names");

        var otto = view.Unlinked.Single(u => u.Id == 7006);
        Assert.AreEqual(1, otto.OpenTickets);
        Assert.IsTrue(view.Unlinked.Single(u => u.Id == 7005).Staff, "Dora holds the watched project's staff role");
        Assert.IsFalse(view.Unlinked.Any(u => u.Id is 7001 or 7002 or 7007), "matched by a PNMT user's name, or neither staff nor assigned");
        Assert.IsTrue(view.RedmineUsers.Any(u => u.Id == 7007 && !u.Member), "an author is someone to pick too");
        Assert.IsFalse(view.RedmineUsers.Any(u => u.Id == 7100), "a group can't be picked");
    }

    [TestMethod]
    public async Task A_link_is_picked_by_id_audited_and_a_Redmine_user_is_one_persons_only()
    {
        using var link = await manager.PutAsJsonAsync($"/api/v1/cookie/admin/redmine/links/{linda}", new RedmineUserLinkRequest(7001));
        Assert.AreEqual(HttpStatusCode.OK, link.StatusCode, await link.Content.ReadAsStringAsync());
        var row = await link.ReadAsync<RedmineLinkRow>();
        Assert.AreEqual(RedmineLinkState.Linked, row.State);
        Assert.AreEqual(N("Linda Link"), row.RedmineName, "the name as Redmine has it");

        using var taken = await manager.PutAsJsonAsync($"/api/v1/cookie/admin/redmine/links/{nomi}", new RedmineUserLinkRequest(7001));
        Assert.AreEqual(HttpStatusCode.Conflict, taken.StatusCode);
        using var unknown = await manager.PutAsJsonAsync($"/api/v1/cookie/admin/redmine/links/{nomi}", new RedmineUserLinkRequest(123_456));
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode, "never a typed id");
        await using (var db = app.NewDb())
        {
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == "RedmineUserLinkChanged" && l.Detail!.Contains("#7001")));
        }

        // The assignee by id first: a ticket's assignee name may differ, the id decides.
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(linda, (await Recipients.AssigneeAsync(db, 7001, "Someone Else", CancellationToken.None))?.Id);
        }

        using var unlink = await manager.PutAsJsonAsync($"/api/v1/cookie/admin/redmine/links/{linda}", new RedmineUserLinkRequest(null));
        Assert.AreEqual(RedmineLinkState.NotSet, (await unlink.ReadAsync<RedmineLinkRow>()).State, "unlinking takes the name too");
        using var again = await manager.PutAsJsonAsync($"/api/v1/cookie/admin/redmine/links/{linda}", new RedmineUserLinkRequest(7001));
        Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
    }

    [TestMethod]
    public async Task Every_exact_match_is_linked_at_once_and_the_profiles_name_clears_a_link()
    {
        using var exact = await manager.PostAsync("/api/v1/cookie/admin/redmine/links/exact", null);
        Assert.AreEqual(HttpStatusCode.OK, exact.StatusCode, await exact.Content.ReadAsStringAsync());
        Assert.IsGreaterThanOrEqualTo(1, (await exact.ReadAsync<RedmineLinkedCount>()).Linked);
        var view = await ViewAsync();
        Assert.AreEqual(RedmineLinkState.Linked, Row(view, max).State);
        Assert.AreEqual(7002, Row(view, max).RedmineUserId);
        Assert.AreEqual(RedmineLinkState.Several, Row(view, sam).State, "several: a person picks");

        // An admin types another name on the profile: no longer a link.
        var admin = app.Client(Roles.Admin);
        var profile = (await admin.GetFromJsonAsync<PersonProfile>($"/api/v1/users/{max}/profile", People.Json))!;
        Assert.AreEqual(7002, profile.RedmineUserId);
        using var save = await admin.PutAsJsonAsync($"/api/v1/users/{max}/profile", new UserProfileRequest(profile.Email, profile.PushoverKey, N("Max Typed"),
            profile.InPlan, profile.ShortCode, profile.Color, profile.DefaultWeek, profile.PlanOrder));
        Assert.AreEqual(HttpStatusCode.OK, save.StatusCode, await save.Content.ReadAsStringAsync());
        Assert.IsNull((await save.ReadAsync<PersonProfile>()).RedmineUserId);
    }

    [TestMethod]
    public void The_check_marks_two_people_with_one_name_and_keeps_groups_and_projects_without_staff_roles_apart()
    {
        var people = new RedminePeople(
            [new RedminePerson(1, "Ann", [new RedmineMembershipOf(10, [4])]), new RedminePerson(2, "Bo", [new RedmineMembershipOf(11, [4])])],
            [new RmRef(9, "Group")], []);
        var known = RedmineLinks.Known(people, [WatchedProject.Default(10) with { StaffRoleIds = [4] }, WatchedProject.Default(11)],
            [(1, "Ann", 5, "Requester", true), (1, "Ann", 5, "Requester", true), (9, "Group", null, null, true), (3, "Cy", null, null, false)]);
        Assert.IsTrue(known[1].Staff);
        Assert.IsFalse(known[2].Staff, "project 11 has no staff roles: everyone but the author counts, so nobody is flagged");
        Assert.AreEqual(2, known[1].OpenTickets);
        Assert.IsFalse(known.ContainsKey(9), "a group");
        Assert.AreEqual("Requester", known[5].Name);
        Assert.AreEqual(0, known[3].OpenTickets, "closed");

        var (rows, unlinked) = RedmineLinks.Check(
            [new RedmineLinkUser(Guid.NewGuid(), "P1", false, null, "cy"), new RedmineLinkUser(Guid.NewGuid(), "P2", true, null, " CY "),
             new RedmineLinkUser(Guid.NewGuid(), "P3", true, 2, "Bo (old name)")], known);
        Assert.IsTrue(rows.Where(r => r.RedmineName is "cy" or " CY ").All(r => r.Shared && r.State == RedmineLinkState.Matches));
        var bo = rows.Single(r => r.RedmineUserId == 2);
        Assert.AreEqual(RedmineLinkState.Linked, bo.State);
        Assert.AreEqual("Bo", bo.RedmineNow, "renamed in Redmine: the name now");
        CollectionAssert.AreEqual(new[] { 1 }, unlinked.Select(u => u.Id).ToArray(), "Ann: staff with tickets; Bo linked; Cy matched");
    }
}
