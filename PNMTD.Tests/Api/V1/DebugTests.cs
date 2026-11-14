using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Models.Enum;
using PNMTD.Notifications;

namespace PNMTD.Tests.Api.V1;

/// <summary>Admin → Debug (docs/ai-operations-plan.md §8): test sends through the real providers, against a fake Pushover.</summary>
[TestClass]
public sealed class DebugTests
{
    private static readonly FakePushover pushover = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;

    internal static readonly Dictionary<string, string?> Settings = new()
    {
        ["Pushover:ApiKey"] = "app-token-1234",
        ["Smtp:Host"] = "",
        ["Smtp:Sender"] = "",
    };

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: Settings, services: s => pushover.Install(s));
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public async Task The_status_names_the_settings_and_the_Pushover_rules_never_a_key()
    {
        var rule = await (await admin.PostJsonAsync("/api/v1/notification-rules", new NotificationRuleRequest
        {
            Name = $"debug-status-{Guid.NewGuid():N}", Recipient = "pushover://u-status-key", Type = NotificationRuleType.ALWAYS, Enabled = true,
        })).ReadAsync<NotificationRuleDetail>();
        var status = await (await admin.GetAsync("/api/v1/debug")).ReadAsync<DebugStatus>();
        Assert.IsTrue(status.Pushover.Configured);
        Assert.AreEqual("1234", status.Pushover.ApiKeyEnd);
        Assert.IsFalse(status.Mail.Configured);
        Assert.IsTrue(status.PushoverRules.Any(r => r.Id == rule.Id));
        Assert.IsFalse(JsonSerializer.Serialize(status).Contains("u-status-key"));
    }

    [TestMethod]
    public async Task A_typed_key_is_sent_with_its_priority_and_audited_without_the_key()
    {
        var result = await (await admin.PostJsonAsync("/api/v1/debug/pushover", new DebugPushoverRequest { UserKey = "u-typed-key", Priority = 1 }))
            .ReadAsync<DebugResult>();
        Assert.IsTrue(result.Ok, result.Detail);
        Assert.AreEqual("a typed key", result.Target);
        StringAssert.StartsWith(result.Detail, "Pushover accepted it (request ");
        var sent = pushover.Sent.Last(m => m.GetProperty("user").GetString() == "u-typed-key");
        Assert.AreEqual("app-token-1234", sent.GetProperty("token").GetString());
        Assert.AreEqual(1, sent.GetProperty("priority").GetInt32());
        StringAssert.StartsWith(sent.GetProperty("title").GetString(), "PNMT test from ");

        var log = await (await admin.GetAsync($"/api/v1/audit-log?type={AuditEvents.DebugNotificationSent}")).ReadAsync<Paged<AuditLogItem>>();
        Assert.IsTrue(log.Items.Any(l => l.Detail == "pushover to a typed key: sent"));
        Assert.IsFalse(log.Items.Any(l => l.Detail?.Contains("u-typed-key") == true));
    }

    [TestMethod]
    public async Task A_rule_sends_to_its_key_and_Pushover_errors_come_back()
    {
        var rule = await (await admin.PostJsonAsync("/api/v1/notification-rules", new NotificationRuleRequest
        {
            Name = $"debug-rule-{Guid.NewGuid():N}", Recipient = "pushover://u-bad", Type = NotificationRuleType.ALWAYS, Enabled = true,
        })).ReadAsync<NotificationRuleDetail>();
        var result = await (await admin.PostJsonAsync("/api/v1/debug/pushover", new DebugPushoverRequest { RuleId = rule.Id }))
            .ReadAsync<DebugResult>();
        Assert.IsFalse(result.Ok);
        Assert.AreEqual($"rule {rule.Name}", result.Target);
        Assert.AreEqual("HTTP 400: user identifier is invalid (request req-bad).", result.Detail);
    }

    [TestMethod]
    public async Task Invalid_requests_and_an_unconfigured_mail_are_answered()
    {
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/debug/pushover", new DebugPushoverRequest())).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await admin.PostJsonAsync("/api/v1/debug/pushover", new DebugPushoverRequest { UserKey = "u", Priority = 2 })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/debug/mail", new DebugMailRequest { Recipient = "nope" })).StatusCode);
        var mail = await (await admin.PostJsonAsync("/api/v1/debug/mail", new DebugMailRequest { Recipient = "ops@example.com" })).ReadAsync<DebugResult>();
        Assert.IsFalse(mail.Ok);
        Assert.AreEqual("Smtp:Host or Smtp:Sender is not set.", mail.Detail);
    }

    [TestMethod]
    public async Task At_most_ten_sends_a_minute()
    {
        using var own = new TestApp(settings: Settings, services: s => pushover.Install(s));
        var client = own.Client(Roles.Admin);
        for (var i = 0; i < 10; i++)
        {
            Assert.AreEqual(HttpStatusCode.OK, (await client.PostJsonAsync("/api/v1/debug/pushover", new DebugPushoverRequest { UserKey = "u-rate" })).StatusCode);
        }
        var limited = await client.PostJsonAsync("/api/v1/debug/pushover", new DebugPushoverRequest { UserKey = "u-rate" });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        StringAssert.Contains(await limited.Content.ReadAsStringAsync(), "Too many test sends");
    }

    /// <summary>Pushover's API as the provider sees it: <c>u-bad</c> is refused, everyone else accepted.</summary>
    internal sealed class FakePushover
    {
        public ConcurrentQueue<JsonElement> Sent { get; } = new();

        public void Install(IServiceCollection services) =>
            services.AddHttpClient(NotificationService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(this));

        private sealed class Handler(FakePushover owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                owner.Sent.Enqueue(body);
                var bad = body.GetProperty("user").GetString() == "u-bad";
                return new HttpResponseMessage(bad ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
                {
                    Content = new StringContent(bad
                        ? """{"user":"invalid","errors":["user identifier is invalid"],"status":0,"request":"req-bad"}"""
                        : $$"""{"status":1,"request":"req-{{owner.Sent.Count}}"}""", Encoding.UTF8, "application/json"),
                };
            }
        }
    }
}
