using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Ai;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tests.Ai;

namespace PNMTD.Tests.Knowledge;

/// <summary>
/// AI tags and sensitive pages (docs/locations-knowledge-plan.md §7, §12), against a fake OpenRouter: the endpoint's
/// refusals, the scan's checks in code, one attempt, the guard's two questions, the sensitive mark, and when the AI
/// question follows a save.
/// </summary>
[TestClass]
public sealed class KbAiTests
{
    private static readonly FakeOpenRouter router = new();
    private static readonly ScriptedRandom random = new();
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient admin = null!;

    /// <summary>A seeded random the tests can override: 0 always asks, 99 never.</summary>
    private sealed class ScriptedRandom() : Random(10)
    {
        public int? Fixed { get; set; }

        public override int Next(int maxValue) => Fixed ?? base.Next(maxValue);
    }

    private static Func<string, JsonNode, string>? answer;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: FakeOpenRouter.Settings, services: s =>
        {
            router.Install(s);
            s.AddSingleton(new KbAiDraw(random));
        });
        user = app.Client(Roles.User);
        admin = app.Client(Roles.Admin);
        router.Answer = (schema, request) => answer?.Invoke(schema, request) ?? """{"tags":[]}""";
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public void Reset()
    {
        random.Fixed = null;
        router.Injection = 0.01;
        router.Nouls.Clear();
        answer = null;
    }

    private static readonly KbStatements Both = new() { NoCredentials = true, NoPersonalData = true };

    private static async Task<KbSaveResult> CreateAsync(KbPageRequest request, HttpClient? client = null) =>
        await (await (client ?? user).PostJsonAsync("/api/v1/kb/pages", request)).ReadAsync<KbSaveResult>();

    private static async Task<KbSaveResult> SaveAsync(KbPageDetail page, Action<KbPageRequest> change, HttpClient? client = null)
    {
        var request = KbPageTests.From(page);
        change(request);
        return await (await (client ?? user).PutJsonAsync($"/api/v1/kb/pages/{page.Id}", request)).ReadAsync<KbSaveResult>();
    }

    private static Task<HttpResponseMessage> ScanAsync(KbPageDetail page, KbStatements? statements = null, int? revision = null) =>
        user.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/ai-tags", new KbAiTagsRequest { Revision = revision ?? page.Revision, Statements = statements ?? Both });

    private static async Task<KbAiTagsStatus> RunAsync(KbPageDetail page)
    {
        var response = await ScanAsync(page);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        var started = await response.ReadAsync<KbAiTagsStatus>();
        await app.Services.GetRequiredService<AiWorker>().DrainAsync(CancellationToken.None);
        return await (await user.GetAsync($"/api/v1/kb/pages/{page.Id}/ai-tags/{started.WorkId}")).ReadAsync<KbAiTagsStatus>();
    }

    [TestMethod]
    public async Task The_scan_is_refused_for_a_sensitive_page_a_missing_statement_an_old_revision_and_a_credential()
    {
        var page = (await CreateAsync(KbPageTests.Request("Refusals", "Plain text."))).Page;
        var half = await ScanAsync(page, new KbStatements { NoCredentials = true });
        Assert.AreEqual(HttpStatusCode.BadRequest, half.StatusCode);
        var old = await ScanAsync(page, revision: page.Revision + 5);
        Assert.AreEqual("edit_conflict", (await old.ProblemAsync()).GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await app.Client(Roles.Viewer).PostJsonAsync($"/api/v1/kb/pages/{page.Id}/ai-tags", new KbAiTagsRequest { Revision = 1, Statements = Both })).StatusCode);

        var request = KbPageTests.Request("Router", "Line one\nadmin password: Gr4n1te!Wolf\nLine three");
        request.AcknowledgeCredentials = true;
        var secret = (await CreateAsync(request)).Page;
        var credential = await ScanAsync(secret);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, credential.StatusCode);
        var problem = await credential.ProblemAsync();
        Assert.AreEqual("looks_like_credential", problem.GetProperty("code").GetString());
        Assert.AreEqual(2, problem.GetProperty("lines")[0].GetInt32());
        Assert.IsFalse(problem.GetRawText().Contains("Gr4n1te"), "line numbers only");

        var sensitive = (await CreateAsync(new KbPageRequest { Title = "Sensitive one", Content = "x", MarkSensitive = true })).Page;
        Assert.IsTrue(sensitive.Sensitive);
        var refused = await ScanAsync(sensitive);
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.AreEqual("sensitive", (await refused.ProblemAsync()).GetProperty("code").GetString());
        Assert.AreEqual(0, router.ChatCalls);
    }

    [TestMethod]
    public async Task A_scan_adds_at_most_ten_checked_tags_as_ai_tags_in_one_revision()
    {
        var vocabulary = $"vocab{Guid.NewGuid():N}"[..14];
        await CreateAsync(KbPageTests.Request("Vocabulary page", "", vocabulary));
        var page = (await CreateAsync(KbPageTests.Request("UPS maintenance", "Call Anna at +41 44 123 45 67 or anna@example.com.\nThe UPS in cage B.", "ups")))
            .Page;
        JsonNode? sent = null;
        answer = (schema, request) =>
        {
            Assert.AreEqual(nameof(KbTagSuggestion), schema);
            sent = JsonNode.Parse(request["messages"]![1]!["content"]!.GetValue<string>())!["data"];
            return new JsonObject
            {
                ["tags"] = new JsonArray("<ip:1>", "10.0.0.1", "anna@example.com", "https://example.com", "2026", "UPS", "Battery", "battery",
                    "x", new string('a', 41), "Cage B", "power", "maintenance", "zrh", "dc", "rack", "floor", "tools", "extra", vocabulary),
            }.ToJsonString();
        };
        var status = await RunAsync(page);
        Assert.AreEqual(AiWorkState.Succeeded, status.State, status.Message);
        // Normalised, without the placeholder, the addresses, the number, the page's own tag, the duplicate and the lengths.
        var candidates = new[] { vocabulary, "battery", "cage-b", "power", "maintenance", "zrh", "dc", "rack", "floor", "tools", "extra" };
        Assert.HasCount(10, status.Added);
        Assert.IsTrue(status.Added.All(candidates.Contains), string.Join(", ", status.Added));
        // What left PNMT: scrubbed, with the vocabulary and the room left.
        var text = sent!["untrustedText"]!.GetValue<string>();
        Assert.IsFalse(text.Contains("123 45 67") || text.Contains("anna@example.com"), text);
        var sentVocabulary = sent["vocabulary"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
        Assert.Contains(vocabulary, sentVocabulary);
        Assert.AreEqual(10, sent["maxTags"]!.GetValue<int>());
        // The vocabulary's first: none of the others before the last of those.
        var known = status.Added.Select(sentVocabulary.Contains).ToList();
        Assert.IsTrue(known.SkipWhile(k => k).All(k => !k), string.Join(", ", status.Added));
        Assert.Contains(vocabulary, status.Added);

        var read = await (await user.GetAsync($"/api/v1/kb/pages/{page.Number}")).ReadAsync<KbPageDetail>();
        Assert.AreEqual(status.Revision, read.Revision);
        Assert.HasCount(10, read.Tags.Where(t => t.Source == KbTagSource.Ai));
        Assert.AreEqual(KbTagSource.Manual, read.Tags.Single(t => t.Name == "ups").Source);
        var revision = (await (await user.GetAsync($"/api/v1/kb/pages/{page.Id}/revisions")).ReadAsync<List<KbRevisionItem>>())[0];
        StringAssert.EndsWith(revision.Changes.Single(c => c.Kind == KbChangeKinds.AiTags).Text, "(statements by test-user)");
        Assert.AreEqual("test-user", revision.ByName);
        CollectionAssert.AreEqual(new[] { KbStatementNames.NoCredentials, KbStatementNames.NoPersonalData }, revision.Declaration!.Statements.ToArray());
        Assert.AreEqual(page.Revision, revision.Declaration.Revision);
        // The guard was asked the two questions as well.
        var guard = router.Requests.Last(r => r.Url.Contains("/decisions", StringComparison.Ordinal)).Body;
        StringAssert.Contains(guard, KbTagsHandler.CredentialsQuestion);
        StringAssert.Contains(guard, KbTagsHandler.PersonalDataQuestion);
    }

    [TestMethod]
    public async Task A_scan_runs_once_and_a_failure_is_the_end()
    {
        var page = (await CreateAsync(KbPageTests.Request("Fails once", "Text."))).Page;
        router.Failures.Enqueue(503);
        var status = await RunAsync(page);
        Assert.AreEqual(AiWorkState.Failed, status.State);
        await using var db = app.NewDb();
        Assert.AreEqual(1, (await db.AiWork.SingleAsync(w => w.Id == status.WorkId)).Attempts);
    }

    [TestMethod]
    public async Task The_guards_questions_stop_the_scan_before_the_main_model()
    {
        var page = (await CreateAsync(KbPageTests.Request("Guarded", "Text about people."))).Page;
        var chats = router.ChatCalls;
        router.Nouls["personalData"] = 0.9;
        var status = await RunAsync(page);
        Assert.AreEqual(AiWorkState.Succeeded, status.State);
        Assert.AreEqual("personalData", status.BlockedBy);
        Assert.IsEmpty(status.Added);
        Assert.AreEqual(chats, router.ChatCalls, "no main model");
        router.Nouls.Clear();
        router.Nouls["credentials"] = 0.95;
        Assert.AreEqual("credentials", (await RunAsync(page)).BlockedBy);
    }

    [TestMethod]
    public async Task Marking_sensitive_stops_a_queued_scan_and_unmarking_needs_both_statements()
    {
        var page = (await CreateAsync(KbPageTests.Request("Will be sensitive", "Text."))).Page;
        var started = await (await ScanAsync(page)).ReadAsync<KbAiTagsStatus>();
        var marked = await (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}/sensitive", new KbSensitiveRequest { Sensitive = true }))
            .ReadAsync<KbPageDetail>();
        Assert.IsTrue(marked.Sensitive);
        var chats = router.ChatCalls;
        await app.Services.GetRequiredService<AiWorker>().DrainAsync(CancellationToken.None);
        var status = await (await user.GetAsync($"/api/v1/kb/pages/{page.Id}/ai-tags/{started.WorkId}")).ReadAsync<KbAiTagsStatus>();
        Assert.AreEqual(AiWorkState.Cancelled, status.State);
        Assert.AreEqual(chats, router.ChatCalls);

        var unmark = new KbSensitiveRequest { Sensitive = false, Revision = marked.Revision };
        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}/sensitive", unmark)).StatusCode);
        unmark.Statements = Both;
        unmark.Revision = marked.Revision - 1;
        Assert.AreEqual(HttpStatusCode.Conflict, (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}/sensitive", unmark)).StatusCode);
        unmark.Revision = marked.Revision;
        var unmarked = await (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}/sensitive", unmark)).ReadAsync<KbPageDetail>();
        Assert.IsFalse(unmarked.Sensitive);
        var revisions = await (await user.GetAsync($"/api/v1/kb/pages/{page.Id}/revisions")).ReadAsync<List<KbRevisionItem>>();
        Assert.AreEqual("unmarked", revisions[0].Changes.Single().Text);
        Assert.AreEqual(marked.Revision, revisions[0].Declaration!.Revision);
        Assert.AreEqual("marked", revisions[1].Changes.Single().Text);
        await using var db = app.NewDb();
        Assert.AreEqual(2, await db.AuthLog.CountAsync(l => l.EventType == AuthEvents.KbPageSensitivityChanged && l.Detail!.Contains($"KB-{page.Number} ")));
    }

    [TestMethod]
    public async Task Redacting_reaches_the_ai_request_log_of_the_pages_scans()
    {
        var secret = "Grüner Kristall " + Guid.NewGuid().ToString("N")[..4];
        var page = (await CreateAsync(KbPageTests.Request("Vault phrase", $"The phrase for the safe is {secret}, said twice."))).Page;
        var other = (await CreateAsync(KbPageTests.Request("Another vault", $"Its phrase is {secret} as well."))).Page;
        // The model echoes it as a tag: the answer holds it too.
        answer = (_, _) => new JsonObject { ["tags"] = new JsonArray(secret, "vault") }.ToJsonString();
        var scanned = await RunAsync(page);
        Assert.AreEqual(AiWorkState.Succeeded, scanned.State, scanned.Message);
        await RunAsync(other);
        var logged = (await RequestsAsync(page.Id)).Select(Body).ToList();
        Assert.IsTrue(logged.Count >= 2 && logged.All(b => b.Contains("Kristall")), "the guard's and the model's request, and the answer, hold it");

        var result = await (await admin.PostJsonAsync($"/api/v1/kb/pages/{page.Id}/redact", new KbRedactRequest { Text = secret })).ReadAsync<KbRedactResult>();
        Assert.AreEqual(logged.Count, result.AiRequests);
        Assert.IsTrue((await RequestsAsync(page.Id)).Select(Body).All(b => !b.Contains("Kristall") && b.Contains("[redacted]")));
        Assert.IsTrue((await RequestsAsync(other.Id)).Select(Body).All(b => b.Contains("Kristall")), "another page's requests are its own");
        var history = await (await admin.GetAsync($"/api/v1/kb/pages/{page.Id}/revisions")).ReadAsync<List<KbRevisionItem>>();
        StringAssert.EndsWith(history[0].Changes.Single().Text, $"and {result.AiRequests} AI requests");
    }

    /// <summary>The logged requests of a page's tag scans.</summary>
    private static async Task<List<AiRequestEntity>> RequestsAsync(Guid page)
    {
        await using var db = app.NewDb();
        var work = await db.AiWork.Where(w => w.Purpose == KbTagsHandler.Name && w.SubjectId == page).Select(w => (Guid?)w.Id).ToListAsync();
        return await db.AiRequests.Where(r => work.Contains(r.WorkId)).ToListAsync();
    }

    /// <summary>A request's bodies as logged: "Kristall" stands in them as it is, escaped or not, unlike its "ü".</summary>
    private static string Body(AiRequestEntity r) => AiRequestLog.Gunzip(r.Request) + "\n" + AiRequestLog.Gunzip(r.Response);

    [TestMethod]
    public async Task Two_clicks_make_one_scan()
    {
        var page = (await CreateAsync(KbPageTests.Request("Clicked twice", "Text."))).Page;
        var started = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ => await (await ScanAsync(page)).ReadAsync<KbAiTagsStatus>()));
        Assert.AreEqual(1, started.Select(s => s.WorkId).Distinct().Count());
        await using var db = app.NewDb();
        Assert.AreEqual(1, await db.AiWork.CountAsync(w => w.Purpose == KbTagsHandler.Name && w.SubjectId == page.Id));
        await app.Services.GetRequiredService<AiWorker>().DrainAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Marking_sensitive_and_redacting_cancel_every_running_scan_of_the_page()
    {
        var scans = app.Services.GetRequiredService<KbAiScans>();
        async Task<CancellationTokenSource[]> RunningAsync(Guid page)
        {
            // Two scans at once (Ai:Parallelism above 1); each ends when cancelled, as the handler does.
            var running = Enumerable.Range(0, 2).Select(_ => scans.Register(page, CancellationToken.None)).ToArray();
            foreach (var cts in running)
            {
                cts.Token.Register(() => Task.Run(() => scans.Done(page, cts)));
            }
            Assert.AreEqual(2, scans.Running(page));
            return running;
        }
        var page = (await CreateAsync(KbPageTests.Request("Two scans", "Text with Quarzsand inside."))).Page;
        var first = await RunningAsync(page.Id);
        await (await user.PutJsonAsync($"/api/v1/kb/pages/{page.Id}/sensitive", new KbSensitiveRequest { Sensitive = true })).ReadAsync<KbPageDetail>();
        Assert.IsTrue(first.All(c => c.IsCancellationRequested));

        var other = (await CreateAsync(KbPageTests.Request("Two more scans", "Text with Quarzsand inside."))).Page;
        var second = await RunningAsync(other.Id);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await (await admin.PostJsonAsync($"/api/v1/kb/pages/{other.Id}/redact", new KbRedactRequest { Text = "Quarzsand" })).ReadAsync<KbRedactResult>();
        Assert.IsTrue(second.All(c => c.IsCancellationRequested));
        Assert.IsLessThan(5000L, watch.ElapsedMilliseconds, "it waited for the scans to stop, not for its timeout");
        Assert.AreEqual(0, scans.Running(other.Id));
    }

    [TestMethod]
    public async Task The_question_always_follows_a_new_page_and_about_one_in_ten_edits()
    {
        var created = await CreateAsync(KbPageTests.Request("Asked at once", "text"));
        Assert.IsTrue(created.AskAiTags);
        var sensitive = await CreateAsync(new KbPageRequest { Title = "Never asked", Content = "text", MarkSensitive = true });
        Assert.IsFalse(sensitive.AskAiTags);

        // Edits of the content by someone not asked yet, with the seeded draw.
        var asked = 0;
        for (var i = 0; i < 150; i++)
        {
            var page = (await CreateAsync(KbPageTests.Request($"Draw {i}", "text"))).Page;
            if ((await SaveAsync(page, r => r.Content = "changed", admin)).AskAiTags)
            {
                asked++;
            }
        }
        Assert.IsTrue(asked is >= 5 and <= 30, $"{asked} of 150");
    }

    [TestMethod]
    public async Task The_question_needs_a_content_change_and_not_twice_in_a_day()
    {
        random.Fixed = 0;
        var page = (await CreateAsync(KbPageTests.Request("Asked by its edits", "text"))).Page;
        var status = await SaveAsync(page, r => r.Status = KbPageStatus.Draft, admin);
        Assert.IsFalse(status.AskAiTags, "not an edit");
        var first = await SaveAsync(status.Page, r => r.Content = "one", admin);
        Assert.IsTrue(first.AskAiTags);
        var second = await SaveAsync(first.Page, r => r.Content = "two", admin);
        Assert.IsFalse(second.AskAiTags, "within 24 hours");
        await using (var db = app.NewDb())
        {
            await db.KbAiQuestions.Where(q => q.PageId == page.Id && q.UserId == TestUsers.Admin)
                .ExecuteUpdateAsync(u => u.SetProperty(q => q.AskedAt, Clock.UtcNow.AddHours(-25)));
        }
        Assert.IsTrue((await SaveAsync(second.Page, r => r.Title = "Asked again", admin)).AskAiTags);
        random.Fixed = 99;
        var other = (await CreateAsync(KbPageTests.Request("Not drawn", "text"))).Page;
        Assert.IsFalse((await SaveAsync(other, r => r.Content = "changed", admin)).AskAiTags);
    }
}
