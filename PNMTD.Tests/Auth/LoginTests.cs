using PNMTD.Auth.Rights;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Security;

namespace PNMTD.Tests.Auth;

/// <summary>Sign-in, sessions, lockout, the rate limit and antiforgery with the real cookie scheme (§4.5, §4.7, §8.5).</summary>
[TestClass]
public sealed class LoginTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp(testSignIn: false);

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    /// <summary>A user with a password of its own (not temporary).</summary>
    private static async Task<(Guid Id, string Name, string Password)> NewUserAsync(string role = Roles.User)
    {
        var name = $"u{Guid.NewGuid():N}"[..20];
        var password = $"pw-{Guid.NewGuid():N}";
        await using var db = app.NewDb();
        var (user, _) = await new UserAccounts(db).CreateAsync(name, null, Roles.IsAdmin(role), Roles.GroupIds(role), password, AuditContext.Cli);
        return (user.Id, name, password);
    }

    private static async Task<List<string>> AuditAsync(Guid userId)
    {
        await using var db = app.NewDb();
        return await db.AuditLog.Where(l => l.UserId == userId || l.Subjects.Contains(userId)).OrderBy(l => l.CreatedAt).Select(l => l.EventType + ":" + l.Detail).ToListAsync();
    }

    [TestMethod]
    public async Task LoginSetsAStrictHttpOnlySessionCookieAndMeKnowsTheUser()
    {
        var (id, name, password) = await NewUserAsync();
        var browser = new Browser(app);

        var login = await browser.LoginAsync(name.ToUpperInvariant(), password);

        Assert.AreEqual("done", login.Next);
        Assert.AreEqual((id, Roles.User), (login.User!.Id, Roles.Of(login.User)));
        Assert.AreEqual(new SessionFlags(false, false, true), login.Flags);
        var session = browser.LastSetCookies.Single(c => c.StartsWith("pnmt.auth=", StringComparison.Ordinal)).ToLowerInvariant();
        StringAssert.Contains(session, "httponly");
        StringAssert.Contains(session, "samesite=strict");
        StringAssert.Contains(session, "path=/;");
        StringAssert.Contains(session, "expires=");
        var csrf = browser.LastSetCookies.Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.IsFalse(csrf.Contains("httponly"), "the SPA reads it");

        var me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.AreEqual(name, me.User.Username);
        var info = await (await browser.GetAsync("/api/v1/system/info")).ReadAsync<SystemInfo>();
        Assert.AreEqual(Roles.User, Roles.Of(me.User));
        CollectionAssert.Contains(await AuditAsync(id), "LoginSucceeded:");
    }

    [TestMethod]
    public async Task MeSignedOutIs401()
    {
        using var response = await new Browser(app).GetAsync("/api/v1/auth/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("unauthorized", (await response.ProblemAsync()).GetProperty("code").GetString());
    }

    /// <summary>Unknown, wrong and inactive look the same to the client; only the audit log tells them apart.</summary>
    [TestMethod]
    public async Task FailedLoginsAllLookTheSame()
    {
        var (id, name, password) = await NewUserAsync();
        var (inactiveId, inactiveName, inactivePassword) = await NewUserAsync();
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == inactiveId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        }

        var bodies = new List<string>();
        foreach (var (user, pw) in new[] { ("nobody-here", password), (name, "wrong-password"), (inactiveName, inactivePassword), ("", "") })
        {
            using var response = await new Browser(app).PostAsync("/api/v1/auth/login", new { username = user, password = pw });
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, user);
            Assert.IsFalse(response.Headers.Contains("Set-Cookie"), user);
            var problem = await response.ProblemAsync();
            bodies.Add($"{problem.GetProperty("code").GetString()} {problem.GetProperty("detail").GetString()}");
        }
        Assert.AreEqual(1, bodies.Distinct().Count(), string.Join(" | ", bodies));
        Assert.AreEqual("invalid_credentials Wrong username or password.", bodies[0]);
        CollectionAssert.Contains(await AuditAsync(id), "LoginFailed:wrong password");
        CollectionAssert.Contains(await AuditAsync(inactiveId), "LoginFailed:inactive");
        await using (var db = app.NewDb())
        {
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.Username == "nobody-here" && l.Detail == "unknown user" && l.UserId == null));
        }
    }

    [TestMethod]
    public async Task FiveWrongPasswordsLockTheAccountUntilAnAdminUnlocks()
    {
        var (id, name, password) = await NewUserAsync();
        for (var i = 0; i < AuthService.FailuresBeforeLockout; i++)
        {
            using var wrong = await new Browser(app).PostAsync("/api/v1/auth/login", new { username = name, password = "wrong-password" });
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Locked: even the right password is refused, with the time to wait.
        using var locked = await new Browser(app).PostAsync("/api/v1/auth/login", new { username = name, password });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.AreEqual("locked_out", (await locked.ProblemAsync()).GetProperty("code").GetString());
        Assert.IsTrue(locked.Headers.RetryAfter?.Delta is { TotalSeconds: > 290 and <= 300 }, locked.Headers.RetryAfter?.ToString());
        CollectionAssert.Contains(await AuditAsync(id), "LockedOut:5 minutes");

        using var admin = new TestApp().Client(Roles.Admin);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/users/{id}/unlock", null)).StatusCode);
        Assert.AreEqual("done", (await new Browser(app).LoginAsync(name, password)).Next);
    }

    [TestMethod]
    public void EachLockoutDoublesTheNextUpToAnHour()
    {
        CollectionAssert.AreEqual(new[] { 5, 10, 20, 40, 60, 60 },
            Enumerable.Range(0, 6).Select(n => (int)AuthService.LockoutFor(n).TotalMinutes).ToArray());
    }

    [TestMethod]
    public async Task TheLoginIsRateLimitedPerAddress()
    {
        using var limited = new TestApp(testSignIn: false, new Dictionary<string, string?> { ["Auth:LoginRateLimit:PermitLimit"] = "3" });
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await new Browser(limited).PostAsync("/api/v1/auth/login", new { username = "nobody", password = "wrong-password" });
            statuses.Add(last.StatusCode);
        }
        CollectionAssert.AreEqual(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests },
            statuses);
        Assert.AreEqual("rate_limited", (await last!.ProblemAsync()).GetProperty("code").GetString());
        Assert.IsNotNull(last.Headers.RetryAfter);
        // Only the login: the API and the sensor API are not limited.
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await new Browser(limited).GetAsync("/api/v1/hosts")).StatusCode);
    }

    [TestMethod]
    public async Task CookieWritesNeedTheAntiforgeryToken()
    {
        var (_, name, password) = await NewUserAsync();
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);

        browser.SendCsrfToken = false;
        using var without = await browser.PostAsync("/api/v1/hosts", new { name = "csrf-test" });
        Assert.AreEqual(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.AreEqual("csrf_token_invalid", (await without.ProblemAsync()).GetProperty("code").GetString());

        browser.SendCsrfToken = true;
        using var with = await browser.PostAsync("/api/v1/hosts", new { name = "csrf-test" });
        Assert.AreEqual(HttpStatusCode.Created, with.StatusCode);
        var host = await with.ReadAsync<HostDetail>();
        Assert.AreEqual(HttpStatusCode.NoContent, (await browser.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}")).StatusCode);
        // Reads need no token.
        browser.SendCsrfToken = false;
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode);
    }

    [TestMethod]
    public async Task ATemporaryPasswordMustBeChangedBeforeAnythingElse()
    {
        using var adminApp = new TestApp();
        var created = await (await adminApp.Client(Roles.Admin).PostJsonAsync("/api/v1/users", new { username = $"t{Guid.NewGuid():N}"[..16], groupIds = Roles.GroupIds(Roles.Viewer) }))
            .ReadAsync<CreatedUser>();
        Assert.AreEqual(20, created.TemporaryPassword.Password.Length);
        var browser = new Browser(app);

        var login = await browser.LoginAsync(created.User.Username, created.TemporaryPassword.Password);
        Assert.IsTrue(login.Flags!.MustChangePassword);

        using var blocked = await browser.GetAsync("/api/v1/hosts");
        Assert.AreEqual(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.AreEqual("must_change_password", (await blocked.ProblemAsync()).GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/system/info")).StatusCode);

        using var wrongCurrent = await browser.PostAsync("/api/v1/account/password", new { currentPassword = "nope", newPassword = "a-new-password-1" });
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        Assert.IsTrue((await wrongCurrent.ProblemAsync()).GetProperty("errors").TryGetProperty("currentPassword", out _));
        using var tooShort = await browser.PostAsync("/api/v1/account/password", new { currentPassword = created.TemporaryPassword.Password, newPassword = "short" });
        Assert.IsTrue((await tooShort.ProblemAsync()).GetProperty("errors").TryGetProperty("newPassword", out _));

        using var changed = await browser.PostAsync("/api/v1/account/password",
            new { currentPassword = created.TemporaryPassword.Password, newPassword = "a-new-password-1" });
        Assert.AreEqual(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode, "the renewed cookie has no must-change claim");
        Assert.IsFalse((await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>()).Flags.MustChangePassword);
        Assert.AreEqual("done", (await new Browser(app).LoginAsync(created.User.Username, "a-new-password-1")).Next);
    }

    [TestMethod]
    public async Task ChangingThePasswordEndsTheOtherSessions()
    {
        var (_, name, password) = await NewUserAsync();
        var here = new Browser(app);
        var elsewhere = new Browser(app);
        await here.LoginAsync(name, password);
        await elsewhere.LoginAsync(name, password);

        Assert.AreEqual(HttpStatusCode.NoContent,
            (await here.PostAsync("/api/v1/account/password", new { currentPassword = password, newPassword = "another-password-1" })).StatusCode);

        Assert.AreEqual(HttpStatusCode.OK, (await here.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.IsFalse(elsewhere.Cookies.ContainsKey("pnmt.auth"), "the rejected session's cookie is cleared");
    }

    [TestMethod]
    public async Task SignOutAllSessionsKeepsThisOne()
    {
        var (_, name, password) = await NewUserAsync();
        var here = new Browser(app);
        var elsewhere = new Browser(app);
        await here.LoginAsync(name, password);
        await elsewhere.LoginAsync(name, password);

        Assert.AreEqual(HttpStatusCode.NoContent, (await here.PostAsync("/api/v1/account/sessions/revoke")).StatusCode);

        Assert.AreEqual(HttpStatusCode.OK, (await here.GetAsync("/api/v1/hosts")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/v1/hosts")).StatusCode);
    }

    [TestMethod]
    public async Task AnAdminsResetEndsTheSessionsAndTheTemporaryPasswordExpires()
    {
        var (id, name, password) = await NewUserAsync();
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        using var admin = new TestApp();

        var reset = await (await admin.Client(Roles.Admin).PostAsync($"/api/v1/users/{id}/reset-password", null)).ReadAsync<TemporaryPassword>();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.IsTrue((reset.ExpiresAt - Clock.UtcNow - UserAccounts.TemporaryPasswordLifetime).Duration() < TimeSpan.FromMinutes(1));
        using (var stale = await new Browser(app).PostAsync("/api/v1/auth/login", new { username = name, password }))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, stale.StatusCode);
        }
        try
        {
            Clock.Provider = new FakeTimeProvider(DateTimeOffset.UtcNow + TimeSpan.FromDays(8));
            using var expired = await new Browser(app).PostAsync("/api/v1/auth/login", new { username = name, password = reset.Password });
            Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode);
        }
        finally
        {
            Clock.Provider = TimeProvider.System;
        }
        CollectionAssert.Contains(await AuditAsync(id), "LoginFailed:temporary password expired");
        var login = await new Browser(app).LoginAsync(name, reset.Password);
        Assert.IsTrue(login.Flags!.MustChangePassword);
    }

    /// <summary>The admin flag is a change of the stamp: the user's sessions end (docs/audit-redmine-zabbix-plan.md §5.4).</summary>
    [TestMethod]
    public async Task TheAdminFlagEndsTheSession()
    {
        var (id, name, password) = await NewUserAsync(Roles.User);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        using var admin = new TestApp();

        var updated = await (await admin.Client(Roles.Admin).PutJsonAsync($"/api/v1/users/{id}", new { displayName = name, isAdmin = true, isActive = true }))
            .ReadAsync<UserItem>();

        Assert.IsTrue(updated.IsAdmin);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/hosts")).StatusCode);
        Assert.IsTrue((await new Browser(app).LoginAsync(name, password)).User!.IsAdmin);
        CollectionAssert.Contains(await AuditAsync(id), "UserAdminChanged:now an admin");
    }

    /// <summary>Groups need no sign-out: a session has its new rights from its next request (§5.4).</summary>
    [TestMethod]
    public async Task AGroupChangeAppliesToTheOpenSession()
    {
        var (id, name, password) = await NewUserAsync(Roles.User);
        var browser = new Browser(app);
        var login = await browser.LoginAsync(name, password);
        Assert.AreEqual(Level.Read, login.User!.Rights[Area.Mail]);
        using var admin = new TestApp();

        await (await admin.Client(Roles.Admin).PutJsonAsync($"/api/v1/users/{id}", new { displayName = name, isActive = true,
            groupIds = Roles.GroupIds(Roles.Viewer) })).ReadAsync<UserItem>();

        var refused = await browser.GetAsync("/api/v1/mail-log");
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, "the session is open, with the Viewers' rights");
        var problem = await refused.ProblemAsync();
        Assert.AreEqual(("missing_right", "Mail", "Read"), (problem.GetProperty("code").GetString(), problem.GetProperty("area").GetString(),
            problem.GetProperty("level").GetString()));
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode);
        var me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.AreEqual(Roles.Viewer, Roles.Of(me.User));
        CollectionAssert.Contains(await AuditAsync(id), "UserGroupsChanged:Operators → Viewers");
    }

    /// <summary>A hash from the WebApp's WebAppUser.SetPassword (10,000 iterations) signs in and is upgraded.</summary>
    [TestMethod]
    public async Task AWebAppHashSignsInAndIsUpgraded()
    {
        var (hash, salt) = LegacyWebAppUser.SetPassword("from the old webapp");
        var (id, name, _) = await NewUserAsync();
        await using (var db = app.NewDb())
        {
            var legacy = PasswordHasher.FromWebAppHash(salt, hash);
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.PasswordHash, legacy));
        }

        Assert.AreEqual("done", (await new Browser(app).LoginAsync(name, "from the old webapp")).Next);

        await using (var db = app.NewDb())
        {
            var stored = await db.Users.Where(u => u.Id == id).Select(u => u.PasswordHash).SingleAsync();
            StringAssert.StartsWith(stored, $"pbkdf2-sha256${PasswordHasher.Iterations}$");
            Assert.IsTrue(PasswordHasher.Verify("from the old webapp", stored));
        }
        CollectionAssert.Contains(await AuditAsync(id), "LoginSucceeded:password hash upgraded");
    }

    [TestMethod]
    public async Task LogoutEndsTheSession()
    {
        var (id, name, password) = await NewUserAsync();
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);

        Assert.AreEqual(HttpStatusCode.NoContent, (await browser.PostAsync("/api/v1/auth/logout")).StatusCode);

        Assert.IsFalse(browser.Cookies.ContainsKey("pnmt.auth"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);
        CollectionAssert.Contains(await AuditAsync(id), "Logout:");
    }

    [TestMethod]
    public async Task TheSeededTestAdminSignsIn()
    {
        var login = await new Browser(app).LoginAsync(TestUsers.NameOf(Roles.Admin), TestUsers.Password);
        Assert.IsTrue(login.User!.IsAdmin);
    }
}

/// <summary>The WebApp's password code (PNMT.WebApp/Authentification/WebAppUser.cs), verbatim.</summary>
internal static class LegacyWebAppUser
{
    public static (string PasswordHash, string Salt) SetPassword(string password)
    {
        byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(128 / 8);
        string hashed = Convert.ToBase64String(Microsoft.AspNetCore.Cryptography.KeyDerivation.KeyDerivation.Pbkdf2(
            password: password!,
            salt: salt,
            prf: Microsoft.AspNetCore.Cryptography.KeyDerivation.KeyDerivationPrf.HMACSHA256,
            iterationCount: 10000,
            numBytesRequested: 256 / 8));
        return (hashed, Convert.ToBase64String(salt));
    }
}
