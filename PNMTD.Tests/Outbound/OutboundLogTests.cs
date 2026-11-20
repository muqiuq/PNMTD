using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Dev;
using PNMTD.Models.Db;
using PNMTD.Notifications;
using PNMTD.Outbound;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Outbound;

/// <summary>
/// The two outbound logs (docs/redmine-queue-outbound-plan.md §6, §7): what each channel records, PNMT's secrets masked, private
/// notes left out, each log trimmed to its own newest records, and a full queue that drops instead of waiting.
/// </summary>
[TestClass]
public sealed class OutboundLogTests
{
    private const string UserKey = "uQiRzpo4DXghDmr9QzzfQu27cmVRsG";
    private static readonly DebugTests.FakePushover pushover = new();
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static NotificationService notifications = null!;
    private static OutboundLog log = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        var settings = new Dictionary<string, string?>(DebugTests.Settings);
        foreach (var (key, value) in FakeRedmine.Settings)
        {
            settings[key] = value;
        }
        app = new TestApp(settings: settings, services: s =>
        {
            pushover.Install(s);
            redmine.Install(s);
        });
        admin = app.Client(Roles.Admin);
        notifications = app.Services.GetRequiredService<NotificationService>();
        log = app.Services.GetRequiredService<OutboundLog>();
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static string Purpose() => $"test {Guid.NewGuid():N}";

    private static async Task<OutboundNotificationEntity> NotificationAsync(string purpose)
    {
        await log.FlushAsync();
        await using var db = app.NewDb();
        return await db.OutboundNotifications.AsNoTracking().SingleAsync(n => n.Purpose == purpose);
    }

