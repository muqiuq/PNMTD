using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Security;

namespace PNMTD.Tests.Auth;

/// <summary>The codes themselves (§4.6, §8.5): RFC 6238's vectors, Base32, drift and replays.</summary>
[TestClass]
public sealed class TotpServiceTests
{
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>RFC 6238 Appendix B, SHA1 (8 digits); the 6-digit code is its last six.</summary>
    [TestMethod]
    [DataRow(59L, "94287082")]
    [DataRow(1111111109L, "07081804")]
    [DataRow(1111111111L, "14050471")]
    [DataRow(1234567890L, "89005924")]
    [DataRow(2000000000L, "69279037")]
    [DataRow(20000000000L, "65353130")]
    public void TheRfc6238VectorsMatch(long unixSeconds, string expected)
    {
        Assert.AreEqual(expected, TotpService.ComputeCodeForStep(RfcKey, unixSeconds / 30, 8));

        var secret = Base32.Encode(RfcKey);
        Assert.AreEqual("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", secret);
        Assert.AreEqual(expected[^6..], TotpService.ComputeCode(secret, DateTime.UnixEpoch.AddSeconds(unixSeconds)));
    }

    /// <summary>RFC 4648 §10, without the padding (authenticator apps do without it).</summary>
    [TestMethod]
    [DataRow("", "")]
    [DataRow("f", "MY")]
    [DataRow("fo", "MZXQ")]
    [DataRow("foo", "MZXW6")]
    [DataRow("foob", "MZXW6YQ")]
    [DataRow("fooba", "MZXW6YTB")]
    [DataRow("foobar", "MZXW6YTBOI")]
    public void Base32MatchesTheRfc4648Vectors(string text, string encoded)
    {
        Assert.AreEqual(encoded, Base32.Encode(Encoding.ASCII.GetBytes(text)));
        Assert.AreEqual(text, Encoding.ASCII.GetString(Base32.Decode(encoded)));
        Assert.AreEqual(text, Encoding.ASCII.GetString(Base32.Decode(encoded.ToLowerInvariant().PadRight((encoded.Length + 7) / 8 * 8, '='))));
    }

    [TestMethod]
    public void Base32RoundTripsAndRefusesOtherCharacters()
    {
        for (var length = 0; length <= 64; length++)
        {
            var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
            CollectionAssert.AreEqual(data, Base32.Decode(Base32.Encode(data)), $"{length} bytes");
        }
        CollectionAssert.AreEqual(Base32.Decode("MZXW6YTBOI"), Base32.Decode(" mzxw 6ytb oi "), "spaces, as the SPA groups it");
        Assert.ThrowsExactly<FormatException>(() => Base32.Decode("MZ1W"));
        var secret = TotpService.GenerateSecret();
        Assert.AreEqual(32, secret.Length);
        Assert.AreEqual(20, Base32.Decode(secret).Length);
    }

    [TestMethod]
    public void ACodeIsValidOneStepEitherWayAndOnlyOnce()
    {
        var secret = TotpService.GenerateSecret();
        var now = new DateTime(2026, 11, 11, 12, 0, 15, DateTimeKind.Utc);
        var step = TotpService.StepAt(now);
        string At(int seconds) => TotpService.ComputeCode(secret, now.AddSeconds(seconds));

        Assert.AreEqual(TotpCheck.Valid, TotpService.Check(secret, At(0), now, null, out var matched));
        Assert.AreEqual(step, matched);
        Assert.AreEqual(TotpCheck.Valid, TotpService.Check(secret, At(-30), now, null, out matched));
        Assert.AreEqual(step - 1, matched);
        Assert.AreEqual(TotpCheck.Valid, TotpService.Check(secret, At(30), now, null, out matched));
        Assert.AreEqual(step + 1, matched);
        Assert.AreEqual(TotpCheck.Invalid, TotpService.Check(secret, At(-60), now, null, out _));
        Assert.AreEqual(TotpCheck.Invalid, TotpService.Check(secret, At(60), now, null, out _));

        // Replays: a step at or before the last one used.
        Assert.AreEqual(TotpCheck.Reused, TotpService.Check(secret, At(0), now, step, out _));
        Assert.AreEqual(TotpCheck.Reused, TotpService.Check(secret, At(-30), now, step, out _));
        Assert.AreEqual(TotpCheck.Valid, TotpService.Check(secret, At(30), now, step, out matched));
        Assert.AreEqual(step + 1, matched);
        Assert.AreEqual(TotpCheck.Valid, TotpService.Check(secret, At(0), now, step - 1, out _));

        var code = At(0);
        Assert.AreEqual(TotpCheck.Valid, TotpService.Check(secret, $" {code[..3]} {code[3..]} ", now, null, out _), "spaces");
        Assert.AreEqual(TotpCheck.Invalid, TotpService.Check(secret, code[..5], now, null, out _));
        Assert.AreEqual(TotpCheck.Invalid, TotpService.Check(secret, "abcdef", now, null, out _));
        Assert.AreEqual(TotpCheck.Invalid, TotpService.Check(secret, null, now, null, out _));
        Assert.AreEqual(TotpCheck.Invalid, TotpService.Check("not base32!", code, now, null, out _));
    }

