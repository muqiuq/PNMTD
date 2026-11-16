using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Models.Enum;
using PNMTD.Notifications;

namespace PNMTD.Tests.Api.V1;

/// <summary>
/// The Pushover priorities as a setting (docs/cookie-object-notes-plan.md §5): read by every send, changed on the Notification
/// manager with Monitoring's Edit, audited; an emergency carries the settings' retry and expiry. Against the fake Pushover.
/// </summary>
[TestClass]
public sealed class NotificationSettingsTests
{
    private static readonly DebugTests.FakePushover pushover = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: DebugTests.Settings, services: s => pushover.Install(s));
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<int> SendAsync(string key, Importance importance)
    {
        var notifications = app.Services.GetRequiredService<NotificationService>();
        var result = await notifications.SendAsync(new NotificationMessage($"pushover://{key}", "s", "m", "m", importance), "test", false, CancellationToken.None);
        Assert.IsTrue(result?.Ok, result?.Detail);
        return pushover.Sent.Last(m => m.GetProperty("user").GetString() == key).GetProperty("priority").GetInt32();
    }

    [TestMethod]
    public async Task The_defaults_are_the_old_mapping_and_a_change_reaches_the_next_send_without_a_restart()
    {
        var before = await (await app.Client(Roles.Viewer).GetAsync("/api/v1/notifications/settings")).ReadAsync<NotificationSettings>();
        Assert.AreEqual((-1, 0, 1, 60, 3600),
            (before.PushoverLow, before.PushoverNormal, before.PushoverHigh, before.EmergencyRetrySeconds, before.EmergencyExpireSeconds));
        Assert.AreEqual(1, await SendAsync("u-high-default", Importance.High));
        Assert.IsFalse(pushover.Sent.Last(m => m.GetProperty("user").GetString() == "u-high-default").TryGetProperty("retry", out _), "no retry below an emergency");

        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await app.Client(Roles.Viewer).PutJsonAsync("/api/v1/notifications/settings", new NotificationSettingsRequest { PushoverHigh = 2 })).StatusCode);
        var changed = await (await app.Client(Roles.User).PutJsonAsync("/api/v1/notifications/settings",
            new NotificationSettingsRequest { PushoverLow = 0, PushoverNormal = 0, PushoverHigh = 2, EmergencyRetrySeconds = 45, EmergencyExpireSeconds = 900 }))
            .ReadAsync<NotificationSettings>();
        Assert.AreEqual(2, changed.PushoverHigh);
        Assert.IsNotNull(changed.ChangedAt);
        Assert.AreEqual(TestUsers.NameOf(Roles.User), changed.ChangedBy);
        try
        {
            Assert.AreEqual(2, await SendAsync("u-high-emergency", Importance.High), "the next send follows the change");
            var sent = pushover.Sent.Last(m => m.GetProperty("user").GetString() == "u-high-emergency");
            Assert.AreEqual(45, sent.GetProperty("retry").GetInt32());
            Assert.AreEqual(900, sent.GetProperty("expire").GetInt32());
            Assert.AreEqual(0, await SendAsync("u-low-now-normal", Importance.Low), "Low is 0 now");

            var log = await (await admin.GetAsync($"/api/v1/audit-log?type={AuditEvents.NotificationSettingsChanged}")).ReadAsync<Paged<AuditLogItem>>();
            Assert.IsTrue(log.Items.Any(l => l.Detail == "Pushover: Low -1 → 0, High 1 → 2, retry 60 → 45 s, expire 3600 → 900 s"),
                string.Join(" | ", log.Items.Select(l => l.Detail)));
        }
        finally
        {
            await app.Client(Roles.User).PutJsonAsync("/api/v1/notifications/settings", new NotificationSettingsRequest());
        }
        Assert.AreEqual(1, await SendAsync("u-high-again", Importance.High), "back to the defaults");
    }

    [TestMethod]
    public async Task Out_of_range_values_are_refused_with_their_field()
    {
        foreach (var (request, field) in new (NotificationSettingsRequest Request, string Field)[]
        {
            (new() { PushoverHigh = 3 }, "pushoverHigh"),
            (new() { PushoverLow = -3 }, "pushoverLow"),
            (new() { EmergencyRetrySeconds = 10 }, "emergencyRetrySeconds"),
            (new() { EmergencyRetrySeconds = 600, EmergencyExpireSeconds = 300 }, "emergencyExpireSeconds"),
            (new() { EmergencyExpireSeconds = 20_000 }, "emergencyExpireSeconds"),
        })
        {
            var response = await admin.PutJsonAsync("/api/v1/notifications/settings", request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, field);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), field);
        }
    }

    [TestMethod]
    public async Task The_debug_test_sends_an_emergency_with_the_settings_repeat()
    {
        var settings = await (await admin.GetAsync("/api/v1/notifications/settings")).ReadAsync<NotificationSettings>();
        var result = await (await admin.PostJsonAsync("/api/v1/debug/pushover", new DebugPushoverRequest { UserKey = "u-debug-emergency", Priority = 2 }))
            .ReadAsync<DebugResult>();
        Assert.IsTrue(result.Ok, result.Detail);
        var sent = pushover.Sent.Last(m => m.GetProperty("user").GetString() == "u-debug-emergency");
        Assert.AreEqual(2, sent.GetProperty("priority").GetInt32());
        Assert.AreEqual(settings.EmergencyRetrySeconds, sent.GetProperty("retry").GetInt32());
        Assert.AreEqual(settings.EmergencyExpireSeconds, sent.GetProperty("expire").GetInt32());
    }
}
