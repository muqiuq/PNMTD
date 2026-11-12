using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Lib.Models;
using PNMTD.Lib.Udp;

namespace PNMTD.Cli;

/// <summary>
/// `pnmtd dev seed` (docs/webui-migration-plan.md §6.2): demo data for `SEED=1 ./start.sh` and the
/// browser tests, at the dashboard's scale (§5.5): 50 hosts, 300 sensors, 90 days of events, and
/// 6 sensors failing now in 3 hosts. Development only, and only into a database without hosts.
/// <list type="bullet">
/// <item>Stable while the stack runs: pings go to 127.0.0.1 (one to a TEST-NET address that never
///   answers, as a failure should), heartbeats are daily, DNS zones are due tomorrow.</item>
/// <item>Notification rules link only sensors without events in the last 30 minutes, so the
///   notification task does not send about the seed; every seeded mail counts as processed.</item>
/// </list>
/// </summary>
internal static class DevSeedCommand
{
    private const string Source = "198.51.100.7:51022";

    private sealed record HostPlan(string Name, string Location, bool Network, bool Enabled = true);

    private sealed record SensorPlan(string Name, SensorType Type, int OkCode, int FailCode, Func<Random, string> Ok, Func<Random, string> Fail,
        int Interval = 0, int Grace = 0, string? Parameters = null, bool Daily = false);

    /// <summary>A sensor that fails now: since how long, with what message.</summary>
    private sealed record Failure(string Host, string Sensor, TimeSpan Since, string Message, int? Code = null);

    /// <summary>A stretch of history with failures (uptime bars with colour).</summary>
    private sealed record Outage(string Host, string Sensor, double FromDaysAgo, double ToDaysAgo, double Probability);

    public static int Run(string[] args, Func<PnmtdDbContext> database, ILogger logger)
    {
        if (args is not ["seed"])
        {
            throw new CommandLineException($"Unknown dev command: {string.Join(' ', args)}");
        }
        if (!Global.IsDevelopment)
        {
            Console.Error.WriteLine("pnmtd dev seed runs in Development only (ASPNETCORE_ENVIRONMENT=Development).");
            return 1;
        }
        using var db = database();
        DatabaseMigrator.Migrate(db, logger);
        if (db.Hosts.Any())
        {
            Console.Error.WriteLine("The database has hosts already: the seed only fills an empty one.");
            return 1;
        }
        var counts = Seed(db);
        Console.WriteLine($"Seeded {counts.Hosts} hosts, {counts.Sensors} sensors, {counts.Events:N0} events.");
        return 0;
    }

    private static readonly SensorPlan[] ServerSensors =
    [
        new("ping", SensorType.PING, 270, 470, _ => "Ping successful", _ => "Ping failed", Interval: 60, Parameters: "127.0.0.1"),
        new("http", SensorType.SIMPLE, 200, 500, r => $"200 OK in {r.Next(40, 260)} ms", _ => "502 Bad Gateway"),
        new("disk /", SensorType.VALUECHECK, 275, 475, r => $"{r.Next(21, 79)} % used", _ => "93 % used", Parameters: "^[1-8]?[0-9] % used$"),
        new("load", SensorType.VALUECHECK, 275, 475, r => $"load {r.NextDouble() * 1.8:0.00}", _ => "load 7.91", Parameters: "^load [0-3]\\."),
        new("backup", SensorType.HEARTBEAT, 200, 460, r => $"nightly backup completed, {r.Next(2, 60)}.{r.Next(0, 9)} GB", _ => "Heartbeat missing",
            Interval: 86_400, Grace: 7_200, Daily: true),
        new("cert", SensorType.SIMPLE, 200, 500, r => $"valid for {r.Next(20, 85)} days", _ => "expires in 3 days"),
    ];

    private static readonly SensorPlan[] NetworkSensors =
    [
        new("ping", SensorType.PING, 270, 470, _ => "Ping successful", _ => "Ping failed", Interval: 60, Parameters: "127.0.0.1"),
        new("uplink", SensorType.SIMPLE, 200, 500, _ => "1 Gbit/s, full duplex", _ => "link down"),
        new("cpu", SensorType.VALUECHECK, 275, 475, r => $"cpu {r.Next(3, 40)} %", _ => "cpu 97 %", Parameters: "^cpu [1-8]?[0-9] %$"),
        new("temperature", SensorType.VALUECHECK, 275, 475, r => $"{r.Next(34, 52)} °C", _ => "78 °C", Parameters: "^[1-6][0-9] °C$"),
        new("config backup", SensorType.HEARTBEAT, 200, 460, r => $"config saved, {r.Next(12, 90)} KB", _ => "Heartbeat missing",
            Interval: 86_400, Grace: 7_200, Daily: true),
        new("fan", SensorType.SIMPLE, 200, 500, r => $"{r.Next(2800, 4200)} rpm", _ => "fan 2 stopped"),
    ];

