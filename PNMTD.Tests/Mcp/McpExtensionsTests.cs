using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Dev;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Udp;
using PNMTD.Mcp;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Tests.Advanced;
using PNMTD.Tests.Auth;
using PNMTD.Tools;
using PNMTD.Tools.Write;

namespace PNMTD.Tests.Mcp;

/// <summary>
/// The tools of docs/mcp-extensions-plan.md: a type's own settings and MikroTik routers, routers' jobs, commands and differences, the new
/// reads, likes and uses, Advanced rules, responsibilities, boards and their messages, the lounge. Over MCP with real tokens, against the
/// fake Zabbix.
/// </summary>
[TestClass]
public sealed class McpExtensionsTests
{
    private static readonly FakeZabbix zabbix = new();
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        var settings = FakeZabbix.Settings.ToDictionary(p => p.Key, p => p.Value);
        settings["Mcp:Enabled"] = "true";
        settings["Development:DoNotSendNotifications"] = "true";
        // kb_changes lists the newest pages: the most it may, so that pages other classes dated later don't crowd this class's out.
        settings["KnowledgeBase:RecentChangesMax"] = "50";
        app = new TestApp(testSignIn: false, settings, zabbix.Install);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    /// <summary>
    /// A fake clock, which enrolling 2FA steps on, from a whole second now. Never anchored ahead of other classes' rows: what this class
    /// writes would be the "newest" their checks look for (a board message's audit entry).
    /// </summary>
    [TestInitialize]
    public void Freeze()
    {
        var now = DateTime.UtcNow;
        Clock.Provider = new FakeTimeProvider(new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero));
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    private static async Task<(Guid Id, Browser Browser, McpClient Client)> WriterAsync(string role = Roles.Admin)
    {
        var (id, _, _, browser) = await McpServerTests.PersonAsync(app, role);
        await TwoFactorLoginTests.EnrollAsync(browser);
        var token = await McpServerTests.TurnOnAsync(browser, "write");
        return (id, browser, await McpServerTests.ClientAsync(app, token.Value!));
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, object args)
    {
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        return McpServerTests.Json(await client.CallToolAsync(tool, dictionary));
    }

    private static async Task<JsonElement> OkAsync(McpClient client, string tool, object args)
    {
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var result = await client.CallToolAsync(tool, dictionary);
        Assert.IsFalse(result.IsError ?? false, $"{tool}: {((TextContentBlock)result.Content[0]).Text}");
        return McpServerTests.Json(result);
    }

    private static async Task<string> ErrorAsync(McpClient client, string tool, object args)
    {
        var result = await CallAsync(client, tool, args);
        Assert.IsTrue(result.TryGetProperty("error", out var error), $"{tool} should have failed: {result.GetRawText()}");
        return error.GetString()!;
    }

    private static async Task<Guid> HostAsync(McpClient client) =>
        (await OkAsync(client, "host_create", new { name = "x-" + Guid.NewGuid().ToString("N")[..10] })).GetProperty("id").GetGuid();

    private static readonly string[] Reads =
    [
        "sensor_types", "status_codes", "router_jobs", "router_changelog", "os_updates_get", "notification_coverage", "kb_changes", "advanced_reports",
        "advanced_test", "certificate_history", "certificate_authorities", "boards_list", "board_get", "lounge_read",
    ];

    private static readonly string[] Writes =
    [
        "router_join_script", "sensors_set_importance", "router_difference_acknowledge", "mikrotik_commands_list", "mikrotik_command_cancel",
        "kb_page_used", "kb_page_like", "advanced_rules_change", "advanced_fields_change", "responsibility_options", "responsibility_create",
        "responsibility_update", "responsibility_link", "responsibility_redmine", "board_message_send", "board_message_withdraw",
    ];

    [TestMethod]
    public async Task TheReadOnlyTokenGetsTheNewReadsAndTheReadWriteTokenEverything()
    {
        var (_, browser, writer) = await WriterAsync();
        await using (writer)
        {
            var read = await McpServerTests.ClientAsync(app, (await McpServerTests.TurnOnAsync(browser, "read")).Value!);
            await using (read)
            {
                var readOnly = (await read.ListToolsAsync()).Select(t => t.Name).ToList();
                CollectionAssert.IsSubsetOf(Reads, readOnly);
                Assert.IsFalse(Writes.Any(readOnly.Contains), "nothing that changes, nothing above the reading levels: " + string.Join(", ", Writes.Where(readOnly.Contains)));
            }
            var all = (await writer.ListToolsAsync()).Select(t => t.Name).ToList();
            CollectionAssert.IsSubsetOf(Reads.Concat(Writes).ToList(), all);
            Assert.IsFalse(ToolCatalog.All.Any(t => t.ForCookie && (Reads.Contains(t.Name) || Writes.Contains(t.Name))), "Cookie gets none of them");
        }
    }

