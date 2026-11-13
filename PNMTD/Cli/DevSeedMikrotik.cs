using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Cli;

/// <summary>
/// The seed's MikroTik routers (docs/mikrotik-plan.md §13, M7): one per office gateway, in Direct SSH mode at
/// TEST-NET addresses, with what PNMTD would have read and stored: a reading, backups with real diffs, events,
/// an update with its log, and commands. Polled daily, so the demo stays as seeded while the stack runs.
/// The WireGuard modes and custom tunnels need the key ring, which the seed has not: add them by hand.
/// </summary>
internal static class DevSeedMikrotik
{
    private sealed record RouterPlan(string Host, string Identity, string Model, string Serial, string Version, string Address, int Uptime);

    private static readonly RouterPlan[] Plans =
    [
        new("office-basel-gw", "basel-gw", "RB5009UG+S+", "HE80A1B2C3D", "7.25.1", "192.0.2.21", 18),
        new("office-bern-gw", "bern-gw", "RB5009UG+S+", "HE80A4E5F6A", "7.25.1", "192.0.2.22", 41),
        new("office-geneva-gw", "geneva-gw", "hAP ax3", "HF10B7C8D9E", "7.25.1", "192.0.2.23", 3),
        new("office-lugano-gw", "lugano-gw", "hAP ax3", "HF10BAB1C2D", "7.24.2", "192.0.2.24", 62),
        new("office-st-gallen-gw", "st-gallen-gw", "CCR2004-1G-12S+2XS", "HD20C3D4E5F", "7.25.1", "192.0.2.25", 27),
    ];

    public static int Seed(PnmtdDbContext db, IReadOnlyDictionary<string, HostEntity> hosts, DateTime now)
    {
        var random = new Random(7);
        db.MikrotikSettings.Add(new MikrotikSettingsEntity
        {
            Tag = "d3m0",
            LatestVersions = """{"stable":{"version":"7.25.1","releasedAt":null},"long-term":{"version":"7.20.8","releasedAt":null}}""",
            VersionsCheckedAt = now.AddHours(-2),
        });
        var routers = new List<(RouterPlan Plan, MikrotikRouterEntity Router)>();
        var n = 0;
        foreach (var plan in Plans)
        {
            var sensor = new SensorEntity
            {
                Id = Guid.NewGuid(), ParentId = hosts[plan.Host].Id, Type = SensorType.MIKROTIK, Name = "router",
                SecretWriteToken = $"demo-mt-w-{++n}-{Guid.NewGuid():N}"[..32], SecretReadToken = $"demo-mt-r-{n}-{Guid.NewGuid():N}"[..32],
                Created = now.AddDays(-60), Enabled = true, Interval = 86400, GracePeriod = 2 * 86400,
            };
            db.Sensors.Add(sensor);
            var reading = Reading(plan, random);
            var router = new MikrotikRouterEntity
            {
                SensorId = sensor.Id, Mode = MikrotikMode.DirectSsh, Address = plan.Address, SshPort = 22, PnmtSourceAddress = "203.0.113.7",
                BackupIntervalHours = 24, SetupState = MikrotikSetupState.Ready, ScriptChangedAt = now.AddDays(-60), ScriptGeneratedAt = now.AddDays(-60),
                HostKeyType = "ssh-ed25519", HostKeyFingerprint = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('='),
                HostKeyPinnedAt = now.AddDays(-60), Identity = reading.Identity, Model = reading.Model, BoardName = reading.BoardName,
                SerialNumber = reading.SerialNumber, Architecture = reading.Architecture, Version = reading.VersionText, Channel = "stable",
                FirmwareCurrent = reading.FirmwareCurrent, FirmwareUpgrade = reading.FirmwareUpgrade, BootedAt = now.AddDays(-plan.Uptime),
                Reading = JsonSerializer.Serialize(reading, VersionCatalog.Json), LastPollAt = now.AddMinutes(-4), LastSuccessAt = now.AddMinutes(-4),
                NotifiedUpdateVersion = plan.Version == "7.25.1" ? null : "7.25.1", Created = now.AddDays(-60),
            };
            db.MikrotikRouters.Add(router);
            routers.Add((plan, router));
        }
        db.SaveChanges();

        foreach (var (plan, router) in routers)
        {
            router.Sensor = db.Sensors.Single(s => s.Id == router.SensorId);
            // A week of polls every 3 hours, as 240 with the reading's summary.
            for (var h = 7 * 24; h >= 0; h -= 3)
            {
                // geneva ran 7.24.2 until its update three days ago.
                var version = plan.Host == "office-geneva-gw" && h > 72 ? "7.24.2" : plan.Version;
                Event(db, router, now.AddHours(-h).AddMinutes(-4), PNMTStatusCodes.MIKROTIK_OK, $"RouterOS {version} (stable) · {plan.Model}");
            }
            // The configuration's history: first login, a firewall rule added at the router, a DNS change.
            Export(db, router, plan, now.AddDays(-60), MikrotikBackupTrigger.FirstLogin, Config(plan, winbox: false, dns: "1.1.1.1,9.9.9.9"));
            Export(db, router, plan, now.AddDays(-20), MikrotikBackupTrigger.Schedule, Config(plan, winbox: true, dns: "1.1.1.1,9.9.9.9"));
            Event(db, router, now.AddDays(-20), PNMTStatusCodes.MIKROTIK_CONFIG_CHANGED, "Configuration changed: +1 −0 lines (version 2)");
            if (plan.Version != "7.25.1")
            {
                Event(db, router, now.AddDays(-6), PNMTStatusCodes.MIKROTIK_UPDATE_AVAILABLE, $"7.25.1 available on stable ({plan.Version} installed)");
            }
        }

        SeedUpdate(db, routers.Single(r => r.Plan.Host == "office-geneva-gw"), now, random);
        SeedCommands(db, routers, now);
        db.SaveChanges();
        return routers.Count;
    }

