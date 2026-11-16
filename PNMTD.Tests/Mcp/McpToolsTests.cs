using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Mcp;
using PNMTD.Models.Db;
using PNMTD.Tests.Auth;
using PNMTD.Tools;

namespace PNMTD.Tests.Mcp;

/// <summary>
/// The tools over MCP (docs/mcp-plan.md §6, §7, §16): writes in the audit as MCP, patches that keep the rest, deleting in
/// its window, sensitive data and secrets that never come out, the catalog's own rules.
/// </summary>
[TestClass]
public sealed class McpToolsTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp(testSignIn: false, new Dictionary<string, string?>
    {
        ["Mcp:Enabled"] = "true",
        ["Mcp:DeletesPerWindow"] = "2",
        ["Development:DoNotSendNotifications"] = "true",
    });

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static FakeTimeProvider time = null!;

    [TestInitialize]
    public void Freeze()
    {
        time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Clock.Provider = time;
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    /// <summary>A person with 2FA and their read & write token's client.</summary>
    private static async Task<(Guid Id, Browser Browser, McpClient Client, McpTokenIssued Token)> WriterAsync(string role = Roles.User)
    {
        var (id, _, _, browser) = await McpServerTests.PersonAsync(app, role);
        await TwoFactorLoginTests.EnrollAsync(browser);
        var token = await McpServerTests.TurnOnAsync(browser, "write");
        return (id, browser, await McpServerTests.ClientAsync(app, token.Value!), token);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, object args)
    {
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var result = await client.CallToolAsync(tool, dictionary);
        return McpServerTests.Json(result);
    }

    private static async Task<JsonElement> OkAsync(McpClient client, string tool, object args)
    {
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var result = await client.CallToolAsync(tool, dictionary);
        Assert.IsFalse(result.IsError ?? false, $"{tool}: {((TextContentBlock)result.Content[0]).Text}");
        return McpServerTests.Json(result);
    }

    [TestMethod]
    public void TheCatalogKeepsItsOwnRules()
    {
        var all = ToolCatalog.All;
        Assert.AreEqual(all.Count, all.Select(t => t.Name).Distinct().Count(), "unique names");
        Assert.IsTrue(all.All(t => System.Text.RegularExpressions.Regex.IsMatch(t.Name, "^[a-z][a-z0-9_]*$")), "snake case");
        Assert.IsTrue(all.Where(t => t.Writes).All(t => t.AuditEvents.Count > 0 && t.AuditEvents.All(AuditCatalog.Contains)),
            "every change declares its events: " + string.Join(", ", all.Where(t => t.Writes && (t.AuditEvents.Count == 0 || !t.AuditEvents.All(AuditCatalog.Contains))).Select(t => t.Name)));
        Assert.IsTrue(all.Where(t => t.Kind == ToolKind.Delete).All(t => t.Destructive || t.Kind == ToolKind.Delete));
        Assert.IsFalse(all.Any(t => t.Level == PNMTD.Auth.Rights.Level.Manage || t.Requires.Any(r => r.Level == PNMTD.Auth.Rights.Level.Manage)), "no Manage level");
        Assert.IsTrue(all.Where(t => t.Area is { } a && !t.CookieOnly).All(t => McpRights.Ceiling.ContainsKey(t.Area!.Value)), "every area is within MCP's ceiling");
        Assert.IsTrue(all.Where(t => t.CookieOnly).All(t => t.ForCookie), "a tool only for Cookie is Cookie's");
        Assert.IsTrue(all.Where(t => t.ForCookie).All(t => t.Kind == ToolKind.Read), "Cookie only reads");
        // The access watch (docs/cookie-access-plan.md §5.2) added four: logins, PNMT's sign-ins (Cookie's only), a profile, an account everywhere.
        CollectionAssert.AreEquivalent(new[] { "cases_list", "case_get", "hosts_search", "host_get", "sensor_events", "location_get", "kb_search", "kb_page",
            "ticket_get", "zabbix_problems", "availability_get", "logins_list", "pnmt_signins", "access_profile", "account_logins" },
            all.Where(t => t.ForCookie).Select(t => t.Name).ToList(), "Cookie keeps its fifteen");
        foreach (var tool in all)
        {
            Assert.AreEqual("object", tool.InputSchema["type"]!.GetValue<string>(), tool.Name);
        }
    }

    [TestMethod]
    public void TheReadOnlyCapAndTheCeilingHoldForEveryArea()
    {
        var admin = new PNMTD.Auth.Rights.UserRights(true, new Dictionary<PNMTD.Auth.Rights.Area, PNMTD.Auth.Rights.Level>());
        var write = McpRights.For(admin, McpTokenKind.Write);
        var read = McpRights.For(admin, McpTokenKind.Read);
        Assert.IsFalse(write.IsAdmin || read.IsAdmin, "never the admin flag");
        Assert.AreEqual(PNMTD.Auth.Rights.Level.Edit, write.Levels[PNMTD.Auth.Rights.Area.Monitoring]);
        Assert.AreEqual(PNMTD.Auth.Rights.Level.Details, read.Levels[PNMTD.Auth.Rights.Area.Monitoring], "the read-only cap reaches Details, a reading level");
        Assert.IsFalse(read.Levels.ContainsKey(PNMTD.Auth.Rights.Area.Commands));
        foreach (var area in Enum.GetValues<PNMTD.Auth.Rights.Area>())
        {
            var info = PNMTD.Auth.Rights.RightsCatalog.Of(area);
            foreach (var level in new[] { PNMTD.Auth.Rights.Level.Manage, PNMTD.Auth.Rights.Level.Decide })
            {
                if (info.Rank(level) > 0)
                {
                    Assert.IsFalse(write.Has(area, level), $"{area}: {level}");
                }
            }
        }
        Assert.IsFalse(write.Levels.ContainsKey(PNMTD.Auth.Rights.Area.Decisions));
        var viewer = new PNMTD.Auth.Rights.UserRights(false, new Dictionary<PNMTD.Auth.Rights.Area, PNMTD.Auth.Rights.Level> { [PNMTD.Auth.Rights.Area.Monitoring] = PNMTD.Auth.Rights.Level.View });
        Assert.IsFalse(McpRights.For(viewer, McpTokenKind.Write).Has(PNMTD.Auth.Rights.Area.Monitoring, PNMTD.Auth.Rights.Level.Edit), "never more than the user");
        Assert.IsFalse(McpRights.For(viewer, McpTokenKind.Read).Has(PNMTD.Auth.Rights.Area.Monitoring, PNMTD.Auth.Rights.Level.Details), "a viewer's read-only token stays at View");
    }

    [TestMethod]
    public async Task AWriteIsInTheAuditAsMcpWithItsChanges()
    {
        var (id, _, client, token) = await WriterAsync();
        await using (client)
        {
            var host = await OkAsync(client, "host_create", new { name = "mcp-host-" + Guid.NewGuid().ToString("N")[..8], notes = "first" });
            var hostId = host.GetProperty("id").GetGuid();
            // A frozen clock gives both entries the same time; a step keeps their order PostgreSQL's to keep.
            time.Advance(TimeSpan.FromMilliseconds(1));
            var changed = await OkAsync(client, "host_update", new { id = hostId, notes = "second" });
            Assert.AreEqual("second", changed.GetProperty("notes").GetString());
            Assert.AreEqual(host.GetProperty("name").GetString(), changed.GetProperty("name").GetString(), "the rest stays");

            await using var db = app.NewDb();
            var entries = await db.AuditLog.Where(l => l.Subjects.Contains(hostId)).OrderBy(l => l.CreatedAt).ToListAsync();
            Assert.AreEqual(2, entries.Count);
            Assert.IsTrue(entries.All(e => e.Actor == AuditActor.Mcp && e.UserId == id && e.ActorRef!.EndsWith(":rw", StringComparison.Ordinal)));
            CollectionAssert.AreEqual(new[] { AuditEvents.HostCreated, AuditEvents.HostUpdated }, entries.Select(e => e.EventType).ToArray());
            StringAssert.Contains(entries[1].Changes, "second");
            var log = await db.McpLog.SingleAsync(l => l.CorrelationId == entries[1].CorrelationId && l.Tool == "host_update");
            Assert.AreEqual(McpOutcome.Ok, log.Outcome);
        }
        _ = token;
    }

    [TestMethod]
    public async Task ASensorComesBackWithoutItsTokensAndAPatchKeepsTheRest()
    {
        var (_, _, client, _) = await WriterAsync();
        await using (client)
        {
            var host = await OkAsync(client, "host_create", new { name = "s-host-" + Guid.NewGuid().ToString("N")[..8] });
            var sensor = await OkAsync(client, "sensor_create", new { hostId = host.GetProperty("id").GetGuid(), type = "HEARTBEAT", name = "beat", intervalSeconds = 300 });
            var id = sensor.GetProperty("id").GetGuid();
            await using var db = app.NewDb();
            var row = await db.Sensors.AsNoTracking().SingleAsync(s => s.Id == id);
            Assert.IsFalse(sensor.GetRawText().Contains(row.SecretWriteToken, StringComparison.Ordinal), "no write token");

            var patched = await OkAsync(client, "sensor_update", new { id, ignore = true });
            Assert.AreEqual((true, 300, "beat"), (patched.GetProperty("ignore").GetBoolean(), patched.GetProperty("intervalSeconds").GetInt32(), patched.GetProperty("name").GetString()));

            var refused = await CallAsync(client, "sensor_create", new { hostId = host.GetProperty("id").GetGuid(), type = "MIKROTIK", name = "router" });
            Assert.AreEqual("invalid_argument", refused.GetProperty("error").GetString());
        }
    }

    [TestMethod]
    public async Task DeletingNeedsTheWindowAndStopsAtItsLimit()
    {
        var (id, browser, client, _) = await WriterAsync();
        await using (client)
        {
            var host = await OkAsync(client, "host_create", new { name = "d-host-" + Guid.NewGuid().ToString("N")[..8] });
            var hostId = host.GetProperty("id").GetGuid();
            var sensors = new List<Guid>();
            for (var i = 0; i < 2; i++)
            {
                sensors.Add((await OkAsync(client, "sensor_create", new { hostId, type = "SIMPLE", name = $"s{i}" })).GetProperty("id").GetGuid());
            }

            var outside = await CallAsync(client, "sensor_delete", new { id = sensors[0] });
            Assert.AreEqual("delete_not_allowed", outside.GetProperty("error").GetString());
            await using (var db = app.NewDb())
            {
                Assert.AreEqual(0, await db.McpLog.CountAsync(l => l.UserId == id && l.Strike), "asking is no strike");
            }

            using (var open = await browser.PostAsync("/api/v1/account/mcp/write/delete-window"))
            {
                Assert.AreEqual(System.Net.HttpStatusCode.OK, open.StatusCode, await open.Content.ReadAsStringAsync());
            }
            var withSensors = await CallAsync(client, "host_delete", new { id = hostId });
            Assert.AreEqual("host_has_sensors", withSensors.GetProperty("error").GetString());
            Assert.AreEqual(2, withSensors.GetProperty("sensors").GetArrayLength());

            var deleted = await OkAsync(client, "sensor_delete", new { id = sensors[1] });
            Assert.AreEqual("s1", deleted.GetProperty("deleted").GetProperty("name").GetString());
            var limit = await CallAsync(client, "sensor_delete", new { id = sensors[0] });
            Assert.AreEqual("delete_limit_reached", limit.GetProperty("error").GetString(), "the host's refusal counted too: 2 of 2");

            await using (var db = app.NewDb())
            {
                Assert.IsFalse(await db.Sensors.AnyAsync(s => s.Id == sensors[1]));
                Assert.IsTrue(await db.Sensors.AnyAsync(s => s.Id == sensors[0]));
                var entry = await db.AuditLog.SingleAsync(l => l.EventType == AuditEvents.SensorDeleted && l.TargetId == sensors[1].ToString());
                Assert.AreEqual(AuditActor.Mcp, entry.Actor);
                StringAssert.Contains(entry.Changes, "SIMPLE", "the sensor as it was");
            }
            using (var stop = await browser.SendAsync(HttpMethod.Delete, "/api/v1/account/mcp/write/delete-window"))
            {
                Assert.AreEqual(System.Net.HttpStatusCode.OK, stop.StatusCode);
            }
            Assert.AreEqual("delete_not_allowed", (await CallAsync(client, "sensor_delete", new { id = sensors[0] })).GetProperty("error").GetString());
        }
    }

    [TestMethod]
    public async Task APageMarkedSensitiveDoesntExistForMcpAndCredentialsAreRefused()
    {
        var (_, _, client, _) = await WriterAsync();
        await using (client)
        {
            var page = await OkAsync(client, "kb_page_create", new { title = "MCP page " + Guid.NewGuid().ToString("N")[..6], content = "# Switches\n\nThe core switch." });
            var number = page.GetProperty("number").GetInt32();
            var read = await OkAsync(client, "kb_page", new { number });
            var saved = await OkAsync(client, "kb_page_update", new { number, revision = page.GetProperty("revision").GetInt32(), content = "# Switches\n\nTwo core switches." });
            Assert.AreEqual(page.GetProperty("revision").GetInt32() + 1, saved.GetProperty("revision").GetInt32());
            var conflict = await CallAsync(client, "kb_page_update", new { number, revision = page.GetProperty("revision").GetInt32(), content = "late" });
            Assert.AreEqual("edit_conflict", conflict.GetProperty("error").GetString());

            var credential = await CallAsync(client, "kb_page_create", new { title = "Router access", content = "password: Sup3rS3cret!x9" });
            Assert.AreEqual("looks_like_credential", credential.GetProperty("error").GetString());

            await using (var db = app.NewDb())
            {
                Assert.AreEqual("mcp", await db.KbRevisions.Where(r => r.PageId == read.GetProperty("id").GetGuid()).OrderByDescending(r => r.Number).Select(r => r.Via).FirstAsync());
                await db.KbPages.Where(p => p.Number == number).ExecuteUpdateAsync(u => u.SetProperty(p => p.Sensitive, true));
            }
            Assert.AreEqual("not_found", (await CallAsync(client, "kb_page", new { number })).GetProperty("error").GetString());
            Assert.AreEqual("not_found", (await CallAsync(client, "kb_page_update", new { number, revision = 2, content = "x" })).GetProperty("error").GetString());
            var search = await OkAsync(client, "search", new { query = $"KB-{number}" });
            Assert.AreEqual(0, search.GetProperty("pages").GetArrayLength());
        }
    }

    [TestMethod]
    public async Task RecipientsAreMaskedAndASensitiveRevisionStaysOut()
    {
        var (_, browser, client, _) = await WriterAsync(Roles.Admin);
        await using (client)
        {
            using (var rule = await browser.PostAsync("/api/v1/notification-rules", new { name = "MCP mail rule " + Guid.NewGuid().ToString("N")[..6], recipient = "anna.muster@example.com", type = "ALWAYS" }))
            {
                Assert.AreEqual(System.Net.HttpStatusCode.Created, rule.StatusCode, await rule.Content.ReadAsStringAsync());
            }
            var recipients = (await OkAsync(client, "notification_rules_list", new { limit = 100 })).GetProperty("items").EnumerateArray()
                .Select(r => r.GetProperty("recipient").GetString()!).ToList();
            CollectionAssert.Contains(recipients, "a…@example.com");
            Assert.IsFalse(recipients.Any(r => r.Contains("anna.muster", StringComparison.Ordinal) || r.Contains("root@", StringComparison.Ordinal)), "no e-mail address");

            var page = await OkAsync(client, "kb_page_create", new { title = "Revisions " + Guid.NewGuid().ToString("N")[..6], content = "one" });
            var number = page.GetProperty("number").GetInt32();
            var second = await OkAsync(client, "kb_page_update", new { number, revision = page.GetProperty("revision").GetInt32(), content = "two: was sensitive" });
            await OkAsync(client, "kb_page_update", new { number, revision = second.GetProperty("revision").GetInt32(), content = "three" });
            await using (var db = app.NewDb())
            {
                // As if the page had been marked sensitive while revision 2 was current.
                await db.KbRevisions.Where(r => r.PageId == page.GetProperty("id").GetGuid() && r.Number == second.GetProperty("revision").GetInt32())
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.Sensitive, true));
            }
            var revisions = await OkAsync(client, "kb_revisions", new { number });
            Assert.IsFalse(revisions.EnumerateArray().Any(r => r.GetProperty("number").GetInt32() == second.GetProperty("revision").GetInt32()));
            var diff = await CallAsync(client, "kb_revisions", new { number, from = second.GetProperty("revision").GetInt32() });
            Assert.AreEqual("not_found", diff.GetProperty("error").GetString());
        }
    }

    /// <summary>
    /// The canary test (docs/mcp-plan.md §6.4): secrets of every kind seeded, every read tool called with broad arguments as an
    /// admin's read & write token; no canary in any result, nor in the MCP log.
    /// </summary>
    [TestMethod]
    public async Task NoSecretOrSensitiveContentEverComesOut()
    {
        var (_, _, client, _) = await WriterAsync(Roles.Admin);
        await using (client)
        {
            var canaryPage = "canary-page-" + Guid.NewGuid().ToString("N");
            var canaryField = "canary-field-" + Guid.NewGuid().ToString("N");
            var host = await OkAsync(client, "host_create", new { name = "canary-host-" + Guid.NewGuid().ToString("N")[..8] });
            var hostId = host.GetProperty("id").GetGuid();
            var udp = await OkAsync(client, "sensor_create", new { hostId, type = "UDP_NODE", name = "canary-node", intervalSeconds = 600 });
            var simple = await OkAsync(client, "sensor_create", new { hostId, type = "SIMPLE", name = "canary-simple" });
            var canaryTag = "canary" + Guid.NewGuid().ToString("N")[..10];
            var page = await OkAsync(client, "kb_page_create", new { title = "Canary page", content = canaryPage, tags = new[] { canaryTag } });
            var location = await OkAsync(client, "location_create", new { name = "canary-location-" + Guid.NewGuid().ToString("N")[..6] });
            var locationId = location.GetProperty("id").GetGuid();
            await OkAsync(client, "hosts_set_location", new { hostIds = new[] { hostId }, locationId });

            var canaries = new List<string> { canaryPage, canaryField, canaryTag };
            await using (var db = app.NewDb())
            {
                foreach (var s in await db.Sensors.AsNoTracking().Where(s => s.ParentId == hostId).ToListAsync())
                {
                    canaries.Add(s.SecretWriteToken);
                    if (s.SecretReadToken is { } r)
                    {
                        canaries.Add(r);
                    }
                }
                var node = await db.UdpNodes.AsNoTracking().SingleAsync(n => n.SensorId == udp.GetProperty("id").GetGuid());
                canaries.Add(Convert.ToHexString(node.Key));
                canaries.Add(Convert.ToBase64String(node.Key));
                await db.KbPages.Where(p => p.Id == page.GetProperty("id").GetGuid()).ExecuteUpdateAsync(u => u.SetProperty(p => p.Sensitive, true));
                var field = new LocationFieldEntity { Id = Guid.NewGuid(), Key = "canary-" + Guid.NewGuid().ToString("N")[..8], Label = "Canary", Type = LocationFieldType.Text, Sensitive = true, Position = 999 };
                db.LocationFields.Add(field);
                db.LocationValues.Add(new LocationValueEntity { LocationId = locationId, FieldId = field.Id, Value = LocationValues.StringValue(canaryField), Revision = 1, UpdatedAt = Clock.UtcNow });
                await db.SaveChangesAsync();
            }

            // What the seeding itself sent (the page's text) is in the log as its own arguments; the reads after it must add nothing.
            long seeded;
            await using (var db = app.NewDb())
            {
                seeded = await db.McpLog.MaxAsync(l => l.Id);
            }
            var calls = new (string Tool, object Args)[]
            {
                ("whoami", new { }), ("dashboard_get", new { hours = 72 }), ("search", new { query = "canary" }), ("hosts_search", new { query = "canary" }),
                ("host_get", new { id = hostId }), ("sensors_list", new { hostId, limit = 100 }), ("sensor_get", new { id = udp.GetProperty("id").GetGuid() }),
                ("sensor_get", new { id = simple.GetProperty("id").GetGuid() }), ("sensor_events", new { id = simple.GetProperty("id").GetGuid() }),
                ("state_changes", new { hostId }), ("locations_list", new { query = "canary" }), ("location_get", new { id = locationId }),
                ("location_details", new { id = locationId }), ("kb_search", new { query = "canary" }), ("kb_page", new { number = page.GetProperty("number").GetInt32() }),
                ("udp_node_get", new { sensorId = udp.GetProperty("id").GetGuid() }), ("cases_list", new { }),
                ("history_get", new { id = hostId }), ("history_get", new { id = udp.GetProperty("id").GetGuid() }), ("notification_rules_list", new { }),
                ("uplink_get", new { }), ("dns_zones_list", new { }), ("mail_rules_list", new { }), ("mails_search", new { }), ("servers_list", new { }),
                ("diagnostics_runs", new { }), ("kb_revisions", new { number = page.GetProperty("number").GetInt32() }), ("kb_tags", new { }),
                ("kb_tags", new { query = "canary" }), ("cookie_decisions", new { }), ("cookie_trace", new { }),
            };
            foreach (var (tool, args) in calls)
            {
                var text = (await CallAsync(client, tool, args)).GetRawText();
                foreach (var canary in canaries)
                {
                    Assert.IsFalse(text.Contains(canary, StringComparison.OrdinalIgnoreCase), $"{tool} let a secret out");
                }
            }
            await using (var db = app.NewDb())
            {
                var everything = string.Join("\n", (await db.McpLog.AsNoTracking().Where(l => l.Id > seeded).ToListAsync())
                    .Select(l => l.Arguments + l.Summary + McpLog.Unpack(l.Result)));
                foreach (var canary in canaries)
                {
                    Assert.IsFalse(everything.Contains(canary, StringComparison.OrdinalIgnoreCase), "the MCP log holds a secret");
                }
            }
        }
    }
}