    [TestMethod]
    public void TheEnrollmentUriNamesTheInstance()
    {
        var uri = TotpService.BuildOtpAuthUri("PNMT (Home: Lab)", "philipp@example.org", "GEZDGNBV");
        Assert.AreEqual("otpauth://totp/PNMT%20%28Home%20Lab%29:philipp%40example.org?secret=GEZDGNBV&issuer=PNMT%20%28Home%20Lab%29&algorithm=SHA1&digits=6&period=30", uri);

        IConfiguration Config(params (string, string)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Item1, v => (string?)v.Item2)).Build();
        Assert.AreEqual("PNMT (Home)", TwoFactorService.Issuer(Config(("Identity", "Home"))));
        Assert.AreEqual("PNMT", TwoFactorService.Issuer(Config()));
        Assert.AreEqual("Ops", TwoFactorService.Issuer(Config(("Identity", "Home"), ("Auth:TotpIssuer", "Ops"))));
    }
}

/// <summary>
/// 2FA with the real cookie scheme (§4.5, §4.6, §8.5): setup, the code step, replays, lockout, turning
/// it off, the admin's reset. Time is frozen and moved by hand: each new code needs a new 30-second step.
/// </summary>
[TestClass]
public sealed class TwoFactorLoginTests
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

    internal static string Code(string secret) => TotpService.ComputeCode(secret, Clock.UtcNow);

    /// <summary>A 6-digit code that is not the current one (nor one step either way).</summary>
    internal static string WrongCode(string secret)
    {
        var near = new[] { -30, 0, 30 }.Select(s => TotpService.ComputeCode(secret, Clock.UtcNow.AddSeconds(s))).ToHashSet();
        return Enumerable.Range(0, 1000).Select(i => (123456 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)).First(c => !near.Contains(c));
    }

    internal static async Task<(Guid Id, string Name, string Password)> NewUserAsync(TestApp on, string role = Roles.User, bool temporary = false)
    {
        var name = $"f{Guid.NewGuid():N}"[..20];
        var password = $"pw-{Guid.NewGuid():N}";
        await using var db = on.NewDb();
        var (user, generated) = await new UserAccounts(db).CreateAsync(name, null, role, temporary ? null : password, AuditContext.Cli);
        return (user.Id, name, generated ?? password);
    }

    /// <summary>Signed in on <paramref name="browser"/>, with 2FA set up through the API; the clock is a step on.</summary>
    internal static async Task<string> EnrollAsync(Browser browser)
    {
        var setup = await (await browser.PostAsync("/api/v1/account/totp/setup")).ReadAsync<TotpSetup>();
        using var confirm = await browser.PostAsync("/api/v1/account/totp/confirm", new { code = Code(setup.Secret) });
        Assert.AreEqual(HttpStatusCode.NoContent, confirm.StatusCode, await confirm.Content.ReadAsStringAsync());
        ((FakeTimeProvider)Clock.Provider).Advance(TimeSpan.FromSeconds(30));
        return setup.Secret;
    }

    private static async Task<(Guid Id, string Name, string Password, string Secret)> EnrolledUserAsync()
    {
        var (id, name, password) = await NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        return (id, name, password, await EnrollAsync(browser));
    }

    private static async Task<List<string>> AuditAsync(Guid userId)
    {
        await using var db = app.NewDb();
        return await db.AuthLog.Where(l => l.UserId == userId).OrderBy(l => l.CreatedAt).Select(l => l.EventType + ":" + l.Detail).ToListAsync();
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.ProblemAsync()).GetProperty("code").GetString();

    [TestMethod]
    public async Task SettingUpNeedsACodeFromTheNewSecretAndEndsTheOtherSessions()
    {
        var (id, name, password) = await NewUserAsync(app);
        var browser = new Browser(app);
        var other = new Browser(app);
        await browser.LoginAsync(name, password);
        await other.LoginAsync(name, password);

        var setup = await (await browser.PostAsync("/api/v1/account/totp/setup")).ReadAsync<TotpSetup>();
        Assert.AreEqual(32, setup.Secret.Length);
        StringAssert.StartsWith(setup.OtpauthUri, $"otpauth://totp/PNMT");
        StringAssert.Contains(setup.OtpauthUri, $":{name}?secret={setup.Secret}&issuer=");
        Assert.IsTrue((setup.ExpiresAt - Clock.UtcNow - TwoFactorService.PendingSetupLifetime).Duration() < TimeSpan.FromSeconds(1));
        await using (var db = app.NewDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == id);
            Assert.IsFalse(user.IsTotpEnabled);
            Assert.IsNotNull(user.PendingTotpSecretProtected);
            Assert.IsFalse(user.PendingTotpSecretProtected.Contains(setup.Secret, StringComparison.OrdinalIgnoreCase), "stored encrypted");
        }

        using var wrong = await browser.PostAsync("/api/v1/account/totp/confirm", new { code = WrongCode(setup.Secret) });
        Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.IsTrue((await wrong.ProblemAsync()).GetProperty("errors").TryGetProperty("code", out _));

        using var confirm = await browser.PostAsync("/api/v1/account/totp/confirm", new { code = Code(setup.Secret) });
        Assert.AreEqual(HttpStatusCode.NoContent, confirm.StatusCode, await confirm.Content.ReadAsStringAsync());

        var me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.IsTrue(me.User.IsTotpEnabled);
        Assert.AreEqual(new SessionFlags(false, false, false), me.Flags);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode, "this session continues");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/auth/me")).StatusCode, "the other one ends");
        await using (var db = app.NewDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == id);
            Assert.IsNull(user.PendingTotpSecretProtected);
            Assert.IsFalse(user.TotpSecretProtected!.Contains(setup.Secret, StringComparison.OrdinalIgnoreCase), "stored encrypted");
            Assert.AreEqual(TotpService.StepAt(Clock.UtcNow), user.TotpLastUsedStep, "the confirming code counts as used");
        }
        CollectionAssert.Contains(await AuditAsync(id), "TotpEnabled:");

        using var again = await browser.PostAsync("/api/v1/account/totp/setup");
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        Assert.AreEqual("totp_already_enabled", await CodeOf(again));
    }

    [TestMethod]
    public async Task ASetupExpiresAfterFifteenMinutesAndANewOneReplacesIt()
    {
        var (_, name, password) = await NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);

        var first = await (await browser.PostAsync("/api/v1/account/totp/setup")).ReadAsync<TotpSetup>();
        time.Advance(TwoFactorService.PendingSetupLifetime + TimeSpan.FromSeconds(1));
        using var expired = await browser.PostAsync("/api/v1/account/totp/confirm", new { code = Code(first.Secret) });
        Assert.AreEqual(HttpStatusCode.Conflict, expired.StatusCode);
        Assert.AreEqual("totp_setup_expired", await CodeOf(expired));

        var second = await (await browser.PostAsync("/api/v1/account/totp/setup")).ReadAsync<TotpSetup>();
        var third = await (await browser.PostAsync("/api/v1/account/totp/setup")).ReadAsync<TotpSetup>();
        Assert.AreEqual(HttpStatusCode.BadRequest, (await browser.PostAsync("/api/v1/account/totp/confirm", new { code = Code(second.Secret) })).StatusCode,
            "the second setup was replaced");
        Assert.AreEqual(HttpStatusCode.NoContent, (await browser.PostAsync("/api/v1/account/totp/confirm", new { code = Code(third.Secret) })).StatusCode);
    }

    [TestMethod]
    public async Task ASignInWithTwoFactorTakesThePasswordThenTheCode()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();
        var browser = new Browser(app);

        var step1 = await browser.LoginAsync(name, password);

        Assert.AreEqual(new LoginResponse("totp", null, null), step1);
        Assert.IsFalse(browser.Cookies.ContainsKey("pnmt.auth"), "not signed in yet");
        var ticket = browser.LastSetCookies.Single(c => c.StartsWith("pnmt.2fa=", StringComparison.Ordinal)).ToLowerInvariant();
        StringAssert.Contains(ticket, "path=/api/v1/auth");
        StringAssert.Contains(ticket, "max-age=300");
        StringAssert.Contains(ticket, "httponly");
        StringAssert.Contains(ticket, "samesite=strict");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);

        using (var wrong = await browser.PostAsync("/api/v1/auth/login/totp", new { code = WrongCode(secret) }))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.AreEqual("invalid_code", await CodeOf(wrong));
        }
        Assert.IsTrue(browser.Cookies.ContainsKey("pnmt.2fa"), "another try");

        using var step2 = await browser.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) });
        var done = await step2.ReadAsync<LoginResponse>();

        Assert.AreEqual("done", done.Next);
        Assert.AreEqual((id, true), (done.User!.Id, done.User.IsTotpEnabled));
        Assert.AreEqual(new SessionFlags(false, false, false), done.Flags);
        Assert.IsTrue(browser.Cookies.ContainsKey("pnmt.auth"));
        Assert.IsFalse(browser.Cookies.ContainsKey("pnmt.2fa"), "used up");
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode);
        var audit = await AuditAsync(id);
        CollectionAssert.Contains(audit, "TotpFailed:wrong code");
        Assert.AreEqual("LoginSucceeded:with 2FA", audit[^1]);
        await using var db = app.NewDb();
        Assert.AreEqual(0, await db.Users.Where(u => u.Id == id).Select(u => u.FailedLoginCount).SingleAsync(), "a sign-in clears the count");
    }

    [TestMethod]
    public async Task ACodeCountsOnlyOnce()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();
        var code = Code(secret);
        var first = new Browser(app);
        await first.LoginAsync(name, password);
        Assert.AreEqual(HttpStatusCode.OK, (await first.PostAsync("/api/v1/auth/login/totp", new { code })).StatusCode);

        var second = new Browser(app);
        Assert.AreEqual("totp", (await second.LoginAsync(name, password)).Next);
        using var replay = await second.PostAsync("/api/v1/auth/login/totp", new { code });
        Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.AreEqual("invalid_code", await CodeOf(replay));
        CollectionAssert.Contains(await AuditAsync(id), "TotpFailed:code already used");

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.AreEqual(HttpStatusCode.OK, (await second.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) })).StatusCode);
    }

    /// <summary>Wrong codes count like wrong passwords, and the right password in between does not reset the count.</summary>
    [TestMethod]
    public async Task WrongCodesCountTowardsTheLockout()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        for (var i = 0; i < 3; i++)
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/v1/auth/login/totp", new { code = WrongCode(secret) })).StatusCode);
        }
        Assert.AreEqual("totp", (await browser.LoginAsync(name, password)).Next);
        for (var i = 0; i < 2; i++)
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/v1/auth/login/totp", new { code = WrongCode(secret) })).StatusCode);
        }

        using var locked = await browser.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.AreEqual("locked_out", await CodeOf(locked));
        Assert.IsNotNull(locked.Headers.RetryAfter?.Delta);
        Assert.IsFalse(browser.Cookies.ContainsKey("pnmt.2fa"), "the sign-in starts again with the password");
        using var step1 = await new Browser(app).PostAsync("/api/v1/auth/login", new { username = name, password });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, step1.StatusCode);
        CollectionAssert.Contains(await AuditAsync(id), "LockedOut:5 minutes");
    }

    [TestMethod]
    public async Task TheCodeStepExpiresAfterFiveMinutesOrWhenTheAccountChanges()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();

        using (var none = await new Browser(app).PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) }))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, none.StatusCode);
            Assert.AreEqual("login_expired", await CodeOf(none));
        }

        foreach (var forgery in new[] { "CfDJ8-forged", "not*base64" })
        {
            var forged = new Browser(app);
            forged.Cookies["pnmt.2fa"] = forgery;
            Assert.AreEqual("login_expired", await CodeOf(await forged.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) })), forgery);
        }

        var late = new Browser(app);
        await late.LoginAsync(name, password);
        time.Advance(TwoFactorService.LoginStepLifetime + TimeSpan.FromSeconds(1));
        using (var expired = await late.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) }))
        {
            Assert.AreEqual("login_expired", await CodeOf(expired));
        }
        Assert.IsFalse(late.Cookies.ContainsKey("pnmt.2fa"));

        var changed = new Browser(app);
        await changed.LoginAsync(name, password);
        await using (var db = app.NewDb())
        {
            await new UserAccounts(db).ResetPasswordAsync(id, "another-password-1", AuditContext.Cli);
        }
        Assert.AreEqual("login_expired", await CodeOf(await changed.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) })));

        await using (var db = app.NewDb())
        {
            Assert.AreEqual(0, await db.Users.Where(u => u.Id == id).Select(u => u.FailedLoginCount).SingleAsync(), "no guess was made");
        }
    }

    [TestMethod]
    public async Task TurningItOffNeedsThePasswordAndACode()
    {
        var (id, name, password) = await NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        var secret = await EnrollAsync(browser);
        var other = new Browser(app);
        await other.LoginAsync(name, password);
        await other.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) });
        time.Advance(TimeSpan.FromSeconds(30));

        using (var wrongPassword = await browser.PostAsync("/api/v1/account/totp/disable", new { password = "not-the-password", code = Code(secret) }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
            var errors = (await wrongPassword.ProblemAsync()).GetProperty("errors");
            Assert.IsTrue(errors.TryGetProperty("password", out _));
            Assert.IsFalse(errors.TryGetProperty("code", out _));
        }
        using (var wrongCode = await browser.PostAsync("/api/v1/account/totp/disable", new { password, code = WrongCode(secret) }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, wrongCode.StatusCode);
            var errors = (await wrongCode.ProblemAsync()).GetProperty("errors");
            Assert.IsTrue(errors.TryGetProperty("code", out _));
            Assert.IsFalse(errors.TryGetProperty("password", out _));
        }
        Assert.AreEqual(HttpStatusCode.OK, (await other.GetAsync("/api/v1/auth/me")).StatusCode, "still on");

        using var disable = await browser.PostAsync("/api/v1/account/totp/disable", new { password, code = Code(secret) });
        Assert.AreEqual(HttpStatusCode.NoContent, disable.StatusCode, await disable.Content.ReadAsStringAsync());

        var me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.AreEqual((false, true), (me.User.IsTotpEnabled, me.Flags.TotpSetupSuggested));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/auth/me")).StatusCode, "the other session ends");
        Assert.AreEqual("done", (await new Browser(app).LoginAsync(name, password)).Next);
        var audit = await AuditAsync(id);
        CollectionAssert.Contains(audit, "TotpFailed:turning 2FA off refused: wrong password");
        CollectionAssert.Contains(audit, "TotpFailed:turning 2FA off refused: wrong code");
        CollectionAssert.Contains(audit, "TotpDisabled:");

        using var again = await browser.PostAsync("/api/v1/account/totp/disable", new { password, code = Code(secret) });
        Assert.AreEqual("totp_not_enabled", await CodeOf(again));
    }

    [TestMethod]
    public async Task AnAdminsResetTurnsItOffEndsTheSessionsAndLiftsALockout()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();
        var session = new Browser(app);
        await session.LoginAsync(name, password);
        Assert.AreEqual(HttpStatusCode.OK, (await session.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) })).StatusCode);
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LockoutEnd, DateTime.UtcNow.AddHours(1)));
        }

        // The seeded admin, on the same server: its session cache forgets the user at once.
        var admin = new Browser(app);
        await admin.LoginAsync(TestUsers.NameOf(Roles.Admin), TestUsers.Password);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/users/{id}/reset-totp")).StatusCode);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/auth/me")).StatusCode, "the sessions end");
        var item = await (await admin.GetAsync($"/api/v1/users/{id}")).ReadAsync<UserItem>();
        Assert.AreEqual((false, null), (item.IsTotpEnabled, item.LockedUntil));
        var login = await new Browser(app).LoginAsync(name, password);
        Assert.AreEqual(("done", true), (login.Next, login.Flags!.TotpSetupSuggested), "the next sign-in offers the setup again");
        Assert.AreEqual("TotpReset:", (await AuditAsync(id)).Single(e => e.StartsWith("TotpReset", StringComparison.Ordinal)));
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/v1/users/{Guid.NewGuid()}/reset-totp")).StatusCode);
    }
}