    private static List<HostPlan> Hosts()
    {
        var hosts = new List<HostPlan>
        {
            new("fw-01", "Zurich DC", true), new("fw-02", "Zurich DC", true), new("router-core", "Zurich DC", true),
            new("switch-core-01", "Zurich DC", true), new("switch-core-02", "Zurich DC", true),
        };
        hosts.AddRange(Enumerable.Range(1, 8).Select(i => new HostPlan($"web-{i:00}", i <= 4 ? "Zurich DC" : "Cloud (fra1)", false)));
        hosts.AddRange(Enumerable.Range(1, 4).Select(i => new HostPlan($"db-{i:00}", "Zurich DC", false)));
        hosts.AddRange(Enumerable.Range(1, 6).Select(i => new HostPlan($"app-{i:00}", i <= 3 ? "Zurich DC" : "Cloud (fra1)", false)));
        hosts.AddRange(new[] { "dns-01", "dns-02", "mail", "nas-01", "nas-02", "backup-srv", "monitoring", "vpn", "proxy-01", "proxy-02" }
            .Select(n => new HostPlan(n, "Zurich DC", false)));
        hosts.AddRange(new[] { ("basel", "Basel"), ("bern", "Bern"), ("geneva", "Geneva"), ("lugano", "Lugano"), ("st-gallen", "St. Gallen") }
            .Select(o => new HostPlan($"office-{o.Item1}-gw", $"{o.Item2} office", true)));
        hosts.AddRange(Enumerable.Range(1, 6).Select(i => new HostPlan($"k8s-node-{i:00}", "Cloud (fra1)", false, Enabled: i != 6)));
        hosts.AddRange(new[] { ("printer-hub", false), ("ups-01", true), ("ups-02", true), ("camera-nvr", true), ("hvac-ctrl", true), ("wiki", true) }
            .Select(h => new HostPlan(h.Item1, "Zurich office", h.Item1.StartsWith("ups", StringComparison.Ordinal), Enabled: h.Item2)));
        return hosts;
    }

    private static readonly Failure[] Failures =
    [
        new("backup-srv", "disk /", TimeSpan.FromMinutes(134), "93 % used"),
        new("backup-srv", "backup", TimeSpan.FromHours(27), "Heartbeat missing"),
        new("fw-01", "ping", TimeSpan.FromMinutes(5), "Ping failed"),
        new("web-03", "http", TimeSpan.FromMinutes(47), "502 Bad Gateway"),
        new("web-03", "cert", TimeSpan.FromHours(6), "expires in 3 days"),
        new("web-03", "load", TimeSpan.FromMinutes(12), "load 7.91"),
    ];

    private static readonly Outage[] Outages =
    [
        new("nas-01", "disk /", 42, 39, 0.6),
        new("web-02", "http", 12, 11, 1),
        new("vpn", "ping", 5.4, 5.1, 1),
        new("db-03", "load", 25.3, 25.1, 0.8),
        new("app-04", "http", 61, 60.5, 0.9),
        new("office-bern-gw", "uplink", 18, 17.6, 1),
        new("k8s-node-02", "disk /", 3.2, 3, 0.5),
    ];

    /// <summary>Short outages within the last day, then OK again: the dashboard's recent state changes.</summary>
    private static readonly (string Host, string Sensor, TimeSpan Ago, TimeSpan Duration)[] Flaps =
    [
        ("web-05", "http", TimeSpan.FromMinutes(192), TimeSpan.FromMinutes(22)),
        ("db-02", "ping", TimeSpan.FromHours(9), TimeSpan.FromMinutes(15)),
        ("office-basel-gw", "uplink", TimeSpan.FromHours(14), TimeSpan.FromMinutes(70)),
        ("app-02", "load", TimeSpan.FromHours(20), TimeSpan.FromMinutes(35)),
    ];

