using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Mcp;
using PNMTD.Models.Db;
using PNMTD.Tests.Auth;
using PNMTD.Tools;

namespace PNMTD.Tests.Mcp;

/// <summary>
/// The MCP server end to end (docs/mcp-plan.md §4, §5, §16): the SDK's own client over HTTP against the test server, tokens
/// switched on through the account's API as the dashboard does.
/// </summary>
[TestClass]
public sealed class McpServerTests
{
    private static TestApp app = null!;
    private static FakeTimeProvider time = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp(testSignIn: false, new Dictionary<string, string?>
    {
        ["Mcp:Enabled"] = "true",
        ["Development:DoNotSendNotifications"] = "true",
    });

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public void Freeze()
    {
        time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Clock.Provider = time;
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    /// <summary>A signed-in user of <paramref name="role"/> and their browser.</summary>
    internal static async Task<(Guid Id, string Name, string Password, Browser Browser)> PersonAsync(TestApp on, string role = Roles.User)
    {
        var (id, name, password) = await TwoFactorLoginTests.NewUserAsync(on, role);
        var browser = new Browser(on);
        Assert.AreEqual("done", (await browser.LoginAsync(name, password)).Next);
        return (id, name, password, browser);
    }

    internal static async Task<McpTokenIssued> TurnOnAsync(Browser browser, string kind)
    {
        using var response = await browser.PostAsync($"/api/v1/account/mcp/{kind}/on");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
        return await response.ReadAsync<McpTokenIssued>();
    }

    internal static async Task<McpClient> ClientAsync(TestApp on, string token)
    {
        var http = on.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, McpSetup.Path),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }, http, NullLoggerFactory.Instance, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    internal static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text).RootElement.Clone();

    private static async Task<HttpResponseMessage> RawAsync(string? token, string? origin = null)
    {
        var http = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, McpSetup.Path)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return await http.SendAsync(request);
    }

    [TestMethod]
    public async Task EveryUserHasTwoTokensWithoutAValueUntilTheyTurnOneOn()
    {
        var (id, _, _, browser) = await PersonAsync(app);
        var account = await (await browser.GetAsync("/api/v1/account/mcp")).ReadAsync<McpAccountItem>();
        Assert.IsTrue(account.Enabled);
        Assert.AreEqual((false, "off", false), (account.Read.On, account.Read.State, account.Read.HasValue));
        Assert.AreEqual((false, false), (account.Write.On, account.Write.HasValue));
        StringAssert.EndsWith(account.Url, "/mcp");

        var issued = await TurnOnAsync(browser, "read");
        StringAssert.StartsWith(issued.Value, McpTokenValues.ReadPrefix);
        Assert.AreEqual(51, issued.Value!.Length);
        Assert.IsTrue(issued.Token.On);
        Assert.AreEqual(issued.Value[^4..], issued.Token.Hint);
        Assert.IsNull((await TurnOnAsync(browser, "read")).Value, "shown once: switching it on again shows nothing");

        await using var db = app.NewDb();
        var rows = await db.McpTokens.Where(t => t.UserId == id).ToListAsync();
        Assert.AreEqual(2, rows.Count);
        var read = rows.Single(t => t.Kind == McpTokenKind.Read);
        Assert.AreEqual(McpTokenValues.Hash(issued.Value), read.Hash, "only the hash is kept");
        Assert.IsFalse(await db.McpTokens.AnyAsync(t => t.Hash == issued.Value));
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == "McpTokenOn" && l.UserId == id && l.Detail!.Contains("a new value was made")));
    }

    [TestMethod]
    public async Task TheReadOnlyTokenListsOnlyWhatReadsAndAnswersAsItsUser()
    {
        var (_, name, _, browser) = await PersonAsync(app);
        var token = (await TurnOnAsync(browser, "read")).Value!;
        await using var client = await ClientAsync(app, token);

        var tools = (await client.ListToolsAsync()).Select(t => t.ProtocolTool).ToList();
        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.IsTrue(names.IsSupersetOf(["whoami", "hosts_search", "host_get", "sensors_list", "search", "kb_search"]), string.Join(", ", names));
        Assert.IsTrue(tools.All(t => ToolCatalog.Find(t.Name)!.Kind is ToolKind.Read or ToolKind.Ask), "nothing that writes");
        Assert.IsFalse(names.Contains("cookie_ask"), "without Jev configured, no talking to Cookie");
        Assert.IsTrue(tools.Single(t => t.Name == "hosts_search").Annotations!.ReadOnlyHint == true);
        StringAssert.Contains(client.ServerInstructions, "<untrusted");

        var whoami = Json(await client.CallToolAsync("whoami", new Dictionary<string, object?>()));
        Assert.AreEqual("read-only", whoami.GetProperty("token").GetString());
        Assert.AreEqual(name, whoami.GetProperty("username").GetString());
        Assert.IsFalse(whoami.GetProperty("rights").EnumerateArray().Any(r => r.GetString()!.EndsWith(": Edit", StringComparison.Ordinal)), "capped at reading");

        var hosts = await client.CallToolAsync("hosts_search", new Dictionary<string, object?> { ["query"] = "nothing-like-this" });
        Assert.IsFalse(hosts.IsError ?? false, ((TextContentBlock)hosts.Content[0]).Text);

        await using var db = app.NewDb();
        var entries = await db.McpLog.Where(l => l.Username == name).OrderBy(l => l.Id).ToListAsync();
        CollectionAssert.IsSubsetOf(new[] { "tools/list", "tools/call" }, entries.Select(e => e.Method).Distinct().ToList());
        var call = entries.Last(e => e.Tool == "hosts_search");
        Assert.AreEqual(McpOutcome.Ok, call.Outcome);
        StringAssert.Contains(call.Arguments, "nothing-like-this");
        Assert.AreEqual("[]", McpLog.Unpack(call.Result));
    }

    [TestMethod]
    public async Task ATokenNeverReachesTheApiAndACookieNeverReachesMcp()
    {
        var (_, _, _, browser) = await PersonAsync(app);
        var token = (await TurnOnAsync(browser, "read")).Value!;

        var api = app.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/hosts")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/auth/me")).StatusCode);

        using var withCookie = await browser.SendAsync(HttpMethod.Post, McpSetup.Path, new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.AreEqual(HttpStatusCode.Unauthorized, withCookie.StatusCode, "the session cookie is no token");
    }

    [TestMethod]
    public async Task RefusedTokensSayWhatToDo()
    {
        using (var none = await RawAsync(null))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, none.StatusCode);
        }
        using (var unknown = await RawAsync(McpTokenValues.New(McpTokenKind.Read)))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, unknown.StatusCode);
            StringAssert.Contains(unknown.Headers.WwwAuthenticate.ToString(), "invalid_token");
        }

        var (_, _, _, browser) = await PersonAsync(app);
        var token = (await TurnOnAsync(browser, "read")).Value!;
        using (var off = await browser.PostAsync("/api/v1/account/mcp/read/off"))
        {
            Assert.AreEqual(HttpStatusCode.OK, off.StatusCode);
        }
        using var refused = await RawAsync(token);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, "a known token that is only off");
        var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual("off", body.GetProperty("error").GetString());
        StringAssert.Contains(body.GetProperty("message").GetString(), "dashboard");
    }

    [TestMethod]
    public async Task ARequestFromAnotherSiteIsRefused()
    {
        var (_, _, _, browser) = await PersonAsync(app);
        var token = (await TurnOnAsync(browser, "read")).Value!;
        using var response = await RawAsync(token, "https://evil.example");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task RegeneratingStopsTheOldValueAtOnce()
    {
        var (_, _, _, browser) = await PersonAsync(app);
        var first = (await TurnOnAsync(browser, "read")).Value!;
        using var regenerate = await browser.PostAsync("/api/v1/account/mcp/read/regenerate");
        var second = (await regenerate.ReadAsync<McpTokenIssued>()).Value!;
        Assert.AreNotEqual(first, second);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RawAsync(first)).StatusCode);
        await using var client = await ClientAsync(app, second);
        Assert.IsNotEmpty(await client.ListToolsAsync(), "the new one is on, as the old one was");
    }

    [TestMethod]
    public async Task TheReadWriteTokenNeedsTwoFactorAndLastsTwelveHours()
    {
        var (_, _, _, browser) = await PersonAsync(app);
        using (var refused = await browser.PostAsync("/api/v1/account/mcp/write/on"))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.AreEqual("totp_required", (await refused.ProblemAsync()).GetProperty("code").GetString());
        }
        await TwoFactorLoginTests.EnrollAsync(browser);
        var token = (await TurnOnAsync(browser, "write")).Value!;
        StringAssert.StartsWith(token, McpTokenValues.WritePrefix);

        time.Advance(TimeSpan.FromHours(11));
        await using (var client = await ClientAsync(app, token))
        {
            var whoami = Json(await client.CallToolAsync("whoami", new Dictionary<string, object?>()));
            Assert.AreEqual("read & write", whoami.GetProperty("token").GetString());
        }
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        using var expired = await RawAsync(token);
        Assert.AreEqual(HttpStatusCode.Forbidden, expired.StatusCode);
        Assert.AreEqual("expired", JsonDocument.Parse(await expired.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());

        var ended = await app.Services.GetRequiredService<McpTokenExpiryTask>().RunAsync(CancellationToken.None);
        Assert.IsGreaterThanOrEqualTo(1, ended);
        var account = await (await browser.GetAsync("/api/v1/account/mcp")).ReadAsync<McpAccountItem>();
        Assert.AreEqual((false, McpTokenEnd.Expired), (account.Write.On, account.Write.EndedWhy));
    }

    [TestMethod]
    public async Task TheReadOnlyTokenLastsAWeekAfterItsUserWasOnline()
    {
        var (id, _, _, browser) = await PersonAsync(app);
        var token = (await TurnOnAsync(browser, "read")).Value!;
        time.Advance(TimeSpan.FromDays(5));
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/hosts")).StatusCode, "online again");
        time.Advance(TimeSpan.FromDays(6));
        await using (var client = await ClientAsync(app, token))
        {
            Assert.IsNotEmpty(await client.ListToolsAsync(), "11 days after switching it on, 6 after being online");
            await client.CallToolAsync("whoami", new Dictionary<string, object?>());
        }
        time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RawAsync(token)).StatusCode, "MCP calls don't count as being online");
        await using var db = app.NewDb();
        Assert.IsTrue(await db.Users.AnyAsync(u => u.Id == id && u.LastSeenAt < Clock.UtcNow.AddDays(-7)));
    }

    [TestMethod]
    public async Task ChangingThePasswordTurnsTheTokensOff()
    {
        var (_, _, password, browser) = await PersonAsync(app);
        var token = (await TurnOnAsync(browser, "read")).Value!;
        using (var change = await browser.PostAsync("/api/v1/account/password", new { currentPassword = password, newPassword = $"new-{Guid.NewGuid():N}" }))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, change.StatusCode);
        }
        using var refused = await RawAsync(token);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.AreEqual("stamp", JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task ThreeStrikesBlockTheTokenAndAlertEveryAdmin()
    {
        var (adminId, _, _, _) = await PersonAsync(app, Roles.Admin);
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == adminId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Email, "admin@example.com"));
        }
        var (id, _, _, browser) = await PersonAsync(app);
        var token = (await TurnOnAsync(browser, "read")).Value!;
        await using (var client = await ClientAsync(app, token))
        {
            for (var i = 0; i < 2; i++)
            {
                var refused = await client.CallToolAsync("drop_all_hosts", new Dictionary<string, object?>());
                Assert.IsTrue(refused.IsError);
                Assert.AreEqual("not_available", Json(refused).GetProperty("error").GetString());
            }
            var normal = await client.CallToolAsync("host_get", new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString() });
            Assert.AreEqual("not_found", Json(normal).GetProperty("error").GetString(), "an ordinary error is no strike");
            await client.CallToolAsync("drop_all_hosts", new Dictionary<string, object?>());
        }
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RawAsync(token)).StatusCode, "blocked");
        var account = await (await browser.GetAsync("/api/v1/account/mcp")).ReadAsync<McpAccountItem>();
        Assert.AreEqual(("blocked", McpTokenEnd.Strikes), (account.Read.State, account.Read.EndedWhy));

        await using (var db = app.NewDb())
        {
            var blocked = await db.AuditLog.SingleAsync(l => l.EventType == "McpTokenBlocked" && l.Subjects.Contains(id));
            StringAssert.Contains(blocked.Detail, "Blocked after 3 refused requests");
            StringAssert.Contains(blocked.Detail, "e-mail");
            StringAssert.Contains(blocked.Detail, "(simulated)");
            Assert.AreEqual(3, await db.McpLog.CountAsync(l => l.UserId == id && l.Strike));
        }
        var again = await TurnOnAsync(browser, "read");
        Assert.AreEqual((true, 0), (again.Token.On, again.Token.Strikes), "switching it on clears the strikes");
    }

    [TestMethod]
    public async Task TheLogShowsWhatHappenedAndAnAdminCanTurnATokenOff()
    {
        var (id, _, _, browser) = await PersonAsync(app);
        var token = (await TurnOnAsync(browser, "read")).Value!;
        await using (var client = await ClientAsync(app, token))
        {
            await client.CallToolAsync("hosts_search", new Dictionary<string, object?> { ["query"] = "mcp-log-check" });
        }
        var mine = await (await browser.GetAsync("/api/v1/account/mcp/log?pageSize=10")).ReadAsync<Paged<McpLogItem>>();
        Assert.IsTrue(mine.Items.All(i => i.UserId == id));
        var call = mine.Items.First(i => i.Tool == "hosts_search");
        var detail = await (await browser.GetAsync($"/api/v1/mcp/log/{call.Id}")).ReadAsync<McpLogDetail>();
        StringAssert.Contains(detail.Arguments, "mcp-log-check");
        Assert.AreEqual("[]", detail.Result);
        Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, (await browser.GetAsync("/api/v1/mcp/log")).StatusCode, "everyone's log needs MCP: View");

        var (_, _, _, admin) = await PersonAsync(app, Roles.Admin);
        var all = await (await admin.GetAsync("/api/v1/mcp/tokens")).ReadAsync<List<McpAdminTokenItem>>();
        var row = all.Single(t => t.UserId == id && t.Token.Kind == McpTokenKind.Read);
        Assert.IsTrue(row.Token.On);
        using (var off = await admin.PostAsync($"/api/v1/mcp/tokens/{row.Id}/off", new { reason = "Left the team" }))
        {
            Assert.AreEqual(System.Net.HttpStatusCode.OK, off.StatusCode, await off.Content.ReadAsStringAsync());
        }
        Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, (await RawAsync(token)).StatusCode);
        var account = await (await browser.GetAsync("/api/v1/account/mcp")).ReadAsync<McpAccountItem>();
        Assert.AreEqual((McpTokenEnd.Admin, "Left the team"), (account.Read.EndedWhy, account.Read.EndedNote));

        var catalog = await (await browser.GetAsync("/api/v1/mcp/catalog")).ReadAsync<McpCatalog>();
        Assert.IsTrue(catalog.Tools.Any(t => t.Name == "sensor_delete" && !t.ReadOnlyToken && t.Destructive));
        Assert.IsTrue(catalog.Ceiling.Any(c => c.Area == PNMTD.Auth.Rights.Area.Mikrotik && c.ReadWrite is null));
    }

    [TestMethod]
    public async Task TheMcpEndpointDoesntExistWhileItIsOff()
    {
        using var off = new TestApp(testSignIn: false);
        using var response = await off.CreateClient().PostAsync(McpSetup.Path, new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