/// <summary>Auth:TotpPolicy (§4.6): RequiredForAdmins and Required hold every call but the setup.</summary>
[TestClass]
public sealed class TotpPolicyTests
{
    private static TestApp forAdmins = null!;
    private static TestApp forEveryone = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        forAdmins = new TestApp(testSignIn: false, new Dictionary<string, string?> { ["Auth:TotpPolicy"] = "RequiredForAdmins" });
        forEveryone = new TestApp(testSignIn: false, new Dictionary<string, string?> { ["Auth:TotpPolicy"] = "required" });
    }

    [ClassCleanup]
    public static void Stop()
    {
        forAdmins.Dispose();
        forEveryone.Dispose();
    }

    [TestInitialize]
    public void Freeze() => Clock.Provider = new FakeTimeProvider(DateTimeOffset.UtcNow);

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.ProblemAsync()).GetProperty("code").GetString();

    [TestMethod]
    public async Task AnAdminWithoutItMaySetItUpAndNothingElse()
    {
        var (_, name, password) = await TwoFactorLoginTests.NewUserAsync(forAdmins, Roles.Admin);
        var browser = new Browser(forAdmins);

        var login = await browser.LoginAsync(name, password);

        Assert.AreEqual(new SessionFlags(false, true, false), login.Flags);
        using (var hosts = await browser.GetAsync("/api/v1/hosts"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, hosts.StatusCode);
            Assert.AreEqual("totp_setup_required", await CodeOf(hosts));
        }
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/system/info")).StatusCode);

        var secret = await TwoFactorLoginTests.EnrollAsync(browser);

        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode);
        Assert.AreEqual(new SessionFlags(false, false, false), (await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>()).Flags);
        using var disable = await browser.PostAsync("/api/v1/account/totp/disable", new { password, code = TwoFactorLoginTests.Code(secret) });
        Assert.AreEqual(HttpStatusCode.Conflict, disable.StatusCode);
        Assert.AreEqual("totp_required", await CodeOf(disable));
    }

    [TestMethod]
    public async Task RequiredForAdminsLeavesTheOtherRolesAPrompt()
    {
        var (_, name, password) = await TwoFactorLoginTests.NewUserAsync(forAdmins, Roles.User);
        var browser = new Browser(forAdmins);
        Assert.AreEqual(new SessionFlags(false, false, true), (await browser.LoginAsync(name, password)).Flags);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode);
    }

    [TestMethod]
    public async Task RequiredHoldsAViewerTooAndSessionsThatWereOpenAlready()
    {
        var (_, name, password) = await TwoFactorLoginTests.NewUserAsync(forEveryone, Roles.Viewer);
        // Signed in where 2FA is optional; the same key ring makes the cookie valid at the other app,
        // as a session is after the setting changes and the server restarts.
        using var optional = new TestApp(testSignIn: false);
        var browser = new Browser(optional);
        await browser.LoginAsync(name, password);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/dashboard")).StatusCode);

        var moved = new Browser(forEveryone);
        foreach (var (cookie, value) in browser.Cookies)
        {
            moved.Cookies[cookie] = value;
        }
        using var dashboard = await moved.GetAsync("/api/v1/dashboard");
        Assert.AreEqual(HttpStatusCode.Forbidden, dashboard.StatusCode);
        Assert.AreEqual("totp_setup_required", await CodeOf(dashboard));
        Assert.IsTrue((await (await moved.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>()).Flags.TotpSetupRequired);
    }

    /// <summary>The password comes first: its gate holds the setup, the 2FA gate lets the password change through.</summary>
    [TestMethod]
    public async Task ATemporaryPasswordIsChangedBeforeTheSetup()
    {
        var (_, name, temporary) = await TwoFactorLoginTests.NewUserAsync(forAdmins, Roles.Admin, temporary: true);
        var browser = new Browser(forAdmins);
        Assert.AreEqual(new SessionFlags(true, true, false), (await browser.LoginAsync(name, temporary)).Flags);

        using (var setup = await browser.PostAsync("/api/v1/account/totp/setup"))
        {
            Assert.AreEqual("must_change_password", await CodeOf(setup));
        }
        Assert.AreEqual(HttpStatusCode.NoContent,
            (await browser.PostAsync("/api/v1/account/password", new { currentPassword = temporary, newPassword = "a-new-password-1" })).StatusCode);
        Assert.AreEqual("totp_setup_required", await CodeOf(await browser.GetAsync("/api/v1/hosts")));
        Assert.AreEqual(HttpStatusCode.OK, (await browser.PostAsync("/api/v1/account/totp/setup")).StatusCode);
    }
}
