using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PNMTD.Auth;
using PNMTD.Cli;
using PNMTD.Import;
using PNMTD.Lib.Security;

namespace PNMTD.Tests.Auth;

/// <summary>Passwords, the first admin, users.json and `pnmtd users …` (§4.4, §4.8, §4.10, §8.5).</summary>
[TestClass]
public sealed class AccountsTests
{
    [TestMethod]
    public void HashesCarryTheirParametersAndVerifyInConstantTime()
    {
        var hash = PasswordHasher.Hash("correct horse");

        StringAssert.StartsWith(hash, "pbkdf2-sha256$210000$");
        Assert.IsTrue(PasswordHasher.Verify("correct horse", hash));
        Assert.IsFalse(PasswordHasher.Verify("Correct horse", hash));
        Assert.IsFalse(PasswordHasher.Verify("correct horse", "garbage"));
        Assert.IsFalse(PasswordHasher.NeedsRehash(hash));
        Assert.AreNotEqual(hash, PasswordHasher.Hash("correct horse"), "salted");
    }

    /// <summary>KeyDerivation.Pbkdf2(HMACSHA256, 10000, 32) and Rfc2898DeriveBytes.Pbkdf2(SHA256) are one algorithm (§4.8).</summary>
    [TestMethod]
    public void WebAppHashesConvertWithoutThePassword()
    {
        var (hash, salt) = LegacyWebAppUser.SetPassword("pnmt-legacy");
        var converted = PasswordHasher.FromWebAppHash(salt, hash);

        Assert.AreEqual($"pbkdf2-sha256$10000${salt}${hash}", converted);
        Assert.IsTrue(PasswordHasher.Verify("pnmt-legacy", converted));
        Assert.IsTrue(PasswordHasher.NeedsRehash(converted));
    }

    [TestMethod]
    public void ThePolicyIsLengthOnly()
    {
        Assert.IsNotNull(PasswordHasher.PolicyViolation("123456789"));
        Assert.IsNull(PasswordHasher.PolicyViolation("1234567890"));
        Assert.IsNotNull(PasswordHasher.PolicyViolation(new string('x', 257)));
        Assert.AreEqual(20, SecurePasswordGenerator.Generate().Length);
        Assert.IsFalse(SecurePasswordGenerator.Generate(200).Any(c => "0O1lI".Contains(c)));
    }

