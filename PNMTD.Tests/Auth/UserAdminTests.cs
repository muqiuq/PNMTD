using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;

namespace PNMTD.Tests.Auth;

/// <summary>User administration and the audit log (§4.9, §4.10, §8.5).</summary>
[TestClass]
public sealed class UserAdminTests
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

    private static string NewName() => $"n{Guid.NewGuid():N}"[..18];

    private static async Task<CreatedUser> CreateAsync(string role, string? name = null) =>
        await (await admin.PostJsonAsync("/api/v1/users", new { username = name ?? NewName(), displayName = "Some One", role })).ReadAsync<CreatedUser>();

    [TestMethod]
    public async Task CreateShowsTheTemporaryPasswordOnce()
    {
        var name = NewName();
        using var response = await admin.PostJsonAsync("/api/v1/users", new { username = name, role = "viewer" });

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadAsync<CreatedUser>();
        Assert.AreEqual((name, Roles.Viewer, true, "Some One"), (created.User.Username, created.User.Role, created.User.MustChangePassword, "Some One"));
        Assert.AreEqual(name, created.User.DisplayName, "defaults to the username");
        var item = await (await admin.GetAsync($"/api/v1/users/{created.User.Id}")).Content.ReadAsStringAsync();
        Assert.IsFalse(item.Contains(created.TemporaryPassword.Password, StringComparison.Ordinal));
        Assert.IsFalse(item.Contains("pbkdf2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UsernamesAreUniqueCaseInsensitivelyAndChecked()
    {
        var name = NewName();
        await CreateAsync(Roles.User, name);

        using var duplicate = await admin.PostJsonAsync("/api/v1/users", new { username = name.ToUpperInvariant(), role = "user" });
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.AreEqual("duplicate_key", (await duplicate.ProblemAsync()).GetProperty("code").GetString());

        using var invalid = await admin.PostJsonAsync("/api/v1/users", new { username = "with space", role = "owner" });
        var errors = (await invalid.ProblemAsync()).GetProperty("errors");
        Assert.IsTrue(errors.TryGetProperty("username", out _));
        Assert.IsTrue(errors.TryGetProperty("role", out _));
    }

    /// <summary>Nobody could manage users any more: the last active admin stays one.</summary>
    [TestMethod]
    public async Task TheLastActiveAdminCannotBeDeletedDemotedOrDeactivated()
    {
        var last = await CreateAsync(Roles.Admin);
        await using var db = app.NewDb();
        // Every other admin inactive for the test, the seeded test-admin included.
        var others = await db.Users.Where(u => u.Role == Roles.Admin && u.IsActive && u.Id != last.User.Id).Select(u => u.Id).ToListAsync();
        await db.Users.Where(u => others.Contains(u.Id)).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        try
        {
            foreach (var request in new Func<Task<HttpResponseMessage>>[]
                     {
                         () => admin.DeleteAsync($"/api/v1/users/{last.User.Id}"),
                         () => admin.PutJsonAsync($"/api/v1/users/{last.User.Id}", new { displayName = "x", role = "User", isActive = true }),
                         () => admin.PutJsonAsync($"/api/v1/users/{last.User.Id}", new { displayName = "x", role = "Admin", isActive = false }),
                     })
            {
                using var response = await request();
                Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
                Assert.AreEqual("last_admin", (await response.ProblemAsync()).GetProperty("code").GetString());
            }
            // Renaming is fine.
            Assert.AreEqual(HttpStatusCode.OK,
                (await admin.PutJsonAsync($"/api/v1/users/{last.User.Id}", new { displayName = "Renamed", role = "Admin", isActive = true })).StatusCode);
        }
        finally
        {
            await db.Users.Where(u => others.Contains(u.Id)).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, true));
        }
        // With another admin active, it may go.
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/users/{last.User.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/users/{last.User.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task ListSearchesAndSorts()
    {
        var tag = $"s{Guid.NewGuid():N}"[..10];
        await CreateAsync(Roles.Viewer, $"{tag}-b");
        await CreateAsync(Roles.Admin, $"{tag}-a");

        var byName = await (await admin.GetAsync($"/api/v1/users?search={tag}")).ReadAsync<Paged<UserItem>>();
        var byRole = await (await admin.GetAsync($"/api/v1/users?search={tag}&sort=-role")).ReadAsync<Paged<UserItem>>();

        CollectionAssert.AreEqual(new[] { $"{tag}-a", $"{tag}-b" }, byName.Items.Select(u => u.Username).ToArray());
        CollectionAssert.AreEqual(new[] { Roles.Viewer, Roles.Admin }, byRole.Items.Select(u => u.Role).ToArray());
    }

    [TestMethod]
    public async Task TheAuditLogFiltersByUserAndType()
    {
        var created = await CreateAsync(Roles.User);
        await admin.PostAsync($"/api/v1/users/{created.User.Id}/reset-password", null);

        var forUser = await (await admin.GetAsync($"/api/v1/audit-log?userId={created.User.Id}")).ReadAsync<Paged<AuditLogItem>>();
        var resets = await (await admin.GetAsync($"/api/v1/audit-log?userId={created.User.Id}&type=PasswordReset")).ReadAsync<Paged<AuditLogItem>>();

        CollectionAssert.AreEqual(new[] { "PasswordReset", "UserCreated" }, forUser.Items.Select(l => l.EventType).ToArray(), "newest first");
        var reset = resets.Items.Single();
        Assert.AreEqual("temporary password", reset.Detail);
        // Who did it is the admin; the account is the target (docs/audit-redmine-zabbix-plan.md §3.2).
        Assert.AreNotEqual(created.User.Id, reset.UserId);
        Assert.AreEqual(AuditTargets.User, reset.TargetType);
        Assert.AreEqual(created.User.Username, reset.TargetName);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/audit-log?type=Nonsense")).StatusCode);
    }
}
