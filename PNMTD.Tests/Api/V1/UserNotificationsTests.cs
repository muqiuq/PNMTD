using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Notifications;
using PNMTD.Tasks;

namespace PNMTD.Tests.Api.V1;

/// <summary>
/// Notifications to people (docs/knowledge-base-extensions-plan.md §10, §16): a batch collects and goes out as one e-mail, each
/// person's choices (kept only where they differ, and asked again when sending), who is skipped and why, the daily limit, the retries,
/// a run stopping at its first failure, interrupted sends, the cleanup, and the alert loop neither touching them nor waiting for them.
/// Real sends to a fake provider for <c>@notify.test</c>, with a fake renderer for <c>kb.liked</c>.
/// </summary>
[TestClass]
public sealed class UserNotificationsTests
{
    private static readonly FakeMail mail = new();
    private static TestApp app = null!;
    private static FakeTimeProvider time = null!;

    /// <summary>Takes every <c>@notify.test</c> address: succeeds, fails, or waits until released.</summary>
    private sealed class FakeMail : INotificationProvider
    {
        public enum Mode { Ok, Fail, Hang }

        public Mode Next { get; set; }

        public List<NotificationMessage> Sent { get; } = [];

        public TaskCompletionSource Entered { get; set; } = new();

        public TaskCompletionSource Release { get; set; } = new();

        public string Name => "email";

        public bool IsMatch(string recipient) => recipient.EndsWith("@notify.test", StringComparison.Ordinal);

        public async Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct)
        {
            if (Next == Mode.Hang)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            lock (Sent)
            {
                Sent.Add(message);
            }
            return Next == Mode.Fail ? DeliveryResult.Failed("454 the test server says no", TimeSpan.Zero) : new DeliveryResult(true, "250 ok", TimeSpan.Zero);
        }

