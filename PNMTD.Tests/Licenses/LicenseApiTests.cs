using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Licenses;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Notifications;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Licenses;

/// <summary>
/// Licences &amp; services with the feature on (docs/licences-plan.md §5–§8): items with and without hosts, their sensors, renewals, charges,
/// keys, documents, templates, categories, the owner's notices, the money views and the search. The class's items are in a category of its
/// own, and its hosts are disabled and removed afterwards, so the shared database's other rows don't count (memory: shared test database).
/// </summary>
[TestClass]
public sealed class LicenseApiTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid category;
    private static readonly List<Guid> Hosts = [];
    private static readonly List<Guid> Users = [];

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?> { ["Licenses:Enabled"] = "true" });
        admin = app.Client(Roles.Admin);
        var name = V1.Unique("cat");
        var list = await (await admin.PostJsonAsync("/api/v1/license-categories", new { name })).ReadAsync<List<LicenseCategoryItem>>();
        category = list.Single(c => c.Name == name).Id;
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            var items = await db.Licenses.Where(l => l.CategoryId == category).Select(l => l.Id).ToListAsync();
            var sensors = await db.LicenseAssignments.Where(a => items.Contains(a.LicenseId) && a.SensorId != null).Select(a => a.SensorId!.Value).ToListAsync();
            await db.UserNotifications.Where(n => n.SubjectId != null && items.Contains(n.SubjectId.Value)).ExecuteDeleteAsync();
            await db.Licenses.Where(l => items.Contains(l.Id)).ExecuteDeleteAsync();
            await db.Sensors.Where(s => sensors.Contains(s.Id)).ExecuteDeleteAsync();
            await db.Hosts.Where(h => Hosts.Contains(h.Id)).ExecuteDeleteAsync();
            await db.LicenseTemplates.Where(t => t.CategoryId == category).ExecuteDeleteAsync();
            await db.LicenseCategories.Where(c => c.Id == category).ExecuteDeleteAsync();
            await db.UserNotificationPreferences.Where(p => Users.Contains(p.UserId)).ExecuteDeleteAsync();
            await db.Users.Where(u => Users.Contains(u.Id)).ExecuteDeleteAsync();
        }
        app.Dispose();
    }

    private static object Body(string type = "License", string kind = "Subscription", DateOnly? expires = null, string renewal = "Manual",
        Guid? owner = null, string billing = "Recurring", int? billingMonths = 12, decimal? price = 120m, int? term = 12, int? seats = null,
        bool seatsFromHosts = false, string? name = null, int? noticeDays = null, bool warnBeforeRenewal = false, DateOnly? starts = null,
        bool recordPast = false, string? note = null, string? licenseKey = null, Guid? templateId = null, bool eolFromTemplate = false,
        int? reviewMonths = null, DateOnly? reviewedOn = null, bool acknowledge = false, string? vendor = null, string? supportContact = null) => new
    {
        type, kind, name = name ?? V1.Unique("item"), categoryId = category, expiresOn = expires ?? (renewal == "None" ? null : Clock.Today.AddYears(1)),
        renewal, ownerId = owner, billing, billingMonths,
        priceAmount = price, currency = "EUR", termMonths = term, seats, seatsFromHosts, noticeDays, warnBeforeRenewal, startsOn = starts,
        recordPastCharges = recordPast, note, licenseKey, templateId, endOfLifeFromTemplate = eolFromTemplate, reviewMonths, reviewedOn,
        acknowledgeCredentials = acknowledge, vendor, supportContact,
    };

    private static async Task<LicenseDetail> CreateAsync(object body, HttpClient? client = null)
    {
        using var response = await (client ?? admin).PostJsonAsync("/api/v1/licenses", body);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<LicenseDetail>();
    }

    private static Task<LicenseDetail> GetAsync(Guid id) => admin.GetAsync($"/api/v1/licenses/{id}").ContinueWith(t => t.Result.ReadAsync<LicenseDetail>()).Unwrap();

    private static async Task<Guid> HostAsync()
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique("lic-host"), enabled: false);
        Hosts.Add(host.Id);
        return host.Id;
    }

    private static async Task<Guid> UserAsync(string name, string? email = "someone@example.org")
    {
        await using var db = app.NewDb();
        var username = V1.Unique("owner").ToLowerInvariant();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(), Username = username, NormalizedUsername = username, DisplayName = name, PasswordHash = "x", SecurityStamp = "x",
            IsActive = true, CreatedAt = DateTime.UtcNow, Email = email,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        Users.Add(user.Id);
        return user.Id;
    }

    private static async Task<List<UserNotificationEntity>> NoticesAsync(Guid item)
    {
        await using var db = app.NewDb();
        return await db.UserNotifications.AsNoTracking().Where(n => n.SubjectId == item).OrderBy(n => n.CreatedAt).ToListAsync();
    }

    private static async Task<EventEntity?> LastEventAsync(Guid sensorId)
    {
        await using var db = app.NewDb();
        return await db.Events.AsNoTracking().Where(e => e.SensorId == sensorId).OrderByDescending(e => e.Created).FirstOrDefaultAsync();
    }

    // --- items, hosts and sensors ----------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnItemOnNoHostHasNoSensorAndKeepsItsStateItself()
    {
        var today = Clock.Today;
        var item = await CreateAsync(Body(expires: today.AddDays(10)));
        Assert.AreEqual(PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE, item.VerdictCode, "evaluated at once, without a sensor");
        StringAssert.StartsWith(item.Display, "LIC-");
        Assert.AreEqual(0, item.Hosts);
        Assert.AreEqual(445, item.Findings.Single().Code);
        await using var db = app.NewDb();
        Assert.IsFalse(await db.LicenseAssignments.AnyAsync(a => a.LicenseId == item.Id), "no assignment");
        Assert.IsFalse(await db.Sensors.AnyAsync(s => s.Type == SensorType.LICENSE && s.Name.Contains(item.Name)), "no sensor anywhere");
        var rows = await (await admin.GetAsync("/api/v1/licenses?type=License")).ReadAsync<List<LicenseRow>>();
        Assert.AreEqual(445, rows.Single(r => r.Id == item.Id).VerdictCode);
    }

    [TestMethod]
    public async Task EachHostGetsASensorThatShowsTheItemsState()
    {
        var host = await HostAsync();
        var item = await CreateAsync(Body(expires: Clock.Today.AddDays(5)));
        var assigned = await (await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/assignments", new { hostIds = new[] { host } }))
            .ReadAsync<List<LicenseAssignmentItem>>();
        var sensorId = assigned.Single().SensorId!.Value;
        await using (var db = app.NewDb())
        {
            var sensor = await db.Sensors.AsNoTracking().SingleAsync(s => s.Id == sensorId);
            Assert.AreEqual(SensorType.LICENSE, sensor.Type);
            Assert.AreEqual($"Licence: {item.Name} ({item.Display})", sensor.Name);
            Assert.AreEqual(host, sensor.ParentId);
        }
        Assert.AreEqual(PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE, (await LastEventAsync(sensorId))!.Code, "the first event at once");
        var detail = await (await admin.GetAsync($"/api/v1/sensors/{sensorId}")).ReadAsync<SensorDetail>();
        Assert.AreEqual(item.Id, detail.License!.Id);
        Assert.AreEqual(1, detail.License.AssignmentSeats);

        // The name follows the item; /sensors can't rename, retype or delete it, and the sensor API takes no events for it.
        var request = Body(expires: Clock.Today.AddDays(5), name: item.Name + " renamed");
        (await admin.PutJsonAsync($"/api/v1/licenses/{item.Id}", request)).EnsureSuccessStatusCode();
        await using (var db = app.NewDb())
        {
            Assert.AreEqual($"Licence: {item.Name} renamed ({item.Display})", (await db.Sensors.AsNoTracking().SingleAsync(s => s.Id == sensorId)).Name);
        }
        using var delete = await admin.DeleteAsync($"/api/v1/sensors/{sensorId}");
        Assert.AreEqual(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.AreEqual("license_sensor", await V1.CodeOf(delete));
        using var create = await admin.PostJsonAsync("/api/v1/sensors", new { hostId = host, type = "LICENSE", name = "x" });
        Assert.AreEqual(HttpStatusCode.BadRequest, create.StatusCode);
        string token;
        await using (var db = app.NewDb())
        {
            token = (await db.Sensors.AsNoTracking().SingleAsync(s => s.Id == sensorId)).SecretWriteToken;
            await db.Sensors.Where(s => s.Id == sensorId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Enabled, true));
        }
        using var posted = await app.Client().GetAsync($"/event/{token}/200/fine");
        Assert.AreEqual(HttpStatusCode.Conflict, posted.StatusCode);

        var removed = await (await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/assignments/remove", new { hostIds = new[] { host } }))
            .ReadAsync<List<LicenseAssignmentItem>>();
        Assert.AreEqual(0, removed.Count);
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.Sensors.AnyAsync(s => s.Id == sensorId), "unassigning removes the sensor");
        }
        Assert.AreEqual(445, (await GetAsync(item.Id)).VerdictCode, "and the item keeps its state");
    }

    [TestMethod]
    public async Task SeatsCountedOnHostsCanBeOverAssigned()
    {
        var hosts = new[] { await HostAsync(), await HostAsync() };
        var item = await CreateAsync(Body(kind: "Perpetual", billing: "OneTime", billingMonths: null, renewal: "None", term: null, seats: 1, seatsFromHosts: true));
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, item.VerdictCode);
        (await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/assignments", new { hostIds = hosts })).EnsureSuccessStatusCode();
        var over = await GetAsync(item.Id);
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OVER_ASSIGNED, over.VerdictCode, "allowed, and a finding");
        Assert.AreEqual(2, over.UsedSeats);
    }

    [TestMethod]
    public async Task ARenewalShows246AndAnAutomaticOneMovesTheExpiry()
    {
        var item = await CreateAsync(Body(expires: Clock.Today.AddDays(3)));
        Assert.AreEqual(445, item.VerdictCode);
        var renewal = await (await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/renew", new { priceAmount = 150m, reference = "R-1" }))
            .ReadAsync<LicenseRenewalItem>();
        Assert.AreEqual(Clock.Today.AddDays(3), renewal.PreviousExpiresOn);
        Assert.AreEqual(Clock.Today.AddDays(4).AddMonths(12).AddDays(-1), renewal.NewExpiresOn);
        var renewed = await GetAsync(item.Id);
        Assert.AreEqual(PNMTStatusCodes.LICENSE_RENEWED, renewed.VerdictCode);
        Assert.AreEqual(150m, renewed.PriceAmount, "the price from now on");

        var auto = await CreateAsync(Body("Service", "Contract", Clock.Today.AddDays(-3), "Automatic", billingMonths: 1, term: 1, noticeDays: 10));
        Assert.IsTrue(auto.ExpiresOn >= Clock.Today, "moved by a term until it runs");
        Assert.AreEqual(PNMTStatusCodes.LICENSE_RENEWED, auto.VerdictCode);
        StringAssert.StartsWith(auto.VerdictMessage, "Renewed automatically until");
        var renewals = await (await admin.GetAsync($"/api/v1/licenses/{auto.Id}/renewals")).ReadAsync<List<LicenseRenewalItem>>();
        Assert.AreEqual(LicenseRenewalKind.AutomaticRenewal, renewals.Single().Kind);
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.LicenseRenewedAutomatically && l.Subjects.Contains(auto.Id)));
    }

    [TestMethod]
    public async Task PnmtRecordsEachBillingPeriodOnceAndPeopleCorrectIt()
    {
        var start = Clock.Today.AddMonths(-2).AddDays(-1);
        var item = await CreateAsync(Body("Service", "Usage", billing: "Variable", billingMonths: 1, price: 100m, term: null, renewal: "None", starts: start,
            recordPast: true));
        var charges = await (await admin.GetAsync($"/api/v1/licenses/{item.Id}/charges")).ReadAsync<List<LicenseChargeItem>>();
        Assert.AreEqual(3, charges.Count, string.Join(", ", charges.Select(c => c.On)));
        Assert.IsTrue(charges.All(c => c is { Source: LicenseChargeSource.Automatic, Kind: LicenseChargeKind.Period, Estimated: true, Amount: 100m }));
        await app.Services.GetRequiredService<LicenseEvaluator>().RunOnceAsync(CancellationToken.None);
        Assert.AreEqual(3, (await (await admin.GetAsync($"/api/v1/licenses/{item.Id}/charges")).ReadAsync<List<LicenseChargeItem>>()).Count, "never twice");

        var first = charges.OrderBy(c => c.On).First();
        var corrected = await (await admin.PutJsonAsync($"/api/v1/licenses/{item.Id}/charges/{first.Id}", new { kind = "Period", amount = 123.45m, reference = "INV-9" }))
            .ReadAsync<LicenseChargeItem>();
        Assert.AreEqual((123.45m, false, LicenseChargeSource.Automatic), (corrected.Amount, corrected.Estimated, corrected.Source), "the invoice clears the estimate");
        using var refused = await admin.DeleteAsync($"/api/v1/licenses/{item.Id}/charges/{first.Id}");
        Assert.AreEqual("automatic_charge", await V1.CodeOf(refused));
        var credit = await (await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/charges", new { kind = "Credit", amount = 20m })).ReadAsync<LicenseChargeItem>();
        Assert.AreEqual(-20m, credit.Amount, "a credit is stored negative");
        (await admin.DeleteAsync($"/api/v1/licenses/{item.Id}/charges/{credit.Id}")).EnsureSuccessStatusCode();

        var fromToday = await CreateAsync(Body("Service", "Subscription", billingMonths: 1, price: 50m, term: null, renewal: "None", starts: start));
        var todays = await (await admin.GetAsync($"/api/v1/licenses/{fromToday.Id}/charges")).ReadAsync<List<LicenseChargeItem>>();
        Assert.AreEqual(0, todays.Count, "the past only on request: the next billing day is the first");
        Assert.AreEqual(Clock.Today, fromToday.ChargesFrom);
    }

    [TestMethod]
    public async Task KeysAreProtectedShownOnRequestAndAudited()
    {
        var item = await CreateAsync(Body(licenseKey: "NJ4MX-VQQ7Q-FKK44-W8FV9-CC9DR"));
        Assert.IsTrue(item.HasLicenseKey);
        Assert.AreEqual("CC9DR", item.LicenseKeyHint);
        await using (var db = app.NewDb())
        {
            var stored = await db.Licenses.AsNoTracking().SingleAsync(l => l.Id == item.Id);
            Assert.IsFalse(stored.LicenseKeyProtected!.Contains("NJ4MX"), "the column isn't the key");
        }
        var keys = await (await admin.GetAsync($"/api/v1/licenses/{item.Id}/keys")).ReadAsync<LicenseKeys>();
        Assert.AreEqual("NJ4MX-VQQ7Q-FKK44-W8FV9-CC9DR", keys.LicenseKey);
        using var viewer = await app.ClientWithRights("Licenses:View").GetAsync($"/api/v1/licenses/{item.Id}/keys");
        Assert.AreEqual(HttpStatusCode.Forbidden, viewer.StatusCode, "keys need Details");
        await using (var db = app.NewDb())
        {
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.LicenseKeysShown && l.Subjects.Contains(item.Id)));
        }
        var found = await (await admin.GetAsync("/api/v1/search?q=CC9DR")).ReadAsync<SearchResult>();
        Assert.IsTrue(found.Licenses!.Any(l => l.Id == item.Id), "a key's last five characters find it");
        var byNumber = await (await admin.GetAsync($"/api/v1/search?q={item.Display}")).ReadAsync<SearchResult>();
        Assert.AreEqual(item.Id, byNumber.Licenses![0].Id);
    }

    [TestMethod]
    public async Task ANoteThatLooksLikeItHoldsAKeyAsksFirst()
    {
        using var warned = await admin.PostJsonAsync("/api/v1/licenses", Body(note: "key: NJ4MXVQQ7QFKK44W8FV9CC9DR"));
        Assert.AreEqual((HttpStatusCode)422, warned.StatusCode);
        Assert.AreEqual("looks_like_credential", await V1.CodeOf(warned));
        await CreateAsync(Body(note: "key: NJ4MXVQQ7QFKK44W8FV9CC9DR", acknowledge: true));
    }

    [TestMethod]
    public async Task DocumentsAreServedOnlyThroughTheirItem()
    {
        var item = await CreateAsync(Body());
        using var content = new ByteArrayContent("LICENSE-FILE-CONTENT"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var upload = await admin.PostAsync($"/api/v1/licenses/{item.Id}/documents?name=office-2024.lic", content);
        Assert.AreEqual(HttpStatusCode.Created, upload.StatusCode, await upload.Content.ReadAsStringAsync());
        var document = await upload.ReadAsync<LicenseDocumentItem>();
        Assert.AreEqual(LicenseDocumentKind.LicenseFile, document.Kind, "suggested from the name");
        using var download = await admin.GetAsync($"/api/v1/licenses/{item.Id}/documents/{document.FileId}/office-2024.lic");
        Assert.AreEqual(HttpStatusCode.OK, download.StatusCode);
        Assert.AreEqual("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue(download.Headers.CacheControl!.NoStore);
        Assert.AreEqual("LICENSE-FILE-CONTENT", await download.Content.ReadAsStringAsync());
        using var generic = await admin.GetAsync($"/api/v1/files/{document.FileId}/office-2024.lic");
        Assert.AreEqual(HttpStatusCode.NotFound, generic.StatusCode, "never through the knowledge base's file URL");
        using var adminDelete = await admin.DeleteAsync($"/api/v1/files/{document.FileId}");
        Assert.AreEqual("license_document", await V1.CodeOf(adminDelete));
        using var details = await app.ClientWithRights("Licenses:View").GetAsync($"/api/v1/licenses/{item.Id}/documents/{document.FileId}/x");
        Assert.AreEqual(HttpStatusCode.Forbidden, details.StatusCode, "downloads need Details");
        using var executable = await admin.PostAsync($"/api/v1/licenses/{item.Id}/documents?name=setup.exe", new ByteArrayContent([1, 2, 3]));
        Assert.AreEqual((HttpStatusCode)422, executable.StatusCode);
        (await admin.DeleteAsync($"/api/v1/licenses/{item.Id}/documents/{document.FileId}")).EnsureSuccessStatusCode();
        await using var db = app.NewDb();
        Assert.IsFalse(await db.FileUses.AnyAsync(u => u.FileId == document.FileId && u.LicenseId == item.Id), "left to the cleanup");
    }

    [TestMethod]
    public async Task AnItemFollowsItsTemplatesEndOfLife()
    {
        var template = await (await admin.PostJsonAsync("/api/v1/license-templates", new
        {
            name = V1.Unique("tpl"), type = "License", categoryId = category, manufacturer = "Microsoft", productName = "Windows Server", edition = "2016",
            kind = "Perpetual", endOfLifeOn = Clock.Today.AddDays(30), endOfLifeNote = "end of extended support",
        })).ReadAsync<LicenseTemplateItem>();
        var item = await CreateAsync(Body(kind: "Perpetual", billing: "OneTime", billingMonths: null, renewal: "None", term: null, templateId: template.Id,
            eolFromTemplate: true));
        Assert.AreEqual(PNMTStatusCodes.LICENSE_EOL_SOON, item.VerdictCode);
        var change = new
        {
            name = template.Name, type = "License", categoryId = category, manufacturer = "Microsoft", productName = "Windows Server", edition = "2016",
            kind = "Perpetual", endOfLifeOn = Clock.Today.AddDays(-1), endOfLifeNote = "end of extended support",
        };
        var changed = await (await admin.PutJsonAsync($"/api/v1/license-templates/{template.Id}", change)).ReadAsync<LicenseTemplateItem>();
        Assert.AreEqual(1, changed.FollowEndOfLife);
        Assert.AreEqual(PNMTStatusCodes.LICENSE_EOL, (await GetAsync(item.Id)).VerdictCode, "evaluated again");
        (await admin.DeleteAsync($"/api/v1/license-templates/{template.Id}")).EnsureSuccessStatusCode();
        var kept = await GetAsync(item.Id);
        Assert.IsFalse(kept.EndOfLifeFromTemplate);
        Assert.AreEqual(Clock.Today.AddDays(-1), kept.EndOfLifeOn, "it keeps the end of life as its own");
    }

    [TestMethod]
    public async Task ACategoryInUseIsDeletedWithItsItemsMoved()
    {
        var name = V1.Unique("cat2");
        var other = (await (await admin.PostJsonAsync("/api/v1/license-categories", new { name })).ReadAsync<List<LicenseCategoryItem>>()).Single(c => c.Name == name);
        var item = await CreateAsync(Body());
        (await admin.PutJsonAsync($"/api/v1/licenses/{item.Id}", new
        {
            type = "License", kind = "Subscription", name = item.Name, categoryId = other.Id, renewal = "Manual", expiresOn = item.ExpiresOn ?? Clock.Today.AddYears(1),
            billing = "Recurring", billingMonths = 12, termMonths = 12,
        })).EnsureSuccessStatusCode();
        using var refused = await admin.DeleteAsync($"/api/v1/license-categories/{other.Id}");
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, "where do its items go?");
        (await admin.DeleteAsync($"/api/v1/license-categories/{other.Id}?moveTo={category}")).EnsureSuccessStatusCode();
        Assert.AreEqual(category, (await GetAsync(item.Id)).CategoryId);
    }

    [TestMethod]
    public async Task RetiringNeedsAConfirmationAndDeletingTheNumber()
    {
        var host = await HostAsync();
        var item = await CreateAsync(Body());
        (await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/assignments", new { hostIds = new[] { host } })).EnsureSuccessStatusCode();
        using var unconfirmed = await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/retire", new { confirmed = false });
        Assert.AreEqual("confirm_needed", await V1.CodeOf(unconfirmed));
        var retired = await (await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/retire", new { confirmed = true })).ReadAsync<LicenseDetail>();
        Assert.AreEqual((LicenseStatus.Retired, 0, (int?)null), (retired.Status, retired.Hosts, retired.VerdictCode));
        using var assign = await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/assignments", new { hostIds = new[] { host } });
        Assert.AreEqual("license_retired", await V1.CodeOf(assign));
        using var wrong = await admin.DeleteAsync($"/api/v1/licenses/{item.Id}?number=99999999");
        Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
        (await admin.DeleteAsync($"/api/v1/licenses/{item.Id}?number={item.Display}")).EnsureSuccessStatusCode();
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/licenses/{item.Id}")).StatusCode);
    }

    // --- the owner's notices (§5.5) ----------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheOwnerIsToldWithoutASensorButNotOfWhatTheyDidThemselves()
    {
        var owner = await UserAsync("Anna Owner");
        var item = await CreateAsync(Body(expires: Clock.Today.AddDays(10), owner: owner));
        var notices = await NoticesAsync(item.Id);
        Assert.AreEqual(1, notices.Count);
        Assert.AreEqual((UserNotificationKinds.LicenseWarning, owner, UserNotificationState.Pending), (notices[0].Kind, notices[0].UserId, notices[0].State));
        StringAssert.StartsWith(notices[0].Detail, "Subscription ends on");
        await app.Services.GetRequiredService<LicenseEvaluator>().RunOnceAsync(CancellationToken.None);
        Assert.AreEqual(1, (await NoticesAsync(item.Id)).Count, "once");

        // The Viewer's own item: created by them, so they aren't told.
        var own = await CreateAsync(Body(expires: Clock.Today.AddDays(10), owner: TestUsers.Viewer), app.ClientWithRights("Licenses:Edit"));
        Assert.AreEqual(0, (await NoticesAsync(own.Id)).Count, "never to the actor");
    }

    [TestMethod]
    public async Task AReminderAndADueReviewAreToldOnce()
    {
        var owner = await UserAsync("Ben Owner");
        var item = await CreateAsync(Body(expires: Clock.Today.AddDays(5), owner: owner, reviewMonths: 1, reviewedOn: Clock.Today.AddMonths(-2)));
        var notices = await NoticesAsync(item.Id);
        Assert.AreEqual(1, notices.Count(n => n.Kind == UserNotificationKinds.LicenseWarning), "within RemindDays: the warning is the reminder");
        Assert.AreEqual(1, notices.Count(n => n.Kind == UserNotificationKinds.LicenseReview));
        await app.Services.GetRequiredService<LicenseEvaluator>().RunOnceAsync(CancellationToken.None);
        Assert.AreEqual(2, (await NoticesAsync(item.Id)).Count);
        await using var db = app.NewDb();
        var stored = await db.Licenses.AsNoTracking().SingleAsync(l => l.Id == item.Id);
        StringAssert.EndsWith(stored.NoticeKey, "|remind");
    }

    [TestMethod]
    public async Task ItemsNobodyOwnsAreToldToWhoeverChoseThemAndAreWorthALookOtherwise()
    {
        var item = await CreateAsync(Body(expires: Clock.Today.AddDays(10)));
        Assert.AreEqual(0, (await NoticesAsync(item.Id)).Count, "nobody chose them yet");
        Assert.IsTrue(item.Notifying.Nobody, "on no host, no owner, nobody chose them: worth a look");
        var chooser = await UserAsync("Carla Contracts");
        await using (var db = app.NewDb())
        {
            db.UserNotificationPreferences.Add(new UserNotificationPreferenceEntity
            {
                UserId = chooser, Kind = UserNotificationKinds.LicenseUnowned, Channel = UserNotificationChannel.Email, Enabled = true, ChangedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var later = await CreateAsync(Body(expires: Clock.Today.AddDays(12)));
        var notices = await NoticesAsync(later.Id);
        Assert.IsTrue(notices.Any(n => n.Kind == UserNotificationKinds.LicenseUnowned && n.UserId == chooser));
        Assert.IsFalse((await GetAsync(later.Id)).Notifying.Nobody, "someone chose the items nobody owns");
        await using (var db = app.NewDb())
        {
            await db.UserNotificationPreferences.Where(p => p.UserId == chooser).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    public async Task TheRendererSaysWhatIsStillTrueWhenItSends()
    {
        var owner = await UserAsync("Dora Owner");
        var item = await CreateAsync(Body(expires: Clock.Today.AddDays(10), owner: owner, price: 3960m));
        var renderer = app.Services.GetServices<IUserNotificationRenderer>().Single(r => r.Kind == UserNotificationKinds.LicenseWarning);
        await using var db = app.NewDb();
        var rows = await db.UserNotifications.Where(n => n.SubjectId == item.Id).ToListAsync();
        var batch = new UserNotificationBatch(UserNotificationKinds.Find(UserNotificationKinds.LicenseWarning)!, owner, "Dora Owner", item.Id, rows);
        var mail = await renderer.RenderAsync(db, batch, "https://pnmt.example.org", CancellationToken.None);
        Assert.IsNotNull(mail);
        StringAssert.Contains(mail.Body, $"{item.Display} {item.Name}: subscription ends on");
        StringAssert.Contains(mail.Body, "EUR 3,960.00 a year");
        StringAssert.Contains(mail.Body, $"https://pnmt.example.org/pnmt-admin/licences/{item.Number}");
        Assert.AreEqual($"you are the owner of {item.Display}", mail.Because);
        (await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/renew", new { })).EnsureSuccessStatusCode();
        Assert.IsNull(await renderer.RenderAsync(db, batch, null, CancellationToken.None), "renewed meanwhile: nothing left to say");
    }

    // --- the money views, links, the history ---------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheOverviewTheCostsAndUpcomingAddUp()
    {
        var monthly = await CreateAsync(Body("Service", "Subscription", Clock.Today.AddDays(20), "Automatic", billingMonths: 1, price: 330m, term: 1,
            vendor: "Contoso"));
        var yearly = await CreateAsync(Body(expires: Clock.Today.AddDays(40), billingMonths: 12, price: 1200m, vendor: "Contoso"));
        var overview = await (await admin.GetAsync($"/api/v1/licenses/overview?category={category}&currency=EUR")).ReadAsync<LicenseOverview>();
        Assert.IsTrue(overview.PerMonth.Single(m => m.Currency == "EUR").Amount >= 430m, "330 + 1200 / 12, and the class's others");
        Assert.AreEqual(24, overview.Months.Count);
        Assert.IsTrue(overview.ByProvider.Any(p => p.Label == "Contoso" && p.Amount >= 330m * 12 + 1200m));
        var costs = await (await admin.GetAsync($"/api/v1/licenses/costs?category={category}&group=item&show=forecast&currency=EUR"
            + $"&from={Clock.Today.AddMonths(1):yyyy-MM}&to={Clock.Today.AddMonths(1):yyyy-MM}")).ReadAsync<LicenseCosts>();
        Assert.AreEqual(330m, costs.Groups.Single(g => g.Key == monthly.Id.ToString()).Total, "next month's charge");
        var upcoming = await (await admin.GetAsync("/api/v1/licenses/upcoming?months=3")).ReadAsync<LicenseUpcoming>();
        Assert.IsTrue(upcoming.ByMonth.SelectMany(m => m.Entries).Any(e => e.Id == yearly.Id && e.What == "expires"));
        Assert.IsTrue(upcoming.ByMonth.SelectMany(m => m.Entries).Count(e => e.Id == monthly.Id && e.What == "renews") >= 2, "each monthly renewal");
        using var csv = await admin.GetAsync($"/api/v1/licenses/export.csv?ids={yearly.Id}");
        var text = await csv.Content.ReadAsStringAsync();
        StringAssert.Contains(text, yearly.Display);
        Assert.IsFalse(text.Contains("licenseKey", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ALinkedSensorShowsTheProvidersSupport()
    {
        var host = await HostAsync();
        var ping = await V1.CreateSensorAsync(admin, host, enabled: false);
        var item = await CreateAsync(Body("Service", "Contract", Clock.Today.AddYears(1), "Manual", vendor: "Swisscom", supportContact: "0800 800 800 (24/7)"));
        (await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/sensor-links", new { sensorIds = new[] { ping.Id }, note = "the primary line" }))
            .EnsureSuccessStatusCode();
        var sensor = await (await app.ClientWithRights("Monitoring:View").GetAsync($"/api/v1/sensors/{ping.Id}")).ReadAsync<SensorDetail>();
        var linked = sensor.LinkedItems!.Single();
        Assert.AreEqual(("Swisscom", "0800 800 800 (24/7)"), (linked.Provider, linked.SupportContact));
        var panel = await (await admin.GetAsync($"/api/v1/licenses/linked?host={host}")).ReadAsync<List<LicenseLinkedItem>>();
        Assert.AreEqual(0, panel.Count, "a link isn't an assignment");
        using var itemSensor = await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/sensor-links", new { sensorIds = new[] { Guid.NewGuid() } });
        Assert.AreEqual(HttpStatusCode.BadRequest, itemSensor.StatusCode);
    }

    [TestMethod]
    public async Task AWindowsHostsActivationIsMatchedByTheKeysLastFiveCharacters()
    {
        var host = await HostAsync();
        var item = await CreateAsync(Body(kind: "Perpetual", billing: "OneTime", billingMonths: null, renewal: "None", term: null,
            licenseKey: "NJ4MX-VQQ7Q-FKK44-W8FV9-7XK2P"));
        await using (var db = app.NewDb())
        {
            var sensor = new SensorEntity { Id = Guid.NewGuid(), ParentId = host, Type = SensorType.WINDOWS, Name = "server", Enabled = false, Created = DateTime.UtcNow };
            sensor.SetNewSecretToken();
            db.Sensors.Add(sensor);
            var system = PNMTD.Cli.SimulatedSections.System("WIN-LIC");
            system = system with { Os = system.Os with { Activation = system.Os.Activation! with { Channel = "Retail", PartialKey = "7xk2p" } } };
            db.AgentSections.Add(new AgentSectionEntity
            {
                SensorId = sensor.Id, Section = PNMTD.Lib.Agent.SectionNames.System, SchemaVersion = PNMTD.Lib.Agent.SectionNames.SchemaVersion, Hash = "x",
                CollectedAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow, ChangedAt = DateTime.UtcNow,
                Content = PNMTD.Agents.AgentSections.Compress(System.Text.Json.JsonSerializer.Serialize(system, PNMTD.Lib.Agent.AgentJson.Options)),
            });
            await db.SaveChangesAsync();
        }
        var byItem = await (await admin.GetAsync($"/api/v1/licenses/activations?license={item.Id}")).ReadAsync<List<LicenseActivation>>();
        Assert.AreEqual((host, "7XK2P", false), (byItem.Single().HostId, byItem.Single().PartialKey, byItem.Single().Assigned), "uses the key, not assigned");
        var byHost = await (await admin.GetAsync($"/api/v1/licenses/activations?host={host}")).ReadAsync<List<LicenseActivation>>();
        Assert.AreEqual(item.Id, byHost.Single().Matches.Single().Id);
    }

    [TestMethod]
    public async Task CookieAndMcpReadThemWithoutKeysAndWithoutCredentialsInTheNote()
    {
        var item = await CreateAsync(Body("Service", "Contract", Clock.Today.AddDays(9), "Manual", vendor: "Swisscom", licenseKey: "SECRETKEY-12345-ABCDE",
            note: "Portal login: key: NJ4MXVQQ7QFKK44W8FV9CC9DR", acknowledge: true));
        var reader = new PNMTD.Tools.ToolContext(app.Services,
            new PNMTD.Auth.Rights.UserRights(false, new Dictionary<PNMTD.Auth.Rights.Area, PNMTD.Auth.Rights.Level> { [PNMTD.Auth.Rights.Area.Licenses] = PNMTD.Auth.Rights.Level.View }),
            PNMTD.Tools.NoMasking.Instance);
        var list = await PNMTD.Cookie.CookieTools.RunAsync("licenses_list", "{\"search\":\"" + item.Display + "\"}", reader, CancellationToken.None);
        StringAssert.Contains(list, item.Display);
        StringAssert.Contains(list, "445");
        var got = await PNMTD.Cookie.CookieTools.RunAsync("license_get", "{\"number\":\"" + item.Display + "\"}", reader, CancellationToken.None);
        StringAssert.Contains(got, "Swisscom");
        Assert.IsFalse(got.Contains("SECRETKEY", StringComparison.Ordinal) || got.Contains("ABCDE", StringComparison.Ordinal), "never the keys, nor their hints");
        Assert.IsFalse(got.Contains("NJ4MXVQQ7QFKK44W8FV9CC9DR", StringComparison.Ordinal), "the note without what looks like a key");
        var costs = await PNMTD.Cookie.CookieTools.RunAsync("licenses_costs", "{\"group\":\"provider\",\"show\":\"forecast\"}", reader, CancellationToken.None);
        StringAssert.Contains(costs, "Swisscom");
        var nobody = new PNMTD.Tools.ToolContext(app.Services, new PNMTD.Auth.Rights.UserRights(false, new Dictionary<PNMTD.Auth.Rights.Area, PNMTD.Auth.Rights.Level>()),
            PNMTD.Tools.NoMasking.Instance);
        StringAssert.Contains(await PNMTD.Cookie.CookieTools.RunAsync("licenses_list", "{}", nobody, CancellationToken.None), "needs a right");
    }

    [TestMethod]
    public async Task AHostsHistoryLeavesTheLicencesOutWithoutTheArea()
    {
        var host = await HostAsync();
        var item = await CreateAsync(Body());
        (await admin.PostJsonAsync($"/api/v1/licenses/{item.Id}/assignments", new { hostIds = new[] { host } })).EnsureSuccessStatusCode();
        var all = await (await admin.GetAsync($"/api/v1/audit-log/subjects/{host}")).ReadAsync<Paged<AuditLogItem>>();
        Assert.IsTrue(all.Items.Any(i => i.EventType == AuditEvents.LicenseAssigned));
        var monitoring = await (await app.ClientWithRights("Monitoring:View").GetAsync($"/api/v1/audit-log/subjects/{host}")).ReadAsync<Paged<AuditLogItem>>();
        Assert.IsFalse(monitoring.Items.Any(i => i.EventType == AuditEvents.LicenseAssigned));
        var history = await (await admin.GetAsync($"/api/v1/licenses/{item.Id}/history")).ReadAsync<Paged<AuditLogItem>>();
        CollectionAssert.IsSubsetOf(new[] { AuditEvents.LicenseCreated, AuditEvents.LicenseAssigned }, history.Items.Select(i => i.EventType).ToList());
    }
}
