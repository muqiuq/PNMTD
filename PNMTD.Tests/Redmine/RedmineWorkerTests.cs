using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Cookie;
using PNMTD.Dev;
using PNMTD.Models.Db;
using PNMTD.Outbound;
using PNMTD.Redmine;

namespace PNMTD.Tests.Redmine;

/// <summary>
/// The Redmine worker (docs/redmine-queue-outbound-plan.md §3, §4, §5): the lanes and slots, joining, the snapshots and their
/// freshness, the refresh button, and that only the worker calls Redmine.
/// </summary>
[TestClass]
public sealed class RedmineWorkerTests
{
    private static readonly FakeRedmine redmine = new();
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static RedmineWorker worker = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp(settings: FakeRedmine.WritableSettings, services: redmine.Install);
        user = app.Client(Roles.User);
        worker = app.Services.GetRequiredService<RedmineWorker>();
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public void Reset()
    {
        redmine.Down = false;
        redmine.RefuseKey = false;
    }

    private static RedmineJob Job(long seq, RedmineLane lane) => new(seq, new RedmineJobSpec(null, lane, "test", "test"));

    [TestMethod]
    public async Task The_lanes_are_worked_in_order_button_write_interactive_background()
    {
        var gate = new RedmineGate(() => 1, keepOne: false);
        var holder = Job(0, RedmineLane.Interactive);
        await gate.AcquireAsync(holder, CancellationToken.None);
        var order = new List<RedmineLane>();
        var waits = new[] { RedmineLane.Background, RedmineLane.Interactive, RedmineLane.Write, RedmineLane.Button }
            .Select((lane, i) => (Lane: lane, Task: gate.AcquireAsync(Job(i + 1, lane), CancellationToken.None))).ToList();
        for (var i = 0; i < waits.Count; i++)
        {
            gate.Release();
            var granted = await Task.WhenAny(waits.Where(w => !order.Contains(w.Lane)).Select(w => w.Task));
            order.Add(waits.Single(w => w.Task == granted).Lane);
        }
        CollectionAssert.AreEqual(new[] { RedmineLane.Button, RedmineLane.Write, RedmineLane.Interactive, RedmineLane.Background }, order);
    }

