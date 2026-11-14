using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using static PNMTD.Tests.Mikrotik.MikrotikRouterTests;

namespace PNMTD.Tests.Mikrotik;

/// <summary>Configuration backups (docs/mikrotik-plan.md §6) against a simulated router.</summary>
[TestClass]
public sealed class MikrotikBackupTests
{
    private static TestApp app = null!;
    private static FakeConnector routers = null!;
    private static HttpClient admin = null!;
    private static HttpClient user = null!;
    private static HttpClient viewer = null!;
    private static Guid hostId;
    private static int next = 30;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        routers = new FakeConnector();
        app = new TestApp(services: s => s.Replace(ServiceDescriptor.Singleton<IRouterConnector>(routers)));
        admin = app.Client(Roles.Admin);
        user = app.Client(Roles.User);
        viewer = app.Client(Roles.Viewer);
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"backup-tests-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        hostId = host.Id;
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    /// <summary>A router with a simulated router behind it.</summary>
    private static async Task<(Guid Id, FakeRouter Fake)> RouterAsync(string version = "7.25.1", HttpClient? client = null, bool secrets = false,
        int? schedule = 24)
    {
        var address = $"198.51.100.{next++}";
        var fake = FakeRouter.From(version);
        routers.Add(address, fake);
        var sensor = await (await (client ?? user).PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = hostId,
            Type = SensorType.MIKROTIK,
            Name = $"router {address}",
            Interval = 300,
            GracePeriod = 600,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = address, IncludeSecrets = secrets, BackupIntervalHours = schedule },
        })).ReadAsync<SensorDetail>();
        return (sensor.Id, fake);
    }

    private static Task<RouterPollOutcome> PollAsync(Guid id) =>
        app.Services.GetRequiredService<RouterPollService>().PollAsync(id, wait: true, CancellationToken.None);

    private static async Task<List<MikrotikBackupEntity>> BackupsAsync(Guid id)
    {
        await using var db = app.NewDb();
        return await db.MikrotikBackups.Where(b => b.SensorId == id).OrderBy(b => b.Number).ToListAsync();
    }

    private static async Task<List<EventEntity>> EventsAsync(Guid id)
    {
        await using var db = app.NewDb();
        return await db.Events.Where(e => e.SensorId == id).OrderBy(e => e.Created).ToListAsync();
    }

    [TestMethod]
    public async Task The_first_login_stores_version_1_and_an_unchanged_export_stores_nothing()
    {
        var (id, fake) = await RouterAsync();
        await PollAsync(id);
        var backups = await BackupsAsync(id);
        Assert.AreEqual(1, backups.Count);
        Assert.AreEqual(1, backups[0].Number);
        Assert.AreEqual(MikrotikBackupTrigger.FirstLogin, backups[0].Trigger);
        Assert.AreEqual("7.25.1", backups[0].RouterOsVersion);
        Assert.IsNull(backups[0].LinesAdded);
        Assert.IsFalse(backups[0].IncludesSecrets);
        CollectionAssert.Contains(fake.Commands, "/export terse");
        Assert.IsFalse((await EventsAsync(id)).Any(e => e.Code == PNMTStatusCodes.MIKROTIK_CONFIG_CHANGED), "version 1 gives no 243");

        // Not due again for 24 hours: the next poll does not export.
        fake.Commands.Clear();
        await PollAsync(id);
        Assert.IsFalse(fake.Commands.Any(c => c.StartsWith("/export", StringComparison.Ordinal)));

        // "Back up now" with the time line changed only: nothing new.
        fake.Export = fake.Export.Replace("# 2026-11-12 23:25:22", "# 2026-11-13 08:00:00");
        var result = await (await user.PostAsync($"/api/v1/sensors/{id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
        Assert.IsFalse(result.Changed, result.Message);
        Assert.AreEqual(1, (await BackupsAsync(id)).Count);
    }

    [TestMethod]
    public async Task A_change_stores_a_version_writes_243_and_shows_in_the_diff()
    {
        var (id, fake) = await RouterAsync();
        await PollAsync(id);
        fake.Export += "/ip dns set servers=192.0.2.53\n";
        fake.Export = fake.Export.Replace("/system identity set name=r25", "/system identity set name=office-gw");
        var result = await (await user.PostAsync($"/api/v1/sensors/{id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(2, result.Backup!.Number);
        Assert.AreEqual(MikrotikBackupTrigger.Manual, result.Backup.Trigger);
        Assert.AreEqual("test-user", result.Backup.CreatedBy);
        Assert.AreEqual(2, result.Backup.LinesAdded);
        Assert.AreEqual(1, result.Backup.LinesRemoved);
        var changed = (await EventsAsync(id)).Single(e => e.Code == PNMTStatusCodes.MIKROTIK_CONFIG_CHANGED);
        Assert.AreEqual("Configuration changed: +2 −1 lines (version 2)", changed.Message);

        var diff = await (await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}/diff")).ReadAsync<MikrotikBackupDiff>();
        Assert.AreEqual(1, diff.From!.Number);
        Assert.AreEqual(2, diff.Added);
        StringAssert.Contains(diff.Unified, "-/system identity set name=r25\n+/system identity set name=office-gw\n");
        StringAssert.Contains(diff.Unified, "+/ip dns set servers=192.0.2.53\n");
        Assert.IsTrue(diff.Hunks.SelectMany(h => h.Lines).Any(l => l.Kind == DiffLineKind.Added && l.Text == "/ip dns set servers=192.0.2.53"));

        var content = await (await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}")).ReadAsync<MikrotikBackupContent>();
        Assert.IsFalse(content.Content.StartsWith("# 20", StringComparison.Ordinal), "stored normalised");
        StringAssert.Contains(content.Content, "name=office-gw");
        var download = await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}/download");
        Assert.AreEqual(HttpStatusCode.OK, download.StatusCode);
        StringAssert.EndsWith(download.Content.Headers.ContentDisposition!.FileName!.Trim('"'), "-v2.rsc");

        // A Viewer sees the list, not the contents.
        var list = await (await viewer.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups")).ReadAsync<Paged<MikrotikBackupItem>>();
        Assert.AreEqual(2, list.TotalCount);
        Assert.AreEqual(2, list.Items[0].Number, "newest first");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task Two_failed_exports_make_442_the_condition_until_one_succeeds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Clock.Provider = time;
        var (id, fake) = await RouterAsync();
        fake.ExportError = "not enough permissions (9)";
        var first = await PollAsync(id);
        Assert.AreEqual("The router answered: not enough permissions (9)", first.ExportError);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_OK, first.Code, "one failure is not yet a condition");

        // Not retried before Backups:RetryMinutes.
        fake.Commands.Clear();
        time.Advance(TimeSpan.FromMinutes(5));
        await PollAsync(id);
        Assert.IsFalse(fake.Commands.Any(c => c.StartsWith("/export", StringComparison.Ordinal)));

        time.Advance(TimeSpan.FromMinutes(30));
        var second = await PollAsync(id);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_BACKUP_FAILED, second.Code);
        Assert.AreEqual("Backups failing: The router answered: not enough permissions (9)", second.Message);
        var detail = await (await user.GetAsync($"/api/v1/sensors/{id}")).ReadAsync<SensorDetail>();
        Assert.AreEqual(2, detail.Mikrotik!.BackupFailures);

        fake.ExportError = null;
        time.Advance(TimeSpan.FromMinutes(31));
        var third = await PollAsync(id);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_OK, third.Code);
        Assert.AreEqual(1, (await BackupsAsync(id)).Count);

        // "Back up now" reports the failure.
        fake.ExportError = "bad command name export";
        var failed = await user.PostAsync($"/api/v1/sensors/{id}/mikrotik/backups", null);
        Assert.AreEqual(HttpStatusCode.Conflict, failed.StatusCode);
        StringAssert.Contains(await failed.Content.ReadAsStringAsync(), "bad command name export");
    }

    [TestMethod]
    public async Task Secrets_only_with_the_policy_and_only_for_Admins()
    {
        var (id, fake) = await RouterAsync(client: admin, secrets: true);
        // The group lacks sensitive: the export runs without show-sensitive (it would write *****).
        await PollAsync(id);
        CollectionAssert.Contains(fake.Commands, "/export terse");
        Assert.IsFalse((await BackupsAsync(id))[0].IncludesSecrets);
        var detail = await (await user.GetAsync($"/api/v1/sensors/{id}")).ReadAsync<SensorDetail>();
        Assert.IsTrue(detail.Mikrotik!.ScriptOutdated, "the group lacks sensitive");

        fake.Policies("ssh", "read", "sensitive");
        fake.Export += "/interface wireguard add name=wg1 private-key=\"UGsL4e17bQnWs1rb8vOGl/gGTpPHz1aec5UTmTqv+Xw=\"\n";
        var result = await (await admin.PostAsync($"/api/v1/sensors/{id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
        CollectionAssert.Contains(fake.Commands, "/export terse show-sensitive");
        Assert.IsTrue(result.Backup!.IncludesSecrets);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}/download")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}/diff")).StatusCode);
        var content = await (await admin.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}")).ReadAsync<MikrotikBackupContent>();
        StringAssert.Contains(content.Content, "private-key=");
        var log = await (await admin.GetAsync($"/api/v1/audit-log?type={AuditEvents.MikrotikBackupDownloaded}")).ReadAsync<Paged<AuditLogItem>>();
        Assert.IsTrue(log.Items.Any(i => i.Detail!.Contains($"router router {routers.LastTarget!.Host}") && i.Detail.Contains("with secrets")));
    }

    [TestMethod]
    public async Task Retention_keeps_young_versions_and_the_newest_per_router()
    {
        var (id, _) = await RouterAsync();
        await using (var db = app.NewDb())
        {
            var now = Clock.UtcNow;
            for (var n = 1; n <= 60; n++)
            {
                db.MikrotikBackups.Add(new MikrotikBackupEntity
                {
                    Id = Guid.NewGuid(), SensorId = id, RouterName = "old", Number = n, Kind = MikrotikBackupKind.Export,
                    Trigger = MikrotikBackupTrigger.Schedule, CreatedAt = now.AddDays(-500 + n), Content = BackupStore.Gzip($"v{n}\n"), ContentHash = $"{n}",
                });
            }
            // Two young ones: kept whatever the count.
            db.MikrotikBackups.Add(new MikrotikBackupEntity
            {
                Id = Guid.NewGuid(), SensorId = id, RouterName = "old", Number = 61, Kind = MikrotikBackupKind.Export,
                Trigger = MikrotikBackupTrigger.Schedule, CreatedAt = now.AddDays(-10), Content = BackupStore.Gzip("v61\n"), ContentHash = "61",
            });
            await db.SaveChangesAsync();
        }
        await app.Services.GetRequiredService<BackupRetentionTask>().RunOnceAsync(CancellationToken.None);
        var left = await BackupsAsync(id);
        Assert.AreEqual(50, left.Count, "the newest 50, which include the young one");
        Assert.AreEqual(12, left[0].Number);
        Assert.AreEqual(61, left[^1].Number);
    }

    [TestMethod]
    public async Task Backups_outlive_their_router_and_an_Admin_can_delete_one()
    {
        var (id, fake) = await RouterAsync();
        await PollAsync(id);
        fake.Export += "/ip dns set servers=192.0.2.53\n";
        var result = await (await user.PostAsync($"/api/v1/sensors/{id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.DeleteAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup!.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/sensors/{id}/mikrotik/backups/{result.Backup.Id}")).StatusCode);
        Assert.AreEqual(1, (await BackupsAsync(id)).Count);

        Assert.AreEqual(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/sensors/{id}")).StatusCode);
        await using var db = app.NewDb();
        var orphan = await db.MikrotikBackups.SingleAsync(b => b.RouterName == $"router {routers.LastTarget!.Host}");
        Assert.IsNull(orphan.SensorId);
    }

    // --- the schedule (docs/mikrotik-backup-plan.md §2) ------------------------------------------------

    [TestMethod]
    public async Task With_the_schedule_off_only_Back_up_now_exports()
    {
        var (id, fake) = await RouterAsync(schedule: null);
        await PollAsync(id);
        Assert.IsFalse(fake.Commands.Any(c => c.StartsWith("/export", StringComparison.Ordinal)), "no version 1 at the first login");
        Assert.AreEqual(0, (await BackupsAsync(id)).Count);

        var result = await (await user.PostAsync($"/api/v1/sensors/{id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
        Assert.IsTrue(result.Changed, result.Message);
        Assert.AreEqual(MikrotikBackupTrigger.Manual, result.Backup!.Trigger);
        fake.Commands.Clear();
        await PollAsync(id);
        Assert.IsFalse(fake.Commands.Any(c => c.StartsWith("/export", StringComparison.Ordinal)));
        var detail = await (await user.GetAsync($"/api/v1/sensors/{id}")).ReadAsync<SensorDetail>();
        Assert.IsNull(detail.Mikrotik!.BackupIntervalHours);
    }

    [TestMethod]
    public async Task A_monthly_schedule_exports_once_its_interval_has_passed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Clock.Provider = time;
        var (id, fake) = await RouterAsync(schedule: 720);
        await PollAsync(id);
        Assert.AreEqual(1, (await BackupsAsync(id)).Count);
        time.Advance(TimeSpan.FromDays(29));
        fake.Commands.Clear();
        await PollAsync(id);
        Assert.IsFalse(fake.Commands.Any(c => c.StartsWith("/export", StringComparison.Ordinal)), "not due after 29 days");
        time.Advance(TimeSpan.FromDays(1.5));
        await PollAsync(id);
        CollectionAssert.Contains(fake.Commands, "/export terse");
    }

    [TestMethod]
    public async Task The_schedule_is_off_or_up_to_a_year_and_Admins_change_it_for_many()
    {
        var response = await user.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = "too rare", Interval = 300, GracePeriod = 600,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = "198.51.100.250", BackupIntervalHours = 8761 },
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var (a, _) = await RouterAsync(schedule: 8760);
        var (b, _) = await RouterAsync();

        // The default for new routers: readable for the form, changed by Admins.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.PutJsonAsync("/api/v1/mikrotik/backup-settings",
            new MikrotikBackupSettingsRequest { DefaultBackupIntervalHours = null })).StatusCode);
        var settings = await (await admin.PutJsonAsync("/api/v1/mikrotik/backup-settings",
            new MikrotikBackupSettingsRequest { DefaultBackupIntervalHours = 168 })).ReadAsync<MikrotikBackupSettings>();
        Assert.AreEqual(168, settings.DefaultBackupIntervalHours);
        Assert.AreEqual(168, (await (await user.GetAsync("/api/v1/mikrotik/backup-settings")).ReadAsync<MikrotikBackupSettings>()).DefaultBackupIntervalHours);

        // Many routers at once: off for both.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.PostJsonAsync("/api/v1/mikrotik/routers/backup-settings",
            new MikrotikRouterBackupRequest { SensorIds = [a, b], SetSchedule = true })).StatusCode);
        var applied = await (await admin.PostJsonAsync("/api/v1/mikrotik/routers/backup-settings",
            new MikrotikRouterBackupRequest { SensorIds = [a, b], SetSchedule = true, BackupIntervalHours = null })).ReadAsync<MikrotikRouterBackupResult>();
        Assert.AreEqual(2, applied.Changed);
        await using var db = app.NewDb();
        Assert.IsTrue(await db.MikrotikRouters.Where(r => r.SensorId == a || r.SensorId == b).AllAsync(r => r.BackupIntervalHours == null));
        await admin.PutJsonAsync("/api/v1/mikrotik/backup-settings", new MikrotikBackupSettingsRequest { DefaultBackupIntervalHours = 24 });
    }


    [TestMethod]
    public async Task A_router_from_before_the_keyed_digests_is_compared_with_its_newest_stored_export()
    {
        // docs/mikrotik-backup-plan.md §3.7: after the upgrade, the router has a plain SHA-256 and no line digests.
        var (id, fake) = await RouterAsync();
        await PollAsync(id);
        await using (var db = app.NewDb())
        {
            var router = await db.MikrotikRouters.SingleAsync(r => r.SensorId == id);
            var backup = await db.MikrotikBackups.SingleAsync(b => b.SensorId == id);
            var plain = ExportText.Hash(BackupStore.Gunzip(backup.Content));
            router.LastExportHash = plain;
            router.LastExportLineDigests = null;
            backup.ContentHash = plain;
            await db.SaveChangesAsync();
        }
        var unchanged = await (await user.PostAsync($"/api/v1/sensors/{id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
        Assert.IsFalse(unchanged.Changed, "the same configuration is no change");
        await using (var db = app.NewDb())
        {
            var router = await db.MikrotikRouters.SingleAsync(r => r.SensorId == id);
            Assert.IsNotNull(router.LastExportLineDigests);
            Assert.AreEqual(router.LastExportHash, (await db.MikrotikBackups.SingleAsync(b => b.SensorId == id)).ContentHash, "the version got its keyed hash");
        }
        fake.Export += "/ip dns set servers=192.0.2.53\n";
        var changed = await (await user.PostAsync($"/api/v1/sensors/{id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
        Assert.IsTrue(changed.Changed);
        Assert.AreEqual(1, changed.Backup!.LinesAdded);
        Assert.AreEqual(0, changed.Backup.LinesRemoved);
    }

}
