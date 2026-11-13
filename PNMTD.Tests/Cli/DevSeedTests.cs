using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PNMTD.Cli;

namespace PNMTD.Tests.Cli;

/// <summary>
/// `pnmtd dev seed` (§6.2): the browser tests and the dashboard's acceptance targets (§5.5) rely on its
/// shape: 50 hosts, 300 sensors, exactly 6 sensors failing now in 3 hosts, and an empty database only.
/// </summary>
[TestClass]
public sealed class DevSeedTests
{
    [TestMethod]
    public async Task TheSeedFillsAnEmptyDatabaseOnceWithSixFailingSensorsInThreeHosts()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        await using var db = TestDatabases.Fresh("pnmtd_seed_cli_test");
        var connection = TestDatabases.ConnectionString("pnmtd_seed_cli_test");

        Assert.AreEqual(0, DevSeedCommand.Run(["seed"], () => Data.PnmtdDbContext.Create(connection), NullLogger.Instance));

        Assert.AreEqual(50, await db.Hosts.CountAsync());
        Assert.AreEqual(300, await db.Sensors.CountAsync());
        Assert.IsGreaterThan(150_000, await db.Events.CountAsync());
        // UDP nodes (docs/udp-iot-plan.md U6): four fan sensors became nodes, with the real codec's packets.
        Assert.AreEqual(4, await db.UdpNodes.CountAsync());
        Assert.AreEqual(4, await db.Sensors.CountAsync(s => s.Type == Lib.Models.Enum.SensorType.UDP_NODE));
        Assert.AreEqual(8, await db.UdpLastPackets.CountAsync());
        Assert.IsGreaterThanOrEqualTo(16, await db.UdpCommandLog.CountAsync());
        Assert.AreEqual(1, await db.UdpFirmware.CountAsync());
        // MikroTik routers (docs/mikrotik-plan.md M7): the office gateways, with backups that differ, an update and commands.
        Assert.AreEqual(5, await db.MikrotikRouters.CountAsync());
        Assert.IsTrue(await db.MikrotikRouters.AllAsync(r => r.Reading != null && r.SetupState == Models.Enums.MikrotikSetupState.Ready));
        Assert.IsGreaterThanOrEqualTo(11, await db.MikrotikBackups.CountAsync());
        Assert.IsTrue(await db.MikrotikBackups.AnyAsync(b => b.LinesAdded == 1 && b.LinesRemoved == 1), "the admin command's change");
        Assert.AreEqual(1, await db.MikrotikJobs.CountAsync(j => j.Kind == Models.Enums.MikrotikJobKind.Update));
        Assert.AreEqual(7, await db.MikrotikJobs.CountAsync(j => j.Kind == Models.Enums.MikrotikJobKind.Command));
        foreach (var packet in await db.UdpLastPackets.ToListAsync())
        {
            Assert.IsTrue(Lib.Udp.PacketCodec.TryParse(packet.Datagram, out _, out _));
        }
        // Failing now: the newest event above 399, on an enabled, not ignored sensor of an enabled host.
        var failing = await db.Database.SqlQueryRaw<string>("""
            SELECT h."Name" || '/' || s."Name" AS "Value"
            FROM sensors s JOIN hosts h ON h."Id" = s."ParentId"
            CROSS JOIN LATERAL (SELECT e."Code" FROM events e WHERE e."SensorId" = s."Id" ORDER BY e."Created" DESC LIMIT 1) last
            WHERE s."Enabled" AND NOT s."Ignore" AND h."Enabled" AND last."Code" > 399
            ORDER BY 1
            """).ToListAsync();
        CollectionAssert.AreEqual(
            new[] { "backup-srv/backup", "backup-srv/disk /", "fw-01/ping", "web-03/cert", "web-03/http", "web-03/load" }, failing);
        Assert.IsTrue(await db.MailLogs.AllAsync(m => m.Processed), "the mail task must find nothing to do");

        Assert.AreEqual(1, DevSeedCommand.Run(["seed"], () => Data.PnmtdDbContext.Create(connection), NullLogger.Instance),
            "only into a database without hosts");
    }

    [TestMethod]
    public void TheSeedRunsInDevelopmentOnly()
    {
        var before = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Assert.AreEqual(1, DevSeedCommand.Run(["seed"], () => throw new InvalidOperationException("no database expected"), NullLogger.Instance));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", before);
        }
    }
}