    /// <summary>geneva went from 7.24.2 to 7.25.1 three days ago, with every step in its log.</summary>
    private static void SeedUpdate(PnmtdDbContext db, (RouterPlan Plan, MikrotikRouterEntity Router) target, DateTime now, Random random)
    {
        var (plan, router) = target;
        var at = now.AddDays(-3);
        var job = new MikrotikJobEntity
        {
            Id = Guid.NewGuid(), BatchId = Guid.NewGuid(), Kind = MikrotikJobKind.Update, SensorId = router.SensorId, RouterName = $"{plan.Host}/router",
            State = MikrotikJobState.Succeeded, Step = MikrotikJobStep.Done, RequestedByName = "admin", AdminUsername = "admin", CreatedAt = at,
            StartedAt = at, FinishedAt = at.AddSeconds(118), FromVersion = "7.24.2", ToVersion = "7.25.1", Channel = "stable",
            Options = """{"binaryBackup":true,"firmware":true,"stopAtFirstFailure":true}""",
        };
        db.MikrotikJobs.Add(job);
        db.SaveChanges();
        var content = new byte[48 * 1024];
        random.NextBytes(content);
        db.MikrotikBackups.Add(new MikrotikBackupEntity
        {
            Id = Guid.NewGuid(), SensorId = router.SensorId, RouterName = router.Sensor.Name, Number = 3, Kind = MikrotikBackupKind.Binary,
            Trigger = MikrotikBackupTrigger.BeforeUpdate, JobId = job.Id, CreatedAt = at.AddSeconds(4), CreatedByName = "admin", RouterOsVersion = "7.24.2",
            Content = content, Size = content.Length, ContentHash = ExportDigest.Hash(SeedDigestKey, content), Encrypted = true,
        });
        var steps = new (int Seconds, MikrotikJobStep Step, MikrotikJobOutcome Outcome, string Message)[]
        {
            (0, MikrotikJobStep.Check, MikrotikJobOutcome.Started, "Reading the router as pnmt"),
            (1, MikrotikJobStep.Check, MikrotikJobOutcome.Ok, $"{plan.Identity} ({plan.Model}, {plan.Serial}): RouterOS 7.24.2 → 7.25.1"),
            (1, MikrotikJobStep.Export, MikrotikJobOutcome.Started, "Exporting the configuration"),
            (3, MikrotikJobStep.Export, MikrotikJobOutcome.Ok, "The configuration is unchanged since the last backup."),
            (3, MikrotikJobStep.BinaryBackup, MikrotikJobOutcome.Started, "Saving a binary backup"),
            (6, MikrotikJobStep.BinaryBackup, MikrotikJobOutcome.Ok, "Stored as version 3 (48 KiB), encrypted with admin's password."),
            (6, MikrotikJobStep.Download, MikrotikJobOutcome.Started, "Downloading RouterOS 7.25.1 (stable)"),
            (31, MikrotikJobStep.Download, MikrotikJobOutcome.Ok, "Downloaded, please reboot router to upgrade it"),
            (31, MikrotikJobStep.Reboot, MikrotikJobOutcome.Started, "Rebooting for the update"),
            (31, MikrotikJobStep.Wait, MikrotikJobOutcome.Started, "Waiting for the router"),
            (112, MikrotikJobStep.Wait, MikrotikJobOutcome.Ok, "Back after 81 s"),
            (112, MikrotikJobStep.Verify, MikrotikJobOutcome.Started, "Checking the version"),
            (113, MikrotikJobStep.Verify, MikrotikJobOutcome.Ok, "RouterOS 7.25.1"),
            (113, MikrotikJobStep.Firmware, MikrotikJobOutcome.Info, "The firmware is current."),
            (118, MikrotikJobStep.Done, MikrotikJobOutcome.Ok, "Updated 7.24.2 → 7.25.1"),
        };
        foreach (var s in steps)
        {
            db.MikrotikJobLog.Add(new MikrotikJobLogEntity { JobId = job.Id, At = at.AddSeconds(s.Seconds), Step = s.Step, Outcome = s.Outcome, Message = s.Message });
        }
        Event(db, router, at.AddSeconds(118), PNMTStatusCodes.MIKROTIK_RESTARTED, $"Restarted at {Clock.ToLocal(at.AddSeconds(100)):HH:mm} (RouterOS 7.25.1)");
        Event(db, router, at.AddSeconds(118), PNMTStatusCodes.MIKROTIK_UPDATED, "Updated 7.24.2 → 7.25.1");
    }

