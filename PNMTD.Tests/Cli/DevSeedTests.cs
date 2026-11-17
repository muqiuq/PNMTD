using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PNMTD.Cli;

namespace PNMTD.Tests.Cli;

/// <summary>
/// `pnmtd dev seed` (§6.2): the browser tests and the dashboard's acceptance targets (§5.5) rely on its
/// shape: 50 hosts (and pve-01), 300 sensors (and the servers with an agent), exactly 6 sensors failing now in 3 hosts, and an
/// empty database only.
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

        // With Redmine and the fake Zabbix (docs/audit-redmine-zabbix-plan.md §11, F): their links and sensor too.
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redmine:Url"] = "http://fakes:7390/redmine", ["Redmine:ApiKey"] = PNMTD.Dev.FakeRedmine.Key, ["Redmine:Projects"] = "it-support network",
                ["Zabbix:Url"] = "http://fakes:7390/zabbix", ["Zabbix:ApiToken"] = PNMTD.Dev.FakeZabbix.Token,
            }).Build();
        Assert.AreEqual(0, DevSeedCommand.Run(["seed"], () => Data.PnmtdDbContext.Create(connection), NullLogger.Instance, configuration));

        // 50, and pve-01 for its Proxmox VE server (docs/linux-sensor-plan.md §12).
        Assert.AreEqual(51, await db.Hosts.CountAsync());
        // 300; two Windows servers with their diagnostics sensors (docs/windows-sensor-plan.md §11); two Linux servers, and web-01's
        // diagnostics sensor (docs/linux-sensor-plan.md §12).
        Assert.AreEqual(320, await db.Sensors.CountAsync(), "and nas-01's Zabbix sensor, seven SSL certificate sensors, and five advanced sensors");
        Assert.AreEqual(long.Parse(new PNMTD.Dev.FakeZabbix().HostId("nas01")), (await db.ZabbixSensors.SingleAsync()).ZabbixHostId);
        // SSL certificates (docs/ssl-certificate-plan.md §9): every state, the failing ones ignored, a private CA, a change waiting.
        Assert.AreEqual(7, await db.CertificateSensors.CountAsync());
        Assert.AreEqual("PNMT demo root", (await db.CertificateAuthorities.SingleAsync()).Name);
        CollectionAssert.AreEquivalent(new[] { 285, 285, 285, 485, 486, 487, 489 },
            await db.CertificateSensors.Select(c => c.LastCode!.Value).ToListAsync());
        Assert.IsTrue(await db.Sensors.Where(s => s.Type == Lib.Models.Enum.SensorType.SSL_CERTIFICATE && s.Ignore).CountAsync() == 4, "the four failing ones");
        Assert.IsTrue(await db.CertificateSensors.AnyAsync(c => c.CurrentVersionId != c.AcceptedVersionId), "vpn's change waits");
        // Advanced sensors (docs/advanced-sensor-plan.md §11): OK, warning, failing, names people, stale; the failing ones ignored.
        CollectionAssert.AreEquivalent(new[] { 295, 295, 296, 460, 495 }, await db.AdvancedSensors.Select(a => a.LastCode!.Value).ToListAsync());
        Assert.AreEqual(2, await db.Sensors.CountAsync(s => s.Type == Lib.Models.Enum.SensorType.ADVANCED && s.Ignore), "the failing ones");
        Assert.IsTrue(await db.AdvancedSensors.AnyAsync(a => a.NamesPeople));
        Assert.IsTrue(await db.AdvancedSamples.CountAsync() > 2_000, "a week of charted values");
        Assert.IsFalse(await db.AdvancedReports.AnyAsync(r => r.EvaluatedAt == null), "judged as the evaluator would");
        Assert.AreEqual(5, await db.RedmineLinks.CountAsync(), "a sensor, three hosts and a page");
        Assert.AreEqual(5, await db.RedmineIssues.CountAsync(), "with PNMT's copy");
        Assert.IsTrue(await db.RedmineLinks.AnyAsync(l => l.IssueId == 9 && db.Sensors.Any(x => x.Id == l.SensorId && x.Name == "disk /")),
            "a failing sensor's ticket: its chip on the dashboard");
        Assert.AreEqual(global::PNMTD.Audit.AuditCatalog.All.Select(e => e.Category).Distinct().Count(), await db.AuditLog.Select(e => e.Category).Distinct().CountAsync(),
            "an audit entry of every category");
        Assert.IsTrue(await db.AuditLog.AnyAsync(e => e.Outcome == global::PNMTD.Audit.AuditOutcome.Denied));
        Assert.AreEqual(4, await db.Agents.CountAsync());
        Assert.AreEqual(2, await db.Agents.CountAsync(a => a.Platform == Lib.Agent.AgentPlatform.Linux));
        Assert.AreEqual(2 * Lib.Agent.SectionNames.Windows.Count + 2 * Lib.Agent.SectionNames.Linux.Count, await db.AgentSections.CountAsync());
        Assert.IsTrue(await db.AgentLogins.AnyAsync(l => l.KeyFingerprint != null && l.Kind == Lib.Agent.LogonKind.Ssh), "SSH logins with keys");
        Assert.IsTrue(await db.AgentLogins.AnyAsync(l => l.Kind == Lib.Agent.LogonKind.Sudo && l.TargetUser == "root"), "sudo");
        Assert.IsTrue(await db.AgentLogins.AnyAsync(l => l.FailureReason == Lib.Agent.LinuxLogins.InvalidUser && l.TriedUsers != null), "the scanners");
        // The key manager (docs/linux-sensor-plan.md §12): the demo users (the first admin before them), their keys and two services',
        // Jan's retired key still authorized on web-01, an unregistered weak key, a key unused for 200 days, three removals, bash scripts.
        Assert.IsTrue(await db.Users.AnyAsync(u => u.NormalizedUsername == "admin" && u.IsAdmin), "the first admin, as the app would create it");
        Assert.IsTrue(await db.Users.AnyAsync(u => u.NormalizedUsername == "jdoe" && !u.IsActive));
        Assert.AreEqual(5, await db.SshKeys.CountAsync());
        Assert.AreEqual(2, await db.SshKeys.CountAsync(k => k.UserId != null && k.Name.StartsWith("Philipp")));
        var retired = await db.SshKeys.SingleAsync(k => k.State == Models.Db.SshKeyState.Retired);
        Assert.IsTrue(await db.SshKeyAuthorizations.AnyAsync(a => a.Fingerprint == retired.Fingerprint), "retired, still authorized");
        Assert.IsTrue(await db.SshKeyAuthorizations.AnyAsync(a => a.Type == "ssh-rsa" && a.Bits == 1024 && !db.SshKeys.Any(k => k.Fingerprint == a.Fingerprint)),
            "an unregistered weak key");
        Assert.IsTrue(await db.SshKeyAuthorizations.AnyAsync(a => a.LastUsedAt < DateTime.UtcNow.AddDays(-180)), "unused for 200 days");
        CollectionAssert.AreEquivalent(new[] { Models.Db.SshKeyRemovalState.Removed, Models.Db.SshKeyRemovalState.Refused, Models.Db.SshKeyRemovalState.Pending },
            await db.SshKeyRemovals.Select(r => r.State).ToListAsync());
        Assert.AreEqual(2, await db.AgentScripts.CountAsync(s => s.Language == Lib.Agent.ScriptLanguage.Bash));
        Assert.AreEqual(3, await db.DiagnosticAssignments.CountAsync());
        Assert.IsTrue(await db.DiagnosticRuns.AnyAsync(r => r.Outcome == Lib.Agent.DiagnosticOutcome.Fail), "the Basel gateway's half hour");
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
        // Locations and the knowledge base (docs/locations-knowledge-plan.md K7), which the browser tests use: two sites, one with two
        // sub-locations and a level below; hosts assigned, and others with only their texts for From the hosts' texts…
        Assert.AreEqual(5, await db.Locations.CountAsync());
        Assert.AreEqual(2, await db.Locations.CountAsync(l => l.ParentId == null));
        Assert.IsTrue(await db.Locations.AnyAsync(l => l.Name == "Rack B4" && l.Parent!.Name == "Cage B" && l.Parent.Parent!.Name == "Zurich DC"));
        Assert.IsGreaterThanOrEqualTo(20, await db.Hosts.CountAsync(h => h.LocationId != null));
        Assert.IsTrue(await db.Hosts.AnyAsync(h => h.LocationId == null && h.Location == "Lugano office"), "a text for From the hosts' texts…");
        // The typical fields with values: Markdown, inherited, sensitive, and one overridden in a sub-location.
        Assert.IsGreaterThanOrEqualTo(12, await db.LocationFields.CountAsync());
        Assert.IsTrue(await db.LocationValues.AnyAsync(v => v.Field!.Sensitive && v.Value != null));
        Assert.IsTrue(await db.LocationValues.AnyAsync(v => v.Field!.Inherited && db.Locations.Any(l => l.Id == v.LocationId && l.ParentId != null)),
            "an inherited field overridden");
        // About twenty pages: statuses, one sensitive and one archived, revisions with notes, links, views over the last 30 days.
        Assert.IsGreaterThanOrEqualTo(20, await db.KbPages.CountAsync());
        Assert.AreEqual(1, await db.KbPages.CountAsync(p => p.Sensitive));
        Assert.AreEqual(1, await db.KbPages.CountAsync(p => p.ArchivedAt != null));
        Assert.AreEqual(3, await db.KbPages.Select(p => p.Status).Distinct().CountAsync());
        Assert.IsGreaterThanOrEqualTo(10, await db.KbRevisions.CountAsync(r => r.Number > 1 && r.Note != null));
        Assert.IsTrue(await db.KbLinks.AnyAsync(l => l.SensorId != null) && await db.KbLinks.AnyAsync(l => l.LocationId != null));
        Assert.IsTrue(await db.KbPageRefs.AnyAsync(), "pages linking pages");
        Assert.IsGreaterThan(10, await db.KbPageViews.Select(v => v.Day).Distinct().CountAsync());
        Assert.IsTrue(await db.KbPages.AnyAsync(p => p.Content.Contains("```mermaid")));
        // An image on a page and one on a location, an attachment: through the store, so they are in the files folder.
        var files = await db.Files.ToListAsync();
        Assert.AreEqual(2, files.Count(f => f.Kind == Models.Db.FileKind.Image));
        Assert.AreEqual(1, files.Count(f => f.Kind == Models.Db.FileKind.Attachment));
        var folder = Environment.GetEnvironmentVariable("KnowledgeBase__FilesPath")!;
        Assert.IsTrue(files.All(f => new FileInfo(Path.Combine(folder, f.Path)).Length == f.Size), "the files on disk");
        Assert.AreEqual(files.Count, await db.FileUses.Select(u => u.FileId).Distinct().CountAsync(), "every file used");

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