    [TestMethod]
    public async Task ANewInstallationGetsItsFirstAdminOnce()
    {
        await using var db = TestDatabases.Fresh("pnmtd_seed_test");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:InitialAdmin:Username"] = "root",
            ["Auth:InitialAdmin:Password"] = "from-init-sh-1234",
        }).Build();

        var (admin, generated) = await new UserAccounts(db).SeedAsync(config);
        var (again, _) = await new UserAccounts(db).SeedAsync(config);

        Assert.AreEqual(("root", Roles.Admin, true), (admin!.Username, admin.Role, admin.MustChangePassword));
        Assert.IsNull(admin.TemporaryPasswordExpiresAt, "init.sh's password does not expire before the first sign-in");
        Assert.IsNull(generated);
        Assert.IsNull(again);
        Assert.AreEqual(LoginStatus.Success, (await new AuthService(db, new UserAccounts(db), TestUsers.TotpSecrets).LoginAsync("ROOT", "from-init-sh-1234", AuditContext.Cli)).Status);

        await using var empty = TestDatabases.Fresh("pnmtd_seed_test");
        var (random, password) = await new UserAccounts(empty).SeedAsync(new ConfigurationBuilder().Build());
        Assert.AreEqual("admin", random!.Username);
        Assert.AreEqual(20, password!.Length);
    }

    private static string UsersJson(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static ImportReport Import(Microsoft.EntityFrameworkCore.DbContext db, string usersPath, bool reset = false) =>
        new SqliteImporter((Data.PnmtdDbContext)db, NullLogger<SqliteImporter>.Instance).Import(new SqliteImportOptions
        {
            SqlitePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v0.12-sample.db"),
            UsersJsonPath = usersPath,
            SourceTimeZone = "UTC",
            Reset = reset,
        });

    /// <summary>The format WebAppUserManager wrote, with hashes made by its SetPassword.</summary>
    [TestMethod]
    public async Task UsersJsonImportsWithTheirPasswords()
    {
        var (adminHash, adminSalt) = LegacyWebAppUser.SetPassword("admin-password-1");
        var (pnmtHash, pnmtSalt) = LegacyWebAppUser.SetPassword("pnmt");
        var (userHash, userSalt) = LegacyWebAppUser.SetPassword("user-password-1");
        var adminId = Guid.NewGuid();
        var path = UsersJson("users", $$"""
            [{"Id":"{{adminId}}","Username":"Philipp","Name":"Philipp A.","IsAdmin":true,"PasswordHash":"{{adminHash}}","Salt":"{{adminSalt}}"},
             {"Id":"{{Guid.NewGuid()}}","Username":"pnmt","Name":"default user","IsAdmin":true,"PasswordHash":"{{pnmtHash}}","Salt":"{{pnmtSalt}}"},
             {"Id":"{{Guid.NewGuid()}}","Username":"ops","Name":"Ops","IsAdmin":false,"PasswordHash":"{{userHash}}","Salt":"{{userSalt}}"}]
            """);
        await using var db = TestDatabases.Fresh("pnmtd_users_import");

        var report = Import(db, path);

        Assert.IsTrue(report.Succeeded, report.ToText());
        CollectionAssert.AreEqual(new[]
        {
            "Philipp (Admin): keeps its password",
            "pnmt (Admin): must change the default password pnmt",
            "ops (User): keeps its password",
        }, report.Users);
        var users = await db.Users.OrderBy(u => u.NormalizedUsername).ToListAsync();
        Assert.AreEqual(adminId, users.Single(u => u.Username == "Philipp").Id, "ids are kept");
        Assert.AreEqual("Philipp A.", users.Single(u => u.Username == "Philipp").DisplayName);
        Assert.IsTrue(users.Single(u => u.Username == "pnmt").MustChangePassword);
        var auth = new AuthService(db, new UserAccounts(db), TestUsers.TotpSecrets);
        Assert.AreEqual(LoginStatus.Success, (await auth.LoginAsync("philipp", "admin-password-1", AuditContext.Cli)).Status);
        Assert.AreEqual(LoginStatus.Success, (await auth.LoginAsync("ops", "user-password-1", AuditContext.Cli)).Status);
        StringAssert.StartsWith(await db.Users.Where(u => u.Username == "ops").Select(u => u.PasswordHash).SingleAsync(), "pbkdf2-sha256$210000$");

        // A second import is refused; --reset replaces everything, the users included.
        Assert.IsFalse(Import(db, path).Succeeded);
        Assert.IsTrue(Import(db, path, reset: true).Succeeded);
        Assert.AreEqual(3, await db.Users.CountAsync());
    }

    /// <summary>A server that started before the import created its first admin: --users refuses, without it the admin stays.</summary>
    [TestMethod]
    public async Task UsersAreNotImportedNextToAnExistingAdmin()
    {
        var (hash, salt) = LegacyWebAppUser.SetPassword("whatever-password");
        var path = UsersJson("late", $$"""[{"Id":"{{Guid.NewGuid()}}","Username":"ops","Name":"Ops","IsAdmin":true,"PasswordHash":"{{hash}}","Salt":"{{salt}}"}]""");
        await using var db = TestDatabases.Fresh("pnmtd_users_late");
        await new UserAccounts(db).SeedAsync(new ConfigurationBuilder().Build());

        var refused = Import(db, path);
        Assert.IsFalse(refused.Succeeded);
        StringAssert.Contains(refused.Failure, "has 1 user(s) already");
        Assert.AreEqual(0, await db.Hosts.CountAsync(), "nothing committed");

        var withoutUsers = new SqliteImporter(db, NullLogger<SqliteImporter>.Instance).Import(new SqliteImportOptions
        {
            SqlitePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v0.12-sample.db"),
            SourceTimeZone = "UTC",
        });
        Assert.IsTrue(withoutUsers.Succeeded, withoutUsers.ToText());
        Assert.AreEqual("admin", (await db.Users.SingleAsync()).Username);
    }

    /// <summary>The oldest users.json: one user without Id and Name, an admin (as WebAppUserManager migrated it).</summary>
    [TestMethod]
    public async Task TheOldestSingleUserFormatBecomesAnAdmin()
    {
        var (hash, salt) = LegacyWebAppUser.SetPassword("first-ever-password");
        var path = UsersJson("oldest", $$"""[{"Username":"pnmt","PasswordHash":"{{hash}}","Salt":"{{salt}}"}]""");
        await using var db = TestDatabases.Fresh("pnmtd_users_oldest");

        var report = Import(db, path);

        Assert.IsTrue(report.Succeeded, report.ToText());
        var user = await db.Users.SingleAsync();
        Assert.AreEqual(("pnmt", "pnmt", Roles.Admin, false), (user.Username, user.DisplayName, user.Role, user.MustChangePassword));
        Assert.AreNotEqual(Guid.Empty, user.Id);
    }

    [TestMethod]
    public async Task NamesThatDifferOnlyInCaseStopTheWholeImport()
    {
        var (hash, salt) = LegacyWebAppUser.SetPassword("whatever-password");
        var path = UsersJson("dupes", $$"""
            [{"Id":"{{Guid.NewGuid()}}","Username":"Ops","Name":"a","IsAdmin":true,"PasswordHash":"{{hash}}","Salt":"{{salt}}"},
             {"Id":"{{Guid.NewGuid()}}","Username":"ops","Name":"b","IsAdmin":false,"PasswordHash":"{{hash}}","Salt":"{{salt}}"}]
            """);
        await using var db = TestDatabases.Fresh("pnmtd_users_dupes");

        var report = Import(db, path);

        Assert.IsFalse(report.Succeeded);
        StringAssert.Contains(report.Failure, "named ops");
        Assert.AreEqual(0, await db.Hosts.CountAsync(), "nothing committed");
    }

    /// <summary>`pnmtd users …` in process, on the run's database.</summary>
    private static (int Exit, string Output) Cli(string? stdin, params string[] args)
    {
        var output = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalIn = Console.In;
        Console.SetOut(output);
        Console.SetError(output);
        if (stdin is not null)
        {
            Console.SetIn(new StringReader(stdin + "\n"));
        }
        try
        {
            return (CommandLine.Run(args), output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }
    }

    [TestMethod]
    public async Task TheUsersCommandAddsResetsUnlocksAndListsUsers()
    {
        var name = $"c{Guid.NewGuid():N}"[..14];
        var (exit, output) = Cli(null, "users", "add", name, "--role", "viewer", "--display-name", "Command Line");
        Assert.AreEqual(0, exit, output);
        var temporary = output.Split('\n').Single(l => l.StartsWith("    ", StringComparison.Ordinal)).Trim();

        await using var db = Data.PnmtdDbContext.Create(Data.DatabaseSettings.ConnectionString(TestDefaults.Configuration));
        var auth = new AuthService(db, new UserAccounts(db), TestUsers.TotpSecrets);
        var login = await auth.LoginAsync(name, temporary, AuditContext.Cli);
        Assert.AreEqual((LoginStatus.Success, Roles.Viewer, true), (login.Status, login.User!.Role, login.User.MustChangePassword));

        Assert.AreEqual(0, Cli("chosen-password-123", "users", "reset-password", name, "--password-stdin").Exit);
        db.ChangeTracker.Clear();
        login = await auth.LoginAsync(name, "chosen-password-123", AuditContext.Cli);
        Assert.AreEqual((LoginStatus.Success, false), (login.Status, login.User!.MustChangePassword), "a chosen password is not temporary");

        Assert.AreEqual(0, Cli(null, "users", "set-role", name, "ADMIN").Exit);
        await db.Users.Where(u => u.Username == name).ExecuteUpdateAsync(u => u.SetProperty(x => x.LockoutEnd, DateTime.UtcNow.AddHours(1)));
        var (_, list) = Cli(null, "users", "list");
        StringAssert.Matches(list, new System.Text.RegularExpressions.Regex($@"{name}\s+Admin\s+active\s+-\s+locked until"));
        Assert.AreEqual(0, Cli(null, "users", "unlock", name).Exit);
        db.ChangeTracker.Clear();
        Assert.IsNull(await db.Users.Where(u => u.Username == name).Select(u => u.LockoutEnd).SingleAsync());
        Assert.IsTrue(await db.AuthLog.AnyAsync(l => l.Username == name && l.EventType == AuthEvents.PasswordReset && l.Detail == "cli: password set"));

        Assert.AreEqual(1, Cli(null, "users", "reset-password", "no-such-user").Exit);
        Assert.AreEqual(1, Cli("short", "users", "add", $"{name}x", "--password-stdin").Exit);
        Assert.AreEqual(2, Cli(null, "users", "frobnicate").Exit);
    }

    /// <summary>reset-totp.sh's command (§4.6): 2FA off, the sessions ended, a lockout lifted, in the audit log as cli.</summary>
    [TestMethod]
    public async Task TheUsersCommandResetsTwoFactor()
    {
        var name = $"t{Guid.NewGuid():N}"[..14];
        Assert.AreEqual(0, Cli("totp-password-123", "users", "add", name, "--password-stdin").Exit);
        await using var db = Data.PnmtdDbContext.Create(Data.DatabaseSettings.ConnectionString(TestDefaults.Configuration));
        await db.Users.Where(u => u.Username == name).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.IsTotpEnabled, true)
            .SetProperty(x => x.TotpSecretProtected, "protected")
            .SetProperty(x => x.TotpLastUsedStep, 42L)
            .SetProperty(x => x.LockoutEnd, DateTime.UtcNow.AddHours(1)));
        var stamp = await db.Users.Where(u => u.Username == name).Select(u => u.SecurityStamp).SingleAsync();

        var (exit, output) = Cli(null, "users", "reset-totp", name.ToUpperInvariant());

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, $"Turned 2FA off for {name}");
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Username == name);
        Assert.AreEqual((false, null, null, null), (user.IsTotpEnabled, user.TotpSecretProtected, user.TotpLastUsedStep, user.LockoutEnd));
        Assert.AreNotEqual(stamp, user.SecurityStamp, "the sessions end");
        Assert.IsTrue(await db.AuthLog.AnyAsync(l => l.UserId == user.Id && l.EventType == AuthEvents.TotpReset && l.Detail == "cli"));

        (exit, output) = Cli(null, "users", "reset-totp", name);
        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "had no 2FA");
        Assert.AreEqual(1, Cli(null, "users", "reset-totp", "no-such-user").Exit);
    }
}
