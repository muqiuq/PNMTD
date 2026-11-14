using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using static PNMTD.Tests.Mikrotik.MikrotikJobTests;

namespace PNMTD.Tests.Mikrotik;

/// <summary>
/// Remote commands (docs/mikrotik-plan.md §8, §14) against the simulated router of the update tests, which
/// unwraps the <c>:parse</c> script and answers like RouterOS 7.25.1 did in the lab. The app's runner runs them.
/// </summary>
[TestClass]
public sealed class MikrotikCommandTests
{
    private static TestApp app = null!;
    private static AdminConnector routers = null!;
    private static HttpClient admin = null!;
    private static HttpClient user = null!;
    private static Guid hostId;
    private static int next = 10;

    /// <summary>Every log line of the app, down to Trace for PNMTD and ASP.NET Core (§14: the password never in a log).</summary>
    private static readonly CapturedLogs logs = new();

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        routers = new AdminConnector();
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Mikrotik:AllowPasswordsOverHttp"] = "true",
            ["Mikrotik:Commands:MaxStoredOutputBytes"] = "2048",
            ["Logging:LogLevel:PNMTD"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
        }, services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IRouterConnector>(routers));
            s.AddSingleton<ILoggerProvider>(logs);
        });
        admin = app.Client(Roles.Admin);
        user = app.Client(Roles.User);
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"command-tests-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        hostId = host.Id;
        await app.Services.GetRequiredService<MikrotikKeyStore>().GetAsync(CancellationToken.None);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<(Guid Id, SimulatedRouter Router)> RouterAsync(string identity = "router")
    {
        var address = $"198.51.100.{next++}";
        var router = new SimulatedRouter { Identity = identity };
        routers.Add(address, router);
        var sensor = await (await admin.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = $"router {address}", Interval = 300, GracePeriod = 600,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = address },
        })).ReadAsync<SensorDetail>();
        await app.Services.GetRequiredService<RouterPollService>().PollAsync(sensor.Id, wait: true, CancellationToken.None);
        return (sensor.Id, router);
    }

    private static Task<HttpResponseMessage> RunAsync(HttpClient client, MikrotikCommandRequest request) =>
        client.PostJsonAsync("/api/v1/mikrotik/commands", request);

    private static async Task<MikrotikCommandsCreated> StartAsync(HttpClient client, MikrotikCommandRequest request)
    {
        var response = await RunAsync(client, request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<MikrotikCommandsCreated>();
    }

    private static async Task<MikrotikCommandDetail> FinishedAsync(HttpClient client, Guid jobId)
    {
        for (var i = 0; i < 300; i++)
        {
            var detail = await (await client.GetAsync($"/api/v1/mikrotik/commands/{jobId}")).ReadAsync<MikrotikCommandDetail>();
            if (detail.Command.State is not (MikrotikJobState.Queued or MikrotikJobState.Running))
            {
                return detail;
            }
            await Task.Delay(100);
        }
        Assert.Fail("The command did not finish.");
        return null!;
    }

    private static MikrotikCommandRequest AsAdmin(Guid[] ids, string command) => new()
    {
        SensorIds = ids, Command = command, RunAs = MikrotikRunAs.Admin, Username = "admin", Password = MikrotikJobTests.AdminPassword,
    };

    [TestMethod]
    public async Task As_pnmt_a_print_shows_its_output_and_a_change_is_refused_by_the_router()
    {
        var (id, router) = await RouterAsync();
        var print = await StartAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = "/ip address print\r\n" });
        Assert.AreEqual(0, print.Refused.Count);
        var done = await FinishedAsync(user, print.Jobs[0].Id);
        Assert.AreEqual(MikrotikJobState.Succeeded, done.Command.State, done.Command.Error);
        Assert.AreEqual("pnmt", done.Command.RunAs);
        Assert.AreEqual("/ip address print", done.Command.Command, "stored as typed, the line ends normalised");
        Assert.AreEqual("Flags: D - DYNAMIC\n#   ADDRESS        INTERFACE\n0   10.0.2.15/24   ether1\n", done.Output, "no escape sequences, no CR");
        Assert.AreEqual(0, done.Command.ExitStatus);
        Assert.IsNull(done.Command.BackupBeforeId, "no exports for pnmt runs by default");
        Assert.AreEqual("/ip address print", router.Scripts.Last(), "sent as one script through :parse");

        var add = await StartAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = "/ip address add address=192.0.2.1/24 interface=ether1" });
        var refused = await FinishedAsync(user, add.Jobs[0].Id);
        Assert.AreEqual(MikrotikJobState.Failed, refused.Command.State);
        Assert.AreEqual("not enough permissions (9)", refused.Command.Error);
        Assert.AreEqual(1, refused.Command.ExitStatus);
        Assert.AreEqual(0, router.Config.Count);

        var error = await FinishedAsync(user, (await StartAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = ":error \"boom\"" })).Jobs[0].Id);
        Assert.AreEqual("boom (:error; line 1)", error.Command.Error, "the exit status, with the last line as the message");

        var flaky = await FinishedAsync(user, (await StartAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = "/flaky" })).Jobs[0].Id);
        Assert.AreEqual(MikrotikJobState.Failed, flaky.Command.State, "RouterOS's error line, though the exit status is 0");
        Assert.AreEqual(0, flaky.Command.ExitStatus);
    }

    [TestMethod]
    public async Task As_an_admin_with_backups_before_and_after_the_diff_and_243_show_the_change()
    {
        var (id, router) = await RouterAsync();
        var created = await StartAsync(admin, AsAdmin([id], "/ip address add address=192.0.2.1/24 interface=ether1"));
        var done = await FinishedAsync(admin, created.Jobs[0].Id);
        Assert.AreEqual(MikrotikJobState.Succeeded, done.Command.State, done.Command.Error);
        Assert.IsTrue(done.Command.AsAdmin);
        Assert.AreEqual("admin", done.Command.RunAs);
        Assert.AreEqual(1, router.Config.Count);
        Assert.IsNotNull(done.Command.BackupBeforeId);
        Assert.IsNotNull(done.Command.BackupAfterId);
        Assert.AreNotEqual(done.Command.BackupBeforeId, done.Command.BackupAfterId);
        Assert.AreEqual(1, done.Added);
        Assert.AreEqual(0, done.Removed);
        Assert.IsTrue(done.Log.Any(l => l.Step == MikrotikJobStep.Check && l.Message.Contains("its group is full (", StringComparison.Ordinal)),
            "the admin's group, as checked");

        // The diff of the two backups is the command's change.
        var diff = await (await admin.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{done.Command.BackupAfterId}/diff?against={done.Command.BackupBeforeId}"))
            .ReadAsync<MikrotikBackupDiff>();
        Assert.IsTrue(diff.Hunks.SelectMany(h => h.Lines).Any(l => l.Kind == DiffLineKind.Added && l.Text.Contains("192.0.2.1/24", StringComparison.Ordinal)));

        await using var db = app.NewDb();
        var events = await db.Events.Where(e => e.SensorId == id && e.Code == PNMTStatusCodes.MIKROTIK_CONFIG_CHANGED).ToListAsync();
        Assert.IsTrue(events.Any(e => e.Message.StartsWith("Configuration changed by a command of", StringComparison.Ordinal) && e.Message.Contains("as admin")),
            string.Join("; ", events.Select(e => e.Message)));
        Assert.IsFalse(events.Any(e => e.Message.Contains("192.0.2.1", StringComparison.Ordinal)), "events never carry the text: Viewers see them");
        Assert.IsTrue(await db.AuditLog.AnyAsync(a => a.EventType == AuditEvents.MikrotikCommandStarted && a.Detail!.Contains(created.BatchId.ToString())));
        Assert.AreEqual(0, await CountMarkerAsync(app, MikrotikJobTests.AdminPassword), "the admin password must never be stored");
        Assert.IsGreaterThan(0, logs.Count, "the capture works");
        Assert.IsFalse(logs.Contains(MikrotikJobTests.AdminPassword), "nor logged");
    }

    [TestMethod]
    public async Task A_batch_groups_its_results_by_output_and_one_at_a_time_stops_at_the_first_failure()
    {
        var a = await RouterAsync("alpha");
        var b = await RouterAsync("alpha");
        var c = await RouterAsync("gamma");
        var created = await StartAsync(user, new MikrotikCommandRequest { SensorIds = [a.Id, b.Id, c.Id], Command = "/system identity print" });
        foreach (var job in created.Jobs)
        {
            await FinishedAsync(user, job.Id);
        }
        var batch = await (await user.GetAsync($"/api/v1/mikrotik/commands/batches/{created.BatchId}")).ReadAsync<MikrotikCommandBatch>();
        Assert.AreEqual(2, batch.Groups.Count);
        Assert.AreEqual(2, batch.Groups[0].Jobs.Count, "the largest group first");
        Assert.AreEqual("  name: alpha\n", batch.Groups[0].Output);
        Assert.AreEqual("  name: gamma\n", batch.Groups[1].Output);

        // As an admin, one at a time by default: the canary's failure stops the rest.
        var stopped = await StartAsync(admin, AsAdmin([a.Id, b.Id, c.Id], "/nosuch thing"));
        var first = await FinishedAsync(admin, stopped.Jobs[0].Id);
        Assert.AreEqual(MikrotikJobState.Failed, first.Command.State, $"{first.Command.Error} | {string.Join(" | ", first.Log.Select(l => $"{l.Step} {l.Outcome} {l.Message}"))}");
        Assert.AreEqual("bad command name nosuch (line 1 column 2)", first.Command.Error);
        foreach (var rest in stopped.Jobs.Skip(1))
        {
            var detail = await FinishedAsync(admin, rest.Id);
            Assert.AreEqual(MikrotikJobState.Cancelled, detail.Command.State);
        }
        Assert.AreEqual(0, c.Router.Scripts.Count(s => s == "/nosuch thing"));
    }

    [TestMethod]
    public async Task A_timeout_keeps_the_partial_output_and_long_output_is_cut()
    {
        var (id, router) = await RouterAsync();
        var slow = await FinishedAsync(user, (await StartAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = ":delay 100", TimeoutSeconds = 5 })).Jobs[0].Id);
        Assert.AreEqual(MikrotikJobState.TimedOut, slow.Command.State);
        Assert.AreEqual("partial output\n", slow.Output);
        Assert.IsNull(slow.Command.ExitStatus);

        router.LogBytes = 10_000;
        var log = await FinishedAsync(user, (await StartAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = "/log print" })).Jobs[0].Id);
        Assert.AreEqual(MikrotikJobState.Succeeded, log.Command.State);
        Assert.IsTrue(log.Command.OutputTruncated);
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(log.Output!) <= 2048);

        var tooLong = await RunAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = new string('x', 16 * 1024 + 1) });
        Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var badTimeout = await RunAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = "/ip address print", TimeoutSeconds = 2 });
        Assert.AreEqual(HttpStatusCode.BadRequest, badTimeout.StatusCode);
    }

    [TestMethod]
    public async Task Cancelling_closes_a_running_commands_session()
    {
        var (id, _) = await RouterAsync();
        var created = await StartAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = "/tool torch interface=ether1", TimeoutSeconds = 600 });
        for (var i = 0; i < 100; i++)
        {
            var now = await (await user.GetAsync($"/api/v1/mikrotik/commands/{created.Jobs[0].Id}")).ReadAsync<MikrotikCommandDetail>();
            if (now.Command.Step == MikrotikJobStep.Run)
            {
                break;
            }
            await Task.Delay(50);
        }
        Assert.AreEqual(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/mikrotik/commands/{created.Jobs[0].Id}")).StatusCode);
        var done = await FinishedAsync(user, created.Jobs[0].Id);
        Assert.AreEqual(MikrotikJobState.Cancelled, done.Command.State);
        StringAssert.Contains(done.Command.Error, "Cancelled while it ran");
    }

    [TestMethod]
    public async Task Users_run_as_pnmt_only_and_never_see_admin_runs_Viewers_neither()
    {
        var (id, _) = await RouterAsync();
        var viewer = app.Client(Roles.Viewer);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RunAsync(viewer, new MikrotikCommandRequest { SensorIds = [id], Command = "/ip address print" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/mikrotik/commands")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RunAsync(user, AsAdmin([id], "/ip address print"))).StatusCode);

        var adminRun = await StartAsync(admin, AsAdmin([id], "/ip address print"));
        await FinishedAsync(admin, adminRun.Jobs[0].Id);
        var mine = await StartAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = "/ip address print" });
        await FinishedAsync(user, mine.Jobs[0].Id);

        var listed = await (await user.GetAsync($"/api/v1/mikrotik/commands?sensorId={id}")).ReadAsync<Paged<MikrotikCommandItem>>();
        Assert.IsTrue(listed.Items.Any(j => j.Id == mine.Jobs[0].Id));
        Assert.IsFalse(listed.Items.Any(j => j.AsAdmin), "admin runs are an Admin's");
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync($"/api/v1/mikrotik/commands/{adminRun.Jobs[0].Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync($"/api/v1/mikrotik/commands/batches/{adminRun.BatchId}")).StatusCode);
        var all = await (await admin.GetAsync($"/api/v1/mikrotik/commands?sensorId={id}")).ReadAsync<Paged<MikrotikCommandItem>>();
        Assert.IsTrue(all.Items.Any(j => j.AsAdmin));

        // The router's job list (CanRead) has its updates only.
        var routerJobs = await (await viewer.GetAsync($"/api/v1/sensors/{id}/mikrotik/jobs")).ReadAsync<Paged<MikrotikJobItem>>();
        Assert.AreEqual(0, routerJobs.Items.Count);
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/sensors/{id}/mikrotik/jobs/{mine.Jobs[0].Id}")).StatusCode);
    }

    [TestMethod]
    public async Task An_Admin_redacts_a_command_and_its_output()
    {
        var (id, _) = await RouterAsync();
        var created = await StartAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = ":error \"secret-Kq2\"" });
        var done = await FinishedAsync(user, created.Jobs[0].Id);
        StringAssert.Contains(done.Output, "secret-Kq2");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.PostAsync($"/api/v1/mikrotik/commands/{done.Command.Id}/redact", null)).StatusCode);
        var redacted = await (await admin.PostAsync($"/api/v1/mikrotik/commands/{done.Command.Id}/redact", null)).ReadAsync<MikrotikCommandItem>();
        Assert.IsNull(redacted.Command);
        Assert.IsNotNull(redacted.RedactedAt);
        var after = await (await user.GetAsync($"/api/v1/mikrotik/commands/{done.Command.Id}")).ReadAsync<MikrotikCommandDetail>();
        Assert.IsNull(after.Output);
        Assert.AreEqual("Redacted.", after.Command.Error);
        Assert.AreEqual(0, await CountMarkerAsync(app, "secret-Kq2"), "the text, the output and the router's error line are gone");
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(a => a.EventType == AuditEvents.MikrotikCommandRedacted && a.Detail!.Contains(done.Command.Id.ToString())));
    }

    [TestMethod]
    public async Task A_command_is_refused_while_an_update_runs_on_the_router()
    {
        var (id, router) = await RouterAsync();
        await using (var db = app.NewDb())
        {
            db.MikrotikJobs.Add(new MikrotikJobEntity
            {
                Id = Guid.NewGuid(), BatchId = Guid.NewGuid(), Kind = MikrotikJobKind.Update, SensorId = id, RouterName = "x",
                State = MikrotikJobState.Running, Step = MikrotikJobStep.Wait, CreatedAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var created = await StartAsync(user, new MikrotikCommandRequest { SensorIds = [id], Command = "/ip address print" });
        Assert.AreEqual(0, created.Jobs.Count);
        Assert.AreEqual("An update is queued or running on it.", created.Refused.Single().Reason);
        await using (var db = app.NewDb())
        {
            await db.MikrotikJobs.Where(j => j.SensorId == id && j.Kind == MikrotikJobKind.Update).ExecuteUpdateAsync(j => j.SetProperty(x => x.State, MikrotikJobState.Failed));
        }
        Assert.AreEqual(0, router.Scripts.Count);
    }
}

/// <summary>A logger provider that keeps every formatted message and exception text.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> lines = new();

    public int Count => lines.Count;

    public bool Contains(string text) => lines.Any(l => l.Contains(text, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}