    /// <summary>A read-only batch over every router, one refused change, and an admin change with its diff.</summary>
    private static void SeedCommands(PnmtdDbContext db, List<(RouterPlan Plan, MikrotikRouterEntity Router)> routers, DateTime now)
    {
        var batch = Guid.NewGuid();
        var at = now.AddDays(-1);
        foreach (var ((plan, router), i) in routers.Select((r, i) => (r, i)))
        {
            var lan = 20 + i;
            db.MikrotikJobs.Add(Command(router, batch, at.AddMicroseconds(i), "/ip address print", null, MikrotikJobState.Succeeded, 0,
                "Flags: D - DYNAMIC\nColumns: ADDRESS, NETWORK, INTERFACE\n#   ADDRESS            NETWORK       INTERFACE\n" +
                $"0   192.168.{lan}.1/24    192.168.{lan}.0  bridge\n1 D {plan.Address}/24     192.0.2.0     ether1\n"));
        }
        var lugano = routers.Single(r => r.Plan.Host == "office-lugano-gw").Router;
        db.MikrotikJobs.Add(Command(lugano, Guid.NewGuid(), now.AddHours(-20), "/ip dns set servers=1.1.1.1,8.8.8.8", null, MikrotikJobState.Failed, 1,
            "not enough permissions (9)\n", "not enough permissions (9)"));

        // bern: a DNS change as the admin, backed up before and after.
        var (bernPlan, bern) = routers.Single(r => r.Plan.Host == "office-bern-gw");
        var change = Command(bern, Guid.NewGuid(), now.AddHours(-19), "/ip dns set servers=1.1.1.1,8.8.8.8\n:put [/ip dns get servers]", "admin",
            MikrotikJobState.Succeeded, 0, "1.1.1.1;8.8.8.8\n");
        change.Options = """{"backupBeforeAndAfter":true,"oneAtATime":true}""";
        db.MikrotikJobs.Add(change);
        db.SaveChanges();
        change.BackupBeforeId = db.MikrotikBackups.Where(b => b.SensorId == bern.SensorId && b.Kind == MikrotikBackupKind.Export)
            .OrderByDescending(b => b.Number).Select(b => b.Id).First();
        var after = Export(db, bern, bernPlan, change.CreatedAt.AddSeconds(3), MikrotikBackupTrigger.AfterCommand,
            Config(bernPlan, winbox: true, dns: "1.1.1.1,8.8.8.8"), change.Id);
        change.BackupAfterId = after;
        Event(db, bern, change.CreatedAt.AddSeconds(3), PNMTStatusCodes.MIKROTIK_CONFIG_CHANGED,
            "Configuration changed by a command of admin as admin: +1 −1 lines (version 3)");
        foreach (var (s, step, outcome, message) in new[]
        {
            (0, MikrotikJobStep.Check, MikrotikJobOutcome.Info, "The password of admin was accepted; its group is full (ssh, read, write, policy, reboot, test, sensitive, ftp, …)."),
            (0, MikrotikJobStep.Export, MikrotikJobOutcome.Ok, "Unchanged since the last backup."),
            (1, MikrotikJobStep.Run, MikrotikJobOutcome.Ok, "Done (0.1 s, exit status 0, 16 bytes of output)"),
            (3, MikrotikJobStep.ExportAfter, MikrotikJobOutcome.Ok, "Stored as version 3: +1 −1 lines."),
            (3, MikrotikJobStep.Done, MikrotikJobOutcome.Ok, "Succeeded"),
        })
        {
            db.MikrotikJobLog.Add(new MikrotikJobLogEntity { JobId = change.Id, At = change.CreatedAt.AddSeconds(s), Step = step, Outcome = outcome, Message = message });
        }
    }

