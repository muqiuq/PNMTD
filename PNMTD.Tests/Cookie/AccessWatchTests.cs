using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Cookie.Access;
using PNMTD.Dev;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// The access watch with the database (docs/cookie-access-plan.md §10.6): a server's login becomes a review in one case per target
/// and account, known history and known access settle the rest; code's notice for a strong signal Cookie didn't judge; Was this
/// you? from the e-mail to the answer; a router's log lines; PNMT's sign-ins; and an access case's ticket only when asked. Cookie
/// stays stopped, the messages are simulated.
/// </summary>
[TestClass]
public sealed partial class AccessWatchTests
{
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid contact;
    private static Guid jdoe;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        var settings = new Dictionary<string, string?>(FakeRedmine.WritableSettings) { ["Cookie:Enabled"] = "true", ["PublicBaseUrl"] = "https://pnmt.example" };
        app = new TestApp(settings: settings, services: redmine.Install);
        admin = app.Client(Roles.Admin);
        await using var db = app.NewDb();
        UserEntity User(string name, string email, bool isAdmin, List<string>? accounts = null) => new()
        {
            Id = Guid.NewGuid(), Username = V1.Unique(name), NormalizedUsername = V1.Unique(name.ToLowerInvariant()), DisplayName = name, PasswordHash = "x",
            SecurityStamp = "s", Email = email, PushoverKey = isAdmin ? "uQiRzpo4DXghDmr9QzzfQu27cmVRsG" : null, IsAdmin = isAdmin, ServerAccounts = accounts ?? [],
        };
        var c = User("Dana Contact", "dana@example.com", true);
        var j = User("Jo Doe", "jo@example.com", false, ["CONTOSO\\jdoe"]);
        db.Users.AddRange(c, j);
        await db.SaveChangesAsync();
        (contact, jdoe) = (c.Id, j.Id);
        // Always working hours: what this class tests doesn't wait for them.
        await V1.AvailabilityHoursAsync(admin, []);
        await SaveAsync(Watch());
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        // The other classes' rounds share the database: nothing of this class waits for Cookie after it.
        await using (var db = app.NewDb())
        {
            await db.AccessReviews.Where(r => r.State == AccessReviewState.Waiting).ExecuteUpdateAsync(u => u.SetProperty(r => r.State, AccessReviewState.NotJudged));
            await db.CookieCases.Where(c => c.Open && c.Kind == CookieCaseKind.Access)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Open, false).SetProperty(c => c.ClosedAt, Clock.UtcNow));
        }
        await SaveAsync(new AccessSettings());
        await V1.AvailabilityHoursAsync(admin, null);
        app.Dispose();
    }

    private static AccessSettings Watch() => new()
    {
        On = true, LearningDays = 0, Contacts = [new AccessContact(contact, null, OutsideHours: true)],
    };

    private static async Task SaveAsync(AccessSettings value)
    {
        using var response = await admin.PutAsJsonAsync("/api/v1/cookie/admin/settings/access", value, CookieSettings.Json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static AccessWatch Watcher => app.Services.GetRequiredService<AccessWatch>();

    private static async Task<Guid> ServerAsync(string name)
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique(name));
        return (await V1.CreateSensorAsync(admin, host.Id, name: "agent")).Id;
    }

    private static async Task<Guid> LoginAsync(Guid sensor, string user, string? source, DateTime at, LogonKind kind = LogonKind.Rdp, string? domain = "CONTOSO",
        LogonOutcome outcome = LogonOutcome.Success, int count = 1)
    {
        await using var db = app.NewDb();
        var row = new AgentLoginEntity
        {
            Id = Guid.NewGuid(), SensorId = sensor, At = at, Count = count, Outcome = outcome, User = user, Domain = domain, Kind = kind, SourceAddress = source,
            Channel = "Security", RecordId = Random.Shared.NextInt64(),
        };
        db.AgentLogins.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<List<AccessReviewEntity>> ReviewsAsync(Guid sensor)
    {
        await using var db = app.NewDb();
        return await db.AccessReviews.AsNoTracking().Where(r => r.SensorId == sensor).OrderBy(r => r.At).ToListAsync();
    }

    [TestMethod]
    public async Task A_new_login_opens_one_case_and_history_and_known_access_settle_the_rest()
    {
        var sensor = await ServerAsync("srv-history");
        var now = Clock.UtcNow;
        await LoginAsync(sensor, "jdoe", "10.1.2.3", now.AddDays(-30));

        var usual = await LoginAsync(sensor, "jdoe", "10.1.2.40", now.AddMinutes(-2));
        await Watcher.ServerLoginsAsync(sensor, [usual], CancellationToken.None);
        Assert.AreEqual(0, (await ReviewsAsync(sensor)).Count, "the same account, network and way is the history");

        var fresh = await LoginAsync(sensor, "jdoe", "10.9.9.9", now.AddMinutes(-1));
        await Watcher.ServerLoginsAsync(sensor, [fresh], CancellationToken.None);
        await Watcher.ServerLoginsAsync(sensor, [fresh], CancellationToken.None);
        var reviews = await ReviewsAsync(sensor);
        Assert.AreEqual(1, reviews.Count, "once, however often it comes");
        Assert.AreEqual(AccessReviewState.Waiting, reviews[0].State);
        StringAssert.Contains(reviews[0].Signals, "new-network");
        Assert.IsFalse(reviews[0].Strong);
        Assert.AreEqual(jdoe, reviews[0].PersonId, "linked by the user's server accounts");

        var again = await LoginAsync(sensor, "jdoe", "10.8.8.8", now);
        await Watcher.ServerLoginsAsync(sensor, [again], CancellationToken.None);
        reviews = await ReviewsAsync(sensor);
        Assert.AreEqual(2, reviews.Count);
        Assert.AreEqual(reviews[0].CaseId, reviews[1].CaseId, "one case per target and account");
        await using (var db = app.NewDb())
        {
            var c = await db.CookieCases.SingleAsync(x => x.Id == reviews[0].CaseId);
            Assert.AreEqual(CookieCaseKind.Access, c.Kind);
            Assert.AreEqual(AccessWatch.KeyOf(sensor, "CONTOSO\\jdoe"), c.Key);
            CollectionAssert.Contains(c.Areas, CookieAccess.LoginsArea, "names and addresses: Monitoring: Details");
        }

        using (var added = await admin.PostAsJsonAsync("/api/v1/cookie/admin/access/known",
                   new AccessKnownRequest("target", AccessTargetKind.Server, sensor, null, "jdoe", "10.7.7.0/24", null, null, "The vendor's VPN"), CookieSettings.Json))
        {
            Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync());
        }
        var covered = await LoginAsync(sensor, "jdoe", "10.7.7.7", now);
        await Watcher.ServerLoginsAsync(sensor, [covered], CancellationToken.None);
        Assert.AreEqual(2, (await ReviewsAsync(sensor)).Count, "known access counts as history");
    }

    [TestMethod]
    public async Task A_strong_signal_Cookie_didnt_judge_gets_codes_notice_after_ten_minutes()
    {
        var sensor = await ServerAsync("srv-strong");
        var now = Clock.UtcNow;
        await LoginAsync(sensor, "backup", "203.0.113.7", now.AddMinutes(-3), outcome: LogonOutcome.Failure, count: 12, domain: null);
        var login = await LoginAsync(sensor, "backup", "203.0.113.7", now.AddMinutes(-1), domain: null);
        await Watcher.ServerLoginsAsync(sensor, [login], CancellationToken.None);
        var review = (await ReviewsAsync(sensor)).Single();
        Assert.IsTrue(review.Strong);
        StringAssert.Contains(review.Signals, "after-failures");
        StringAssert.Contains(review.Signals, "public-source");

        var task = app.Services.GetRequiredService<AccessWatchTask>();
        Assert.AreEqual(0, (await task.FallbackAsync(CancellationToken.None)).CodeNotices, "Cookie has 10 minutes");
        await using (var db = app.NewDb())
        {
            await db.AccessReviews.Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.CreatedAt, now.AddMinutes(-11)));
        }
        Assert.AreEqual(1, (await task.FallbackAsync(CancellationToken.None)).CodeNotices);
        await using (var db = app.NewDb())
        {
            var judged = await db.AccessReviews.SingleAsync(r => r.Id == review.Id);
            Assert.AreEqual((AccessReviewState.Alarming, "code"), (judged.State, judged.JudgedBy), "two strong signals: alarming");
            var c = await db.CookieCases.SingleAsync(x => x.Id == judged.CaseId);
            Assert.AreEqual("alarming", c.AccessNotifiedVerdict);
            var sent = await db.CookieOutbox.Where(o => o.CaseId == c.Id).ToListAsync();
            CollectionAssert.AreEquivalent(new[] { CookieChannel.AccessEmail, CookieChannel.AccessPushover }, sent.Select(o => o.Channel).ToList(),
                "alarming: e-mail and Pushover, to the contact");
            Assert.IsTrue(sent.All(o => o.UserId == contact && o.State == CookieOutboxState.Simulated && o.Source == "code"));
            StringAssert.Contains(sent[0].Body, "Cookie couldn't judge this login");
            StringAssert.Contains(sent[0].Body, "203.0.113.7");
        }
        // One notice per case and verdict.
        var notices = app.Services.GetRequiredService<AccessNotices>();
        var twice = await notices.NotifyAsync(review.CaseId!.Value, AccessVerdict.Alarming, null, "code", CancellationToken.None);
        Assert.AreEqual(0, twice.Sent, twice.Detail);
    }

    [GeneratedRegex("/was-this-you/([A-Za-z0-9_-]+)")]
    private static partial Regex TokenIn();

    [TestMethod]
    public async Task Was_this_you_asks_once_and_No_raises_the_alarm()
    {
        var sensor = await ServerAsync("srv-person");
        var now = Clock.UtcNow;
        await LoginAsync(sensor, "jdoe", "10.1.2.3", now.AddDays(-20));
        var login = await LoginAsync(sensor, "jdoe", "10.6.6.6", now.AddMinutes(-1));
        await Watcher.ServerLoginsAsync(sensor, [login], CancellationToken.None);
        var caseId = (await ReviewsAsync(sensor)).Single().CaseId!.Value;
        var ask = app.Services.GetRequiredService<WasThisYou>();
        StringAssert.Contains(await ask.AskAsync(caseId, AccessVerdict.Unusual, CancellationToken.None), "asked Jo Doe");
        Assert.IsNull(await ask.AskAsync(caseId, AccessVerdict.Suspicious, CancellationToken.None), "once per case");
        string token;
        await using (var db = app.NewDb())
        {
            var mail = await db.CookieOutbox.SingleAsync(o => o.CaseId == caseId && o.Channel == CookieChannel.WasThisYou);
            Assert.AreEqual((jdoe, CookieOutboxState.Simulated), (mail.UserId!.Value, mail.State));
            StringAssert.Contains(mail.Body, "10.6.6.6");
            token = TokenIn().Match(mail.Body).Groups[1].Value;
            Assert.AreNotEqual("", token);
            var review = await db.AccessReviews.SingleAsync(r => r.CaseId == caseId);
            Assert.AreNotEqual(token, review.TokenHash, "only the hash is kept");
        }

        var anonymous = app.Client();
        var view = await (await anonymous.GetAsync($"/api/v1/access/was-this-you/{token}")).ReadAsync<WasThisYouView>();
        Assert.IsTrue(view.Open);
        StringAssert.Contains(view.Login, "10.6.6.6");
        var reopened = await (await anonymous.GetAsync($"/api/v1/access/was-this-you/{token}")).ReadAsync<WasThisYouView>();
        Assert.IsTrue(reopened.Open, "opening the page answers nothing");

        using (var no = await anonymous.PostAsJsonAsync($"/api/v1/access/was-this-you/{token}", new WasThisYouRequest(false)))
        {
            Assert.AreEqual(HttpStatusCode.OK, no.StatusCode, await no.Content.ReadAsStringAsync());
        }
        using (var twice = await anonymous.PostAsJsonAsync($"/api/v1/access/was-this-you/{token}", new WasThisYouRequest(true)))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, twice.StatusCode, "the link works once");
        }
        await using (var db = app.NewDb())
        {
            var c = await db.CookieCases.SingleAsync(x => x.Id == caseId);
            Assert.AreEqual(("alarming", AccessService.NotExpected, true), (c.AccessVerdict, c.AccessAnswer, c.Open));
            var told = await db.CookieOutbox.Where(o => o.CaseId == caseId && o.Channel == CookieChannel.AccessEmail).ToListAsync();
            Assert.AreEqual(contact, told.Single().UserId, "the contact is told as alarming");
            StringAssert.Contains(told[0].Body, "Jo Doe says it wasn't them");
            Assert.IsTrue(await db.AuditLog.AnyAsync(a => a.EventType == PNMTD.Audit.AuditEvents.AccessPersonAnswered));
        }
        var status = (await app.Services.GetRequiredService<CaseBook>().RunAsync(false, CancellationToken.None));
        Assert.IsNull(status.Error);
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(OverallState.Alert, (await db.CookieRuntime.SingleAsync()).CodeStatus, "a login not expected is an alert");
        }
    }

    [TestMethod]
    public async Task A_routers_log_lines_are_stored_once_and_PNMTs_own_poll_is_left_out()
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique("rtr"));
        using var created = await admin.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = host.Id, Type = SensorType.MIKROTIK, Name = "router", Interval = 300, GracePeriod = 600,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = "192.0.2.50", SourceAddress = "192.0.2.10" },
        });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var sensor = (await created.ReadAsync<SensorDetail>()).Id;
        var local = Clock.LocalNow;
        string T(int minutesAgo) => local.AddMinutes(-minutesAgo).ToString("HH:mm:ss");
        var reading = new RouterReading
        {
            LogRead = true,
            Clock = local.ToString("yyyy-MM-dd HH:mm:ss"),
            Users = [new RouterUser("admin", "full", false), new RouterUser("pnmt", "pnmt-read", false)],
            Groups = [new RouterGroup("full", ["read", "write", "policy"]), new RouterGroup("pnmt-read", ["ssh", "read"])],
            Active = [new RouterActiveUser("pnmt", "now", "10.99.0.1", "ssh")],
            LogLines =
            [
                new RouterLogLine(T(6), "user pnmt logged in from 10.99.0.1 via ssh"),
                new RouterLogLine(T(5), "login failure for user admin from 203.0.113.9 via winbox"),
                new RouterLogLine(T(5), "login failure for user admin from 203.0.113.9 via winbox (2)"),
                new RouterLogLine(T(4), "login failure for user admin from 203.0.113.9 via winbox (3)"),
                new RouterLogLine(T(3), "user admin logged in from 203.0.113.9 via winbox"),
                new RouterLogLine(T(1), "user admin logged out from 203.0.113.9 via winbox"),
                new RouterLogLine(T(1), "user bob logged in via something-new"),
            ],
        };
        var store = app.Services.GetRequiredService<RouterLoginStore>();
        await store.StoreAsync(sensor, reading, null, CancellationToken.None);
        await store.StoreAsync(sensor, reading, null, CancellationToken.None);
        await using var db = app.NewDb();
        var logins = await db.AgentLogins.AsNoTracking().Where(l => l.SensorId == sensor).ToListAsync();
        Assert.IsFalse(logins.Any(l => l.User == "pnmt"), "PNMT's own poll isn't stored");
        var success = logins.Single(l => l.Outcome == LogonOutcome.Success);
        Assert.AreEqual(("admin", LogonKind.Winbox, true, RouterLogins.Channel), (success.User, success.Kind, success.IsAdmin, success.Channel));
        Assert.IsNotNull(success.LoggedOffAt, "the logout ends it");
        var failures = logins.Where(l => l.Outcome == LogonOutcome.Failure).ToList();
        Assert.AreEqual(3, failures.Sum(f => f.Count), "aggregated, and read twice counted once");
        var router = await db.MikrotikRouters.AsNoTracking().SingleAsync(r => r.SensorId == sensor);
        Assert.AreEqual(("10.99.0.1", 1), (router.PnmtAddress, router.LogUnmatched));
        var review = await db.AccessReviews.AsNoTracking().SingleAsync(r => r.SensorId == sensor);
        Assert.AreEqual(AccessTargetKind.Router, review.Target);
        Assert.IsTrue(review.Strong, "after failures, from a public address");
        StringAssert.Contains(review.Signals, "after-failures");
    }

    [TestMethod]
    public async Task PNMTs_sign_ins_are_read_from_the_audit_log()
    {
        string name;
        await using (var db = app.NewDb())
        {
            var runtime = await CaseBook.RuntimeAsync(db, CancellationToken.None);
            runtime.AccessAuditAt = Clock.UtcNow.AddMinutes(-1);
            name = V1.Unique("pat");
            db.Users.Add(new UserEntity
            {
                Id = Guid.NewGuid(), Username = name, NormalizedUsername = name.ToUpperInvariant(), DisplayName = "Pat", PasswordHash = "x", SecurityStamp = "s",
            });
            db.AuditLog.Add(new AuditLogEntity
            {
                Id = Guid.NewGuid(), CreatedAt = Clock.UtcNow, Username = name, EventType = PNMTD.Audit.AuditEvents.LoginSucceeded, Category = PNMTD.Audit.AuditCategory.Auth,
                Actor = PNMTD.Audit.AuditActor.User, Outcome = PNMTD.Audit.AuditOutcome.Succeeded, IpAddress = "198.51.100.20",
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0",
            });
            await db.SaveChangesAsync();
        }
        Assert.IsGreaterThanOrEqualTo(1, await Watcher.SignInsAsync(CancellationToken.None));
        await using (var db = app.NewDb())
        {
            var review = await db.AccessReviews.AsNoTracking().SingleAsync(r => r.Target == AccessTargetKind.Pnmt && r.Account == name);
            Assert.AreEqual(("web", "Firefox on Windows"), (review.Way, review.Device));
            StringAssert.Contains(review.Signals, "public-source");
            var c = await db.CookieCases.AsNoTracking().SingleAsync(x => x.Id == review.CaseId);
            CollectionAssert.AreEqual(new[] { CookieAccess.AuditLogArea }, c.Areas.ToArray(), "PNMT's sign-ins: Audit log: View");
        }
        Assert.AreEqual(0, await Watcher.SignInsAsync(CancellationToken.None), "the cursor moved on");
    }

    [TestMethod]
    public async Task An_access_case_gets_a_ticket_only_when_someone_asks()
    {
        var sensor = await ServerAsync("srv-ticket");
        var login = await LoginAsync(sensor, "ops", "10.4.4.4", Clock.UtcNow, domain: null);
        await Watcher.ServerLoginsAsync(sensor, [login], CancellationToken.None);
        var settings = app.Services.GetRequiredService<CookieSettings>();
        await settings.SaveAsync(new CookieRedmineSettings { TicketProjectId = 1 }, null, "test", null, CancellationToken.None);
        try
        {
            CookieCaseEntity c;
            await using (var db = app.NewDb())
            {
                var caseId = (await db.AccessReviews.AsNoTracking().SingleAsync(r => r.SensorId == sensor)).CaseId;
                c = await db.CookieCases.AsNoTracking().SingleAsync(x => x.Id == caseId);
            }
            var rights = new UserRights(false, new Dictionary<Area, Level> { [Area.Monitoring] = Level.Edit, [Area.Redmine] = Level.Link });
            var p = await app.Services.GetRequiredService<PrivacyFilter>().OpenAsync(CancellationToken.None);
            var ticket = new ProposedAction(CookieActionKind.Ticket, null, 0, null, "Who is ops?", "A new account logged in.", "New account.", [], 0.95, Urgency.Important);
            var executor = app.Services.GetRequiredService<CookieExecutor>();
            var refused = await executor.HandleAsync(new ActionRequest(new CookieProposal(ticket), c, null, rights, p, c.Areas), CancellationToken.None);
            Assert.AreEqual("refused", refused.Outcome);
            StringAssert.Contains(refused.Detail, "only when someone asks");
            var asked = await executor.HandleAsync(new ActionRequest(new CookieProposal(ticket), Asked(c), null, rights, p, c.Areas), CancellationToken.None);
            Assert.AreNotEqual("refused", asked.Outcome, asked.Detail);
        }
        finally
        {
            await settings.SaveAsync(new CookieRedmineSettings(), null, "test", null, CancellationToken.None);
        }
    }

    private static CookieCaseEntity Asked(CookieCaseEntity c)
    {
        c.TicketAskedAt = Clock.UtcNow;
        c.TicketAskedBy = "Dana";
        return c;
    }
}