    [TestMethod]
    public async Task Background_never_takes_the_last_slot_and_a_raised_job_goes_first()
    {
        var gate = new RedmineGate(() => 2, keepOne: true);
        await gate.AcquireAsync(Job(1, RedmineLane.Background), CancellationToken.None);
        var second = Job(2, RedmineLane.Background);
        var waiting = gate.AcquireAsync(second, CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(waiting.IsCompleted, "the last slot is kept for the other lanes");
        var interactive = gate.AcquireAsync(Job(3, RedmineLane.Interactive), CancellationToken.None);
        await interactive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(waiting.IsCompleted);

        // Both slots busy: a queued interactive job, and the background one raised by a button press.
        var later = gate.AcquireAsync(Job(4, RedmineLane.Interactive), CancellationToken.None);
        Assert.IsTrue(second.Raise(RedmineLane.Button));
        gate.Pump();
        gate.Release();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(later.IsCompleted, "the raised job went first");
        gate.Release();
        await later.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task The_same_key_is_one_job_and_a_fresh_one_joins_only_one_not_started()
    {
        var runs = 0;
        var release = new TaskCompletionSource();
        async Task<int> Work(RedmineCalls calls)
        {
            Interlocked.Increment(ref runs);
            await calls.GetAsync<RmTrackers>("/trackers.json", "test");
            await release.Task;
            return runs;
        }
        var key = $"test:{Guid.NewGuid():N}";
        var first = worker.RunAsync(new RedmineJobSpec(key, RedmineLane.Background, "test", "test"), Work);
        var joined = worker.RunAsync(new RedmineJobSpec(key, RedmineLane.Button, "test", "test"), Work);
        for (var i = 0; i < 100 && !worker.QueueState().Jobs.Any(j => j.Key == key && j.Started); i++)
        {
            await Task.Delay(20);
        }
        Assert.AreEqual(RedmineLane.Button, worker.QueueState().Jobs.Single(j => j.Key == key).Lane, "joining from the button lane raised it");
        var fresh = worker.RunAsync(new RedmineJobSpec(key, RedmineLane.Interactive, "test", "test", Fresh: true), Work);
        release.SetResult();
        await Task.WhenAll(first, joined, fresh);
        Assert.AreEqual(2, runs, "the joined one ran once; the fresh one didn't join the running job");
        Assert.AreEqual(await first, await joined);
    }

    [TestMethod]
    public async Task A_waiter_that_stops_waiting_doesnt_stop_the_job()
    {
        var release = new TaskCompletionSource();
        var done = false;
        using var cts = new CancellationTokenSource();
        var job = worker.RunAsync(new RedmineJobSpec($"test:{Guid.NewGuid():N}", RedmineLane.Interactive, "test", "test"), async _ =>
        {
            await release.Task;
            done = true;
            return true;
        });
        var waiter = job.WaitAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => waiter);
        release.SetResult();
        Assert.IsTrue(await job);
        Assert.IsTrue(done);
    }

    [TestMethod]
    public async Task An_old_snapshot_answers_at_once_and_is_refreshed_behind_the_page()
    {
        var first = await (await user.GetAsync("/api/v1/redmine/overview")).ReadAsync<RedmineOverview>();
        Assert.IsNotNull(first.Freshness.RefreshedAt);
        var again = await (await user.GetAsync("/api/v1/redmine/overview")).ReadAsync<RedmineOverview>();
        Assert.AreEqual(first.Freshness.RefreshedAt, again.Freshness.RefreshedAt, "a minute isn't over: the same snapshot, nothing asked of Redmine");

        // A write makes it due: the next look answers with the old one, and a refresh runs behind it.
        worker.AfterWrite(null, RedmineLane.Write);
        var due = await (await user.GetAsync("/api/v1/redmine/overview")).ReadAsync<RedmineOverview>();
        Assert.AreEqual(first.Freshness.RefreshedAt, due.Freshness.RefreshedAt);
        Assert.IsTrue(due.Freshness.Refreshing);
        RedmineOverview after = due;
        for (var i = 0; i < 200 && (after.Freshness.Refreshing || after.Freshness.RefreshedAt == first.Freshness.RefreshedAt); i++)
        {
            await Task.Delay(25);
            after = await (await user.GetAsync("/api/v1/redmine/overview")).ReadAsync<RedmineOverview>();
        }
        Assert.IsTrue(after.Freshness.RefreshedAt > first.Freshness.RefreshedAt);
        Assert.AreEqual(first.Tiles, after.Tiles);
    }

    [TestMethod]
    public async Task The_refresh_button_waits_for_the_new_data_and_keeps_the_old_when_Redmine_fails()
    {
        var before = await (await user.GetAsync("/api/v1/redmine/issues/10")).ReadAsync<RedmineIssueDetail>();
        var refreshed = await user.PostAsync("/api/v1/redmine/refresh?view=issue&id=10", null);
        Assert.AreEqual(HttpStatusCode.OK, refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());
        var freshness = await refreshed.ReadAsync<RedmineFreshness>();
        Assert.IsTrue(freshness.RefreshedAt > before.Freshness.RefreshedAt);
        Assert.IsFalse(freshness.Refreshing);

        var search = await user.PostAsync("/api/v1/redmine/refresh?view=search&status=all&page=1&pageSize=25", null);
        Assert.AreEqual(HttpStatusCode.OK, search.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.PostAsync("/api/v1/redmine/refresh?view=nothing", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.PostAsync("/api/v1/redmine/refresh?view=issue&id=999", null)).StatusCode);

        redmine.Down = true;
        Assert.AreEqual(HttpStatusCode.BadGateway, (await user.PostAsync("/api/v1/redmine/refresh?view=issue&id=10", null)).StatusCode);
        var kept = await (await user.GetAsync("/api/v1/redmine/issues/10")).ReadAsync<RedmineIssueDetail>();
        Assert.AreEqual(before.Subject, kept.Subject, "the old data stays");
        Assert.IsNotNull(kept.Freshness.FailedAt);
        StringAssert.Contains(kept.Freshness.Error, "doesn't answer");
        redmine.Down = false;
        var back = await (await user.PostAsync("/api/v1/redmine/refresh?view=issue&id=10", null)).ReadAsync<RedmineFreshness>();
        Assert.IsNull(back.FailedAt);
    }

    [TestMethod]
    public async Task A_note_a_person_ordered_goes_in_the_button_lane_and_Cookies_own_in_the_write_lane()
    {
        var outbox = app.Services.GetRequiredService<Outbox>();
        var log = app.Services.GetRequiredService<OutboundLog>();
        var own = await outbox.SendAsync(new OutboxRequest(CookieChannel.RedminePrivateNote, "#9: a note", "Cookie's own note", IssueId: 9, Recipient: "#9",
            Unlimited: true), CancellationToken.None);
        var ordered = await outbox.SendAsync(new OutboxRequest(CookieChannel.RedminePrivateNote, "#9: ordered", "An admin's order", IssueId: 9, Recipient: "#9",
            Unlimited: true, OrderedBy: "alice", ByPerson: true), CancellationToken.None);
        Assert.AreEqual(CookieOutboxState.Sent, own.State, own.Detail);
        Assert.AreEqual(CookieOutboxState.Sent, ordered.State, ordered.Detail);
        await log.FlushAsync();
        await using var db = app.NewDb();
        var writes = await db.RedmineCalls.AsNoTracking().Where(c => c.Kind == RedmineCallKind.Write && c.Target.EndsWith("/issues/9.json"))
            .OrderByDescending(c => c.Id).Take(2).ToListAsync();
        Assert.AreEqual(RedmineLane.Button, writes[0].Lane);
        Assert.AreEqual("alice", writes[0].AskedBy);
        Assert.AreEqual(RedmineLane.Write, writes[1].Lane);
        Assert.AreEqual("Cookie", writes[1].AskedBy);
        Assert.AreEqual("Cookie: private note on #9", writes[1].Purpose);
        var check = await db.RedmineCalls.AsNoTracking().Where(c => c.JobId == writes[0].JobId).OrderBy(c => c.Id).ToListAsync();
        CollectionAssert.AreEqual(new[] { RedmineCallKind.Read, RedmineCallKind.Write }, check.Select(c => c.Kind).ToArray(),
            "the ticket's project read fresh, then the note, in one job");
    }

    [TestMethod]
    public void Only_the_worker_calls_RedmineClient()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PNMTD.slnx")))
        {
            root = root.Parent;
        }
        Assert.IsNotNull(root, "the repository");
        var allowed = new[]
        {
            "PNMTD/Redmine/RedmineClient.cs", "PNMTD/Redmine/RedmineWorker.cs", "PNMTD/Redmine/RedmineSetup.cs",
            // Only the client's name and its certificate handler, not its calls.
            "PNMTD/Dev/FakeRedmine.cs", "PNMTD/Zabbix/ZabbixClient.cs",
        };
        var users = Directory.EnumerateFiles(Path.Combine(root.FullName, "PNMTD"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("RedmineClient", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root.FullName, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Except(allowed).ToList();
        CollectionAssert.AreEqual(Array.Empty<string>(), users, "Redmine is called through RedmineWorker only (docs/redmine-queue-outbound-plan.md §3.1)");
    }
}