        public Task<OutboundDescription> DescribeAsync(NotificationMessage message, CancellationToken ct) =>
            Task.FromResult(new OutboundDescription("SMTP", "smtp://fake", [], message.Long));
    }

    /// <summary>kb.liked's e-mail: who liked, in order; nothing when told so.</summary>
    private sealed class FakeRenderer : IUserNotificationRenderer
    {
        public bool Nothing { get; set; }

        public string Kind => UserNotificationKinds.KbLiked;

        public Task<RenderedNotification?> RenderAsync(PnmtdDbContext db, UserNotificationBatch batch, string? baseUrl, CancellationToken ct) =>
            Task.FromResult(Nothing ? null : new RenderedNotification($"{batch.Rows.Count} liked it", string.Join(", ", batch.Rows.Select(r => r.ActorName)),
                "Test likes", "you wrote it"));
    }

    private static readonly FakeRenderer renderer = new();

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(
            settings: new Dictionary<string, string?>
            {
                ["Development:DoNotSendNotifications"] = "false", ["UserNotifications:MaxPerUserDay"] = "3",
                ["PublicBaseUrl"] = "https://pnmt.example.com",
            },
            services: s =>
            {
                s.Insert(0, ServiceDescriptor.Singleton<INotificationProvider>(mail));
                s.AddSingleton<IUserNotificationRenderer>(renderer);
            });
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public void Freeze()
    {
        // A clean queue: other classes' likes and uses in the shared database would fall due on this clock too.
        using (var db = app.NewDb())
        {
            db.UserNotifications.Where(n => n.State == UserNotificationState.Pending || n.State == UserNotificationState.Sending)
                .ExecuteUpdate(u => u.SetProperty(n => n.State, UserNotificationState.Cancelled));
        }
        var now = DateTimeOffset.UtcNow;
        time = new FakeTimeProvider(new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero));
        Clock.Provider = time;
        mail.Next = FakeMail.Mode.Ok;
        mail.Sent.Clear();
        renderer.Nothing = false;
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    private static UserNotifications Service => app.Services.GetRequiredService<UserNotifications>();

    /// <summary>A person of their own, in Viewers (Knowledge base: View) unless said otherwise.</summary>
    private static async Task<Guid> PersonAsync(bool viewer = true, bool active = true, bool email = true)
    {
        var id = Guid.NewGuid();
        var name = $"notify-{id:N}"[..20];
        await using var db = app.NewDb();
        var user = new UserEntity
        {
            Id = id, Username = name, NormalizedUsername = name, DisplayName = name, PasswordHash = "x", SecurityStamp = "x", CreatedAt = Clock.UtcNow,
            IsActive = active, Email = email ? $"{name}@notify.test" : null,
        };
        if (viewer)
        {
            user.Groups.Add(new UserGroupEntity { UserId = id, GroupId = GroupEntity.Viewers });
        }
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task EnqueueAsync(Guid recipient, string batch, string actor, Guid? actorId = null, Guid? source = null)
    {
        await using var db = app.NewDb();
        await Service.EnqueueAsync(db, new UserNotice(UserNotificationKinds.KbLiked, [recipient], null, batch, source ?? Guid.NewGuid(), actorId ?? Guid.NewGuid(), actor),
            CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private static async Task<List<UserNotificationEntity>> RowsAsync(Guid user)
    {
        await using var db = app.NewDb();
        return await db.UserNotifications.AsNoTracking().Where(n => n.UserId == user).OrderBy(n => n.Id).ToListAsync();
    }

    private static Task<int> RunAsync() => Service.RunOnceAsync(CancellationToken.None);

    [TestMethod]
    public async Task A_batch_collects_its_window_and_goes_out_as_one_email()
    {
        var reader = await PersonAsync();
        await EnqueueAsync(reader, "kb.liked:a", "Anna");
        time.Advance(TimeSpan.FromMinutes(1));
        await EnqueueAsync(reader, "kb.liked:a", "Ben");
        await EnqueueAsync(reader, "kb.liked:a", "Myself", actorId: reader);
        var rows = await RowsAsync(reader);
        Assert.HasCount(2, rows, "the actor is never a recipient");
        Assert.AreEqual(rows[0].DueAt, rows[1].DueAt, "the second joins the first's batch");

        time.Advance(TimeSpan.FromMinutes(13));
        Assert.AreEqual(0, await RunAsync());
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, await RunAsync());
        var sent = mail.Sent.Single();
        Assert.AreEqual("2 liked it", sent.Subject);
        StringAssert.StartsWith(sent.Long, "Anna, Ben");
        StringAssert.Contains(sent.Long, "You get this because you wrote it. Turn it off: https://pnmt.example.com/pnmt-admin/account#notifications");
        rows = await RowsAsync(reader);
        Assert.IsTrue(rows.All(r => r.State == UserNotificationState.Sent && r.SentAt is not null));
        Assert.AreEqual(1, rows.Select(r => r.MessageId).Distinct().Count());
        await using var db = app.NewDb();
        Assert.IsTrue(await db.KeyValues.AnyAsync(k => k.Key == "last_user_notification_run"), "task health sees the second timer");

        // An undone like before its batch went out sends nothing.
        var source = Guid.NewGuid();
        await EnqueueAsync(reader, "kb.liked:b", "Eve", source: source);
        await using (var cancel = app.NewDb())
        {
            Assert.AreEqual(1, await UserNotifications.CancelAsync(cancel, source, CancellationToken.None));
        }
        time.Advance(TimeSpan.FromMinutes(20));
        Assert.AreEqual(0, await RunAsync());
        Assert.AreEqual(UserNotificationState.Cancelled, (await RowsAsync(reader))[^1].State);
    }

    [TestMethod]
    public async Task Choices_are_kept_where_they_differ_and_asked_again_when_sending()
    {
        var viewer = app.Client(Roles.Viewer);
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == TestUsers.Viewer).ExecuteUpdateAsync(u => u.SetProperty(x => x.Email, "viewer@notify.test"));
        }
        var settings = await (await viewer.GetAsync("/api/v1/account/notifications")).ReadAsync<UserNotificationSettings>();
        Assert.IsTrue(settings.HasEmail);
        CollectionAssert.AreEquivalent(new[] { UserNotificationKinds.KbLiked, UserNotificationKinds.KbUsed }, settings.Kinds.Select(k => k.Kind).ToArray());
        Assert.IsTrue(settings.Kinds.All(k => k.Channels.Single().Enabled && k.Channels.Single().Default));

        var off = new UserNotificationSettingsRequest { Items = [new UserNotificationChoice { Kind = UserNotificationKinds.KbLiked, Enabled = false }] };
        settings = await (await viewer.PutJsonAsync("/api/v1/account/notifications", off)).ReadAsync<UserNotificationSettings>();
        Assert.IsFalse(settings.Kinds.Single(k => k.Kind == UserNotificationKinds.KbLiked).Channels.Single().Enabled);
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(1, await db.UserNotificationPreferences.CountAsync(p => p.UserId == TestUsers.Viewer));
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.NotificationPreferencesChanged && l.Detail!.Contains("by e-mail off")));
        }
        // Off: nothing is enqueued.
        await EnqueueAsync(TestUsers.Viewer, "kb.liked:off", "Anna");
        Assert.IsEmpty(await RowsAsync(TestUsers.Viewer));

        // Back on (the default): the stored choice goes. Enqueued, then turned off before it went out: skipped.
        var on = new UserNotificationSettingsRequest { Items = [new UserNotificationChoice { Kind = UserNotificationKinds.KbLiked, Enabled = true }] };
        await (await viewer.PutJsonAsync("/api/v1/account/notifications", on)).ReadAsync<UserNotificationSettings>();
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(0, await db.UserNotificationPreferences.CountAsync(p => p.UserId == TestUsers.Viewer));
        }
        await EnqueueAsync(TestUsers.Viewer, "kb.liked:on", "Anna");
        await (await viewer.PutJsonAsync("/api/v1/account/notifications", off)).ReadAsync<UserNotificationSettings>();
        time.Advance(TimeSpan.FromMinutes(15));
        await RunAsync();
        var row = (await RowsAsync(TestUsers.Viewer)).Single();
        Assert.AreEqual(UserNotificationState.Skipped, row.State);
        StringAssert.Contains(row.Outcome, "turned it off");
        await (await viewer.PutJsonAsync("/api/v1/account/notifications", on)).ReadAsync<UserNotificationSettings>();

        // Without the right, the kinds aren't offered, and can't be chosen.
        var none = app.ClientWithRights("Monitoring:View");
        Assert.IsEmpty((await (await none.GetAsync("/api/v1/account/notifications")).ReadAsync<UserNotificationSettings>()).Kinds);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await none.PutJsonAsync("/api/v1/account/notifications", off)).StatusCode);
    }

    [TestMethod]
    public async Task Who_cant_get_it_is_skipped_with_why_and_the_daily_limit_holds()
    {
        var inactive = await PersonAsync(active: false);
        var noEmail = await PersonAsync(email: false);
        var noRight = await PersonAsync(viewer: false);
        foreach (var person in new[] { inactive, noEmail, noRight })
        {
            await EnqueueAsync(person, "kb.liked:who", "Anna");
        }
        var busy = await PersonAsync();
        for (var i = 0; i < 4; i++)
        {
            await EnqueueAsync(busy, $"kb.liked:busy{i}", "Anna");
            time.Advance(TimeSpan.FromSeconds(1));
        }
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.AreEqual(3, await RunAsync());
        StringAssert.Contains((await RowsAsync(inactive)).Single().Outcome, "deactivated");
        StringAssert.Contains((await RowsAsync(noEmail)).Single().Outcome, "no e-mail address");
        StringAssert.Contains((await RowsAsync(noRight)).Single().Outcome, "no longer has Knowledge base: View");
        var rows = await RowsAsync(busy);
        Assert.AreEqual(3, rows.Count(r => r.State == UserNotificationState.Sent));
        Assert.AreEqual(UserNotificationState.Skipped, rows[^1].State);
        StringAssert.Contains(rows[^1].Outcome, "daily limit of 3");

        // Nothing to say: skipped too.
        var quiet = await PersonAsync();
        renderer.Nothing = true;
        await EnqueueAsync(quiet, "kb.liked:quiet", "Anna");
        time.Advance(TimeSpan.FromMinutes(15));
        await RunAsync();
        StringAssert.Contains((await RowsAsync(quiet)).Single().Outcome, "Nothing left to say");
    }

    [TestMethod]
    public async Task A_failure_is_retried_later_a_run_stops_at_it_and_the_fifth_failure_is_the_end()
    {
        var first = await PersonAsync();
        var second = await PersonAsync();
        await EnqueueAsync(first, "kb.liked:fail", "Anna");
        time.Advance(TimeSpan.FromSeconds(1));
        await EnqueueAsync(second, "kb.liked:fail", "Anna");
        time.Advance(TimeSpan.FromMinutes(15));
        mail.Next = FakeMail.Mode.Fail;
        Assert.AreEqual(0, await RunAsync());
        var row = (await RowsAsync(first)).Single();
        Assert.AreEqual(UserNotificationState.Pending, row.State);
        Assert.AreEqual(1, row.Attempts);
        Assert.AreEqual(Clock.UtcNow + UserNotifications.Retries[0], Clock.AsUtc(row.NextAttemptAt), "a minute later");
        StringAssert.Contains(row.Outcome, "the test server says no");
        Assert.AreEqual(0, (await RowsAsync(second)).Single().Attempts, "the run stopped at the first failure");

        foreach (var wait in UserNotifications.Retries)
        {
            time.Advance(wait);
            await RunAsync();
            await RunAsync();
        }
        row = (await RowsAsync(first)).Single();
        Assert.AreEqual(UserNotificationState.Failed, row.State);
        Assert.AreEqual(UserNotifications.Retries.Length + 1, row.Attempts);

        // Back up: the other one goes out at its next attempt.
        mail.Next = FakeMail.Mode.Ok;
        time.Advance(TimeSpan.FromHours(3));
        await RunAsync();
        Assert.AreEqual(UserNotificationState.Sent, (await RowsAsync(second)).Single().State);
    }

    [TestMethod]
    public async Task An_interrupted_send_goes_again_and_finished_rows_leave_after_thirty_days()
    {
        var person = await PersonAsync();
        await EnqueueAsync(person, "kb.liked:stopped", "Anna");
        await using (var db = app.NewDb())
        {
            await db.UserNotifications.Where(n => n.UserId == person).ExecuteUpdateAsync(u => u
                .SetProperty(n => n.State, UserNotificationState.Sending).SetProperty(n => n.NextAttemptAt, Clock.UtcNow));
        }
        time.Advance(TimeSpan.FromMinutes(5));
        await RunAsync();
        Assert.AreEqual(UserNotificationState.Sending, (await RowsAsync(person)).Single().State, "maybe still sending");
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.AreEqual(1, await RunAsync());
        Assert.AreEqual(UserNotificationState.Sent, (await RowsAsync(person)).Single().State);

        await EnqueueAsync(person, "kb.liked:waiting", "Anna");
        await using (var db = app.NewDb())
        {
            // Still pending after 31 days (never sent): kept; the finished one goes.
            await db.UserNotifications.Where(n => n.UserId == person && n.BatchKey == "kb.liked:waiting")
                .ExecuteUpdateAsync(u => u.SetProperty(n => n.NextAttemptAt, Clock.UtcNow.AddDays(60)));
        }
        time.Advance(TimeSpan.FromDays(31));
        await RunAsync();
        var left = await RowsAsync(person);
        Assert.AreEqual("kb.liked:waiting", left.Single().BatchKey);
    }

    [TestMethod]
    public async Task The_alert_loop_neither_touches_them_nor_waits_for_them()
    {
        var person = await PersonAsync();
        await EnqueueAsync(person, "kb.liked:alerts", "Anna");
        time.Advance(TimeSpan.FromMinutes(15));
        var task = ActivatorUtilities.CreateInstance<NotificationTask>(app.Services);
        task.RunOnce();
        Assert.AreEqual(UserNotificationState.Pending, (await RowsAsync(person)).Single().State);

        mail.Next = FakeMail.Mode.Hang;
        mail.Entered = new TaskCompletionSource();
        mail.Release = new TaskCompletionSource();
        var hanging = Task.Run(() => task.RunUserNotificationsOnceAsync());
        await mail.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var alerts = Task.Run(task.RunOnce);
        Assert.AreSame(alerts, await Task.WhenAny(alerts, Task.Delay(TimeSpan.FromSeconds(30))), "the alerts ran while a person's e-mail hung");
        mail.Release.TrySetResult();
        Assert.AreEqual(1, await hanging);
    }
}
