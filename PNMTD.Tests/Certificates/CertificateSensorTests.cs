using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Api.V1.Contracts;
using PNMTD.Certificates;
using PNMTD.Dev;
using PNMTD.Lib.Certificates;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Certificates;

/// <summary>
/// The SSL certificate sensor through the API (docs/ssl-certificate-plan.md §6, §10), against a loopback TLS server: the
/// settings and their defaults, the rights, the first check's pin, renewals and unexpected changes, accepting with the
/// confirmation, the resets, the cleanup, the retention, and one event per verdict.
/// </summary>
[TestClass]
public sealed class CertificateSensorTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static FakeIdentity root = null!;
    private static FakeIdentity sub = null!;
    private static FakeIdentity otherRoot = null!;
    private static FakeIdentity otherSub = null!;
    private static FakeTlsServer server = null!;
    private static Guid hostId;
    private static Guid authorityId;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?> { ["Certificates:KeepVersions"] = "3" });
        admin = app.Client(Roles.Admin);
        root = FakePki.Root("Sensor Test Root", "Sensor Test CA");
        sub = FakePki.Intermediate(root, "Sensor Test Sub CA");
        otherRoot = FakePki.Root("Other Test Root", "Other Test CA");
        otherSub = FakePki.Intermediate(otherRoot, "Other Test Sub CA");
        server = new FakeTlsServer(IPAddress.Loopback);
        server.Set("fallback.test", FakePki.Leaf(sub, ["fallback.test"]), sub);
        server.Start();
        // A disabled host: its sensors are checked all the same (Enabled is the sensor's), but stay off the dashboard, whose
        // recent changes the other classes count on.
        hostId = (await V1.CreateHostAsync(admin, V1.Unique("tls-host"), enabled: false)).Id;
        var ca = await (await admin.PostJsonAsync("/api/v1/certificate-authorities", new { name = V1.Unique("Sensor Test CA"), pem = root.Pem })).ReadAsync<CertificateAuthorityItem>();
        authorityId = ca.Id;
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            var sensors = await db.Sensors.Where(s => s.ParentId == hostId).Select(s => s.Id).ToListAsync();
            await db.Events.Where(e => sensors.Contains(e.SensorId)).ExecuteDeleteAsync();
            await db.Sensors.Where(s => s.ParentId == hostId).ExecuteDeleteAsync();
        }
        await server.DisposeAsync();
        app.Dispose();
    }

    private static string Address => $"127.0.0.1:{server.Port}";

    private static object Request(string name, object? certificate = null, string? parameters = null, int interval = 0, SensorType type = SensorType.SSL_CERTIFICATE) =>
        new { hostId, type, name = V1.Unique(name), parameters = parameters ?? Address, interval, enabled = true, certificate };

    private static async Task<SensorDetail> CreateAsync(string site, object certificate, HttpClient? client = null)
    {
        using var response = await (client ?? admin).PostJsonAsync("/api/v1/sensors", Request(site, certificate));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<SensorDetail>();
    }

    private static object Private(string site, object? more = null) =>
        Merge(new Dictionary<string, object?> { ["trust"] = "Private", ["authorityIds"] = new[] { authorityId }, ["serverName"] = site }, more);

    private static object Pinned(string site, object? more = null) =>
        Merge(new Dictionary<string, object?> { ["trust"] = "Pinned", ["serverName"] = site }, more);

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> values, object? more)
    {
        if (more is not null)
        {
            foreach (var p in JsonSerializer.SerializeToElement(more, ApiJson.Options).EnumerateObject())
            {
                values[p.Name] = p.Value;
            }
        }
        return values;
    }

    private static async Task<CertificateSensorDetail> CheckAsync(Guid id) =>
        await (await admin.PostAsync($"/api/v1/sensors/{id}/certificate/check", null)).ReadAsync<CertificateSensorDetail>();

    private static async Task<List<(int Code, string? Message)>> EventsAsync(Guid id)
    {
        await using var db = app.NewDb();
        return (await db.Events.AsNoTracking().Where(e => e.SensorId == id).OrderBy(e => e.Created).Select(e => new { e.Code, e.Message }).ToListAsync())
            .Select(e => (e.Code, e.Message)).ToList();
    }

    [TestMethod]
    public async Task SettingsAreValidatedAndDefaultByMode()
    {
        async Task<string> Field(object request)
        {
            using var response = await admin.PostJsonAsync("/api/v1/sensors", request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return string.Join(",", (await V1.ErrorsOf(response)).EnumerateObject().Select(p => p.Name));
        }
        Assert.AreEqual("parameters", await Field(Request("bad", parameters: "http://x")));
        Assert.AreEqual("interval", await Field(Request("short", interval: 3_599)));
        Assert.AreEqual("interval", await Field(Request("long", interval: 2_592_001)));
        Assert.AreEqual("certificate.authorityIds", await Field(Request("noca", new { trust = "Private" })));
        Assert.AreEqual("certificate.authorityIds", await Field(Request("unknownca", new { trust = "Private", authorityIds = new[] { Guid.NewGuid() } })));
        Assert.AreEqual("certificate.alertOnCertificateChange", await Field(Request("pin", new { trust = "Pinned", alertOnCertificateChange = false })));
        Assert.AreEqual("certificate.warnDays", await Field(Request("warn", new { warnDays = 0 })));
        Assert.AreEqual("certificate.serverName", await Field(Request("sni", new { serverName = "10.0.0.1" })));

        var created = await CreateAsync("shop", new { });
        Assert.AreEqual(86_400, created.Interval, "0 is daily");
        var c = created.Certificate!;
        Assert.AreEqual((CertificateTrust.Public, true, true, false, 14, false, false, true),
            (c.Trust, c.CheckName, c.CheckExpiry, c.IncompleteChainFails, c.WarnDays, c.AlertOnCertificateChange, c.AlertOnKeyChange, c.AlertOnIssuerChange));
        var pinned = (await CreateAsync("pinned-defaults", new { trust = "Pinned" })).Certificate!;
        Assert.AreEqual((false, true, true, false, false), (pinned.CheckName, pinned.CheckExpiry, pinned.AlertOnCertificateChange, pinned.AlertOnKeyChange,
            pinned.AlertOnIssuerChange));

        using var url = await admin.PutJsonAsync($"/api/v1/sensors/{created.Id}", Request("url", new { }, parameters: "https://Shop.Example.com:8443/cart"));
        var updated = await url.ReadAsync<SensorDetail>();
        Assert.AreEqual("shop.example.com:8443", updated.Parameters);
        Assert.AreEqual(("shop.example.com", 8443), (updated.Certificate!.Host, updated.Certificate.Port));
    }

    [TestMethod]
    public async Task TheRightsOfTheArea()
    {
        var monitoringOnly = app.ClientWithRights("Monitoring:Edit");
        using (var refused = await monitoringOnly.PostJsonAsync("/api/v1/sensors", Request("noright", new { })))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
            var problem = await refused.ProblemAsync();
            Assert.AreEqual(("missing_right", "Certificates", "Edit"),
                (problem.GetProperty("code").GetString(), problem.GetProperty("area").GetString(), problem.GetProperty("level").GetString()));
        }
        var both = app.ClientWithRights("Monitoring:Edit,Certificates:Edit");
        var sensor = await CreateAsync("rights", new { }, both);

        // A Viewer of Monitoring sees the sensor and its summary, not the certificate.
        var viewer = app.ClientWithRights("Monitoring:View");
        var seen = await (await viewer.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        Assert.IsNotNull(seen.Certificate);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/sensors/{sensor.Id}/certificate")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await app.ClientWithRights("Certificates:View").GetAsync($"/api/v1/sensors/{sensor.Id}/certificate")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.ClientWithRights("Certificates:View").PostAsync($"/api/v1/sensors/{sensor.Id}/certificate/check", null)).StatusCode);

        // Changing or deleting it needs Certificates: Edit too; linking a rule doesn't.
        using (var rename = await monitoringOnly.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", Request("renamed", new { })))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, rename.StatusCode);
        }
        Assert.AreEqual(HttpStatusCode.Forbidden, (await monitoringOnly.DeleteAsync($"/api/v1/sensors/{sensor.Id}")).StatusCode);
        // Turning another sensor into a certificate sensor is a write to one too.
        var ping = await V1.CreateSensorAsync(admin, hostId, SensorType.PING, parameters: "example.com", interval: 60);
        using (var turn = await monitoringOnly.PutJsonAsync($"/api/v1/sensors/{ping.Id}", Request("turned", new { }, parameters: "example.com")))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, turn.StatusCode);
        }
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.ClientWithRights("Certificates:Edit").PostJsonAsync("/api/v1/certificate-authorities",
            new { name = "x", pem = root.Pem })).StatusCode, "only Manage adds a CA");
        Assert.AreEqual(HttpStatusCode.NoContent, (await both.DeleteAsync($"/api/v1/sensors/{sensor.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task TheFirstCheckPinsARenewalPassesAndANewKeyWaitsForAPerson()
    {
        var site = $"{V1.Unique("renew")}.test";
        var first = FakePki.Leaf(sub, [site]);
        server.Set(site, first, sub);
        var sensor = await CreateAsync(site, Private(site));

        var checkedOnce = await CheckAsync(sensor.Id);
        Assert.AreEqual(CertificateParser.Sha256(first.Certificate), checkedOnce.Current!.Facts.Sha256);
        Assert.IsNull(checkedOnce.Accepted, "the current one is the accepted one");
        Assert.AreEqual(CertificateCheckService.FirstCheck, checkedOnce.Summary.AcceptedBy);
        Assert.IsTrue(checkedOnce.Validation!.Trusted, string.Join(";", checkedOnce.Validation.Problems));
        Assert.AreEqual(sub.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
            checkedOnce.Current.Chain[1].Subject.CommonName);
        CollectionAssert.AreEqual(new[] { PNMTStatusCodes.SSL_OK }, (await EventsAsync(sensor.Id)).Select(e => e.Code).ToArray());

        // A renewal with a new key: no rule alarms on that by default, so 286 once and the baseline moves.
        server.Set(site, FakePki.Leaf(sub, [site]), sub);
        var renewed = await CheckAsync(sensor.Id);
        Assert.AreEqual(CertificateCheckService.Renewal, renewed.Summary.AcceptedBy);
        Assert.IsNull(renewed.Summary.ChangePending);
        CollectionAssert.AreEqual(new[] { PNMTStatusCodes.SSL_OK, PNMTStatusCodes.SSL_CHANGED }, (await EventsAsync(sensor.Id)).Select(e => e.Code).ToArray());
        await CheckAsync(sensor.Id);
        Assert.HasCount(2, await EventsAsync(sensor.Id), "the same certificate again writes nothing");

        // Now fail on a new key: a renewal that keeps its key passes, a new key waits.
        var keep = FakePki.Leaf(sub, [site]);
        server.Set(site, keep, sub);
        await CheckAsync(sensor.Id);
        using (var rules = await admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", Request(site, Private(site, new { alertOnKeyChange = true }))))
        {
            Assert.AreEqual(HttpStatusCode.OK, rules.StatusCode, await rules.Content.ReadAsStringAsync());
        }
        server.Set(site, FakePki.Leaf(sub, [site], key: keep.Key), sub);
        Assert.IsNull((await CheckAsync(sensor.Id)).Summary.ChangePending, "the same key");
        var intruder = FakePki.Leaf(sub, [site]);
        server.Set(site, intruder, sub);
        var changed = await CheckAsync(sensor.Id);
        Assert.IsNotNull(changed.Summary.ChangePending);
        CollectionAssert.AreEqual(new[] { "new key" }, changed.Summary.ChangePending.Kinds.ToArray());
        Assert.IsTrue(changed.Change!.Alarm);
        Assert.IsTrue(changed.Change.Fields.Single(f => f.Field == "Key (SPKI SHA-256)").Changed);
        Assert.IsFalse(changed.Change.Fields.Single(f => f.Field == "Issuer").Changed);
        Assert.AreEqual(PNMTStatusCodes.SSL_CHANGED_UNEXPECTEDLY, (await EventsAsync(sensor.Id))[^1].Code);

        // Accepting: only with the confirmation, only what the person saw.
        var sha = CertificateParser.Sha256(intruder.Certificate);
        using (var unconfirmed = await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/certificate/accept", new { sha256 = sha }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
            Assert.AreEqual("confirmation_required", await V1.CodeOf(unconfirmed));
        }
        using (var stale = await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/certificate/accept",
            new { sha256 = CertificateParser.Sha256(keep.Certificate), confirmed = true }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.AreEqual("certificate_changed_again", await V1.CodeOf(stale));
        }
        var accepted = await (await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/certificate/accept",
            new { sha256 = sha.ToLowerInvariant(), confirmed = true, note = "  new key after the HSM move  " })).ReadAsync<CertificateSensorDetail>();
        Assert.IsNull(accepted.Summary.ChangePending);
        Assert.AreEqual("new key after the HSM move", accepted.AcceptedNote);
        Assert.AreEqual(TestUsers.NameOf(Roles.Admin), accepted.Summary.AcceptedBy);
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, (await EventsAsync(sensor.Id))[^1].Code);
        using (var again = await admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/certificate/accept", new { sha256 = sha, confirmed = true }))
        {
            Assert.AreEqual("no_change", await V1.CodeOf(again));
        }
        await using var db = app.NewDb();
        var entry = await db.AuditLog.AsNoTracking().Where(e => e.EventType == AuditEvents.CertificateAccepted && e.TargetId == sensor.Id.ToString() && e.Outcome == AuditOutcome.Succeeded)
            .OrderByDescending(e => e.CreatedAt).FirstAsync();
        StringAssert.Contains(entry.Detail, sha);
        StringAssert.Contains(entry.Detail, "new key after the HSM move");
    }

    [TestMethod]
    public async Task APinnedSelfSignedCertificateIsWatchedForChange()
    {
        var site = $"{V1.Unique("device")}.test";
        server.Set(site, FakePki.Leaf(null, ["localhost"]));
        var sensor = await CreateAsync(site, Pinned(site));
        var first = await CheckAsync(sensor.Id);
        Assert.IsTrue(first.Current!.Facts.SelfSigned);
        Assert.IsFalse(first.Validation!.NameMatches, "a device's localhost certificate");
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, (await EventsAsync(sensor.Id))[^1].Code, "pinned: no name check by default");
        server.Set(site, FakePki.Leaf(null, ["localhost"]));
        var changed = await CheckAsync(sensor.Id);
        Assert.IsNotNull(changed.Summary.ChangePending);
        Assert.AreEqual(PNMTStatusCodes.SSL_CHANGED_UNEXPECTEDLY, (await EventsAsync(sensor.Id))[^1].Code);

        // The tab's facts and the downloads.
        var versions = await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/certificate/versions")).ReadAsync<Paged<CertificateVersionItem>>();
        Assert.AreEqual(2, versions.TotalCount);
        Assert.IsTrue(versions.Items[0].Current);
        Assert.IsTrue(versions.Items[1].Accepted);
        using var pem = await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/certificate/versions/{versions.Items[0].Id}/pem");
        Assert.AreEqual("application/x-pem-file", pem.Content.Headers.ContentType?.MediaType);
        StringAssert.StartsWith(await pem.Content.ReadAsStringAsync(), "-----BEGIN CERTIFICATE-----");
        var version = await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/certificate/versions/{versions.Items[1].Id}")).ReadAsync<CertificateItem>();
        Assert.AreEqual(versions.Items[1].Sha256, version.Facts.Sha256);
    }

    [TestMethod]
    public async Task AnotherEndpointStartsOverAndTheTypeChangedAwayCleansUp()
    {
        var site = $"{V1.Unique("move")}.test";
        server.Set(site, FakePki.Leaf(sub, [site]), sub);
        var sensor = await CreateAsync(site, Private(site));
        await CheckAsync(sensor.Id);

        // Another interval moves the next check; another trust checks at once; another endpoint starts over.
        using (var interval = await admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", Request(site, Private(site), interval: 3_600)))
        {
            var s = (await interval.ReadAsync<SensorDetail>()).Certificate!;
            Assert.AreEqual(s.LastCheckAt!.Value.AddHours(1), s.NextCheckAt);
        }
        using (var trust = await admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", Request(site, Pinned(site), interval: 3_600)))
        {
            var s = (await trust.ReadAsync<SensorDetail>()).Certificate!;
            Assert.IsNull(s.NextCheckAt);
            Assert.IsNotNull(s.AcceptedAt, "the baseline stays");
            Assert.AreEqual((true, false), (s.AlertOnCertificateChange, s.AlertOnIssuerChange), "the pinned mode's defaults");
        }
        using (var moved = await admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", Request(site, Pinned(site), parameters: $"localhost:{server.Port}", interval: 3_600)))
        {
            var s = (await moved.ReadAsync<SensorDetail>()).Certificate!;
            Assert.IsNull(s.AcceptedAt);
            Assert.IsNull(s.ExpiresAt);
            Assert.IsNull(s.NextCheckAt);
        }
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(1, await db.CertificateVersions.CountAsync(v => v.SensorId == sensor.Id), "the versions stay in the history");
        }

        // The type changed away: the row, its links and versions go.
        using (var simple = await admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", Request(site, type: SensorType.SIMPLE)))
        {
            Assert.AreEqual(HttpStatusCode.OK, simple.StatusCode, await simple.Content.ReadAsStringAsync());
        }
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.CertificateSensors.AnyAsync(c => c.SensorId == sensor.Id));
            Assert.IsFalse(await db.CertificateVersions.AnyAsync(v => v.SensorId == sensor.Id));
        }

        // Deleting a certificate sensor removes its versions (the cascade).
        var other = await CreateAsync(site, Private(site));
        await CheckAsync(other.Id);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/sensors/{other.Id}")).StatusCode);
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.CertificateVersions.AnyAsync(v => v.SensorId == other.Id));
            Assert.IsFalse(await db.CertificateSensorAuthorities.AnyAsync(a => a.SensorId == other.Id));
        }
    }

    [TestMethod]
    public async Task UnreachableAfterTwoFailedChecks()
    {
        var closed = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using var response = await admin.PostJsonAsync("/api/v1/sensors", Request("down", new { }, parameters: $"127.0.0.1:{port}"));
        var sensor = await response.ReadAsync<SensorDetail>();
        var once = await CheckAsync(sensor.Id);
        Assert.AreEqual(1, once.Summary.ConsecutiveFailures);
        Assert.IsEmpty(await EventsAsync(sensor.Id), "one failure is no event");
        Assert.IsGreaterThan(Clock.UtcNow.AddMinutes(10), once.Summary.NextCheckAt!.Value, "retried in 15 minutes");
        var twice = await CheckAsync(sensor.Id);
        Assert.AreEqual(2, twice.Summary.ConsecutiveFailures);
        var events = await EventsAsync(sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.SSL_UNREACHABLE, events.Single().Code);
        StringAssert.Contains(events[0].Message, "connection refused");
    }

    [TestMethod]
    public async Task RetentionKeepsTheNewestAndTheReferencedOnes()
    {
        var site = $"{V1.Unique("keep")}.test";
        server.Set(site, FakePki.Leaf(null, [site]));
        var sensor = await CreateAsync(site, Pinned(site));
        await CheckAsync(sensor.Id);
        Guid accepted;
        await using (var db = app.NewDb())
        {
            accepted = (await db.CertificateSensors.SingleAsync(c => c.SensorId == sensor.Id)).AcceptedVersionId!.Value;
        }
        for (var i = 0; i < 5; i++)
        {
            server.Set(site, FakePki.Leaf(null, [site]));
            await CheckAsync(sensor.Id);
        }
        await using (var db = app.NewDb())
        {
            var kept = await db.CertificateVersions.Where(v => v.SensorId == sensor.Id).Select(v => v.Id).ToListAsync();
            Assert.HasCount(4, kept, "the newest three (Certificates:KeepVersions), and the accepted one");
            CollectionAssert.Contains(kept, accepted);
        }
    }

    /// <summary>§7: an event per verdict change, so a rule "on every event" or "on change" tells once, not every minute or day.</summary>
    [TestMethod]
    public async Task OneEventForADayOfEvaluations()
    {
        var site = $"{V1.Unique("soon")}.test";
        server.Set(site, FakePki.Leaf(sub, [site], notAfter: DateTimeOffset.UtcNow.AddDays(5)), sub);
        var sensor = await CreateAsync(site, Private(site));
        await CheckAsync(sensor.Id);
        var checks = app.Services.GetRequiredService<CertificateCheckService>();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            Clock.Provider = time;
            for (var i = 0; i < 48; i++)
            {
                time.Advance(TimeSpan.FromMinutes(30));
                await checks.EvaluateAsync(sensor.Id, CancellationToken.None);
            }
        }
        finally
        {
            Clock.Provider = TimeProvider.System;
        }
        var events = await EventsAsync(sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.SSL_ABOUT_TO_EXPIRE, events.Single().Code, string.Join("\n", events));
        await using var db = app.NewDb();
        var row = await db.CertificateSensors.SingleAsync(c => c.SensorId == sensor.Id);
        Assert.IsLessThanOrEqualTo(time.GetUtcNow().UtcDateTime.AddMinutes(61), row.NextCheckAt!.Value, "a failing certificate is checked hourly");
    }

    [TestMethod]
    public async Task AnIssuerOutsideThePrivateCaIsInvalidAndTheChangeIsNamed()
    {
        var site = $"{V1.Unique("ca-switch")}.test";
        server.Set(site, FakePki.Leaf(sub, [site]), sub);
        var sensor = await CreateAsync(site, Private(site));
        await CheckAsync(sensor.Id);
        server.Set(site, FakePki.Leaf(otherSub, [site]), otherSub);
        var detail = await CheckAsync(sensor.Id);
        var last = (await EventsAsync(sensor.Id))[^1];
        Assert.AreEqual(PNMTStatusCodes.SSL_INVALID, last.Code, last.Message);
        StringAssert.Contains(last.Message, "not by the sensor's private CA");
        StringAssert.Contains(last.Message, "also changed unexpectedly");
        Assert.IsTrue(detail.Change!.Issuer);
    }
}