    [TestMethod]
    public async Task A_Pushover_message_is_recorded_with_the_token_and_the_user_key_masked()
    {
        var purpose = Purpose();
        var result = await notifications.SendAsync(new NotificationMessage($"pushover://{UserKey}", "Disk full", "C: is full", "C: is full on srv-1"),
            purpose, false, CancellationToken.None, "alice");
        Assert.IsTrue(result!.Ok, result.Detail);
        var n = await NotificationAsync(purpose);
        Assert.AreEqual((OutboundChannel.Pushover, OutboundOutcome.Ok, 200, "alice", "Disk full"), (n.Channel, n.Outcome, n.StatusCode, n.AskedBy, n.Summary));
        Assert.AreEqual(PushoverNotification.PUSHOVER_ENDPOINT, n.Target);
        Assert.AreEqual("pushover://•••VRsG", n.Recipient);
        var body = OutboundLog.Gunzip(n.RequestBody)!;
        StringAssert.Contains(body, "\"token\":\"•••1234\"");
        StringAssert.Contains(body, "\"user\":\"•••VRsG\"");
        StringAssert.Contains(body, "C: is full");
        Assert.IsFalse(body.Contains("app-token-1234", StringComparison.Ordinal) || body.Contains(UserKey, StringComparison.Ordinal));
        StringAssert.Contains(OutboundLog.Gunzip(n.ResponseBody), "\"status\":1");

        // The API: the list without bodies, the record in full.
        var list = await (await admin.GetAsync($"/api/v1/system/outbound/notifications?channel=Pushover&search={Uri.EscapeDataString(purpose)}"))
            .ReadAsync<Paged<OutboundNotificationItem>>();
        Assert.AreEqual(n.Id, list.Items.Single().Id);
        var detail = await (await admin.GetAsync($"/api/v1/system/outbound/notifications/{n.Id}")).ReadAsync<OutboundDetail>();
        Assert.AreEqual(body, detail.RequestBody);
        Assert.IsTrue(detail.RequestHeaders.Any(h => h.Name == "Content-Type"));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.User).GetAsync($"/api/v1/system/outbound/notifications/{n.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task A_webhook_is_recorded_with_its_URL_as_it_is()
    {
        var purpose = Purpose();
        const string url = "https://hooks.example.com/services/T0001/B0002/abcdefghijklmnopqrstuvwx";
        var result = await notifications.SendAsync(new NotificationMessage(url, "Alert", "short", "the long text"), purpose, false, CancellationToken.None);
        Assert.IsFalse(result!.Ok, "the fake answers 404");
        var n = await NotificationAsync(purpose);
        Assert.AreEqual((OutboundChannel.Http, OutboundOutcome.Failed, 404, url, url), (n.Channel, n.Outcome, n.StatusCode, n.Target, n.Recipient));
        Assert.AreEqual("Alert\nthe long text", OutboundLog.Gunzip(n.RequestBody));
        Assert.AreEqual("PNMT", n.AskedBy);
    }

    [TestMethod]
    public async Task A_simulated_send_and_one_that_never_went_out_are_recorded_too()
    {
        var simulated = Purpose();
        await notifications.SendAsync(new NotificationMessage($"pushover://{UserKey}", "Simulated", "s", "l"), simulated, true, CancellationToken.None);
        var s = await NotificationAsync(simulated);
        Assert.AreEqual((OutboundOutcome.Simulated, "POST"), (s.Outcome, s.Method));
        StringAssert.Contains(OutboundLog.Gunzip(s.RequestBody), "\"token\":\"•••1234\"");
        Assert.IsNull(s.ResponseBody);

        // E-mail without Smtp:Host: nothing goes out, the log says why.
        var unsent = Purpose();
        var result = await notifications.SendAsync(new NotificationMessage("alice@example.com", "Mail", "s", "l"), unsent, false, CancellationToken.None);
        Assert.IsFalse(result!.Ok);
        var u = await NotificationAsync(unsent);
        Assert.AreEqual((OutboundChannel.Email, OutboundOutcome.Failed, "alice@example.com"), (u.Channel, u.Outcome, u.Recipient));
        Assert.AreEqual("Smtp:Host or Smtp:Sender is not set.", u.Error);
    }

    [TestMethod]
    public async Task A_mail_is_recorded_with_its_MIME_and_the_SMTP_conversation_without_the_password()
    {
        using var smtp = new FakeSmtp();
        var settings = new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "127.0.0.1", ["Smtp:Port"] = smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Smtp:Sender"] = "pnmt@example.com", ["Smtp:Username"] = "pnmt", ["Smtp:Password"] = "s3cret-Passw0rd",
        };
        using var own = new TestApp(settings: settings);
        var purpose = Purpose();
        var result = await own.Services.GetRequiredService<NotificationService>()
            .SendAsync(new NotificationMessage("alice@example.com", "Disk full", "short", "C: is full on srv-1"), purpose, false, CancellationToken.None, "Cookie");
        Assert.IsTrue(result!.Ok, result.Detail);
        await own.Services.GetRequiredService<OutboundLog>().FlushAsync();
        await using var db = own.NewDb();
        var n = await db.OutboundNotifications.AsNoTracking().SingleAsync(x => x.Purpose == purpose);
        Assert.AreEqual((OutboundChannel.Email, OutboundOutcome.Ok, "SMTP", "Cookie"), (n.Channel, n.Outcome, n.Method, n.AskedBy));
        Assert.AreEqual($"smtp://127.0.0.1:{smtp.Port}", n.Target);
        var mime = OutboundLog.Gunzip(n.RequestBody)!;
        StringAssert.Contains(mime, "Subject: Disk full");
        StringAssert.Contains(mime, "C: is full on srv-1");
        StringAssert.Contains(OutboundLog.Gunzip(n.ResponseBody), "queued as FAKE1");
        var transcript = OutboundLog.Gunzip(n.Transcript)!;
        StringAssert.Contains(transcript, "AUTH PLAIN");
        var secret = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0pnmt\0s3cret-Passw0rd"));
        Assert.IsFalse(transcript.Contains(secret, StringComparison.Ordinal) || transcript.Contains("s3cret-Passw0rd", StringComparison.Ordinal),
            "AUTH is redacted");
        Assert.IsTrue(n.RequestHeaders.Contains("Subject", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task A_Redmine_call_is_recorded_with_its_job_the_key_masked_and_no_private_note()
    {
        var detail = await app.Client(Roles.User).GetAsync("/api/v1/redmine/issues/9");
        Assert.AreEqual(HttpStatusCode.OK, detail.StatusCode);
        await log.FlushAsync();
        await using var db = app.NewDb();
        var call = await db.RedmineCalls.AsNoTracking().Where(c => c.Purpose == "ticket #9" && c.Summary == "the ticket").OrderByDescending(c => c.Id).FirstAsync();
        Assert.AreEqual((RedmineCallKind.Read, "GET", RedmineLane.Interactive, OutboundOutcome.Ok, 200), (call.Kind, call.Method, call.Lane, call.Outcome, call.StatusCode));
        Assert.IsNotNull(call.JobId);
        StringAssert.Contains(call.RequestHeaders, "•••6789");
        Assert.IsFalse(call.RequestHeaders.Contains(FakeRedmine.Key, StringComparison.Ordinal));
        var body = OutboundLog.Gunzip(call.ResponseBody)!;
        Assert.IsFalse(body.Contains("4711", StringComparison.Ordinal), "the private note stays out, as on the page");
        StringAssert.Contains(body, OutboundRedactor.PrivateNote);

        var job = await (await admin.GetAsync($"/api/v1/system/outbound/redmine?job={call.JobId}")).ReadAsync<Paged<RedmineCallItem>>();
        Assert.IsTrue(job.Items.All(i => i.JobId == call.JobId) && job.Items.Any(i => i.Id == call.Id));
        var full = await (await admin.GetAsync($"/api/v1/system/outbound/redmine/{call.Id}")).ReadAsync<OutboundDetail>();
        Assert.AreEqual("•••6789", full.RequestHeaders.Single(h => h.Name == "X-Redmine-API-Key").Value);
        var live = await (await admin.GetAsync("/api/v1/system/outbound/live")).ReadAsync<OutboundLive>();
        Assert.AreEqual(4, live.Redmine.Slots);
        Assert.IsTrue(live.NewestRedmineId >= call.Id);
    }

    [TestMethod]
    public async Task Each_log_keeps_its_own_newest_records()
    {
        await using var db = app.NewDb();
        for (var i = 0; i < 5; i++)
        {
            db.OutboundNotifications.Add(new OutboundNotificationEntity { StartedAt = DateTime.UtcNow, Purpose = "trim", Target = "t", Summary = "s", AskedBy = "PNMT" });
            db.RedmineCalls.Add(new RedmineCallEntity { StartedAt = DateTime.UtcNow, Purpose = "trim", Target = "t", Summary = "s", AskedBy = "PNMT" });
        }
        await db.SaveChangesAsync();
        var notifications = await db.OutboundNotifications.CountAsync();
        var calls = await db.RedmineCalls.CountAsync();
        var newest = await db.OutboundNotifications.MaxAsync(n => n.Id);
        var options = new OptionsMonitor(new OutboundOptions { KeepNotifications = notifications - 2, KeepRedmine = 3 });
        var trimming = new OutboundLog(app.Services.GetRequiredService<IDbContextFactory<PNMTD.Data.PnmtdDbContext>>(), options, NullLogger<OutboundLog>.Instance);
        await trimming.TrimAsync(db, OutboundLog.RedmineLog, CancellationToken.None);
        Assert.AreEqual(3, await db.RedmineCalls.CountAsync());
        Assert.AreEqual(notifications, await db.OutboundNotifications.CountAsync(), "Redmine's calls never push a notification out");
        await trimming.TrimAsync(db, OutboundLog.NotificationsLog, CancellationToken.None);
        Assert.AreEqual(notifications - 2, await db.OutboundNotifications.CountAsync());
        Assert.IsTrue(await db.OutboundNotifications.AnyAsync(n => n.Id == newest));
        Assert.IsTrue(calls >= 5);
    }

    [TestMethod]
    public void A_full_queue_drops_and_counts_instead_of_waiting()
    {
        var unwritten = new OutboundLog(app.Services.GetRequiredService<IDbContextFactory<PNMTD.Data.PnmtdDbContext>>(),
            new OptionsMonitor(new OutboundOptions()), NullLogger<OutboundLog>.Instance);
        for (var i = 0; i < 5_001; i++)
        {
            unwritten.Record(new OutboundNotificationEntity { Purpose = "flood" }, null, null);
        }
        Assert.AreEqual(1, unwritten.DroppedNotifications);
        Assert.AreEqual(0, unwritten.DroppedRedmine);
    }

    [TestMethod]
    public void The_redactor_masks_PNMTs_secrets_only()
    {
        Assert.AreEqual("•••6789", OutboundRedactor.Mask(FakeRedmine.Key));
        Assert.AreEqual("•••", OutboundRedactor.Mask("short"));
        Assert.IsTrue(OutboundRedactor.IsSecretHeader("x-redmine-api-key") && OutboundRedactor.IsSecretHeader("Authorization"));
        Assert.IsFalse(OutboundRedactor.IsSecretHeader("Content-Type"));
        Assert.AreEqual("""{"token":"•••1234","user":"•••VRsG","message":"hi"}""",
            OutboundRedactor.PushoverBody($$"""{"token":"app-token-1234","user":"{{UserKey}}","message":"hi"}"""));
        Assert.AreEqual("not json", OutboundRedactor.PushoverBody("not json"));
        var redmineBody = """{"issue":{"id":1,"journals":[{"id":1,"notes":"secret 4711","private_notes":true},{"id":2,"notes":"public","private_notes":false}]}}""";
        var cleaned = OutboundRedactor.WithoutPrivateNotes(redmineBody)!;
        Assert.IsFalse(cleaned.Contains("4711", StringComparison.Ordinal));
        StringAssert.Contains(cleaned, "public");
        Assert.AreEqual("""{"issues":[]}""", OutboundRedactor.WithoutPrivateNotes("""{"issues":[]}"""));
    }

    private sealed class OptionsMonitor(OutboundOptions value) : IOptionsMonitor<OutboundOptions>
    {
        public OutboundOptions CurrentValue => value;

        public OutboundOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<OutboundOptions, string?> listener) => null;
    }

    /// <summary>
    /// An SMTP server just good enough for MailKit: EHLO with AUTH PLAIN, MAIL, RCPT, DATA, QUIT. It accepts everything and
    /// answers DATA with "queued as FAKE1".
    /// </summary>
    private sealed class FakeSmtp : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();

        public FakeSmtp()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        private async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 fake ESMTP");
                while (await reader.ReadLineAsync() is { } line)
                {
                    var command = line.Split(' ')[0].ToUpperInvariant();
                    switch (command)
                    {
                        case "EHLO":
                            await writer.WriteAsync("250-fake\r\n250-AUTH PLAIN\r\n250 SIZE 10000000\r\n");
                            break;
                        case "HELO":
                            await writer.WriteLineAsync("250 fake");
                            break;
                        case "AUTH":
                            await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                            break;
                        case "MAIL":
                            await writer.WriteLineAsync("250 2.1.0 Ok");
                            break;
                        case "RCPT":
                            await writer.WriteLineAsync("250 2.1.5 Ok");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            while (await reader.ReadLineAsync() is { } data && data != ".")
                            {
                            }
                            await writer.WriteLineAsync("250 2.0.0 Ok: queued as FAKE1");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 2.0.0 Bye");
                            return;
                        default:
                            await writer.WriteLineAsync("250 Ok");
                            break;
                    }
                }
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            stop.Dispose();
        }
    }
}
