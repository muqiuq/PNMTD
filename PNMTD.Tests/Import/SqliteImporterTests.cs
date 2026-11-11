using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PNMTD.Data;
using PNMTD.Import;
using PNMTD.Lib.Helper;

namespace PNMTD.Tests.Import;

/// <summary>
/// The one-time move from v0.12 (docs/webui-migration-plan.md §3.4, §8.4), against
/// Fixtures/v0.12-sample.db: a database a real v0.12.1 wrote (tools/contract.sh make-import-fixture),
/// with data in every table, plus two mail log rows on the edges of Zurich's DST switches.
/// Every test imports into a database of its own.
/// </summary>
[TestClass]
public sealed class SqliteImporterTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "v0.12-sample.db");

    private static PnmtdDbContext FreshDatabase(string name)
    {
        var server = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PNMTD_TEST_DB") ?? TestDefaults.DefaultServer)
        {
            Database = "postgres",
            Pooling = false,
        };
        using (var connection = new NpgsqlConnection(server.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {name} WITH (FORCE); CREATE DATABASE {name};";
            command.ExecuteNonQuery();
        }
        server.Database = name;
        var db = PnmtdDbContext.Create(server.ConnectionString);
        DatabaseMigrator.Migrate(db, NullLogger.Instance);
        return db;
    }

    private static ImportReport Import(PnmtdDbContext db, string? path = null, string? zone = "Europe/Zurich", bool reset = false) =>
        new SqliteImporter(db, NullLogger<SqliteImporter>.Instance).Import(new SqliteImportOptions
        {
            SqlitePath = path ?? FixturePath,
            SourceTimeZone = zone,
            Reset = reset,
        });

    private static long SqliteCount(string table)
    {
        using var connection = new SqliteConnection($"Data Source={FixturePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return (long)command.ExecuteScalar()!;
    }

    [TestMethod]
    public void ImportsEveryTableAndConvertsTimes()
    {
        using var db = FreshDatabase("pnmtd_import_all");
        var report = Import(db);

        Assert.IsTrue(report.Succeeded, report.ToText());
        Assert.AreEqual(SqliteCount("events"), db.Events.LongCount());
        Assert.AreEqual(SqliteCount("sensors"), db.Sensors.LongCount());
        Assert.AreEqual(SqliteCount("NotificationRuleSensor"), db.NotificationRuleSensor.LongCount());
        Assert.AreEqual(SqliteCount("notificationruleevent"), db.NotificationRuleEvents.LongCount());
        Assert.AreEqual(SqliteCount("dnszoneentries"), db.DnsZoneEntries.LongCount());
        Assert.AreEqual(SqliteCount("dnszonelogentries"), db.DnsZoneLogEntries.LongCount());
        Assert.AreEqual(SqliteCount("maillogs"), db.MailLogs.LongCount());
        Assert.IsTrue(db.Events.Any() && db.NotificationRuleEvents.Any() && db.DnsZoneEntries.Any(), "the fixture covers every table");

        // 2026-03-29 01:30 in Zurich is CET (+1), half an hour before the switch to summer time.
        var beforeSpring = db.MailLogs.Single(m => m.RemoteId == "imap-1");
        Assert.AreEqual(new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc).AddTicks(1234560), beforeSpring.Created);
        Assert.AreEqual(DateTimeKind.Utc, beforeSpring.Created.Kind);
        // 2026-10-25 02:30 happens twice in Zurich; .NET reads an ambiguous time as standard time (+1).
        var autumn = db.MailLogs.Single(m => m.RemoteId == "imap-2");
        Assert.AreEqual(new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc), autumn.Created);
        // v0.12's "never" stays never.
        Assert.AreEqual(DateTime.MinValue, autumn.MessageDate);
        Assert.AreEqual(beforeSpring.ProcessedById, db.MailInputs.Single().Id);

        // LAST_* key values are ISO 8601 UTC now; other values are left alone.
        var lastDnsCheck = db.KeyValues.Single(k => k.Key == "last_dns_check").Value;
        StringAssert.EndsWith(lastDnsCheck, "Z");
        Assert.AreEqual("kept as is", db.KeyValues.Single(k => k.Key == "contract_note").Value);
        Assert.AreEqual("9.9.9.9", db.KeyValues.Single(k => k.Key == "dns_servers").Value);

        // The tokens are untouched, duplicates included (§3.3).
        Assert.AreEqual(2, db.Sensors.Count(s => s.Name == "dupA" || s.Name == "dupB"));
        Assert.AreEqual(1, db.Sensors.Where(s => s.Name == "dupA" || s.Name == "dupB").Select(s => s.SecretWriteToken).Distinct().Count());
        Assert.IsTrue(report.Warnings.Any(w => w.Contains("dupA, dupB")), report.ToText());
    }

    [TestMethod]
    public void SourceTimeZoneShiftsEveryTimestamp()
    {
        using var zurich = FreshDatabase("pnmtd_import_zurich");
        using var utc = FreshDatabase("pnmtd_import_utc");
        Assert.IsTrue(Import(zurich).Succeeded);
        Assert.IsTrue(Import(utc, zone: null).Succeeded);

        var inZurich = zurich.Events.OrderBy(e => e.Id).Select(e => new { e.Id, e.Created }).ToList();
        var inUtc = utc.Events.OrderBy(e => e.Id).Select(e => new { e.Id, e.Created }).ToList();
        CollectionAssert.AreEqual(inUtc.Select(e => e.Id).ToList(), inZurich.Select(e => e.Id).ToList());
        foreach (var (z, u) in inZurich.Zip(inUtc))
        {
            var offset = Clock.FromName("Europe/Zurich").GetUtcOffset(DateTime.SpecifyKind(u.Created, DateTimeKind.Unspecified));
            Assert.AreEqual(u.Created - offset, z.Created);
        }
    }

    [TestMethod]
    public void RefusesAnImportIntoData_ResetReplacesIt()
    {
        using var db = FreshDatabase("pnmtd_import_twice");
        Assert.IsTrue(Import(db).Succeeded);
        var events = db.Events.LongCount();

        var again = Import(db);
        Assert.IsFalse(again.Succeeded);
        StringAssert.Contains(again.Failure, "already holds data");
        Assert.AreEqual(events, db.Events.LongCount());

        var reset = Import(db, reset: true);
        Assert.IsTrue(reset.Succeeded, reset.ToText());
        Assert.AreEqual(events, db.Events.LongCount());
    }

    [TestMethod]
    public void RefusesAnotherSchema()
    {
        var copy = Path.Combine(Path.GetTempPath(), $"pnmtd-import-schema-{Guid.NewGuid():N}.db");
        File.Copy(FixturePath, copy);
        try
        {
            using (var connection = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM __EFMigrationsHistory WHERE MigrationId = '20250404182653_DoneZoneHostLink'";
                command.ExecuteNonQuery();
            }
            using var db = FreshDatabase("pnmtd_import_schema");
            var report = Import(db, copy);
            Assert.IsFalse(report.Succeeded);
            StringAssert.Contains(report.Failure, "v0.12.x");
            Assert.AreEqual(0, db.Sensors.LongCount());
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [TestMethod]
    public void RefusesAnUnknownZoneAndAMissingFile()
    {
        using var db = FreshDatabase("pnmtd_import_bad");
        StringAssert.Contains(Import(db, zone: "Mars/Olympus").Failure, "Unknown time zone");
        StringAssert.Contains(Import(db, path: "/nonexistent/pnmtd.db").Failure, "does not exist");
        Assert.AreEqual(0, db.Hosts.LongCount());
    }
}
