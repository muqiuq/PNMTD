using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Boards;
using PNMTD.Cookie;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tests.Ai;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// Cookie's board messages with a fake model (docs/board-messages-plan.md §8, §15): off, no board, a board not in the list, no right;
/// suppress; a duplicate; both limits; an all-clear that ends the case's alert; Seraph's reject; Propose with its 30 minutes; and the
/// lounge's tool, through the same executor.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CookieBoardMessageTests
{
    private static readonly FakeOpenRouter router = new();
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid cookieGroup;
    private static readonly UserRights With = new(false, new Dictionary<Area, Level> { [Area.Monitoring] = Level.View, [Area.Boards] = Level.Message });
    private static readonly UserRights Without = new(false, new Dictionary<Area, Level> { [Area.Monitoring] = Level.View, [Area.Boards] = Level.View });

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>(FakeOpenRouter.Settings) { ["Cookie:Enabled"] = "true" }, services: router.Install);
        admin = app.Client(Roles.Admin);
        await using var db = app.NewDb();
        var group = new GroupEntity { Id = Guid.NewGuid(), Name = V1.Unique("Cookie's boards"), NormalizedName = V1.Unique("cookie's boards") };
        group.Rights.Add(new GroupRightEntity { GroupId = group.Id, Area = Area.Monitoring, Level = Level.View });
        group.Rights.Add(new GroupRightEntity { GroupId = group.Id, Area = Area.Boards, Level = Level.Message });
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        cookieGroup = group.Id;
        await Save("rights", new CookieRightsSettings { GroupId = cookieGroup });
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await Save("actions", new ActionSettings());
        await Save("rights", new CookieRightsSettings());
        await using (var db = app.NewDb())
        {
            await db.CookieInstructions.Where(i => i.EndedAt == null).ExecuteUpdateAsync(u => u.SetProperty(i => i.EndedAt, Clock.UtcNow));
        }
        app.Dispose();
    }

    [TestInitialize]
    public async Task Reset()
    {
        router.Answer = null;
        app.Services.GetRequiredService<BoardRuntime>().Reset();
        await using var db = app.NewDb();
        await db.BoardMessages.ExecuteDeleteAsync();
        await db.CookieDecisions.Where(d => d.Kind == "board-message").ExecuteDeleteAsync();
        await db.CookieInstructions.Where(i => i.EndedAt == null).ExecuteUpdateAsync(u => u.SetProperty(i => i.EndedAt, Clock.UtcNow));
    }

    private static async Task Save<T>(string section, T value)
    {
        using var response = await admin.PutAsync(V1.CookieSettingsPath(section),
            new StringContent(JsonSerializer.Serialize(value, CookieSettings.Json), System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static Task Levels(ActionLevel level, params Guid[] boards) => Save("actions", new ActionSettings
    {
        Levels = new ActionLevels { BoardMessage = new ActionRule(level) },
        BoardMessages = new BoardMessageSettings { BoardIds = boards, Seconds = 120 },
    });

    private static async Task<Guid> BoardAsync(bool messages = true, bool enabled = true)
    {
        var created = await (await admin.PostJsonAsync("/api/v1/boards", new { name = V1.Unique("cookie-board"), columns = 1, from = "empty" })).ReadAsync<BoardDetail>();
        if (!messages || !enabled)
        {
            using var saved = await admin.PutJsonAsync($"/api/v1/boards/{created.Id}", new
            {
                name = created.Name, description = (string?)null, enabled, networks = Array.Empty<object>(),
                layout = created.Layout with { Look = created.Layout.Look with { Messages = messages } }, revision = created.Revision,
            });
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        }
        return created.Id;
    }

    /// <summary>A case of Cookie's: a problem on a host of its own.</summary>
    private static async Task<CookieCaseEntity> CaseAsync()
    {
        await using var db = app.NewDb();
        var c = new CookieCaseEntity
        {
            Id = Guid.NewGuid(), Kind = CookieCaseKind.Problem, Key = $"test:{Guid.NewGuid():N}", Open = true, Title = "Core switch down",
            Importance = PNMTD.Lib.Models.Enum.Importance.High, OpenedAt = Clock.UtcNow, ChangedAt = Clock.UtcNow, ProgressAt = Clock.UtcNow, Areas = ["Monitoring"],
        };
        db.CookieCases.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static ProposedAction Message(Guid board, string kind = "alert", string title = "Core switch down in building B", double confidence = 0.95) =>
        new(CookieActionKind.BoardMessage, null, 0, board.ToString(), title, "Phones and Wi-Fi in building B are out. We are on it.",
            "Many people are affected now.", [], confidence, Urgency.Urgent, kind);

    private static async Task<CookieDecisionEntity> HandleAsync(ProposedAction a, CookieCaseEntity? c, UserRights? rights = null)
    {
        var p = await app.Services.GetRequiredService<PrivacyFilter>().OpenAsync(CancellationToken.None);
        return await app.Services.GetRequiredService<CookieExecutor>()
            .HandleAsync(new ActionRequest(new CookieProposal(a), c, null, rights ?? With, p, ["Monitoring"]), CancellationToken.None);
    }

    private static void Seraph(string verdict) => router.Answer = (schema, _) => schema == nameof(SeraphVerdict)
        ? $$"""{"verdict":"{{verdict}}","reason":"the evidence says so","confidence":0.95}"""
        : """{"answer":"pong"}""";

    private static async Task<List<BoardMessageEntity>> MessagesAsync()
    {
        await using var db = app.NewDb();
        return await db.BoardMessages.AsNoTracking().OrderBy(m => m.SentAt).ToListAsync();
    }

    [TestMethod]
    public async Task BothGatesTheLevelAndTheBoardDecide()
    {
        var board = await BoardAsync();
        var quiet = await BoardAsync(messages: false);
        var c = await CaseAsync();
        Seraph("approve");

        await Levels(ActionLevel.Off, board);
        Assert.AreEqual("off", (await HandleAsync(Message(board), c)).Outcome);
        await Levels(ActionLevel.Auto);
        StringAssert.Contains((await HandleAsync(Message(board), c)).Detail, "Not one of the boards");
        await Levels(ActionLevel.Auto, board, quiet);
        StringAssert.Contains((await HandleAsync(Message(board), c, Without)).Detail, "needs Boards: Message");
        StringAssert.Contains((await HandleAsync(Message(quiet), c)).Detail, "doesn't show messages");
        StringAssert.Contains((await HandleAsync(Message(board, kind: "party"), c)).Detail, "kind");
        Assert.AreEqual(0, (await MessagesAsync()).Count);

        var done = await HandleAsync(Message(board), c);
        Assert.AreEqual("done", done.Outcome, done.Detail);
        Assert.AreEqual("approve", done.Verdict);
        var sent = (await MessagesAsync()).Single();
        Assert.AreEqual((BoardMessageSource.Cookie, BoardMessageKind.Alert, c.Id, done.Id, 120),
            (sent.Source, sent.Kind, sent.CookieCaseId, sent.CookieDecisionId, sent.Seconds));
        CollectionAssert.AreEqual(new[] { board }, sent.BoardIds);
        Assert.IsNull(sent.SentById);
        await using (var db = app.NewDb())
        {
            var decision = await db.CookieDecisions.SingleAsync(d => d.Id == done.Id);
            CollectionAssert.Contains(decision.Areas, "Boards");
            var audit = await db.AuditLog.Where(l => l.EventType == AuditEvents.BoardMessageSent).OrderByDescending(l => l.CreatedAt).FirstAsync();
            Assert.AreEqual(AuditActor.Cookie, audit.Actor);
        }

        // One per case and board: the next is a duplicate.
        Assert.AreEqual("duplicate", (await HandleAsync(Message(board, title: "Still down"), c)).Outcome);
    }

    [TestMethod]
    public async Task AnAllClearEndsTheCasesAlertAndTakesItsPlace()
    {
        var board = await BoardAsync();
        var c = await CaseAsync();
        Seraph("approve");
        await Levels(ActionLevel.Auto, board);
        Assert.AreEqual("done", (await HandleAsync(Message(board), c)).Outcome);
        var clear = await HandleAsync(Message(board, kind: "success", title: "Building B is back"), c);
        Assert.AreEqual("done", clear.Outcome, clear.Detail);
        var messages = await MessagesAsync();
        Assert.AreEqual(2, messages.Count);
        Assert.IsNotNull(messages[0].WithdrawnAt, "the alert ended");
        Assert.AreEqual("Cookie", messages[0].WithdrawnByName);
        Assert.AreEqual((BoardMessageKind.Success, (DateTime?)null), (messages[1].Kind, messages[1].WithdrawnAt));
    }

    [TestMethod]
    public async Task SuppressSeraphAndTheLimitsHoldItBack()
    {
        var board = await BoardAsync();
        await Levels(ActionLevel.Auto, board);

        Seraph("reject");
        Assert.AreEqual("rejected", (await HandleAsync(Message(board), await CaseAsync())).Outcome);

        Seraph("approve");
        var instructions = app.Services.GetRequiredService<Instructions>();
        await instructions.CreateAsync(new InstructionDraft("suppress", "channel", "board-message", "Board messages", "Not today", Clock.UtcNow.AddDays(1)),
            confirmed: true, "admin", TestUsers.Admin, "Ada Admin", CancellationToken.None);
        Assert.AreEqual("muted", (await HandleAsync(Message(board), await CaseAsync())).Outcome);
        await using (var db = app.NewDb())
        {
            await db.CookieInstructions.Where(i => i.EndedAt == null).ExecuteUpdateAsync(u => u.SetProperty(i => i.EndedAt, Clock.UtcNow));
        }

        // Two an hour on one board by default: the third, from another case, is held back.
        Assert.AreEqual("done", (await HandleAsync(Message(board), await CaseAsync())).Outcome);
        Assert.AreEqual("done", (await HandleAsync(Message(board), await CaseAsync())).Outcome);
        var third = await HandleAsync(Message(board), await CaseAsync());
        Assert.AreEqual("held-back", third.Outcome);
        StringAssert.Contains(third.Detail, "an hour on one board");

        // And 10 a day across boards; here 2.
        await Save("actions", new ActionSettings
        {
            Levels = new ActionLevels { BoardMessage = new ActionRule(ActionLevel.Auto) },
            BoardMessages = new BoardMessageSettings { BoardIds = [board, await BoardAsync()], Seconds = 60 },
            Limits = new OutputLimits(BoardMessagesPerDay: 2),
        });
        var other = (await app.Services.GetRequiredService<CookieSettings>().GetAsync<ActionSettings>(CancellationToken.None)).BoardMessages.BoardIds[1];
        var day = await HandleAsync(Message(other), await CaseAsync());
        Assert.AreEqual("held-back", day.Outcome);
        StringAssert.Contains(day.Detail, "a day");
    }

    [TestMethod]
    public async Task AProposalWaitsAtMostThirtyMinutes()
    {
        var board = await BoardAsync();
        Seraph("approve");
        await Levels(ActionLevel.Propose, board);
        var proposed = await HandleAsync(Message(board), await CaseAsync());
        Assert.AreEqual("proposed", proposed.Outcome);
        await using (var db = app.NewDb())
        {
            var decision = await db.Decisions.SingleAsync(d => d.Id == proposed.InboxDecisionId);
            Assert.IsTrue(decision.AdminOnly, "Boards isn't an inbox area");
            Assert.IsTrue(decision.ExpiresAt <= Clock.UtcNow.AddMinutes(31) && decision.ExpiresAt >= Clock.UtcNow.AddMinutes(29), decision.ExpiresAt?.ToString("u"));
        }
        var approved = await app.Services.GetRequiredService<CookieExecutor>().RunApprovedAsync(proposed.Id, "Ada Admin", CancellationToken.None);
        Assert.AreEqual("done", approved.Outcome, approved.Detail);
        Assert.AreEqual("Ada Admin", (await MessagesAsync()).Single().ApprovedByName);

        var late = await HandleAsync(Message(board, kind: "warning", title: "Mail delays"), await CaseAsync());
        Assert.AreEqual("proposed", late.Outcome);
        await using (var db = app.NewDb())
        {
            await db.CookieDecisions.Where(d => d.Id == late.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.At, Clock.UtcNow.AddMinutes(-31)));
        }
        var expired = await app.Services.GetRequiredService<CookieExecutor>().RunApprovedAsync(late.Id, "Ada Admin", CancellationToken.None);
        Assert.AreEqual("expired", expired.Outcome);
        Assert.AreEqual(1, (await MessagesAsync()).Count);
    }

    [TestMethod]
    public async Task TheLoungeAsksThroughTheSameExecutor()
    {
        var board = await BoardAsync();
        Seraph("approve");
        await Levels(ActionLevel.Auto, board);
        var p = await app.Services.GetRequiredService<PrivacyFilter>().OpenAsync(CancellationToken.None);
        var executor = app.Services.GetRequiredService<CookieExecutor>();
        LoungeBoardMessageRequest Asked(string kind = "maintenance") =>
            new(PNMTD.Cookie.Lounge.LoungeVoices.Cookie, "Anna Muster", board, kind, "Patching tonight", "The VPN restarts at 22:00.", ["Monitoring"], p);

        var done = await executor.HandleLoungeBoardMessageAsync(Asked(), CancellationToken.None);
        Assert.AreEqual("done", done.Outcome, done.Detail);
        StringAssert.Contains(done.Detail, "Shown on");
        var sent = (await MessagesAsync()).Single();
        Assert.AreEqual((BoardMessageKind.Maintenance, (Guid?)null), (sent.Kind, sent.CookieCaseId));

        await Levels(ActionLevel.Off, board);
        Assert.AreEqual("off", (await executor.HandleLoungeBoardMessageAsync(Asked(), CancellationToken.None)).Outcome);
        await Levels(ActionLevel.Auto);
        Assert.AreEqual("refused", (await executor.HandleLoungeBoardMessageAsync(Asked(), CancellationToken.None)).Outcome, "not Cookie's board");

        // The tool is offered only with a board and the asker's right; its definition is there then.
        var turn = new PNMTD.Cookie.Lounge.LoungeTurn
        {
            Voice = PNMTD.Cookie.Lounge.LoungeVoices.Cookie, SceneId = 1, Rights = UserRights.None, Privacy = p, People = new Dictionary<string, Guid>(),
        };
        Assert.IsFalse(PNMTD.Cookie.Lounge.LoungeTools.Definitions(turn).ToJsonString().Contains("send_board_message"));
        turn.MayBoard = true;
        Assert.IsTrue(PNMTD.Cookie.Lounge.LoungeTools.Definitions(turn).ToJsonString().Contains("send_board_message"));
    }
}