    private static (int Hosts, int Sensors, long Events) Seed(PnmtdDbContext db)
    {
        var random = new Random(42);
        var now = Clock.UtcNow;
        var hosts = new Dictionary<string, HostEntity>();
        var sensors = new List<(SensorEntity Sensor, SensorPlan Plan, HostPlan Host)>();
        var counter = 0;

        foreach (var plan in Hosts())
        {
            var host = new HostEntity
            {
                Id = Guid.NewGuid(), Name = plan.Name, Location = plan.Location, Created = now.AddDays(-120), Enabled = plan.Enabled,
                Notes = plan.Name switch
                {
                    "backup-srv" => "Veeam repository, 2 × 16 TB RAID 1. Contact: storage team.",
                    "fw-01" => "Primary firewall. HA pair with fw-02.",
                    "printer-hub" => "Decommissioned in October; kept for its history.",
                    _ => null,
                },
            };
            hosts[plan.Name] = host;
            db.Hosts.Add(host);
            foreach (var sensorPlan in plan.Network ? NetworkSensors : ServerSensors)
            {
                counter++;
                var name = sensorPlan.Name;
                // A few sensors that never reported (no data) and a few switched off.
                var noData = (plan.Name, name) is ("k8s-node-05", "cert") or ("wiki", "backup") or ("office-geneva-gw", "fan");
                var parameters = (plan.Name, name) == ("fw-01", "ping") ? "192.0.2.1" : sensorPlan.Parameters;
                var sensor = new SensorEntity
                {
                    Id = Guid.NewGuid(), ParentId = host.Id, Type = sensorPlan.Type,
                    Name = noData && name == "cert" ? "gpu" : noData && name == "backup" ? "search index" : noData ? "fan 2" : name,
                    SecretWriteToken = $"demo-w-{counter:000}-{Guid.NewGuid():N}"[..32], SecretReadToken = $"demo-r-{counter:000}-{Guid.NewGuid():N}"[..32],
                    Created = noData ? now.AddHours(-3) : now.AddDays(-120),
                    Enabled = !((plan.Name, name) is ("monitoring", "cert") or ("proxy-02", "cert")),
                    Ignore = (plan.Name, name) is ("ups-02", "temperature") or ("camera-nvr", "fan") or ("hvac-ctrl", "temperature") or ("app-06", "cert"),
                    Interval = sensorPlan.Interval, GracePeriod = sensorPlan.Grace, Parameters = parameters,
                };
                db.Sensors.Add(sensor);
                if (!noData)
                {
                    sensors.Add((sensor, sensorPlan, plan));
                }
            }
        }
        db.SaveChanges();

        var events = WriteEvents(db, sensors, random, now);
        SeedRules(db, hosts, sensors.Select(s => s.Sensor).ToList(), now);
        SeedDns(db, hosts, now);
        SeedMail(db, hosts, sensors.Select(s => s.Sensor).ToList(), now);
        SeedUdp(db, sensors.Select(s => s.Sensor).ToList(), hosts, now);
        foreach (var key in new[]
        {
            KeyValueKeyEnums.LAST_HEARTBEAT_TASK_RUN, KeyValueKeyEnums.LAST_NOTIFICATION_TASK_RUN, KeyValueKeyEnums.LAST_PING_TASK_RUN,
            KeyValueKeyEnums.LAST_TIMESPAN_CONDITIONS_CHECK_TASK_RUN, KeyValueKeyEnums.LAST_DNS_CHECK, KeyValueKeyEnums.LAST_MAIL_PROCESSING,
        })
        {
            db.SetKeyValueEntryByEnum(key, now.ToString("o", CultureInfo.InvariantCulture));
        }
        db.Database.ExecuteSqlRaw("ANALYZE events");
        return (hosts.Count, counter, events);
    }

