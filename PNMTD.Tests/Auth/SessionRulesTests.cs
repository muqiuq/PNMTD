using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;

namespace PNMTD.Tests.Auth;

/// <summary>When the password and the day's code are due (docs/mcp-plan.md §3.2): 04:00 in PNMT's zone, whatever the season.</summary>
[TestClass]
public sealed class SessionRulesTests
{
    private TimeZoneInfo zone = null!;

    [TestInitialize]
    public void Zurich()
    {
        zone = Clock.TimeZone;
        Clock.TimeZone = Clock.FromName("Europe/Zurich");
    }

    [TestCleanup]
    public void Restore() => Clock.TimeZone = zone;

    private static DateTime Local(int year, int month, int day, int hour, int minute = 0) =>
        Clock.ToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified));

    private static string AsLocal(DateTime? instant) => Clock.ToLocal(instant!.Value).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public void TheCodeIsDueAtTheFirstFourOClockAfterIt()
    {
        var options = new AuthOptions();
        Assert.AreEqual("2026-11-17 04:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 9, 0), options)), "a morning: the next day");
        Assert.AreEqual("2026-11-17 04:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 23, 59), options)), "late: still the next day");
        Assert.AreEqual("2026-11-16 04:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 3, 59), options)), "before four: the same morning");
        Assert.AreEqual("2026-11-17 04:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 4, 0), options)), "at four exactly: the next day");
        Assert.AreEqual("2026-11-18 04:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 9, 0), new AuthOptions { TotpDays = 2 })));
        Assert.AreEqual("2026-11-16 23:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 9, 0), new AuthOptions { ResetHour = 23 })));
        Assert.AreEqual("2026-11-17 00:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 9, 0), new AuthOptions { ResetHour = 0 })));
        Assert.IsNull(SessionRules.TotpDue(Local(2026, 11, 16, 9, 0), new AuthOptions { TotpDays = 0 }));
    }

    [TestMethod]
    public void BothDaylightSavingChangesStillEndTheDayAtFour()
    {
        var options = new AuthOptions();
        // 2026-03-29: 02:00 becomes 03:00 (a day of 23 hours); 2026-10-25: 03:00 becomes 02:00 (25 hours).
        var spring = SessionRules.TotpDue(Local(2026, 3, 28, 12, 0), options);
        var autumn = SessionRules.TotpDue(Local(2026, 10, 24, 12, 0), options);
        Assert.AreEqual("2026-03-29 04:00", AsLocal(spring));
        Assert.AreEqual("2026-10-25 04:00", AsLocal(autumn));
        Assert.AreEqual(TimeSpan.FromHours(15), spring - Local(2026, 3, 28, 12, 0), "an hour less");
        Assert.AreEqual(TimeSpan.FromHours(17), autumn - Local(2026, 10, 24, 12, 0), "an hour more");
        // An hour the spring change skips.
        Assert.AreEqual("2026-03-29 03:00", AsLocal(SessionRules.TotpDue(Local(2026, 3, 28, 12, 0), new AuthOptions { ResetHour = 2 })));
    }

    [TestMethod]
    public void ThePasswordIsDueAtFourAfterThirtyDays()
    {
        var options = new AuthOptions();
        Assert.AreEqual("2026-12-17 04:00", AsLocal(SessionRules.LoginEnds(Local(2026, 11, 16, 9, 0), options)));
        Assert.AreEqual("2026-12-16 04:00", AsLocal(SessionRules.LoginEnds(Local(2026, 11, 16, 2, 0), options)));
        Assert.AreEqual("2026-11-23 04:00", AsLocal(SessionRules.LoginEnds(Local(2026, 11, 16, 9, 0), new AuthOptions { LoginDays = 6 })));
        Assert.IsNull(SessionRules.LoginEnds(Local(2026, 11, 16, 9, 0), new AuthOptions { LoginDays = 0 }));
    }
}

/// <summary>The rules on real sessions (docs/mcp-plan.md §3.3, §3.4): the 30 days, the day's code, last online, and the update.</summary>
[TestClass]
public sealed class SessionTimesTests
{
    private static TestApp app = null!;
    private static FakeTimeProvider time = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp(testSignIn: false);

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public void Freeze()
    {
        time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Clock.Provider = time;
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.ProblemAsync()).GetProperty("code").GetString();

    private static AuthOptions Options => app.Services.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue;

    /// <summary>A user with 2FA, signed in with the password and a code on a browser of their own.</summary>
    private static async Task<(Guid Id, Browser Browser, string Secret)> SignedInWithTwoFactorAsync()
    {
        var (id, name, password) = await TwoFactorLoginTests.NewUserAsync(app);
        var setup = new Browser(app);
        await setup.LoginAsync(name, password);
        var secret = await TwoFactorLoginTests.EnrollAsync(setup);
        var browser = new Browser(app);
        Assert.AreEqual("totp", (await browser.LoginAsync(name, password)).Next);
        using var step2 = await browser.PostAsync("/api/v1/auth/login/totp", new { code = TwoFactorLoginTests.Code(secret) });
        Assert.AreEqual(HttpStatusCode.OK, step2.StatusCode, await step2.Content.ReadAsStringAsync());
        return (id, browser, secret);
    }

    [TestMethod]
    public async Task TheDaysCodeIsAskedForOverThePageAndTheSessionGoesOn()
    {
        var (id, browser, secret) = await SignedInWithTwoFactorAsync();
        var me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.IsFalse(me.Session.TotpDue);
        Assert.AreEqual(SessionRules.LoginEnds(Clock.UtcNow, Options), me.Session.SignedInUntil);

        time.SetUtcNow(SessionRules.TotpDue(Clock.UtcNow, Options)!.Value.AddMinutes(1));

        using (var held = await browser.GetAsync("/api/v1/hosts"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, held.StatusCode);
            Assert.AreEqual(SessionRules.TotpDueCode, await CodeOf(held));
        }
        me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.IsTrue(me.Session.TotpDue, "who it is still answers, and says so");
        using (var wrong = await browser.PostAsync("/api/v1/auth/totp/daily", new { code = TwoFactorLoginTests.WrongCode(secret) }))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.AreEqual("invalid_code", await CodeOf(wrong));
        }
        using (var right = await browser.PostAsync("/api/v1/auth/totp/daily", new { code = TwoFactorLoginTests.Code(secret) }))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, right.StatusCode, await right.Content.ReadAsStringAsync());
        }
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode, "the same session goes on");
        me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.IsFalse(me.Session.TotpDue);

        await using var db = app.NewDb();
        var events = await db.AuditLog.Where(l => l.UserId == id).Select(l => l.EventType + ":" + l.Detail).ToListAsync();
        CollectionAssert.Contains(events, "TotpDailyConfirmed:");
        CollectionAssert.Contains(events, "TotpFailed:wrong code (the day's code)");
    }

    [TestMethod]
    public async Task WithoutTwoFactorThereIsNoDaysCode()
    {
        var (_, name, password) = await TwoFactorLoginTests.NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        time.Advance(TimeSpan.FromDays(3));
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode);
        using var daily = await browser.PostAsync("/api/v1/auth/totp/daily", new { code = "123456" });
        Assert.AreEqual(HttpStatusCode.Conflict, daily.StatusCode);
        Assert.AreEqual("totp_not_enabled", await CodeOf(daily));
    }

    [TestMethod]
    public async Task AfterThirtyDaysThePasswordIsAskedForAgain()
    {
        var (_, name, password) = await TwoFactorLoginTests.NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        var ends = SessionRules.LoginEnds(Clock.UtcNow, Options)!.Value;

        time.SetUtcNow(ends.AddMinutes(-1));
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode, "a minute before");
        time.SetUtcNow(ends.AddMinutes(1));
        using var expired = await browser.GetAsync("/api/v1/hosts");
        Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.AreEqual(SessionRules.LoginExpiredCode, await CodeOf(expired));
        Assert.IsFalse(browser.Cookies.ContainsKey(AuthSetup.CookieName), "the cookie is removed");

        Assert.AreEqual("done", (await browser.LoginAsync(name, password)).Next);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode, "a new sign-in starts a new month");
    }

    [TestMethod]
    public async Task ChangingThePasswordStartsTheMonthAgain()
    {
        var (_, name, password) = await TwoFactorLoginTests.NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        var first = SessionRules.LoginEnds(Clock.UtcNow, Options)!.Value;
        time.Advance(TimeSpan.FromDays(20));
        using (var change = await browser.PostAsync("/api/v1/account/password", new { currentPassword = password, newPassword = $"new-{Guid.NewGuid():N}" }))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, change.StatusCode, await change.Content.ReadAsStringAsync());
        }
        time.SetUtcNow(first.AddHours(1));
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode);
    }

    [TestMethod]
    public async Task ASessionFromBeforeTheUpdateGoesOnAndGetsTheTimes()
    {
        var (id, _, _) = await TwoFactorLoginTests.NewUserAsync(app);
        PNMTD.Models.Db.UserEntity user;
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsTotpEnabled, true));
            user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        }
        // The principal a 1.5 sign-in wrote: no signed_in, no totp_at.
        var principal = SessionClaims.Create(user);
        var identity = (System.Security.Claims.ClaimsIdentity)principal.Identity!;
        foreach (var claim in identity.Claims.Where(c => c.Type is SessionClaims.SignedIn or SessionClaims.TotpAt).ToList())
        {
            identity.RemoveClaim(claim);
        }
        var cookie = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { IsPersistent = true, IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) },
            CookieAuthenticationDefaults.AuthenticationScheme);
        var browser = new Browser(app);
        browser.Cookies[AuthSetup.CookieName] = cookie.TicketDataFormat.Protect(ticket);

        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode, "not signed out by the update");
        Assert.IsTrue(browser.LastSetCookies.Any(c => c.StartsWith(AuthSetup.CookieName + "=", StringComparison.Ordinal)), "renewed with the times");
        var me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.IsFalse(me.Session.TotpDue, "the code counts as entered at the update");
        Assert.AreEqual(SessionRules.LoginEnds(Clock.UtcNow, Options), me.Session.SignedInUntil);

        time.SetUtcNow(SessionRules.TotpDue(Clock.UtcNow, Options)!.Value.AddMinutes(1));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/v1/hosts")).StatusCode, "due at the next four o'clock");
    }

    [TestMethod]
    public async Task LastOnlineIsWhatTheGatesLetThrough()
    {
        var (id, browser, _) = await SignedInWithTwoFactorAsync();
        async Task<DateTime?> SeenAsync()
        {
            await using var db = app.NewDb();
            return await db.Users.Where(u => u.Id == id).Select(u => u.LastSeenAt).SingleAsync();
        }

        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode);
        var first = await SeenAsync();
        Assert.IsNotNull(first);
        Assert.IsTrue(Clock.UtcNow - first.Value < TimeSpan.FromMinutes(1), "the sign-in's requests count too");

        time.Advance(TimeSpan.FromMinutes(2));
        await browser.GetAsync("/api/v1/hosts");
        Assert.AreEqual(first, await SeenAsync(), "at most every five minutes");

        time.Advance(LastSeen.Every);
        await browser.GetAsync("/api/v1/hosts");
        var second = await SeenAsync();
        Assert.IsTrue(second > first);

        time.SetUtcNow(SessionRules.TotpDue(Clock.UtcNow, Options)!.Value.AddMinutes(10));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/v1/hosts")).StatusCode);
        Assert.AreEqual(second, await SeenAsync(), "a session the day's code holds up isn't online");
    }
}

/// <summary><c>Auth__TotpDays=0</c> and <c>Auth__LoginDays=0</c>: as before 1.6.</summary>
[TestClass]
public sealed class SessionRulesOffTests
{
    [TestMethod]
    public async Task WithTheRulesOffSessionsAreAsBefore()
    {
        using var app = new TestApp(testSignIn: false, new Dictionary<string, string?> { ["Auth:TotpDays"] = "0", ["Auth:LoginDays"] = "0" });
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Clock.Provider = time;
        try
        {
            var (_, name, password) = await TwoFactorLoginTests.NewUserAsync(app);
            var setup = new Browser(app);
            await setup.LoginAsync(name, password);
            await TwoFactorLoginTests.EnrollAsync(setup);
            time.Advance(TimeSpan.FromDays(40));
            Assert.AreEqual(HttpStatusCode.OK, (await setup.GetAsync("/api/v1/hosts")).StatusCode);
            var me = await (await setup.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
            Assert.AreEqual(new SessionTimes(false, null), me.Session);
        }
        finally
        {
            Clock.Provider = TimeProvider.System;
        }
    }
}
