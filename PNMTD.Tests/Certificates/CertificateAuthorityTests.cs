using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Certificates;
using PNMTD.Dev;
using PNMTD.Lib.Models.Enum;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Certificates;

/// <summary>The private CAs (docs/ssl-certificate-plan.md §4.5): the PEM's rules, and adding and removing them.</summary>
[TestClass]
public sealed class CertificateAuthorityTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static FakeIdentity root = null!;
    private static FakeIdentity sub = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        admin = app.Client(Roles.Admin);
        root = FakePki.Root("Authority Test Root", "Authority Test CA");
        sub = FakePki.Intermediate(root, "Authority Test Sub CA");
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public void ThePemsRules()
    {
        using (var alone = CertificateAuthorityService.Read(root.Pem))
        {
            Assert.IsTrue(alone.Valid, alone.Error);
            Assert.IsEmpty(alone.Intermediates);
        }
        using (var both = CertificateAuthorityService.Read(sub.Pem + root.Pem))
        {
            Assert.IsTrue(both.Valid, both.Error);
            Assert.HasCount(1, both.Intermediates);
            Assert.StartsWith(root.Pem.Trim(), both.Pem.Trim(), "stored root first");
        }
        var leaf = FakePki.Leaf(sub, ["server.test"]);
        StringAssert.Contains(CertificateAuthorityService.Read(root.Pem + leaf.Pem).Error, "is not a CA certificate");
        StringAssert.Contains(CertificateAuthorityService.Read(root.Pem + FakePki.Root("Second Root").Pem).Error, "More than one root");
        StringAssert.Contains(CertificateAuthorityService.Read(sub.Pem).Error, "No root");
        var foreign = FakePki.Intermediate(FakePki.Root("Foreign Root"), "Foreign Sub CA");
        StringAssert.Contains(CertificateAuthorityService.Read(root.Pem + foreign.Pem).Error, "isn't signed by Authority Test Root");
        StringAssert.Contains(CertificateAuthorityService.Read("hello").Error, "No certificate");
        using var withKey = CertificateAuthorityService.Read(root.Pem + root.Key.ExportPkcs8PrivateKeyPem());
        Assert.IsTrue(withKey.Valid);
        Assert.IsTrue(withKey.PrivateKeyLeftOut);
        Assert.DoesNotContain("PRIVATE KEY", withKey.Pem, "never stored");
    }

    [TestMethod]
    public async Task AddPreviewAndRemove()
    {
        var preview = await (await admin.PostJsonAsync("/api/v1/certificate-authorities/preview", new { pem = root.Pem + sub.Pem })).ReadAsync<CertificateAuthorityPreview>();
        Assert.IsTrue(preview.Valid, preview.Error);
        Assert.HasCount(2, preview.Certificates);
        Assert.IsTrue(preview.Certificates[0].Root);

        var name = V1.Unique("Corp Root");
        var created = await (await admin.PostJsonAsync("/api/v1/certificate-authorities", new { name, pem = root.Pem + sub.Pem })).ReadAsync<CertificateAuthorityItem>();
        Assert.AreEqual(1, created.IntermediateCount);
        using (var sameName = await admin.PostJsonAsync("/api/v1/certificate-authorities", new { name, pem = FakePki.Root("Other").Pem }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, sameName.StatusCode);
        }
        using (var sameRoot = await admin.PostJsonAsync("/api/v1/certificate-authorities", new { name = V1.Unique("Again"), pem = root.Pem }))
        {
            Assert.AreEqual("authority_exists", await V1.CodeOf(sameRoot));
        }
        var listed = await (await admin.GetAsync("/api/v1/certificate-authorities")).ReadAsync<List<CertificateAuthorityItem>>();
        Assert.IsTrue(listed.Any(a => a.Id == created.Id && a.UsedBy == 0));
        using var pem = await admin.GetAsync($"/api/v1/certificate-authorities/{created.Id}/pem");
        StringAssert.Contains(await pem.Content.ReadAsStringAsync(), "BEGIN CERTIFICATE");

        // In use: refused, naming the sensor; unused again: removed.
        var host = await V1.CreateHostAsync(admin);
        using var sensorResponse = await admin.PostJsonAsync("/api/v1/sensors", new
        {
            hostId = host.Id, type = SensorType.SSL_CERTIFICATE, name = "intranet", parameters = "intranet.test",
            certificate = new { trust = "Private", authorityIds = new[] { created.Id } },
        });
        var sensor = await sensorResponse.ReadAsync<SensorDetail>();
        Assert.AreEqual(created.Name, sensor.Certificate!.Authorities.Single().Name);
        using (var inUse = await admin.DeleteAsync($"/api/v1/certificate-authorities/{created.Id}"))
        {
            Assert.AreEqual("authority_in_use", await V1.CodeOf(inUse));
            StringAssert.Contains((await inUse.ProblemAsync()).GetProperty("detail").GetString(), "intranet");
        }
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/sensors/{sensor.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/certificate-authorities/{created.Id}")).StatusCode);

        await using var db = app.NewDb();
        var types = await db.AuditLog.AsNoTracking().Where(e => e.TargetId == created.Id.ToString() && e.Outcome == AuditOutcome.Succeeded)
            .Select(e => e.EventType).ToListAsync();
        CollectionAssert.IsSubsetOf(new[] { AuditEvents.CertificateAuthorityAdded, AuditEvents.CertificateAuthorityRemoved }, types);
    }
}