    private static MikrotikJobEntity Command(MikrotikRouterEntity router, Guid batch, DateTime at, string text, string? admin, MikrotikJobState state,
        int exit, string output, string? error = null) => new()
    {
        Id = Guid.NewGuid(), BatchId = batch, Kind = MikrotikJobKind.Command, SensorId = router.SensorId, RouterName = $"{router.Sensor.Parent?.Name ?? "?"}/router",
        State = state, Step = MikrotikJobStep.Done, RequestedByName = "admin", AdminUsername = admin, CreatedAt = at, StartedAt = at,
        FinishedAt = at.AddSeconds(admin is null ? 1 : 3), Command = text, RunAs = admin ?? RouterPoll.UserName, TimeoutSeconds = 60, ExitStatus = exit,
        Output = output, Error = error, Options = """{"backupBeforeAndAfter":false,"oneAtATime":false}""",
    };

    private static readonly byte[] SeedDigestKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private static Guid? Export(PnmtdDbContext db, MikrotikRouterEntity router, RouterPlan plan, DateTime at, MikrotikBackupTrigger trigger, string config,
        Guid? jobId = null)
    {
        var raw = $"# {Clock.ToLocal(at):yyyy-MM-dd HH:mm:ss} by RouterOS {plan.Version}\n# software id = DEMO-{plan.Serial[^4..]}\n#\n" +
                  $"# model = {plan.Model}\n# serial number = {plan.Serial}\n" + config;
        // The seed has no Data Protection: its digests use a key of its own. A seeded router that ever answered would
        // count its first real export as a change once.
        var stored = BackupStore.SaveExportAsync(db, router, router.Sensor.Name, raw, false, trigger, null, "admin", at, SeedDigestKey, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (stored.Backup is { } b)
        {
            b.JobId = jobId;
        }
        db.SaveChanges();
        return stored.Backup?.Id;
    }

    private static string Config(RouterPlan plan, bool winbox, string dns)
    {
        var lan = 20 + Array.IndexOf(Plans, plan);
        var lines = new List<string>
        {
            "/interface bridge add admin-mac=48:A9:8A:00:00:01 auto-mac=no comment=defconf name=bridge",
            "/interface ethernet set [ find default-name=ether1 ] comment=WAN",
            "/interface list add comment=defconf name=WAN",
            "/interface list add comment=defconf name=LAN",
            $"/ip pool add name=dhcp ranges=192.168.{lan}.10-192.168.{lan}.254",
            "/ip dhcp-server add address-pool=dhcp interface=bridge name=defconf",
            "/interface bridge port add bridge=bridge comment=defconf interface=ether2",
            "/interface bridge port add bridge=bridge comment=defconf interface=ether3",
            "/interface list member add comment=defconf interface=bridge list=LAN",
            "/interface list member add comment=defconf interface=ether1 list=WAN",
            $"/ip address add address=192.168.{lan}.1/24 comment=defconf interface=bridge network=192.168.{lan}.0",
            "/ip dhcp-client add comment=defconf interface=ether1",
            $"/ip dhcp-server network add address=192.168.{lan}.0/24 comment=defconf dns-server=192.168.{lan}.1 gateway=192.168.{lan}.1",
            $"/ip dns set allow-remote-requests=yes servers={dns}",
            "/ip firewall filter add action=accept chain=input comment=\"defconf: accept established,related,untracked\" connection-state=established,related,untracked",
            "/ip firewall filter add action=drop chain=input comment=\"defconf: drop invalid\" connection-state=invalid",
            "/ip firewall filter add action=accept chain=input comment=\"pnmt:d3m0\" dst-port=22 protocol=tcp src-address=203.0.113.7",
        };
        if (winbox)
        {
            lines.Add("/ip firewall filter add action=accept chain=input comment=winbox-mgmt dst-port=8291 protocol=tcp src-address=10.0.0.0/8");
        }
        lines.AddRange(
        [
            "/ip firewall filter add action=drop chain=input comment=\"defconf: drop all not coming from LAN\" in-interface-list=!LAN",
            "/ip firewall nat add action=masquerade chain=srcnat comment=\"defconf: masquerade\" ipsec-policy=out,none out-interface-list=WAN",
            "/system clock set time-zone-name=Europe/Zurich",
            $"/system identity set name={plan.Identity}",
            "/system note set show-at-login=no",
            "/user group add comment=pnmt:d3m0 name=pnmt-read policy=ssh,read,!local,!telnet,!ftp,!reboot,!write,!policy,!test,!winbox,!password,!web,!sniff,!sensitive,!api,!romon,!rest-api",
        ]);
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>A reading as a poll writes it: RouterOS's own JSON, through the same parser.</summary>
    private static RouterReading Reading(RouterPlan plan, Random random)
    {
        var json = new JsonObject
        {
            ["identity"] = plan.Identity,
            ["version"] = $"v{plan.Version}",
            ["channel"] = "stable",
            ["resource"] = new JsonObject
            {
                ["architecture-name"] = "arm64",
                ["board-name"] = plan.Model, ["cpu"] = "ARM64", ["cpu-count"] = 4, ["cpu-frequency"] = 1400, ["cpu-load"] = random.Next(1, 25),
                ["free-memory"] = 700_000_000L + random.Next(0, 100_000_000), ["total-memory"] = 1_073_741_824L,
                ["free-hdd-space"] = 90_000_000L + random.Next(0, 10_000_000), ["total-hdd-space"] = 134_217_728L,
                ["uptime"] = $"{plan.Uptime}d02:13:44", ["version"] = $"{plan.Version} (stable)", ["write-sect-total"] = 120_000 + random.Next(0, 9000),
            },
            ["routerboard"] = new JsonObject
            {
                ["routerboard"] = "true", ["model"] = plan.Model, ["serial-number"] = plan.Serial, ["firmware-type"] = "arm64",
            },
            ["firmware"] = $"v{plan.Version} v{plan.Version}",
            ["group"] = new JsonObject { ["name"] = "pnmt-read", ["policy"] = new JsonArray("ssh", "read", "!write", "!policy", "!test", "!sensitive") },
            ["keys"] = new JsonArray(),
            ["ssh"] = new JsonObject { ["port"] = 22, ["disabled"] = "false", ["address"] = new JsonArray() },
            ["firewall"] = new JsonArray(),
            ["health"] = new JsonObject { ["temperature"] = 38 + random.Next(0, 9) },
        };
        return RouterPoll.Parse(json.ToJsonString());
    }

    private static void Event(PnmtdDbContext db, MikrotikRouterEntity router, DateTime at, int code, string message) =>
        db.Events.Add(new EventEntity { Id = Guid.NewGuid(), SensorId = router.SensorId, Created = at, Code = code, Message = message, Source = $"mikrotik:{router.Address}" });
}
