using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Api.V1;

/// <summary>Notification rules, DNS zones, mail input rules, the mail log and key values (§3.7, §8.3).</summary>
[TestClass]
public sealed class ConfigurationApiTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient viewer = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
        viewer = app.Client(Roles.Viewer);
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    [DataRow("ops@example.com", "email")]
    [DataRow("pushover://uQiRzpo4DXghDmr9QzzfQu27cmVRsG", "pushover")]
    [DataRow("https://hooks.example.com/services/T000/B000/XXXX", "http")]
    public async Task RulesMatchAProvider(string recipient, string provider)
    {
        var rule = await (await user.PostJsonAsync("/api/v1/notification-rules", new { name = V1.Unique("rule"), recipient, type = "ONLY_FAILURES" }))
            .ReadAsync<NotificationRuleDetail>();
        Assert.AreEqual((provider, NotificationRuleType.ONLY_FAILURES, recipient), (rule.Provider, rule.Type, rule.Recipient));

        // A Viewer sees where it goes, not the keys in the address.
        var seen = await (await viewer.GetAsync($"/api/v1/notification-rules/{rule.Id}")).ReadAsync<NotificationRuleDetail>();
        Assert.AreEqual(provider switch
        {
            "pushover" => "pushover://…",
            "http" => "https://hooks.example.com/…",
            _ => recipient,
        }, seen.Recipient);
        await user.DeleteAsync($"/api/v1/notification-rules/{rule.Id}");
    }

    [TestMethod]
    public async Task RulesRejectRecipientsNoProviderTakes()
    {
        using var response = await user.PostJsonAsync("/api/v1/notification-rules", new { name = "nowhere", recipient = "not an address" });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsTrue((await V1.ErrorsOf(response)).TryGetProperty("recipient", out _));
    }

    [TestMethod]
    public async Task RulesListTheirSensorsWithHostNames()
    {
        var host = await V1.CreateHostAsync(user);
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        var rule = await (await user.PostJsonAsync("/api/v1/notification-rules", new { name = V1.Unique("rule"), recipient = "ops@example.com" }))
            .ReadAsync<NotificationRuleDetail>();

        Assert.AreEqual(HttpStatusCode.NoContent, (await user.PutAsync($"/api/v1/notification-rules/{rule.Id}/sensors/{sensor.Id}", null)).StatusCode);
        var detail = await (await viewer.GetAsync($"/api/v1/notification-rules/{rule.Id}")).ReadAsync<NotificationRuleDetail>();
        var linked = detail.Sensors.Single();
        Assert.AreEqual((sensor.Id, sensor.Name, host.Id, host.Name), (linked.SensorId, linked.SensorName, linked.HostId, linked.HostName));
        var list = await (await viewer.GetAsync($"/api/v1/notification-rules?search={rule.Name}")).ReadAsync<Paged<NotificationRuleItem>>();
        Assert.AreEqual(1, list.Items.Single().SensorCount);

        Assert.AreEqual(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/notification-rules/{rule.Id}")).StatusCode);
        using var db = app.NewDb();
        Assert.IsFalse(await db.NotificationRuleSensor.AnyAsync(l => l.NotificationRuleId == rule.Id));
        await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
    }

    [TestMethod]
    public async Task ZonesTakeTheirNameFromTheZoneFile()
    {
        var zone = await (await user.PostJsonAsync("/api/v1/dns-zones", new { zoneFileContent = DbTestHelper.DnsExampleZoneFile, interval = 900 }))
            .ReadAsync<DnsZoneDetail>();

        Assert.AreEqual("example.com", zone.ZoneName);
        Assert.IsTrue(zone.RequiresProcessing);
        Assert.AreEqual(900, zone.Interval);
        Assert.IsNull(zone.LastChecked);

        using var invalid = await user.PostJsonAsync("/api/v1/dns-zones", new { zoneFileContent = "just text", interval = 10 });
        var errors = await V1.ErrorsOf(invalid);
        Assert.IsTrue(errors.TryGetProperty("zoneFileContent", out _));
        Assert.IsTrue(errors.TryGetProperty("interval", out _));

        Assert.AreEqual(HttpStatusCode.NoContent, (await user.PostAsync($"/api/v1/dns-zones/{zone.Id}/force-update", null)).StatusCode);
        Assert.IsTrue((await (await viewer.GetAsync($"/api/v1/dns-zones/{zone.Id}")).ReadAsync<DnsZoneDetail>()).ForceUpdate);
        await user.DeleteAsync($"/api/v1/dns-zones/{zone.Id}");
    }

    [TestMethod]
    public async Task ZoneRecordsCanBeIgnoredAndDeletionTakesLogAndSensor()
    {
        var host = await V1.CreateHostAsync(user);
        var zone = await (await user.PostJsonAsync("/api/v1/dns-zones", new { zoneFileContent = DbTestHelper.DnsExampleZoneFile, hostId = host.Id }))
            .ReadAsync<DnsZoneDetail>();
        var recordId = Guid.NewGuid();
        using (var db = app.NewDb())
        {
            // What DnsCheckTask writes: a record, a log line and the zone's sensor.
            db.DnsZoneEntries.Add(new DnsZoneEntryEntity { Id = recordId, DnsZoneId = zone.Id, Name = "www.example.com.", RecordType = DnsZoneResourceType.A,
                ReferenceValue = "203.0.113.12", TTL = 300, Updated = Clock.UtcNow });
            db.DnsZoneLogEntries.Add(new DnsZoneLogEntryEntity { Id = Guid.NewGuid(), DnsZoneId = zone.Id, Created = Clock.UtcNow, Message = "mismatch",
                EntryType = DnsZoneLogEntryType.ENTRY_DISCREPANCY });
            db.Sensors.Add(new SensorEntity { Id = Guid.NewGuid(), ParentId = host.Id, Type = SensorType.DNS, Name = "example.com", TextId = zone.Id.ToString(),
                Enabled = true, Created = Clock.UtcNow, SecretWriteToken = Guid.NewGuid().ToString() });
            await db.SaveChangesAsync();
        }

        var record = await (await user.PutJsonAsync($"/api/v1/dns-zones/{zone.Id}/records/{recordId}", new { ignore = true })).ReadAsync<DnsRecord>();
        Assert.IsTrue(record.Ignore);
        var list = await (await viewer.GetAsync("/api/v1/dns-zones?search=example.com&pageSize=500")).ReadAsync<Paged<DnsZoneItem>>();
        var item = list.Items.Single(z => z.Id == zone.Id);
        Assert.AreEqual((1, 1, host.Name), (item.RecordCount, item.IgnoredCount, item.Host!.Name));
        var log = await (await viewer.GetAsync($"/api/v1/dns-zones/{zone.Id}/log")).ReadAsync<Paged<DnsLogEntry>>();
        Assert.AreEqual(("mismatch", "example.com"), (log.Items.Single().Message, log.Items[0].ZoneName));

        Assert.AreEqual(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/dns-zones/{zone.Id}")).StatusCode);
        using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.DnsZoneLogEntries.AnyAsync(l => l.DnsZoneId == zone.Id));
            Assert.IsFalse(await db.Sensors.AnyAsync(s => s.TextId == zone.Id.ToString()));
        }
        await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
    }

    [TestMethod]
    public async Task MailRulesCheckTheirExpressionsAndNameTheirSensor()
    {
        var host = await V1.CreateHostAsync(user);
        var sensor = await V1.CreateSensorAsync(user, host.Id);
        var rule = await (await user.PostJsonAsync("/api/v1/mail-input-rules", new
        {
            name = "backup", fromTest = "backup@", okTest = "success", okCode = 200, failTest = "failed", failCode = 500,
            extractMessageRegex = " Result: (?<msg>.*) ", sensorId = sensor.Id,
        })).ReadAsync<MailInputRule>();
        Assert.AreEqual((sensor.Name, host.Name), (rule.Sensor!.Name, rule.Sensor.HostName));
        Assert.AreEqual("Result: (?<msg>.*)", rule.ExtractMessageRegex);

        using var invalid = await user.PostJsonAsync("/api/v1/mail-input-rules", new { name = "bad", fromTest = "(", okTest = "x" });
        var errors = await V1.ErrorsOf(invalid);
        Assert.IsTrue(errors.TryGetProperty("fromTest", out _));
        Assert.IsTrue(errors.TryGetProperty("okCode", out _));
        await user.DeleteAsync($"/api/v1/mail-input-rules/{rule.Id}");
        await user.DeleteAsync($"/api/v1/hosts/{host.Id}");
    }

    [TestMethod]
    public async Task TheMailLogIsNewestFirstAndNotForViewers()
    {
        var rule = await (await user.PostJsonAsync("/api/v1/mail-input-rules", new { name = V1.Unique("mail"), fromTest = "backup@" })).ReadAsync<MailInputRule>();
        var subject = V1.Unique("subject");
        using (var db = app.NewDb())
        {
            for (var i = 0; i < 3; i++)
            {
                db.MailLogs.Add(new MailLogEntity { Id = Guid.NewGuid(), RemoteId = $"r{i}", Created = Clock.UtcNow.AddMinutes(i), MessageDate = Clock.Never,
                    From = "backup@example.com", To = "pnmt@example.com", Subject = $"{subject} {i}", Content = "Result: success",
                    Processed = i > 0, ProcessedById = i > 0 ? rule.Id : null, ProcessLog = "" });
            }
            await db.SaveChangesAsync();
        }

        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/mail-log")).StatusCode);
        var log = await (await user.GetAsync($"/api/v1/mail-log?search={subject}")).ReadAsync<Paged<MailLogItem>>();
        CollectionAssert.AreEqual(new[] { $"{subject} 2", $"{subject} 1", $"{subject} 0" }, log.Items.Select(m => m.Subject).ToArray());
        Assert.AreEqual(rule.Name, log.Items[0].ProcessedBy!.Name);
        Assert.IsNull(log.Items[2].MessageDate);
        var unprocessed = await (await user.GetAsync($"/api/v1/mail-log?search={subject}&processed=false")).ReadAsync<Paged<MailLogItem>>();
        Assert.AreEqual(1, unprocessed.TotalCount);
        var detail = await (await user.GetAsync($"/api/v1/mail-log/{log.Items[0].Id}")).ReadAsync<MailLogDetail>();
        Assert.AreEqual("Result: success", detail.Content);

        // Deleting the rule keeps its mail, without the name.
        await user.DeleteAsync($"/api/v1/mail-input-rules/{rule.Id}");
        Assert.IsNull((await (await user.GetAsync($"/api/v1/mail-log/{log.Items[0].Id}")).ReadAsync<MailLogDetail>()).ProcessedBy);
    }

    [TestMethod]
    public async Task KeyValuesAreUniqueLowerCaseAndForAdminsOnly()
    {
        var key = V1.Unique("Setting");
        var created = await (await admin.PostJsonAsync("/api/v1/key-values", new { key, value = "1", note = "n" })).ReadAsync<KeyValueItem>();
        Assert.AreEqual(key.ToLowerInvariant(), created.Key);

        using var duplicate = await admin.PostJsonAsync("/api/v1/key-values", new { key = key.ToUpperInvariant(), value = "2" });
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.AreEqual("duplicate_key", await V1.CodeOf(duplicate));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/key-values")).StatusCode);

        var updated = await (await admin.PutJsonAsync($"/api/v1/key-values/{created.Id}", new { key, value = "3", enabled = false })).ReadAsync<KeyValueItem>();
        Assert.AreEqual(("3", false), (updated.Value, updated.Enabled));
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/key-values/{created.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task TaskWrittenKeyValuesAreReadOnlyAndDescribed()
    {
        using (var db = app.NewDb())
        {
            db.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_DNS_CHECK);
            db.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_DNS_CHECK);
        }
        var list = await (await admin.GetAsync("/api/v1/key-values?search=last_dns_check")).ReadAsync<Paged<KeyValueItem>>();
        var entry = list.Items.Single();
        Assert.IsTrue(entry.IsReadOnly);
        Assert.IsNotNull(entry.Description);
        Assert.IsTrue(DateTime.TryParse(entry.Value, out _));

        using var put = await admin.PutJsonAsync($"/api/v1/key-values/{entry.Id}", new { key = entry.Key, value = "x" });
        Assert.AreEqual(HttpStatusCode.Conflict, put.StatusCode);
        Assert.AreEqual("read_only", await V1.CodeOf(put));
    }
}
