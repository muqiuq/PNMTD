using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Tests;

/// <summary>
/// Once per test run: a fresh PostgreSQL database (pnmtd_test) on the server PNMTD_TEST_DB names, by
/// default the one tools/test-db.sh starts, and the settings the tests need.
/// </summary>
[TestClass]
public static class TestDefaults
{
    public const string DefaultServer = "Host=127.0.0.1;Port=55432;Username=postgres;Password=test";

    [AssemblyInitialize]
    public static void Apply(TestContext _)
    {
        // DbTestHelper's seed requires these unless user secrets give others.
        SetIfMissing("Development__Email", "root@localhost");
        SetIfMissing("Development__HttpNotificationUrl", "http://localhost");
        SetIfMissing("Development__DoNotSendNotifications", "true");
        // Warnings and errors only: the Development settings log every SQL command.
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command", "Warning");
        // Many sign-ins per minute from one address; the rate limit test sets its own.
        Environment.SetEnvironmentVariable("Auth__LoginRateLimit__PermitLimit", "100000");
        // The monitoring tasks would add events under the tests' feet.
        Environment.SetEnvironmentVariable("BackgroundTasks__Enabled", "false");
        // The UDP listener only where a test asks for it, on a free port (Udp:Enabled, Udp:Port 0).
        Environment.SetEnvironmentVariable("Udp__Enabled", "false");
        Environment.SetEnvironmentVariable("Database__ConnectionString", CreateDatabase());
        // Not UTC by default: several time checks are vacuous in UTC (PNMTD_TEST_TZ picks another zone).
        Clock.TimeZone = Clock.FromName(Environment.GetEnvironmentVariable("PNMTD_TEST_TZ") ?? "Europe/Zurich");

        var scratch = Directory.CreateTempSubdirectory("pnmtd-tests-");
        Environment.SetEnvironmentVariable("DataProtection__KeysPath", Path.Combine(scratch.FullName, "keys"));
        WebRoot = CreateWebRoot(scratch.FullName);

        Configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        // Migrated and seeded once, before any app starts (DbTestHelper's hosts, sensors and events).
        using var db = PnmtdDbContext.Create(DatabaseSettings.ConnectionString(Configuration));
        db.Database.Migrate();
        DbTestHelper.Populate(db, Configuration);
        TestUsers.Seed(db);
    }

    public static IConfiguration Configuration { get; private set; } = null!;

    /// <summary>A stand-in for the UI build: index.html and one hashed asset under pnmt-admin/.</summary>
    public static string WebRoot { get; private set; } = null!;

    public const string IndexHtml = "<!doctype html><html><head><script type=\"module\" src=\"/pnmt-admin/assets/app-3f9a.js\"></script></head><body><div id=\"app\"></div></body></html>";

    private static string CreateWebRoot(string scratch)
    {
        var root = Path.Combine(scratch, "wwwroot");
        Directory.CreateDirectory(Path.Combine(root, "pnmt-admin", "assets"));
        File.WriteAllText(Path.Combine(root, "pnmt-admin", "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(root, "pnmt-admin", "assets", "app-3f9a.js"), "console.log('pnmt');");
        return root;
    }

    private static string CreateDatabase()
    {
        var server = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PNMTD_TEST_DB") ?? DefaultServer)
        {
            Database = "postgres",
            Pooling = false,
        };
        try
        {
            using var connection = new NpgsqlConnection(server.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP DATABASE IF EXISTS pnmtd_test WITH (FORCE); CREATE DATABASE pnmtd_test;";
            command.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException(
                $"No PostgreSQL for the tests at {server.Host}:{server.Port}: run tools/test-db.sh, or set PNMTD_TEST_DB. ({ex.Message})", ex);
        }
        server.Database = "pnmtd_test";
        server.Pooling = true;
        return server.ConnectionString;
    }

    private static void SetIfMissing(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
