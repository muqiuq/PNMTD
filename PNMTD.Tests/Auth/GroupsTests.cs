using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Auth;

/// <summary>
/// Groups and rights (docs/audit-redmine-zabbix-plan.md §5): levels add up across groups, a level includes the ones below,
/// the catalog refuses what an area doesn't have, every change is in the audit log, and the update turns the roles into
/// the admin flag and two groups.
/// </summary>
[TestClass]
public sealed class GroupsTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<GroupDetail> CreateAsync(string name, object rights) =>
        await (await admin.PostJsonAsync("/api/v1/groups", new { name, rights })).ReadAsync<GroupDetail>();

    [TestMethod]
    public void LevelsAddUpAndIncludeTheOnesBelow()
    {
        var combined = UserRights.Combine([(Area.Monitoring, Level.View), (Area.Monitoring, Level.Edit), (Area.KnowledgeBase, Level.Manage)]);
        var rights = new UserRights(false, combined);
        Assert.IsTrue(rights.Has(Area.Monitoring, Level.View), "Edit includes View");
        Assert.IsTrue(rights.Has(Area.KnowledgeBase, Level.Edit));
        Assert.IsFalse(rights.Has(Area.Locations, Level.View));
        Assert.IsFalse(rights.Has(Area.Mail, Level.Read));
        Assert.IsTrue(new UserRights(true, new Dictionary<Area, Level>()).Has(Area.AuditLog, Level.View), "the admin flag has every right");
    }

    [TestMethod]
    public async Task AGroupIsCreatedChangedAndDeletedWithItsAudit()
    {
        var name = $"Helpdesk {Guid.NewGuid():N}"[..20];
        var group = await CreateAsync(name, new { Monitoring = "View", Mail = "Read" });
        Assert.AreEqual(Level.Read, group.Rights[Area.Mail]);

        var updated = await (await admin.PutJsonAsync($"/api/v1/groups/{group.Id}",
            new { name, rights = new { Monitoring = "Edit", Mail = "None", Commands = "Run" } })).ReadAsync<GroupDetail>();
        Assert.AreEqual(Level.Edit, updated.Rights[Area.Monitoring]);
        Assert.IsFalse(updated.Rights.ContainsKey(Area.Mail), "None gives nothing");

        var members = await (await admin.PutJsonAsync($"/api/v1/groups/{group.Id}/members", new { userIds = new[] { TestUsers.Viewer } }))
            .ReadAsync<GroupDetail>();
        Assert.AreEqual(TestUsers.NameOf(Roles.Viewer), members.Members.Single().Name);
        var access = await (await admin.GetAsync($"/api/v1/users/{TestUsers.Viewer}/access")).ReadAsync<AccessItem>();
        var monitoring = access.Rights.Single(r => r.Area == Area.Monitoring);
        Assert.AreEqual(Level.Edit, monitoring.Level, "the highest of Viewers' View and this group's Edit");
        CollectionAssert.AreEqual(new[] { name }, monitoring.From.ToArray());

        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/groups/{group.Id}")).StatusCode);
        await using var db = app.NewDb();
        var id = group.Id.ToString();
        var events = await db.AuditLog.AsNoTracking().Where(l => l.TargetId == id).OrderBy(l => l.CreatedAt).Select(l => l.EventType + ": " + l.Detail)
            .ToListAsync();
        CollectionAssert.AreEqual(new[]
        {
            $"GroupCreated: {name}: Monitoring View, Mail Read",
            $"GroupRightsChanged: {name}: Monitoring: View → Edit, Commands: none → Run, Mail: Read → none",
            $"GroupMembersChanged: {name}: added {TestUsers.NameOf(Roles.Viewer)}",
            $"GroupDeleted: {name} deleted; members then: {TestUsers.NameOf(Roles.Viewer)}",
        }, events);
    }

    [TestMethod]
    public async Task ALevelAnAreaDoesNotHaveIsRefused()
    {
        using var response = await admin.PostJsonAsync("/api/v1/groups", new { name = "Odd", rights = new { Mail = "Edit" } });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains((await response.ProblemAsync()).GetProperty("errors").GetProperty("rights")[0].GetString(), "Mail has no level Edit");
        using var duplicate = await admin.PostJsonAsync("/api/v1/groups", new { name = "operators" });
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode, "names are unique, ignoring case");
    }

    [TestMethod]
    public async Task TheCatalogListsEveryAreaWithItsLevels()
    {
        var catalog = await (await app.Client(Roles.Viewer).GetAsync("/api/v1/rights")).ReadAsync<List<RightsAreaItem>>();
        Assert.AreEqual(17, catalog.Count, "16, and Cookie since docs/cookie-plan.md §10.1");
        CollectionAssert.AreEqual(new[] { Level.View, Level.Edit, Level.Manage },
            catalog.Single(a => a.Area == Area.KnowledgeBase).Levels.Select(l => l.Level).ToArray());
        CollectionAssert.AreEqual(new[] { Level.Use, Level.Instruct, Level.Manage },
            catalog.Single(a => a.Area == Area.Cookie).Levels.Select(l => l.Level).ToArray());
        Assert.AreEqual("MikroTik & WireGuard", catalog.Single(a => a.Area == Area.Mikrotik).Label);
    }

    [TestMethod]
    public async Task TheLastAdminKeepsTheFlag()
    {
        await using var db = app.NewDb();
        var others = await db.Users.Where(u => u.IsAdmin && u.IsActive && u.Id != TestUsers.Admin).Select(u => u.Id).ToListAsync();
        await db.Users.Where(u => others.Contains(u.Id)).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        try
        {
            using var response = await admin.PutJsonAsync($"/api/v1/users/{TestUsers.Admin}", new { displayName = "Test Admin", isAdmin = false, isActive = true });
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            Assert.AreEqual("last_admin", (await response.ProblemAsync()).GetProperty("code").GetString());
        }
        finally
        {
            await db.Users.Where(u => others.Contains(u.Id)).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, true));
        }
    }

    /// <summary>The update (§5.3): Admin the flag, User a member of Operators, Viewer of Viewers; a decision for admins AdminOnly.</summary>
    [TestMethod]
    public async Task TheUpdateTurnsTheRolesIntoTheFlagAndTwoGroups()
    {
        await using var db = TestDatabases.Empty("pnmtd_test_groups_upgrade");
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261114124440_AuditLog");
        var (a, u, v) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var decision = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO users ("Id", "Username", "NormalizedUsername", "DisplayName", "PasswordHash", "Role", "IsActive", "MustChangePassword",
                "SecurityStamp", "IsTotpEnabled", "FailedLoginCount", "LockoutCount", "CreatedAt") VALUES
                ({a}, 'ada', 'ada', 'Ada', 'x', 'Admin', TRUE, FALSE, 's', FALSE, 0, 0, now()),
                ({u}, 'uli', 'uli', 'Uli', 'x', 'User', TRUE, FALSE, 's', FALSE, 0, 0, now()),
                ({v}, 'vera', 'vera', 'Vera', 'x', 'Viewer', TRUE, FALSE, 's', FALSE, 0, 0, now());
            """);
        await db.Database.ExecuteSqlAsync($$"""
            INSERT INTO decisions ("Id", "Kind", "State", "RequiredRole", "Title", "Summary", "Payload", "PayloadVersion", "Occurrences", "Subjects",
                "Disputed", "Priority", "CreatedAt", "LastSeenAt")
            VALUES ({{decision}}, 'mail.rule', 0, 'Admin', 't', 's', '{}'::jsonb, 1, 1, '{}'::uuid[], FALSE, 0, now(), now())
            """);
        await migrator.MigrateAsync();

        var users = await db.Users.AsNoTracking().Include(x => x.Groups).ToDictionaryAsync(x => x.Username);
        Assert.IsTrue(users["ada"].IsAdmin);
        Assert.AreEqual(0, users["ada"].Groups.Count);
        CollectionAssert.AreEqual(new[] { GroupEntity.Operators }, users["uli"].Groups.Select(m => m.GroupId).ToArray());
        CollectionAssert.AreEqual(new[] { GroupEntity.Viewers }, users["vera"].Groups.Select(m => m.GroupId).ToArray());
        var operators = await db.GroupRights.AsNoTracking().Where(r => r.GroupId == GroupEntity.Operators).ToDictionaryAsync(r => r.Area, r => r.Level);
        CollectionAssert.AreEquivalent(Roles.Operators.ToList(), operators.ToList(), "the tests' Operators are the update's");
        var viewers = await db.GroupRights.AsNoTracking().Where(r => r.GroupId == GroupEntity.Viewers).ToDictionaryAsync(r => r.Area, r => r.Level);
        CollectionAssert.AreEquivalent(Roles.Viewers.ToList(), viewers.ToList());
        Assert.IsTrue(await db.Decisions.AsNoTracking().Where(d => d.Id == decision).Select(d => d.AdminOnly).SingleAsync());
    }
}