    [TestMethod]
    public async Task ARouterIsMadeOverMcpAndOnlyADirectSshScriptComesOut()
    {
        var (id, _, client) = await WriterAsync();
        await using (client)
        {
            var hostId = await HostAsync(client);
            var wireGuard = await CallAsync(client, "sensor_create", new { hostId, type = "MIKROTIK", name = "gw" });
            StringAssert.Contains(wireGuard.GetProperty("message").GetString(), "WireGuard", "the default mode needs the gateway, which these tests don't run");

            var router = await OkAsync(client, "sensor_create", new
            {
                hostId, type = "MIKROTIK", name = "branch-gw", enabled = false,
                mikrotik = new { mode = "DirectSsh", address = "192.0.2.10", sourceAddress = "192.0.2.1", backupIntervalHours = 0 },
            });
            var sensorId = router.GetProperty("id").GetGuid();
            Assert.AreEqual((MonitoringWriteTools.RouterInterval, MonitoringWriteTools.RouterGrace),
                (router.GetProperty("intervalSeconds").GetInt32(), router.GetProperty("gracePeriodSeconds").GetInt32()), "the form's defaults");
            Assert.AreEqual("DirectSsh", router.GetProperty("router").GetProperty("mode").GetString());
            StringAssert.Contains(router.GetProperty("next").GetString(), "router_join_script");
            await using (var db = app.NewDb())
            {
                var row = await db.MikrotikRouters.AsNoTracking().SingleAsync(r => r.SensorId == sensorId);
                Assert.AreEqual(("192.0.2.10", "192.0.2.1", (int?)null), (row.Address, row.PnmtSourceAddress, row.BackupIntervalHours));
                Assert.IsFalse(row.ManagementAccess || row.IncludeSecrets || row.Diagnostics, "the admin's settings stay off");
            }

            var script = await OkAsync(client, "router_join_script", new { sensorId });
            var text = script.GetProperty("script").GetString()!;
            StringAssert.Contains(text, "/user add name=pnmt");
            Assert.IsFalse(text.Contains("private-key", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue((await OkAsync(client, "router_get", new { sensorId })).TryGetProperty("differences", out _));

            await using (var db = app.NewDb())
            {
                var viewed = await db.AuditLog.Where(l => l.EventType == AuditEvents.MikrotikJoinScriptViewed && l.UserId == id).ToListAsync();
                Assert.AreEqual(AuditActor.Mcp, viewed.Single().Actor, "the view is in the audit, as MCP's");
                // As if it had been switched to a WireGuard mode in PNMT: its script has the router's private key now.
                await db.MikrotikRouters.Where(r => r.SensorId == sensorId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Mode, MikrotikMode.WireGuardSsh));
            }
            Assert.AreEqual("not_available", await ErrorAsync(client, "router_join_script", new { sensorId }));
            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "sensor_update", new { id = sensorId, mikrotik = new { mode = "DirectSsh" } }),
                "a router's settings change in PNMT");
        }
    }

    [TestMethod]
    public async Task ATypesOwnSettingsArePatchedAndWhatCutsADeviceOffIsRefused()
    {
        var (_, _, client) = await WriterAsync();
        await using (client)
        {
            var hostId = await HostAsync(client);

            // An SSL certificate sensor (§3.3): left-out fields stay; Pinned is a person's.
            var ssl = await OkAsync(client, "sensor_create", new
            {
                hostId, type = "SSL_CERTIFICATE", name = "intranet", enabled = false, parameters = "127.0.0.1:1",
                certificate = new { warnDays = 30, checkName = false, serverName = "intranet.example.org" },
            });
            var sslId = ssl.GetProperty("id").GetGuid();
            await OkAsync(client, "sensor_update", new { id = sslId, certificate = new { alertOnIssuerChange = true } });
            await using (var db = app.NewDb())
            {
                var c = await db.CertificateSensors.AsNoTracking().SingleAsync(x => x.SensorId == sslId);
                Assert.AreEqual((30, false, "intranet.example.org", true), (c.WarnDays, c.CheckName, c.ServerName, c.AlertOnIssuerChange));
            }
            await OkAsync(client, "sensor_update", new { id = sslId, certificate = new { serverName = "" } });
            await using (var db = app.NewDb())
            {
                Assert.IsNull((await db.CertificateSensors.AsNoTracking().SingleAsync(x => x.SensorId == sslId)).ServerName, "an empty text clears");
            }
            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "sensor_update", new { id = sslId, certificate = new { trust = "Pinned" } }));
            Assert.AreEqual(0, (await OkAsync(client, "certificate_history", new { sensorId = sslId })).GetProperty("items").GetArrayLength());

            // A UDP node (§3.5): its settings, never a new key family on a change.
            var deviceId = Random.Shared.NextInt64(1_000_000, 4_000_000_000);
            var udp = await OkAsync(client, "sensor_create", new
            {
                hostId, type = "UDP_NODE", name = "pump", intervalSeconds = 600, udp = new { deviceId, sessionMode = "JoinRequired", ackMode = "Always" },
            });
            var udpId = udp.GetProperty("id").GetGuid();
            byte[] key;
            int suites;
            await using (var db = app.NewDb())
            {
                var n = await db.UdpNodes.AsNoTracking().SingleAsync(x => x.SensorId == udpId);
                Assert.AreEqual((deviceId, UdpSessionMode.JoinRequired, UdpAckMode.Always), (n.DeviceId, n.SessionMode, n.AckMode));
                (key, suites) = (n.Key, n.AllowedSuites);
            }
            await OkAsync(client, "sensor_update", new { id = udpId, udp = new { replayProtection = false } });
            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "sensor_update", new { id = udpId, udp = new { keyFamily = "Aes" } }));
            await using (var db = app.NewDb())
            {
                var n = await db.UdpNodes.AsNoTracking().SingleAsync(x => x.SensorId == udpId);
                Assert.AreEqual((false, UdpSessionMode.JoinRequired, UdpAckMode.Always, suites, KeyFamily.HmacSha256),
                    (n.ReplayProtection, n.SessionMode, n.AckMode, n.AllowedSuites, n.KeyFamily), "the rest stays");
                CollectionAssert.AreEqual(key, n.Key, "the same key");
            }

            // A Zabbix sensor (§3.4), as zabbix_attach would have made it: its rules, its Zabbix host kept.
            var simple = (await OkAsync(client, "sensor_create", new { hostId, type = "SIMPLE", name = "zbx" })).GetProperty("id").GetGuid();
            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "sensor_update", new { id = simple, zabbix = new { countSuppressed = true } }),
                "an object for another type");
            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "sensor_update", new { id = simple, udp = new { replayProtection = true } }));
            var zabbixHost = Random.Shared.NextInt64(900_000_000, 999_999_999);
            await using (var db = app.NewDb())
            {
                await db.Sensors.Where(s => s.Id == simple).ExecuteUpdateAsync(u => u.SetProperty(s => s.Type, SensorType.ZABBIX));
                db.ZabbixSensors.Add(new ZabbixSensorEntity { SensorId = simple, ZabbixHostId = zabbixHost, Created = Clock.UtcNow });
                await db.SaveChangesAsync();
            }
            await OkAsync(client, "sensor_update", new { id = simple, zabbix = new { minSeverity = "High", countSuppressed = true } });
            await using (var db = app.NewDb())
            {
                var z = await db.ZabbixSensors.AsNoTracking().SingleAsync(x => x.SensorId == simple);
                Assert.AreEqual((zabbixHost, PNMTD.Zabbix.ZabbixSeverity.High, true, true), (z.ZabbixHostId, z.MinSeverity, z.CountSuppressed, z.CountAcknowledged));
            }
        }
    }

    [TestMethod]
    public async Task TheTypesTheCodesAndImportanceInBulk()
    {
        var (id, _, client) = await WriterAsync();
        await using (client)
        {
            var types = (await OkAsync(client, "sensor_types", new { })).EnumerateArray().ToDictionary(t => t.GetProperty("type").GetString()!);
            Assert.IsNotNull(types["PING"].GetProperty("parameters").GetString());
            Assert.IsTrue(types["MIKROTIK"].GetProperty("creatableOverMcp").GetBoolean());
            Assert.IsFalse(types["LINUX"].GetProperty("creatableOverMcp").GetBoolean());
            Assert.IsTrue((await OkAsync(client, "status_codes", new { })).EnumerateArray().Any(c => c.GetProperty("code").GetInt32() == 460));

            var hostId = await HostAsync(client);
            var sensors = new List<Guid>();
            for (var i = 0; i < 2; i++)
            {
                sensors.Add((await OkAsync(client, "sensor_create", new { hostId, type = "SIMPLE", name = $"s{i}" })).GetProperty("id").GetGuid());
            }
            Assert.AreEqual(2, (await OkAsync(client, "sensors_set_importance", new { sensorIds = sensors, importance = "High" })).GetProperty("changed").GetInt32());
            await using (var db = app.NewDb())
            {
                Assert.IsTrue(await db.Sensors.Where(s => sensors.Contains(s.Id)).AllAsync(s => s.Importance == Importance.High));
                var entry = await db.AuditLog.Where(l => l.EventType == AuditEvents.SensorsImportanceChanged && l.UserId == id).SingleAsync();
                Assert.AreEqual(AuditActor.Mcp, entry.Actor);
            }
            await OkAsync(client, "sensors_set_importance", new { sensorIds = sensors, hostImportance = true });
            await using (var db = app.NewDb())
            {
                Assert.IsTrue(await db.Sensors.Where(s => sensors.Contains(s.Id)).AllAsync(s => s.Importance == null), "the host's again");
            }
            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "sensors_set_importance", new { sensorIds = sensors }));
        }
    }

    [TestMethod]
    public async Task ARoutersJobsAndOnesOwnCommands()
    {
        var (id, _, client) = await WriterAsync();
        await using (client)
        {
            var hostId = await HostAsync(client);
            var sensorId = (await OkAsync(client, "sensor_create", new
            {
                hostId, type = "MIKROTIK", name = "core-gw", enabled = false, mikrotik = new { mode = "DirectSsh", address = "192.0.2.11" },
            })).GetProperty("id").GetGuid();
            var update = Guid.NewGuid();
            var mine = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            await using (var db = app.NewDb())
            {
                MikrotikJobEntity Job(Guid jobId, MikrotikJobKind kind, MikrotikJobState state, Guid? by) => new()
                {
                    Id = jobId, BatchId = Guid.NewGuid(), Kind = kind, SensorId = sensorId, RouterName = "core-gw", State = state, RequestedBy = by,
                    RequestedByName = "someone", CreatedAt = Clock.UtcNow, Command = kind == MikrotikJobKind.Command ? "/system identity print" : null,
                    RunAs = kind == MikrotikJobKind.Command ? "pnmt" : null, FromVersion = "7.19", ToVersion = "7.20", FinishedAt = Clock.UtcNow,
                };
                db.MikrotikJobs.AddRange(Job(update, MikrotikJobKind.Update, MikrotikJobState.Succeeded, id),
                    Job(mine, MikrotikJobKind.Command, MikrotikJobState.Succeeded, id), Job(theirs, MikrotikJobKind.Command, MikrotikJobState.Queued, Guid.NewGuid()));
                await db.SaveChangesAsync();
                db.MikrotikJobLog.Add(new MikrotikJobLogEntity { JobId = update, At = Clock.UtcNow, Step = MikrotikJobStep.Done, Outcome = MikrotikJobOutcome.Ok, Message = "Updated to 7.20" });
                await db.SaveChangesAsync();
            }
            var jobs = await OkAsync(client, "router_jobs", new { sensorId });
            Assert.AreEqual(update, jobs.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid(), "updates only");
            var job = await OkAsync(client, "router_jobs", new { sensorId, jobId = update });
            StringAssert.Contains(job.GetRawText(), "Updated to 7.20");

            var commands = await OkAsync(client, "mikrotik_commands_list", new { sensorId });
            CollectionAssert.IsSubsetOf(new[] { mine, theirs }, commands.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList());
            Assert.AreEqual("job_finished", await ErrorAsync(client, "mikrotik_command_cancel", new { id = mine }));
            await ErrorAsync(client, "mikrotik_command_cancel", new { id = theirs });
            await using (var db = app.NewDb())
            {
                Assert.AreEqual(MikrotikJobState.Queued, (await db.MikrotikJobs.AsNoTracking().SingleAsync(j => j.Id == theirs)).State, "not someone else's");
            }

            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "router_difference_acknowledge", new { sensorId, action = "acknowledge" }),
                "the differences' ids are needed");
            await ErrorAsync(client, "router_changelog", new { sensorId, version = "not-a-version" });
        }
    }

    [TestMethod]
    public async Task TheNewReadsAnswerAndKeepSensitivePagesOut()
    {
        var (_, _, client) = await WriterAsync();
        await using (client)
        {
            var hostId = await HostAsync(client);
            await OkAsync(client, "sensor_create", new { hostId, type = "SIMPLE", name = "coverage" });
            var coverage = await OkAsync(client, "notification_coverage", new { });
            Assert.IsTrue(coverage.GetProperty("total").GetInt32() >= 1);
            Assert.AreEqual(1, (await OkAsync(client, "notification_coverage", new { hostId })).GetProperty("sensors").GetProperty("total").GetInt32());
            await OkAsync(client, "os_updates_get", new { });
            await OkAsync(client, "certificate_authorities", new { });

            var page = await OkAsync(client, "kb_page_create", new { title = "Changes " + Guid.NewGuid().ToString("N")[..6], content = "# Printers\n\nThe toner." });
            var number = page.GetProperty("number").GetInt32();
            bool Listed(JsonElement changes) => changes.GetProperty("changes").EnumerateArray().Any(c => c.GetProperty("number").GetInt32() == number);
            Assert.IsTrue(Listed(await OkAsync(client, "kb_changes", new { })));
            await using (var db = app.NewDb())
            {
                await db.KbPages.Where(p => p.Number == number).ExecuteUpdateAsync(u => u.SetProperty(p => p.Sensitive, true));
            }
            Assert.IsFalse(Listed(await OkAsync(client, "kb_changes", new { })), "a sensitive page isn't there for machines");
        }
    }

    [TestMethod]
    public async Task LikesAndUsesAreReactionsWithoutAnAuditEntry()
    {
        var (id, _, client) = await WriterAsync();
        await using (client)
        {
            var number = (await OkAsync(client, "kb_page_create", new { title = "Toner " + Guid.NewGuid().ToString("N")[..6], content = "Change the toner." }))
                .GetProperty("number").GetInt32();
            var liked = await OkAsync(client, "kb_page_like", new { number, liked = true });
            Assert.AreEqual((1, true), (liked.GetProperty("likes").GetInt32(), liked.GetProperty("likedByMe").GetBoolean()));
            Assert.AreEqual(1, (await OkAsync(client, "kb_page_used", new { number, note = "fixed the printer" })).GetProperty("uses").GetInt32());
            Assert.AreEqual("used_recently", await ErrorAsync(client, "kb_page_used", new { number }));
            await using var db = app.NewDb();
            foreach (var tool in new[] { "kb_page_like", "kb_page_used" })
            {
                var call = await db.McpLog.AsNoTracking().Where(l => l.UserId == id && l.Tool == tool && l.Outcome == McpOutcome.Ok).FirstAsync();
                Assert.IsFalse(await db.AuditLog.AnyAsync(a => a.CorrelationId == call.CorrelationId), $"{tool}: a reaction, not an operation");
            }
            await db.KbPages.Where(p => p.Number == number).ExecuteUpdateAsync(u => u.SetProperty(p => p.Sensitive, true));
            Assert.AreEqual("not_found", await ErrorAsync(client, "kb_page_like", new { number, liked = false }));
        }
    }

    [TestMethod]
    public async Task AdvancedRulesAreChangedAndJudgedAtOnce()
    {
        var (id, _, client) = await WriterAsync();
        await using (client)
        {
            var hostId = await HostAsync(client);
            var sensorId = (await OkAsync(client, "sensor_create", new { hostId, type = "ADVANCED", name = "backup-app" })).GetProperty("id").GetGuid();
            string token;
            await using (var db = app.NewDb())
            {
                token = (await db.Sensors.AsNoTracking().SingleAsync(s => s.Id == sensorId)).SecretWriteToken;
            }
            using (var posted = await AdvancedTesting.PostAsync(app, token, """{"disk":{"free_gb":20}}"""))
            {
                Assert.IsTrue(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync());
            }
            await AdvancedTesting.WaitJudgedAsync(app, sensorId);

            var created = await OkAsync(client, "advanced_rules_change", new { sensorId, action = "create", name = "Free disk", path = "disk.free_gb", @operator = "Ge", operand = "5" });
            var rule = created.GetProperty("sensor").GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Free disk");
            Assert.AreEqual("Pass", rule.GetProperty("outcome").GetString(), "judged against the latest report at once");
            var ruleId = rule.GetProperty("id").GetGuid();
            var changed = await OkAsync(client, "advanced_rules_change", new { sensorId, action = "update", ruleId, operand = "50" });
            var after = changed.GetProperty("sensor").GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == ruleId);
            Assert.AreEqual("Fail", after.GetProperty("outcome").GetString(), "the path stays, the new operand fails it");
            StringAssert.Contains(after.GetProperty("condition").GetString(), "50");

            var test = await OkAsync(client, "advanced_test", new
            {
                sensorId, report = """{"disk":{"free_gb":2}}""", rules = new[] { new { name = "Some disk", path = "disk.free_gb", @operator = "Gt", operand = "1" } },
            });
            StringAssert.Contains(test.GetProperty("note").GetString(), "nothing was stored");
            Assert.AreEqual(1, (await OkAsync(client, "advanced_reports", new { sensorId })).GetProperty("items").GetArrayLength(), "the test stored nothing");

            var field = await OkAsync(client, "advanced_fields_change", new { sensorId, action = "create", name = "Free", path = "disk.free_gb", unit = "GB" });
            Assert.IsTrue(field.GetProperty("sensor").GetProperty("fields").EnumerateArray().Any(f => f.GetProperty("name").GetString() == "Free"));
            await OkAsync(client, "advanced_rules_change", new { sensorId, action = "remove", ruleId });
            await using (var db = app.NewDb())
            {
                Assert.IsFalse(await db.AdvancedRules.AnyAsync(r => r.Id == ruleId));
                var entries = await db.AuditLog.Where(l => l.UserId == id && (l.EventType == AuditEvents.AdvancedRulesChanged || l.EventType == AuditEvents.AdvancedFieldsChanged)).ToListAsync();
                Assert.AreEqual(4, entries.Count);
                Assert.IsTrue(entries.All(e => e.Actor == AuditActor.Mcp));
            }
        }
    }

    [TestMethod]
    public async Task ResponsibilitiesAreCreatedAndEditedViaMcp()
    {
        var (id, _, client) = await WriterAsync();
        await using (client)
        {
            var options = await OkAsync(client, "responsibility_options", new { });
            Assert.IsTrue(options.GetProperty("people").EnumerateArray().Any(p => p.GetProperty("id").GetGuid() == id));

            var created = await OkAsync(client, "responsibility_create", new { name = "Network " + Guid.NewGuid().ToString("N")[..6], responsibleId = id });
            var rid = created.GetProperty("id").GetGuid();
            Assert.AreEqual(id, created.GetProperty("responsible").GetProperty("id").GetGuid());
            var revision = (await OkAsync(client, "responsibility_get", new { id = rid })).GetProperty("editing").GetProperty("revision").GetInt32();
            await OkAsync(client, "responsibility_update", new { id = rid, summary = "Switches and routers", description = "# Network\n\nThe core switches.", baseRevision = revision });
            Assert.AreEqual("edit_conflict", await ErrorAsync(client, "responsibility_update", new { id = rid, description = "late", baseRevision = revision }));
            var current = (await OkAsync(client, "responsibility_get", new { id = rid })).GetProperty("editing").GetProperty("revision").GetInt32();
            Assert.AreEqual("looks_like_credential", await ErrorAsync(client, "responsibility_update", new { id = rid, description = "password: Sup3rS3cret!x9", baseRevision = current }));

            var hostId = await HostAsync(client);
            var linked = await OkAsync(client, "responsibility_link", new { id = rid, action = "link", hostId });
            Assert.AreEqual(1, linked.GetProperty("links").GetArrayLength());
            var unlinked = await OkAsync(client, "responsibility_link", new { id = rid, action = "unlink", hostId });
            Assert.AreEqual(0, unlinked.GetProperty("links").GetArrayLength());
            await ErrorAsync(client, "responsibility_redmine", new { id = rid, action = "add", projectId = 1, userId = id });

            await using var db = app.NewDb();
            var history = await db.ResponsibilityHistory.AsNoTracking().Where(h => h.ResponsibilityId == rid).ToListAsync();
            Assert.IsTrue(history.Count >= 4 && history.All(h => h.Via == "mcp"), "every change says via MCP");
            var audit = await db.AuditLog.Where(l => l.UserId == id && l.EventType.StartsWith("Responsibility")).ToListAsync();
            CollectionAssert.IsSubsetOf(new[] { AuditEvents.ResponsibilityCreated, AuditEvents.ResponsibilityChanged, AuditEvents.ResponsibilityLinked, AuditEvents.ResponsibilityUnlinked },
                audit.Select(a => a.EventType).ToList());
            Assert.IsTrue(audit.All(a => a.Actor == AuditActor.Mcp));
        }
    }

    [TestMethod]
    public async Task BoardMessagesComeFromThePersonViaMcpAndBoardsNeverWithTheirUrl()
    {
        var (id, browser, client) = await WriterAsync();
        await using (client)
        {
            using var created = await browser.SendAsync(HttpMethod.Post, "/api/v1/boards", new { name = "Wall " + Guid.NewGuid().ToString("N")[..6], columns = 1, from = "empty" });
            Assert.IsTrue(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            var board = await created.ReadAsync<BoardDetail>();
            var layout = board.Layout with { Look = board.Layout.Look with { Messages = true } };
            using (var saved = await browser.SendAsync(HttpMethod.Put, $"/api/v1/boards/{board.Id}", new
            {
                name = board.Name, description = (string?)null, enabled = true, networks = Array.Empty<object>(), layout, revision = board.Revision,
            }))
            {
                Assert.IsTrue(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            }
            using var url = await browser.SendAsync(HttpMethod.Get, $"/api/v1/boards/{board.Id}/url");
            var address = (await url.ReadAsync<BoardUrl>()).Url;
            var boardToken = address[(address.LastIndexOf('/') + 1)..];
            foreach (var (tool, args) in new (string, object)[] { ("boards_list", new { }), ("board_get", new { id = board.Id }) })
            {
                Assert.IsFalse((await OkAsync(client, tool, args)).GetRawText().Contains(boardToken, StringComparison.Ordinal), $"{tool} let the board's URL out");
            }

            var sent = await OkAsync(client, "board_message_send", new { boardIds = new[] { board.Id }, kind = "Alert", title = "Core switch maintenance at 14:00", seconds = 120 });
            var messageId = sent.GetProperty("id").GetGuid();
            Assert.AreEqual("Mcp", sent.GetProperty("source").GetString());
            await using (var db = app.NewDb())
            {
                var row = await db.BoardMessages.AsNoTracking().SingleAsync(m => m.Id == messageId);
                Assert.AreEqual((BoardMessageSource.Mcp, (Guid?)id), (row.Source, row.SentById));
                Assert.AreEqual(AuditActor.Mcp, (await db.AuditLog.SingleAsync(l => l.EventType == AuditEvents.BoardMessageSent && l.TargetId == messageId.ToString())).Actor);
                db.BoardMessages.Add(new BoardMessageEntity
                {
                    Id = Guid.NewGuid(), BoardIds = [board.Id], Kind = BoardMessageKind.Information, Title = "Someone else's", Seconds = 120, SentAt = Clock.UtcNow,
                    EndsAt = Clock.UtcNow.AddMinutes(2), Source = BoardMessageSource.Person, SentById = Guid.NewGuid(), SentByName = "Someone",
                });
                await db.SaveChangesAsync();
            }
            var listed = await OkAsync(client, "boards_list", new { });
            var others = listed.GetProperty("openMessages").EnumerateArray().Where(m => m.GetProperty("id").GetGuid() != messageId && m.GetProperty("boards").EnumerateArray().Any(b => b.GetProperty("id").GetGuid() == board.Id)).ToList();
            Assert.AreEqual("not_yours", await ErrorAsync(client, "board_message_withdraw", new { id = others.Single().GetProperty("id").GetGuid() }));
            Assert.IsFalse((await OkAsync(client, "board_message_withdraw", new { id = messageId })).GetProperty("open").GetBoolean());

            var lounge = await OkAsync(client, "lounge_read", new { });
            Assert.IsTrue(lounge.TryGetProperty("lines", out _));

            // Other classes count the open messages and the boards that show them: this one's go.
            await using (var db = app.NewDb())
            {
                await db.BoardMessages.Where(m => m.BoardIds.Contains(board.Id)).ExecuteDeleteAsync();
            }
            using var removed = await browser.SendAsync(HttpMethod.Delete, $"/api/v1/boards/{board.Id}");
            Assert.IsTrue(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        }
    }
}