    /// <summary>
    /// Every 3 hours for 90 days (daily sensors once a night), 0.3 % failures, the outages, the flaps,
    /// and the current failures; the newest event of a healthy sensor is minutes old. COPY, not EF:
    /// about 200,000 rows.
    /// </summary>
    private static long WriteEvents(PnmtdDbContext db, List<(SensorEntity Sensor, SensorPlan Plan, HostPlan Host)> sensors, Random random, DateTime now)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }
        long count = 0;
        using var writer = connection.BeginBinaryImport("""COPY events ("Id","SensorId","Created","Message","Source","Code") FROM STDIN (FORMAT BINARY)""");
        void Write(Guid sensor, DateTime at, string message, int code)
        {
            writer.StartRow();
            writer.Write(Guid.NewGuid(), NpgsqlDbType.Uuid);
            writer.Write(sensor, NpgsqlDbType.Uuid);
            writer.Write(at, NpgsqlDbType.TimestampTz);
            writer.Write(message, NpgsqlDbType.Text);
            writer.Write(Source, NpgsqlDbType.Text);
            writer.Write(code, NpgsqlDbType.Integer);
            count++;
        }

        foreach (var (sensor, plan, host) in sensors)
        {
            var failure = Failures.FirstOrDefault(f => f.Host == host.Name && f.Sensor == plan.Name);
            var flap = Flaps.FirstOrDefault(f => f.Host == host.Name && f.Sensor == plan.Name);
            // A disabled host stopped reporting 20 days ago; a sensor switched off, 9 days ago.
            var end = !host.Enabled ? now.AddDays(-20) : !sensor.Enabled ? now.AddDays(-9) : failure is not null ? now - failure.Since : now;
            var step = plan.Daily ? TimeSpan.FromDays(1) : TimeSpan.FromHours(3);
            var first = plan.Daily ? now.Date.AddDays(-90).AddHours(1 + random.Next(0, 3)) : now.AddDays(-90).AddMinutes(random.Next(0, 180));
            for (var at = first; at < end - TimeSpan.FromMinutes(30); at += step)
            {
                var daysAgo = (now - at).TotalDays;
                var outage = Outages.FirstOrDefault(o => o.Host == host.Name && o.Sensor == plan.Name && daysAgo <= o.FromDaysAgo && daysAgo >= o.ToDaysAgo);
                // Inside a planned flap, only the flap's own events.
                if (flap.Host is not null && at >= now - flap.Ago - TimeSpan.FromMinutes(5) && at <= now - flap.Ago + flap.Duration + TimeSpan.FromMinutes(5))
                {
                    continue;
                }
                // Rare random failures, none in the last two days: the recent changes are the planned ones.
                var failed = outage is not null ? random.NextDouble() < outage.Probability : daysAgo > 2 && random.NextDouble() < 0.003;
                Write(sensor.Id, at, failed ? plan.Fail(random) : plan.Ok(random), failed ? plan.FailCode : plan.OkCode);
            }
            if (flap.Host is not null)
            {
                Write(sensor.Id, now - flap.Ago, plan.Fail(random), plan.FailCode);
                Write(sensor.Id, now - flap.Ago + flap.Duration / 2, plan.Fail(random), plan.FailCode);
                Write(sensor.Id, now - flap.Ago + flap.Duration, plan.Ok(random), plan.OkCode);
            }
            if (failure is not null)
            {
                // The last success, then failures from when it started until a few minutes ago.
                Write(sensor.Id, now - failure.Since - TimeSpan.FromMinutes(4), plan.Ok(random), plan.OkCode);
                var interval = failure.Since > TimeSpan.FromHours(2) ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);
                for (var at = now - failure.Since; at < now - TimeSpan.FromMinutes(1); at += interval)
                {
                    Write(sensor.Id, at, failure.Message, failure.Code ?? plan.FailCode);
                }
            }
            else if (host.Enabled && sensor.Enabled)
            {
                // Fresh: the last report minutes ago (a daily sensor, last night).
                var last = plan.Daily ? now.Date.AddHours(1).AddMinutes(random.Next(0, 90)) : now - TimeSpan.FromMinutes(random.Next(1, 25));
                Write(sensor.Id, last > now ? last.AddDays(-1) : last, plan.Ok(random), plan.OkCode);
            }
        }
        writer.Complete();
        return count;
    }

    private static void SeedRules(PnmtdDbContext db, Dictionary<string, HostEntity> hosts, List<SensorEntity> sensors, DateTime now)
    {
        var onCall = new NotificationRuleEntity
        {
            Id = Guid.NewGuid(), Name = "Ops on-call", Recipient = "pushover://uQiRzpo4DXghDmr9QzzfQu27cmVRsG",
            Type = NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK, Enabled = true,
        };
        var team = new NotificationRuleEntity
        {
            Id = Guid.NewGuid(), Name = "Storage team", Recipient = "storage@example.org", Type = NotificationRuleType.ONLY_FAILURES_ON_CHANGE, Enabled = true,
        };
        var chat = new NotificationRuleEntity
        {
            Id = Guid.NewGuid(), Name = "Chat webhook", Recipient = "https://hooks.example.org/services/pnmt/T0A1B2C3", Type = NotificationRuleType.ALWAYS,
            Enabled = false,
        };
        db.NotificationRules.AddRange(onCall, team, chat);
        // Daily sensors only: none of them has an event in the notification task's 30-minute window.
        var daily = sensors.Where(s => s.Interval == 86_400 && s.Enabled).ToList();
        foreach (var sensor in daily.Where(s => hosts.Values.First(h => h.Id == s.ParentId).Name is "backup-srv" or "nas-01" or "nas-02" or "db-01" or "db-02"))
        {
            db.NotificationRuleSensor.Add(new NotificationRuleSensorEntity { Id = Guid.NewGuid(), NotificationRuleId = team.Id, SensorId = sensor.Id });
        }
        foreach (var sensor in daily.Take(12))
        {
            db.NotificationRuleSensor.Add(new NotificationRuleSensorEntity { Id = Guid.NewGuid(), NotificationRuleId = onCall.Id, SensorId = sensor.Id });
        }
        db.SaveChanges();
    }

    private static void SeedDns(PnmtdDbContext db, Dictionary<string, HostEntity> hosts, DateTime now)
    {
        (string Name, DnsZoneResourceType Type, string Reference, string Actual)[] orgRecords =
        [
            ("example.org.", DnsZoneResourceType.NS, "ns1.example.org.", "ns1.example.org."),
            ("example.org.", DnsZoneResourceType.MX, "10 mail.example.org.", "10 mail.example.org."),
            ("example.org.", DnsZoneResourceType.A, "203.0.113.10", "203.0.113.10"),
            ("www.example.org.", DnsZoneResourceType.A, "203.0.113.11", "203.0.113.99"),
            ("www.example.org.", DnsZoneResourceType.AAAA, "2001:db8::11", "2001:db8::11"),
            ("mail.example.org.", DnsZoneResourceType.A, "203.0.113.25", "203.0.113.25"),
            ("example.org.", DnsZoneResourceType.TXT, "\"v=spf1 mx -all\"", "\"v=spf1 mx -all\""),
        ];
        (string Name, DnsZoneResourceType Type, string Reference, string Actual)[] netRecords =
        [
            ("example.net.", DnsZoneResourceType.NS, "ns1.example.org.", "ns1.example.org."),
            ("example.net.", DnsZoneResourceType.A, "198.51.100.20", "198.51.100.20"),
            ("vpn.example.net.", DnsZoneResourceType.A, "198.51.100.21", "198.51.100.21"),
        ];
        foreach (var (zoneName, records, host) in new[] { ("example.org", orgRecords, "dns-01"), ("example.net", netRecords, "dns-02") })
        {
            var zone = new DnsZoneEntity
            {
                Id = Guid.NewGuid(), ZoneName = zoneName, HostId = hosts[host].Id, Enabled = true, Interval = 86_400,
                LastChecked = now.AddHours(-1), NextCheck = now.AddHours(23), RequiresProcessing = false, ForceUpdate = false,
                RecordsMatch = records.All(r => r.Reference == r.Actual),
                ZoneFileContent = string.Join('\n', new[] { $"$ORIGIN {zoneName}.", "$TTL 3600" }
                    .Concat(records.Select(r => $"{r.Name,-22} 3600 IN {r.Type,-5} {r.Reference}"))) + "\n",
            };
            db.DnsZones.Add(zone);
            foreach (var r in records)
            {
                db.DnsZoneEntries.Add(new DnsZoneEntryEntity
                {
                    Id = Guid.NewGuid(), DnsZoneId = zone.Id, Name = r.Name, RecordType = r.Type, TTL = 3600, ReferenceValue = r.Reference,
                    ActualValue = r.Actual, IsMatch = r.Reference == r.Actual, NumOfFailuresInARow = r.Reference == r.Actual ? 0 : 3,
                    Updated = now.AddHours(-1),
                });
            }
            if (records.Any(r => r.Reference != r.Actual))
            {
                db.DnsZoneLogEntries.Add(new DnsZoneLogEntryEntity
                {
                    Id = Guid.NewGuid(), Created = now.AddHours(-1), DnsZoneId = zone.Id, EntryType = DnsZoneLogEntryType.ENTRY_DISCREPANCY,
                    Message = "www.example.org. A: expected 203.0.113.11, resolvers answer 203.0.113.99",
                });
            }
            db.DnsZoneLogEntries.Add(new DnsZoneLogEntryEntity
            {
                Id = Guid.NewGuid(), Created = now.AddDays(-30), DnsZoneId = zone.Id, EntryType = DnsZoneLogEntryType.NEW_ENTRY,
                Message = $"{records[^1].Name} {records[^1].Type} added",
            });
        }
        db.SaveChanges();
    }

    private static void SeedMail(PnmtdDbContext db, Dictionary<string, HostEntity> hosts, List<SensorEntity> sensors, DateTime now)
    {
        SensorEntity Sensor(string host, string name) => sensors.First(s => s.ParentId == hosts[host].Id && s.Name == name);
        var veeam = new MailInputRuleEntity
        {
            Id = Guid.NewGuid(), Name = "Veeam job report", Enabled = true, FromTest = "veeam@example\\.org", SubjectTest = "^\\[(Success|Warning|Failed)\\]",
            OkTest = "\\[Success\\]", OkCode = 200, FailTest = "\\[Failed\\]", FailCode = 500, DefaultCode = 500,
            ExtractMessageRegex = "Job (.+) finished", SensorOutputId = Sensor("backup-srv", "backup").Id,
        };
        var nas = new MailInputRuleEntity
        {
            Id = Guid.NewGuid(), Name = "Synology alerts", Enabled = true, FromTest = "nas-01@example\\.org", SubjectTest = null, BodyTest = "Volume",
            OkTest = null, OkCode = null, FailTest = "degraded|crashed", FailCode = 500, DefaultCode = 200, ExtractMessageRegex = null,
            SensorOutputId = Sensor("nas-01", "disk /").Id,
        };
        db.MailInputs.AddRange(veeam, nas);
        for (var i = 0; i < 9; i++)
        {
            var at = now.AddHours(-(i * 9 + 2));
            var isVeeam = i % 3 != 2;
            db.MailLogs.Add(new MailLogEntity
            {
                Id = Guid.NewGuid(), RemoteId = $"<seed-{i}@example.org>", Created = at, MessageDate = at.AddMinutes(-1),
                From = isVeeam ? "veeam@example.org" : "nas-01@example.org", To = "pnmt@example.org",
                Subject = isVeeam ? $"[{(i == 1 ? "Failed" : "Success")}] Nightly backup (3 VMs)" : "Volume 1 health report",
                Content = isVeeam ? $"Job Nightly backup finished at {at:HH:mm}.\n3 of 3 VMs processed." : "Volume 1 is healthy. 41 % used.",
                // All processed: the mail task would otherwise turn them into events at once.
                Processed = true, ProcessedById = i == 8 ? null : isVeeam ? veeam.Id : nas.Id,
                ProcessLog = i == 8 ? "no rule matched" : $"matched {(isVeeam ? veeam.Name : nas.Name)} → {(i == 1 ? 500 : 200)}",
            });
        }
        db.SaveChanges();
    }

    /// <summary>
    /// UDP nodes (docs/udp-iot-plan.md §8, U6): four of the seeded fan sensors become UDP nodes, so
    /// the counts stay as they are. Daily heartbeats keep them OK while the stack runs; their last
    /// packets, commands with their log, a firmware image and updates are built with the real codec.
    /// </summary>
    private static void SeedUdp(PnmtdDbContext db, List<SensorEntity> sensors, Dictionary<string, HostEntity> hosts, DateTime now)
    {
        var plans = new (string Host, string Name, KeyFamily Family, SecuritySuite[] Suites, UdpSessionMode Session, string Notes)[]
        {
            ("office-lugano-gw", "climate node", KeyFamily.HmacSha256, [SecuritySuite.HmacSha256_32, SecuritySuite.HmacSha256_64], UdpSessionMode.None,
                "ESP32-C3 on the meeting room wall. SIM 8941 0000 1234 5678 901. Battery changed 2026-09-03."),
            ("office-st-gallen-gw", "door sensor", KeyFamily.HmacSha256, [SecuritySuite.Token32, SecuritySuite.HmacSha256_32], UdpSessionMode.None,
                "Reed contact on the server room door; TOKEN32 heartbeats from a shell script."),
            ("ups-01", "battery monitor", KeyFamily.Aes, [SecuritySuite.AesCmac32, SecuritySuite.AesCcm64], UdpSessionMode.JoinRequired,
                "nRF9160, NB-IoT. Joins at every start."),
            ("ups-02", "water meter", KeyFamily.HmacMd5, [SecuritySuite.HmacMd5_32], UdpSessionMode.None,
                "Legacy meter firmware: HMAC-MD5 only."),
        };
        var random = new Random(7);
        var image = new byte[64 * 1024];
        random.NextBytes(image);
        image[0] = 0xE9;
        var firmware = new UdpFirmwareEntity
        {
            Id = Guid.NewGuid(), Name = "Climate node", Version = "1.5.0", FileName = "climate-1.5.0.bin", Size = image.Length,
            Sha256 = System.Security.Cryptography.SHA256.HashData(image), Content = image, Notes = "Faster sensor polling; fixes the NTP drift.",
            UploadedAt = now.AddDays(-3), UploadedByName = "admin",
        };
        db.UdpFirmware.Add(firmware);
        long deviceId = 0;
        foreach (var plan in plans)
        {
            var sensor = sensors.FirstOrDefault(x => x.ParentId == hosts[plan.Host].Id && x.Name == "fan");
            if (sensor is null)
            {
                continue;
            }
            deviceId++;
            sensor.Type = SensorType.UDP_NODE;
            sensor.Name = plan.Name;
            sensor.Interval = 86_400;
            sensor.GracePeriod = 7_200;
            sensor.Parameters = null;
            sensor.Notes = plan.Notes;
            var key = new byte[16];
            var token = new byte[4];
            random.NextBytes(key);
            random.NextBytes(token);
            var device = new UdpDevice((uint)deviceId, plan.Family, key, token) { NextCounter = (uint)random.Next(400, 4000) };
            var endpoint = $"198.51.100.{20 + deviceId}:{random.Next(30000, 60000)}";
            var suite = plan.Suites[^1].IsEncrypted() ? plan.Suites[0] : plan.Suites[^1];
            var node = new UdpNodeEntity
            {
                SensorId = sensor.Id, DeviceId = deviceId, KeyFamily = plan.Family, AllowedSuites = SecuritySuites.Mask(plan.Suites),
                Key = key, Token = token, KeySource = UdpSecretSource.Generated, TokenSource = UdpSecretSource.Generated,
                KeyChangedAt = now.AddDays(-60), TokenChangedAt = now.AddDays(-60), SessionMode = plan.Session,
                AckMode = UdpAckMode.OnRequest, ReplayProtection = true, LastEndpoint = endpoint, LastSuite = suite,
                LastSeenAt = now.AddMinutes(-random.Next(2, 40)), LastBootAt = now.AddDays(-12), LastBootReason = 1,
                FirmwareVersion = deviceId == 1 ? "1.5.0" : "1.4.2", Created = now.AddDays(-60),
            };
            if (plan.Session == UdpSessionMode.JoinRequired)
            {
                device.SessionKey = SessionKeys.Derive(plan.Family, key, 0xA1B2C3D4, 0x01020304);
                node.SessionKey = device.SessionKey;
                node.SessionStartedAt = now.AddDays(-12);
            }
            var heartbeat = device.Build(PacketType.Heartbeat, suite, [], ack: true);
            var ack = PacketCodec.Build(new PacketHeader(PacketType.Ack, suite, device.DeviceId, (ushort)heartbeat.Fcnt), [], device.SessionKey ?? key, token, heartbeat.Fcnt);
            var data = device.Build(PacketType.Data, plan.Suites[^1], System.Text.Encoding.UTF8.GetBytes($"{random.Next(19, 24)}.{random.Next(0, 9)}C {random.Next(35, 60)}%"));
            node.Counter = FrameCounter.Accept(CounterState.Empty, data.Fcnt);
            db.UdpNodes.Add(node);
            db.UdpLastPackets.Add(new UdpLastPacketEntity
            {
                SensorId = sensor.Id, Type = PacketType.Heartbeat, ReceivedAt = node.LastSeenAt.Value.AddMinutes(-10), Endpoint = endpoint, Suite = suite,
                Fcnt = heartbeat.Fcnt, Datagram = heartbeat.Datagram, Payload = [], Response = ack,
            });
            var plaintext = PacketCodec.TryParse(data.Datagram, out var parsed, out _) ? PacketCodec.Open(parsed, device.SessionKey ?? key, token, data.Fcnt) : null;
            db.UdpLastPackets.Add(new UdpLastPacketEntity
            {
                SensorId = sensor.Id, Type = PacketType.Data, ReceivedAt = node.LastSeenAt.Value, Endpoint = endpoint, Suite = data.Suite,
                Fcnt = data.Fcnt, Datagram = data.Datagram, Payload = plaintext,
            });
            db.Events.Add(new EventEntity { Id = Guid.NewGuid(), SensorId = sensor.Id, Created = node.LastSeenAt.Value.AddMinutes(-10), Code = PNMTStatusCodes.UDP_ALIVE, Message = "Alive", Source = "udp:" + endpoint });
            db.Events.Add(new EventEntity { Id = Guid.NewGuid(), SensorId = sensor.Id, Created = node.LastSeenAt.Value, Code = PNMTStatusCodes.UDP_DATA, Message = PacketCodec.Render(plaintext ?? []), Source = "udp:" + endpoint });

            // A completed command with its whole communication, and one that waits for the next packet.
            var slot = device.Build(PacketType.Heartbeat, suite, []);
            var commandBytes = new byte[] { 0x02, 0x02, 0x58 };
            var command = new UdpCommandEntity
            {
                Id = Guid.NewGuid(), SensorId = sensor.Id, DeviceId = deviceId, NodeName = sensor.Name, CmdId = 0, Payload = commandBytes,
                State = UdpCommandState.Completed, Delivery = UdpCommandDelivery.ResultRequired, MaxAttempts = 3, Attempts = 1,
                Created = now.AddDays(-2), CreatedByName = "admin", ExpiresAt = now.AddDays(-1), LastSentAt = now.AddDays(-2).AddMinutes(8),
                CompletedAt = now.AddDays(-2).AddMinutes(8), ResultStatus = 0, Result = "interval 600"u8.ToArray(),
            };
            var sent = PacketCodec.Build(new PacketHeader(PacketType.Command, suite, device.DeviceId, (ushort)slot.Fcnt), Payloads.Command(0, commandBytes),
                device.SessionKey ?? key, token, slot.Fcnt);
            var result = device.Build(PacketType.CmdResult, suite, Payloads.CmdResult(new CmdResultPayload(0, 0, command.Result)), ack: true);
            db.UdpCommands.Add(command);
            db.UdpCommands.Add(new UdpCommandEntity
            {
                Id = Guid.NewGuid(), SensorId = sensor.Id, DeviceId = deviceId, NodeName = sensor.Name, CmdId = 1, Payload = "status"u8.ToArray(),
                State = UdpCommandState.Queued, Delivery = UdpCommandDelivery.ResultRequired, MaxAttempts = 3, Created = now.AddMinutes(-5),
                CreatedByName = "admin", ExpiresAt = now.AddDays(1),
            });
            node.NextCmdId = 2;
            UdpCommandLogEntity Log(UdpLogKind kind, DateTime at, Action<UdpCommandLogEntity> fill)
            {
                var row = new UdpCommandLogEntity { Created = at, CommandId = command.Id, SensorId = sensor.Id, DeviceId = deviceId, NodeName = sensor.Name, Kind = kind, CmdId = 0 };
                fill(row);
                return row;
            }
            db.UdpCommandLog.AddRange(
                Log(UdpLogKind.Queued, command.Created, l => { l.Payload = commandBytes; l.UserName = "admin"; l.Note = "ResultRequired, expires in 24 h, 3 bytes"; }),
                Log(UdpLogKind.Sent, command.LastSentAt!.Value, l => { l.Direction = UdpDirection.Down; l.Attempt = 1; l.Endpoint = endpoint; l.Suite = suite; l.Fcnt = slot.Fcnt; l.Datagram = sent; l.Payload = commandBytes; }),
                Log(UdpLogKind.ResultReceived, command.CompletedAt!.Value, l => { l.Direction = UdpDirection.Up; l.Endpoint = endpoint; l.Suite = suite; l.Fcnt = result.Fcnt; l.Datagram = result.Datagram; l.Payload = Payloads.CmdResult(new CmdResultPayload(0, 0, command.Result!)); l.ResultStatus = 0; l.Note = "OK"; }));
            db.UdpCommandLog.Add(new UdpCommandLogEntity
            {
                Created = now.AddMinutes(-5), SensorId = sensor.Id, DeviceId = deviceId, NodeName = sensor.Name, Kind = UdpLogKind.Queued, CmdId = 1,
                Payload = "status"u8.ToArray(), UserName = "admin", Note = "ResultRequired, expires in 24 h, 6 bytes",
                CommandId = db.ChangeTracker.Entries<UdpCommandEntity>().Last().Entity.Id,
            });

            // The climate node runs the uploaded image; the others are offered it.
            var job = new UdpOtaJobEntity
            {
                Id = Guid.NewGuid(), SensorId = sensor.Id, DeviceId = deviceId, NodeName = sensor.Name, FirmwareId = firmware.Id, FirmwareName = firmware.Name,
                FirmwareVersion = firmware.Version, FirmwareSize = firmware.Size, FirmwareSha256 = firmware.Sha256, UpdateId = 0, ChunkSize = 448,
                State = deviceId == 1 ? UdpOtaState.Applied : UdpOtaState.Queued, MaxOfferAttempts = 20, OfferAttempts = (short)(deviceId == 1 ? 1 : 0),
                BytesServed = deviceId == 1 ? image.Length + 896 : 0, ChunksServed = deviceId == 1 ? 149 : 0, ChunkRetransmissions = deviceId == 1 ? 2 : 0,
                HighestOffset = deviceId == 1 ? image.Length : 0, Created = now.AddDays(-3), CreatedByName = "admin",
                OfferedAt = deviceId == 1 ? now.AddDays(-3).AddMinutes(9) : null, StartedAt = deviceId == 1 ? now.AddDays(-3).AddMinutes(9) : null,
                LastRequestAt = deviceId == 1 ? now.AddDays(-3).AddMinutes(14) : null, FinishedAt = deviceId == 1 ? now.AddDays(-3).AddMinutes(16) : null,
                ExpiresAt = now.AddDays(4), AppliedVersion = deviceId == 1 ? "1.5.0" : null,
            };
            if (deviceId is 1 or 2)
            {
                db.UdpOtaJobs.Add(job);
                node.NextUpdateId = 1;
                db.UdpCommandLog.Add(new UdpCommandLogEntity
                {
                    Created = job.Created, OtaJobId = job.Id, SensorId = sensor.Id, DeviceId = deviceId, NodeName = sensor.Name, Kind = UdpLogKind.OtaQueued,
                    CmdId = 0, UserName = "admin", Note = $"{firmware.Name} {firmware.Version}, {firmware.Size} bytes, chunks of 448",
                });
                if (deviceId == 1)
                {
                    foreach (var (kind, minutes, note) in new[] { (UdpLogKind.OtaOffered, 9, "offered"), (UdpLogKind.OtaStarted, 9, "first GET"), (UdpLogKind.OtaVerified, 14, "the SHA-256 matched on the device"), (UdpLogKind.OtaApplied, 16, "running 1.5.0") })
                    {
                        db.UdpCommandLog.Add(new UdpCommandLogEntity
                        {
                            Created = job.Created.AddMinutes(minutes), OtaJobId = job.Id, SensorId = sensor.Id, DeviceId = deviceId, NodeName = sensor.Name,
                            Kind = kind, CmdId = 0, Endpoint = endpoint, Suite = suite, Note = note,
                            Direction = kind == UdpLogKind.OtaOffered ? UdpDirection.Down : UdpDirection.Up,
                        });
                    }
                }
            }
        }
        db.SaveChanges();
    }
}
