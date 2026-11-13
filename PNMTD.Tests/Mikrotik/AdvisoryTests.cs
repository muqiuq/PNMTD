using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Ai;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Events;
using PNMTD.Tests.Ai;

namespace PNMTD.Tests.Mikrotik;

/// <summary>RouterOS advisories (docs/ai-operations-plan.md §10): MikroTik's feed, the assessment, and the routers behind.</summary>
[TestClass]
public sealed class AdvisoryTests
{
    private static readonly FakeOpenRouter router = new();
    private static TestApp app = null!;
    private static bool verifierAgrees = true;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: FakeOpenRouter.Settings, services: s => router.Install(s));
        router.Answer = (schema, request) =>
        {
            if (schema != nameof(ReleaseRating))
            {
                return verifierAgrees ? """{"answer":"agree","issues":[]}""" : """{"answer":"disagree","issues":["no sign of exploitation"]}""";
            }
            var data = JsonNode.Parse(request["messages"]![1]!["content"]!.GetValue<string>())!["data"]!;
            var cve = System.Text.RegularExpressions.Regex.Match(data["untrustedChangelog"]!.GetValue<string>(), @"CVE-\d{4}-\d+").Value;
            return new JsonObject
            {
                ["severity"] = "critical", ["zeroDay"] = false, ["exploitedInTheWild"] = true, ["remoteWithoutCredentials"] = true,
                ["cves"] = new JsonArray(cve), ["areas"] = new JsonArray("www"), ["summary"] = "Fixes a remotely exploitable flaw.", ["reasoning"] = "Exploited.",
            }.ToJsonString();
        };
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikrotik", "releases-2026-11-13.rss"));

    private static string Feed(string version, string channel, string lines) => $$"""
        <?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel>
        <item><title>RouterOS {{version}} [{{channel}}]</title><content:encoded><![CDATA[What's new in {{version}}:<br><br>{{lines}}]]></content:encoded>
        <guid>RouterOS {{version}} [{{channel}}]</guid><pubDate>Mon, 02 Nov 2026 11:44:38 +0000</pubDate></item></channel></rss>
        """;

    [TestMethod]
    public void The_feed_is_parsed_and_assessed_by_code()
    {
        var releases = AdvisoryFeed.Parse(Fixture());
        CollectionAssert.AreEquivalent(new[] { "7.24.6 long-term", "7.25.1 stable" }, releases.Select(r => $"{r.Version} {r.Channel}").ToArray());
        var lts = releases.Single(r => r.Version == "7.24.6");
        Assert.IsTrue(lts.Changelog.Split('\n').Any(l => l.StartsWith("*) system - improve stability", StringComparison.Ordinal)));
        var code = AdvisoryFeed.Assess(lts.Changelog);
        Assert.AreEqual(ReleaseSeverity.Important, code.Severity, "a CVE alone is important, not critical");
        CollectionAssert.Contains(code.Cves.ToList(), "CVE-2026-98432");

        Assert.AreEqual(ReleaseSeverity.Critical, AdvisoryFeed.Assess("*) www - fix a flaw that is actively exploited;").Severity);
        Assert.AreEqual(ReleaseSeverity.Critical, AdvisoryFeed.Assess("*) ssh - fix authentication bypass (CVE-2026-1234);").Severity);
        Assert.AreEqual(ReleaseSeverity.Normal, AdvisoryFeed.Assess("*) wifi - improve roaming;").Severity);
    }

    private static async Task<(Guid Id, string Name)> RouterAsync(string version, string channel)
    {
        var admin = app.Client(Roles.Admin);
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"adv-{Guid.NewGuid():N}"[..20] })).ReadAsync<HostDetail>();
        var name = $"gw-adv-{Guid.NewGuid():N}"[..14];
        var sensor = await (await admin.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = host.Id, Type = SensorType.MIKROTIK, Name = name, Interval = 300, GracePeriod = 600,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = "198.51.100.250" },
        })).ReadAsync<SensorDetail>();
        await using var db = app.NewDb();
        await db.MikrotikRouters.Where(r => r.SensorId == sensor.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Version, version).SetProperty(r => r.Channel, channel));
        return (sensor.Id, name);
    }

    private static async Task<EventEntity?> AdvisoryEventAsync()
    {
        var id = await app.Services.GetRequiredService<SystemSensors>().IdAsync(SystemSensorKeys.RouterOsAdvisories, CancellationToken.None);
        await using var db = app.NewDb();
        return await db.Events.Where(e => e.SensorId == id).OrderByDescending(e => e.Created).FirstOrDefaultAsync();
    }

    [TestMethod]
    public async Task A_very_bad_fix_with_routers_behind_alerts_until_they_caught_up()
    {
        var (sensor, name) = await RouterAsync("7.99.0", "testing");
        var feed = app.Services.GetRequiredService<AdvisoryFeed>();
        verifierAgrees = true;
        await feed.ProcessAsync(Feed("7.99.1", "testing", "*) www - fix remote code execution (CVE-2026-7777);<br>*) wifi - improve roaming;"), CancellationToken.None);
        StringAssert.DoesNotMatch((await AdvisoryEventAsync())?.Message ?? "", new System.Text.RegularExpressions.Regex("7\\.99\\.1"),
            "the code's critical waits for the AI's rating");
        await app.Services.GetRequiredService<AiWorker>().DrainAsync(CancellationToken.None);

        await using (var db = app.NewDb())
        {
            var release = await db.MikrotikReleases.SingleAsync(r => r.Version == "7.99.1" && r.Channel == "testing");
            Assert.AreEqual(ReleaseSeverity.Critical, release.Severity);
            Assert.AreEqual("verified", release.AssessedBy);
            CollectionAssert.AreEqual(new[] { "CVE-2026-7777" }, release.Cves);
        }
        var alert = await AdvisoryEventAsync();
        Assert.AreEqual(PNMTStatusCodes.ROUTEROS_SECURITY_UPDATE, alert!.Code);
        StringAssert.Contains(alert.Message, "Critical RouterOS fix 7.99.1 [testing] (CVE-2026-7777)");
        StringAssert.Contains(alert.Message, name);

        var admin = app.Client(Roles.Admin);
        var releases = await (await admin.GetAsync("/api/v1/mikrotik/releases")).ReadAsync<List<MikrotikReleaseItem>>();
        Assert.IsTrue(releases.Single(r => r.Version == "7.99.1").RoutersBehind.Any(r => r.Id == sensor));
        var detail = (await (await admin.GetAsync($"/api/v1/sensors/{sensor}")).ReadAsync<SensorDetail>()).Mikrotik!;
        Assert.AreEqual(ReleaseSeverity.Critical, detail.SecurityFix);

        await using (var db = app.NewDb())
        {
            await db.MikrotikRouters.Where(r => r.SensorId == sensor).ExecuteUpdateAsync(u => u.SetProperty(r => r.Version, "7.99.1"));
        }
        await feed.EvaluateAsync(CancellationToken.None);
        Assert.AreEqual(PNMTStatusCodes.SYSTEM_OK, (await AdvisoryEventAsync())!.Code);
    }

    [TestMethod]
    public async Task A_critical_rating_the_verifier_disputes_is_only_important()
    {
        await RouterAsync("7.98.0", "development");
        verifierAgrees = false;
        try
        {
            await app.Services.GetRequiredService<AdvisoryFeed>().ProcessAsync(
                Feed("7.98.1", "development", "*) ssh - fix a security issue (CVE-2026-8888);"), CancellationToken.None);
            await app.Services.GetRequiredService<AiWorker>().DrainAsync(CancellationToken.None);
        }
        finally
        {
            verifierAgrees = true;
        }
        await using var db = app.NewDb();
        var release = await db.MikrotikReleases.SingleAsync(r => r.Version == "7.98.1");
        Assert.AreEqual(ReleaseSeverity.Important, release.Severity);
        Assert.AreEqual("disputed", release.AssessedBy);
    }
}
