using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Models.Enums;
using static PNMTD.Tests.Mikrotik.MikrotikRouterTests;

namespace PNMTD.Tests.Mikrotik;

/// <summary>The backups' one-way encryption (docs/mikrotik-backup-plan.md §3) against a simulated router.</summary>
[TestClass]
public sealed class MikrotikBackupEncryptionTests
{
    private static TestApp app = null!;
    private static FakeConnector routers = null!;
    private static HttpClient admin = null!;
    private static HttpClient user = null!;
    private static Guid hostId;
    private static int next = 120;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        routers = new FakeConnector();
        app = new TestApp(settings: new Dictionary<string, string?> { ["Mikrotik:AllowPasswordsOverHttp"] = "true" },
            services: s => s.Replace(ServiceDescriptor.Singleton<IRouterConnector>(routers)));
        admin = app.Client(Roles.Admin);
        user = app.Client(Roles.User);
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"encryption-tests-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        hostId = host.Id;
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, string address, bool? encrypt) =>
        await client.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = $"router {address}", Interval = 300, GracePeriod = 600,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = address, EncryptBackups = encrypt },
        });

    private static async Task<(Guid Id, FakeRouter Fake)> RouterAsync(bool encrypt)
    {
        var address = $"198.51.100.{next++}";
        var fake = FakeRouter.From("7.25.1");
        routers.Add(address, fake);
        var sensor = await (await CreateAsync(admin, address, encrypt)).ReadAsync<SensorDetail>();
        return (sensor.Id, fake);
    }

    private static Task<RouterPollOutcome> PollAsync(Guid id) =>
        app.Services.GetRequiredService<RouterPollService>().PollAsync(id, wait: true, CancellationToken.None);

    private static Task<MikrotikBackupSettings> SetKeyAsync() =>
        admin.PostJsonAsync("/api/v1/mikrotik/backup-keys", new MikrotikBackupKeyRequest { Recipient = AgeEncryptionTests.TestRecipient })
            .ContinueWith(t => t.Result.ReadAsync<MikrotikBackupSettings>()).Unwrap();

    [TestMethod]
    public async Task Encrypted_backups_are_age_files_PNMTD_cannot_read()
    {
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.PostJsonAsync("/api/v1/mikrotik/backup-keys",
            new MikrotikBackupKeyRequest { Recipient = AgeEncryptionTests.TestRecipient })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/mikrotik/backup-keys",
            new MikrotikBackupKeyRequest { Recipient = AgeEncryptionTests.TestIdentity })).StatusCode, "a secret key is refused");
        var settings = await SetKeyAsync();
        Assert.AreEqual(AgeEncryptionTests.TestRecipient, settings.Key!.Recipient);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await CreateAsync(user, "198.51.100.199", encrypt: true)).StatusCode, "an Admin's choice");

        var (id, fake) = await RouterAsync(encrypt: true);
        await PollAsync(id);
        var first = await (await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups")).ReadAsync<Paged<MikrotikBackupItem>>();
        Assert.AreEqual(settings.Key.Fingerprint, first.Items.Single().EncryptedWith);
        var content = await (await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{first.Items[0].Id}")).ReadAsync<MikrotikBackupContent>();
        Assert.IsNull(content.Content, "PNMTD cannot read it");
        var file = Convert.FromBase64String(content.Ciphertext!);
        Assert.AreEqual(ExportText.Normalise(fake.Export), Gunzip(AgeEncryptionTests.Decrypt(file, AgeEncryptionTests.TestIdentity)));

        // Still compared by digests: one line more is version 2, +1 −0.
        fake.Export += "/ip dns set servers=192.0.2.53\n";
        var result = await (await user.PostAsync($"/api/v1/sensors/{id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
        Assert.IsTrue(result.Changed);
        Assert.AreEqual((1, 0), (result.Backup!.LinesAdded, result.Backup.LinesRemoved));
        var diff = await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}/diff");
        Assert.AreEqual(HttpStatusCode.Conflict, diff.StatusCode);
        StringAssert.Contains(await diff.Content.ReadAsStringAsync(), "backup_encrypted");
        var download = await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}/download");
        StringAssert.EndsWith(download.Content.Headers.ContentDisposition!.FileName!.Trim('"'), "-v2.rsc.gz.age");
        StringAssert.StartsWith(Encoding.ASCII.GetString(await download.Content.ReadAsByteArrayAsync(), 0, 21), "age-encryption.org/v1");
    }

    [TestMethod]
    public async Task Retiring_the_key_turns_encryption_off_everywhere()
    {
        await SetKeyAsync();
        var (id, fake) = await RouterAsync(encrypt: true);
        await PollAsync(id);
        var settings = await (await admin.PostAsync("/api/v1/mikrotik/backup-keys/retire", null)).ReadAsync<MikrotikBackupSettings>();
        Assert.IsNull(settings.Key);
        Assert.AreEqual(0, settings.RoutersEncrypted);
        Assert.IsNotNull(settings.Keys!.Single(k => k.Recipient == AgeEncryptionTests.TestRecipient).RetiredAt);
        var detail = await (await user.GetAsync($"/api/v1/sensors/{id}")).ReadAsync<SensorDetail>();
        Assert.IsFalse(detail.Mikrotik!.EncryptBackups);

        // From now on plaintext; the encrypted version before cannot be read, so the change is not counted.
        fake.Export += "/ip dns set servers=192.0.2.54\n";
        await using (var db = app.NewDb())
        {
            var router = await db.MikrotikRouters.SingleAsync(r => r.SensorId == id);
            router.LastExportLineDigests = null;
            await db.SaveChangesAsync();
        }
        var result = await (await user.PostAsync($"/api/v1/sensors/{id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
        Assert.IsNull(result.Backup!.EncryptedWith);
        Assert.IsNull(result.Backup.LinesAdded);
        Assert.AreEqual($"Version {result.Backup.Number} stored.", result.Message);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await CreateAsync(admin, "198.51.100.198", encrypt: true)).StatusCode, "no key, no encryption");
        await SetKeyAsync();
    }

    private static string Gunzip(byte[] gzip)
    {
        using var stream = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
