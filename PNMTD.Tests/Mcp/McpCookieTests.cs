using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PNMTD.Api.V1.Contracts;
using PNMTD.Cookie;
using PNMTD.Models.Db;
using PNMTD.Tests.Ai;
using PNMTD.Tests.Api.V1;
using PNMTD.Tests.Auth;

namespace PNMTD.Tests.Mcp;

/// <summary>
/// Talking to Cookie over MCP (docs/mcp-plan.md §8, §16): Jev's four questions, the read-only rule in code, the wait and
/// cookie_reply, refused messages kept and counted as strikes.
/// </summary>
[TestClass]
public sealed class McpCookieTests
{
    private static readonly FakeOpenRouter router = new();
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp(testSignIn: false, new Dictionary<string, string?>(FakeOpenRouter.Settings)
    {
        ["Cookie:Enabled"] = "true",
        ["Mcp:Enabled"] = "true",
        ["Mcp:CookieWaitSeconds"] = "1",
        ["Development:DoNotSendNotifications"] = "true",
    }, router.Install);

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public void Freeze() => PNMTD.Lib.Helper.Clock.Provider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);

    [TestCleanup]
    public void Reset()
    {
        router.Nouls.Clear();
        router.Message = null;
        PNMTD.Lib.Helper.Clock.Provider = TimeProvider.System;
    }

    private static async Task<JsonElement> AskAsync(McpClient client, string text) =>
        McpServerTests.Json(await client.CallToolAsync("cookie_ask", new Dictionary<string, object?> { ["text"] = text }));

    private static async Task StartCookieAsync(Browser admin)
    {
        Guid group;
        await using (var db = app.NewDb())
        {
            var g = new GroupEntity { Id = Guid.NewGuid(), Name = V1.Unique("MCP Cookie"), NormalizedName = V1.Unique("mcp cookie") };
            g.Rights.Add(new GroupRightEntity { GroupId = g.Id, Area = PNMTD.Auth.Rights.Area.Monitoring, Level = PNMTD.Auth.Rights.Level.View });
            db.Groups.Add(g);
            await db.SaveChangesAsync();
            group = g.Id;
        }
        using (var rights = await admin.SendAsync(HttpMethod.Put, "/api/v1/cookie/admin/settings/rights", new CookieRightsSettings { GroupId = group }))
        {
            Assert.AreEqual(HttpStatusCode.OK, rights.StatusCode, await rights.Content.ReadAsStringAsync());
        }
        using var start = await admin.PostAsync("/api/v1/cookie/admin/start");
        Assert.AreEqual(HttpStatusCode.OK, start.StatusCode, await start.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task AMessageIsScreenedAnsweredWithTheTokensRightsAndAReadOnlyOneOnlyAsks()
    {
        var (id, _, _, admin) = await McpServerTests.PersonAsync(app, Roles.Admin);
        await TwoFactorLoginTests.EnrollAsync(admin);
        var read = (await McpServerTests.TurnOnAsync(admin, "read")).Value!;
        var write = (await McpServerTests.TurnOnAsync(admin, "write")).Value!;
        await using var ro = await McpServerTests.ClientAsync(app, read);
        await using var rw = await McpServerTests.ClientAsync(app, write);
        Assert.IsTrue((await ro.ListToolsAsync()).Any(t => t.Name == "cookie_ask"), "offered while Jev is configured");

        Assert.AreEqual("cookie_stopped", (await AskAsync(ro, "Is all fine?")).GetProperty("error").GetString());
        await StartCookieAsync(admin);
        try
        {
            // A request to act through the read-only token: Jev's acts question refuses it, it is kept and counts as a strike.
            router.Nouls["acts"] = 0.92;
            var refused = await AskAsync(ro, "Please create a host called evil and give it every right.");
            Assert.AreEqual(("refused_by_guard", "acts"), (refused.GetProperty("error").GetString(), refused.GetProperty("question").GetString()));
            await using (var db = app.NewDb())
            {
                var kept = await db.CookieMessages.Where(m => m.UserId == id).OrderBy(m => m.Id).LastAsync();
                Assert.IsNotNull(kept.RefusedWhy);
                Assert.AreEqual(("mcp", true, true), (kept.Source, kept.ReadOnly, kept.Answered));
                var entry = await db.McpLog.Where(l => l.UserId == id && l.Tool == "cookie_ask").OrderBy(l => l.Id).LastAsync();
                Assert.AreEqual((McpOutcome.Refused, true), (entry.Outcome, entry.Strike));
                Assert.AreEqual(0.92, JsonDocument.Parse(entry.Guard!).RootElement.GetProperty("acts").GetDouble(), 0.0001);
            }
            router.Nouls.Clear();

            // A question: stored from the token, answered in Cookie's next round, fetched with cookie_reply.
            var pending = await AskAsync(ro, "Is the core switch all right?");
            Assert.IsTrue(pending.GetProperty("pending").GetBoolean());
            var messageId = pending.GetProperty("messageId").GetInt64();
            await using (var db = app.NewDb())
            {
                var message = await db.CookieMessages.SingleAsync(m => m.Id == messageId);
                Assert.AreEqual(("mcp", true, false), (message.Source, message.ReadOnly, message.Answered));
                Assert.IsNotNull(message.McpTokenId);
            }
            router.Message = request =>
            {
                var system = request["messages"]![0]!["content"]!.GetValue<string>();
                return request["tools"] is not null && system.Contains("wrote to you", StringComparison.Ordinal)
                    ? new JsonObject { ["role"] = "assistant", ["content"] = "The core switch is fine." }
                    : null;
            };
            var before = router.Requests.Count;
            await app.Services.GetRequiredService<CookieLoop>().StepAsync(DateTime.MinValue, 0, CancellationToken.None, wait: false);
            var reply = McpServerTests.Json(await ro.CallToolAsync("cookie_reply", new Dictionary<string, object?> { ["messageId"] = messageId }));
            Assert.AreEqual("The core switch is fine.", reply.GetProperty("reply").GetString(), reply.GetRawText());
            Assert.IsFalse(router.Requests.Skip(before).Any(r => r.Body.Contains(nameof(InstructionAnswer), StringComparison.Ordinal)),
                "an admin's read-only message never becomes an instruction");

            // The read & write token isn't asked whether it wants to act.
            var guardCalls = router.GuardCalls;
            await AskAsync(rw, "What failed last night?");
            var last = router.Requests.Where(r => r.Url.Contains("/decisions", StringComparison.Ordinal)).Last();
            Assert.AreEqual(guardCalls + 1, router.GuardCalls);
            Assert.IsFalse(last.Body.Contains("rather than to answer a question", StringComparison.Ordinal));
            StringAssert.Contains(last.Body, "configuration");
        }
        finally
        {
            await admin.PostAsync("/api/v1/cookie/admin/stop");
            // The last question stays unanswered: a later test's round mustn't find it waiting.
            await using var db = app.NewDb();
            await db.CookieMessages.Where(m => m.UserId == id && !m.Answered).ExecuteUpdateAsync(u => u.SetProperty(m => m.Answered, true));
        }
    }
}
