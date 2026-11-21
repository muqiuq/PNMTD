using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.OsUpdates;
using PNMTD.Sensor.Core.Service;
using PNMTD.Services.Events;
using PNMTD.Tests.Agents;

namespace PNMTD.Tests.Linux;

/// <summary>
/// OS updates on PNMTD (docs/linux-os-updates-plan.md §5, §6.5, §9, §12): the section's pending packages and the window in the configuration;
/// a run's start, log (a gap asks for the missing part) and end (checked against the log's hash, acknowledged), runs started from the API and
/// refused by the server, retention per kind, 467 for a failed upgrade until it is marked handled, and the high-risk check: a KEV CVE fixed by
/// a pending update turns the sensor bad (466), an acknowledgement ends it (audited), and stronger evidence or the time ends the
/// acknowledgement. Servers: Run is needed to start anything.
/// </summary>
[TestClass]
public sealed class OsUpdatesTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Agents:JoinRateLimitPerMinute"] = "1000",
            ["Agents:HelloTimeoutSeconds"] = "2",
            ["OsUpdates:KeepUpdateLogs"] = "3",
            ["OsUpdates:KeepUpgradeLogs"] = "2",
            ["OsUpdates:Advisories:Enabled"] = "true",
        });
        admin = app.Client(Roles.Admin);
        hostId = await AgentHarness.CreateHostAsync(admin, "os-updates-tests");
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            await db.Hosts.Where(h => h.Id == hostId).ExecuteUpdateAsync(u => u.SetProperty(h => h.Enabled, false));
        }
        app.Dispose();
    }

    private static AgentRightsSummary Rights(bool upgrade = true) => new("config.yaml", null, false, [], 0, [], false, [], 0, 60, false, false, null,
        new OsUpdatesRightsSummary(true, upgrade, upgrade, true));

    private static readonly IReadOnlyList<string> Capabilities = [AgentCapabilities.Collect, AgentCapabilities.Access, AgentCapabilities.Keys, AgentCapabilities.OsUpdates];

    private static PendingPackage Package(string name, string source, string installed, string candidate, bool security = true) =>
        new(name, "amd64", installed, candidate, source, candidate, installed, security ? "Debian-Security:12/oldstable-security" : "Debian:12.15/oldstable", security, true);

    private static OsUpdatesSection Section(params PendingPackage[] packages) => new("debian:bookworm", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(20),
        packages, packages.Length, new AptPlan(packages.Length, 0, 0, 0, 5_000_000, 10_000, [], AptPlan.HashOf([])),
        new AptPlan(packages.Length, 0, 1, 0, 5_000_000, 10_000, ["libold1"], AptPlan.HashOf(["libold1"])), false, [], new NeedsRestartInfo(["ssh.service"], false, null, null),
        false, [], null, null, []);

    private sealed record Server(AgentHarness.Joined Joined, AgentSession Session, Handler Handler, CancellationTokenSource Stop, Task Receiving) : IAsyncDisposable
    {
        public Guid Id => Joined.Grant.SensorId;

        public async ValueTask DisposeAsync()
        {
            await Stop.CancelAsync();
            try
            {
                await Receiving;
            }
            catch (Exception)
            {
            }
            await Session.DisposeAsync();
            Stop.Dispose();
        }
    }

    /// <summary>An agent that records PNMTD's requests and answers os-update-run as <see cref="Refuse"/> says.</summary>
    private sealed class Handler : IAgentSessionHandler
    {
        public ConcurrentQueue<(string Type, System.Text.Json.JsonElement Body)> Requests { get; } = new();

        public string? Refuse { get; set; }

        public Task OnConfigAsync(AgentConfig config, CancellationToken ct) => Task.CompletedTask;

        public Task<Ack> OnRequestAsync(AgentSession session, SignedContent request, CancellationToken ct)
        {
            Requests.Enqueue((request.Type, request.Body));
            return Task.FromResult(request.Type == AgentMessageTypes.OsUpdateRun && Refuse is { } why ? new Ack(request.RequestId, false, why) : new Ack(request.RequestId, true));
        }

        public void OnIgnored(string type, string reason)
        {
        }
    }

    private static async Task<Server> JoinAsync(string hostname, bool upgrade = true)
    {
        var token = await AgentHarness.TokenAsync(admin, hostId, null, AgentPlatform.Linux);
        var joined = await AgentHarness.JoinAsync(app, token.Token, hostname, AgentPlatform.Linux);
        var session = await AgentHarness.OpenAsync(app, joined, rights: Rights(upgrade), capabilities: Capabilities);
        var handler = new Handler();
        var stop = new CancellationTokenSource();
        return new Server(joined, session, handler, stop, session.RunAsync(handler, stop.Token));
    }

    private static async Task SendSectionAsync(Server server, OsUpdatesSection section)
    {
        await server.Session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.OsUpdates, section), CancellationToken.None);
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.OsPendingPackages.CountAsync(p => p.SensorId == server.Id) == section.Packages.Count;
        });
    }

    private static Task<OsUpdatesView> ViewAsync(Server server) =>
        admin.GetAsync($"/api/v1/linux/servers/{server.Id}/os-updates").ContinueWith(t => t.Result.ReadAsync<OsUpdatesView>()).Unwrap();

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task<List<EventEntity>> HeartbeatAsync(Server server, int expected)
    {
        await server.Session.SendAsync(AgentMessageTypes.Heartbeat, AgentHarness.Heartbeat(server.Session) with { Hostname = server.Joined.Grant.HostName }, CancellationToken.None);
        return await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, server.Id), e => e.Count >= expected);
    }

    [TestMethod]
    public async Task TheSectionsPackagesAndTheWindowReachEachOther()
    {
        await using var server = await JoinAsync("osu-section");
        Assert.AreEqual(new OsUpdatesConfig(true, true, "04:00", 120), server.Session.Config.OsUpdates, "PNMT's window in the welcome (§4.2).");
        await SendSectionAsync(server, Section(Package("libssl3", "openssl", "3.0.20-1~deb12u2", "3.0.22-1~deb12u2"), Package("tzdata", "tzdata", "2026b-0", "2026f-0", false)));
        var view = await ViewAsync(server);
        Assert.AreEqual(2, view.Pending);
        Assert.AreEqual(1, view.Security);
        Assert.AreEqual("debian:bookworm", view.Distribution);
        Assert.IsTrue(view.Capable && view.Connected && view.Enabled);
        Assert.IsNull(view.Unavailable);
        Assert.AreEqual(OsUpgradeMode.Upgrade, view.DefaultMode);
        CollectionAssert.AreEqual(new[] { "libold1" }, view.FullUpgrade!.Removals.ToList());
        CollectionAssert.AreEqual(new[] { "ssh.service" }, view.NeedsRestart!.Services.ToList());
        var first = view.Packages.Single(p => p.Name == "libssl3").FirstSeenAt;
        await SendSectionAsync(server, Section(Package("libssl3", "openssl", "3.0.20-1~deb12u2", "3.0.22-1~deb12u3")));
        var again = await ViewAsync(server);
        Assert.AreEqual("3.0.22-1~deb12u3", again.Packages.Single().Candidate);
        Assert.AreEqual(first, again.Packages.Single().FirstSeenAt, "A new candidate keeps the package's first sight.");
        var summary = await (await admin.GetAsync("/api/v1/linux/os-updates/summary")).ReadAsync<OsUpdatesSummary>();
        Assert.IsTrue(summary.ByServer.Any(r => r.SensorId == server.Id && r.Pending == 1));
        Assert.IsTrue(summary.ByPackage.Any(p => p.Source == "openssl" && p.Version == "3.0.22-1~deb12u3"));
    }

    [TestMethod]
    public async Task AScheduledUpdatesLogAndEndAreStoredCheckedAndAcknowledged()
    {
        await using var server = await JoinAsync("osu-run");
        var runId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        await server.Session.SendAsync(AgentMessageTypes.OsUpdateStarted,
            new OsUpdateStartedMessage(runId, OsUpdateKind.Update, null, OsUpdateTrigger.Schedule, null, started, "apt-get -q update"), CancellationToken.None);
        const string first = "Hit:1 http://deb.debian.org/debian bookworm InRelease\n";
        const string second = "Fetched 467 kB in 0s (2162 kB/s)\nReading package lists...\n";
        await server.Session.SendAsync(AgentMessageTypes.OsUpdateOutput, new OsUpdateOutputMessage(runId, 0, first), CancellationToken.None);
        // A part that arrives before the one it follows: PNMTD asks for what it misses.
        await server.Session.SendAsync(AgentMessageTypes.OsUpdateOutput, new OsUpdateOutputMessage(runId, first.Length + 10, "lost"), CancellationToken.None);
        var resend = await AgentHarness.EventuallyAsync(() => Task.FromResult(server.Handler.Requests.ToList()), r => r.Any(x => x.Type == AgentMessageTypes.OsUpdateLog));
        Assert.AreEqual(first.Length, resend.First(x => x.Type == AgentMessageTypes.OsUpdateLog).Body.GetProperty("fromOffset").GetInt64());
        await server.Session.SendAsync(AgentMessageTypes.OsUpdateOutput, new OsUpdateOutputMessage(runId, first.Length, second), CancellationToken.None);
        await server.Session.SendAsync(AgentMessageTypes.OsUpdateOutput, new OsUpdateOutputMessage(runId, first.Length, second), CancellationToken.None);
        var log = first + second;
        await server.Session.SendAsync(AgentMessageTypes.OsUpdateResult, new OsUpdateResultMessage(runId, OsUpdateKind.Update, null, OsUpdateTrigger.Schedule, null,
            OsUpdateState.Succeeded, 0, OsUpdateSummary.Empty with { RepositoriesReached = 1, Fetched = "Fetched 467 kB in 0s (2162 kB/s)" }, null, started,
            DateTimeOffset.UtcNow, Encoding.UTF8.GetByteCount(log), Sha(log)), CancellationToken.None);
        var received = await AgentHarness.EventuallyAsync(() => Task.FromResult(server.Handler.Requests.ToList()), r => r.Any(x => x.Type == AgentMessageTypes.OsUpdateReceived));
        Assert.AreEqual(runId, received.First(x => x.Type == AgentMessageTypes.OsUpdateReceived).Body.GetProperty("runIds")[0].GetGuid());
        var runs = await (await admin.GetAsync($"/api/v1/linux/servers/{server.Id}/os-updates/runs?kind=Update")).ReadAsync<Paged<OsUpdateRunItem>>();
        var run = runs.Items.Single();
        Assert.AreEqual(OsUpdateState.Succeeded, run.State);
        Assert.AreEqual(OsUpdateTrigger.Schedule, run.Trigger);
        Assert.IsFalse(run.LogIncomplete, "The hash matched: the repeated part was stored once.");
        var part = await (await admin.GetAsync($"/api/v1/linux/servers/{server.Id}/os-updates/runs/{runId}/log?from=0")).ReadAsync<OsUpdateLogPart>();
        Assert.AreEqual(log, part.Text);
        Assert.IsTrue(part.Finished);
        var rest = await (await admin.GetAsync($"/api/v1/linux/servers/{server.Id}/os-updates/runs/{runId}/log?from={part.NextOffset}")).ReadAsync<OsUpdateLogPart>();
        Assert.AreEqual("", rest.Text);
        var file = await admin.GetAsync($"/api/v1/linux/servers/{server.Id}/os-updates/runs/{runId}/log/download");
        StringAssert.EndsWith(file.Content.Headers.ContentDisposition?.FileName?.Trim('"'), "-update.log");
    }

    [TestMethod]
    public async Task AnEndWithoutItsLogAsksForItOnceThenKeepsWhatIsThere()
    {
        await using var server = await JoinAsync("osu-nolog");
        var runId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        OsUpdateResultMessage End() => new(runId, OsUpdateKind.Update, null, OsUpdateTrigger.Schedule, null, OsUpdateState.Succeeded, 0, OsUpdateSummary.Empty, null, at,
            DateTimeOffset.UtcNow, 120, Sha("gone"));
        await server.Session.SendAsync(AgentMessageTypes.OsUpdateResult, End(), CancellationToken.None);
        var asked = await AgentHarness.EventuallyAsync(() => Task.FromResult(server.Handler.Requests.ToList()), r => r.Any(x => x.Type == AgentMessageTypes.OsUpdateLog));
        Assert.AreEqual(0, asked.First(x => x.Type == AgentMessageTypes.OsUpdateLog).Body.GetProperty("fromOffset").GetInt64());
        Assert.IsFalse(asked.Any(x => x.Type == AgentMessageTypes.OsUpdateReceived), "Not acknowledged while the log is missing.");
        // The server's copy is gone: the end comes again without it.
        await server.Session.SendAsync(AgentMessageTypes.OsUpdateResult, End(), CancellationToken.None);
        await AgentHarness.EventuallyAsync(() => Task.FromResult(server.Handler.Requests.ToList()), r => r.Any(x => x.Type == AgentMessageTypes.OsUpdateReceived));
        var run = (await (await admin.GetAsync($"/api/v1/linux/servers/{server.Id}/os-updates/runs")).ReadAsync<Paged<OsUpdateRunItem>>()).Items.Single();
        Assert.AreEqual(OsUpdateState.Succeeded, run.State);
        Assert.IsTrue(run.LogIncomplete);
    }

    [TestMethod]
    public async Task RunsFromTheApiNeedServersRunAndAServerMayRefuseThem()
    {
        await using var server = await JoinAsync("osu-api");
        await SendSectionAsync(server, Section(Package("libssl3", "openssl", "3.0.20-1~deb12u2", "3.0.22-1~deb12u2")));
        var viewer = app.Client(Roles.User);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.PostJsonAsync($"/api/v1/linux/servers/{server.Id}/os-updates/upgrade",
            new OsUpdateUpgradeRequest(OsUpgradeMode.Upgrade, null))).StatusCode, "Operators have no Servers: Run.");
        server.Handler.Refuse = "dpkg was interrupted: run dpkg --configure -a on the server first.";
        var refused = await (await admin.PostJsonAsync($"/api/v1/linux/servers/{server.Id}/os-updates/upgrade",
            new OsUpdateUpgradeRequest(OsUpgradeMode.FullUpgrade, AptPlan.HashOf(["libold1"])))).ReadAsync<OsUpdateStarted>();
        Assert.AreEqual(OsUpdateState.Refused, refused.State);
        Assert.AreEqual(server.Handler.Refuse, refused.Message);
        var request = server.Handler.Requests.Last(r => r.Type == AgentMessageTypes.OsUpdateRun).Body;
        Assert.AreEqual("FullUpgrade", request.GetProperty("mode").GetString());
        Assert.AreEqual(AptPlan.HashOf(["libold1"]), request.GetProperty("removalsHash").GetString(), "The removals the person saw go along.");
        server.Handler.Refuse = null;
        var started = await (await admin.PostJsonAsync($"/api/v1/linux/servers/{server.Id}/os-updates/upgrade", new OsUpdateUpgradeRequest(OsUpgradeMode.Upgrade, null)))
            .ReadAsync<OsUpdateStarted>();
        Assert.AreEqual(OsUpdateState.Running, started.State);
        var second = await admin.PostJsonAsync($"/api/v1/linux/servers/{server.Id}/os-updates/update", new { });
        Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode, "One run at a time.");
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(a => a.EventType == AuditEvents.OsUpdateRequested && a.TargetId == server.Id.ToString()));
    }

    [TestMethod]
    public async Task AServerThatDoesntAllowUpgradesIsRefusedByPnmtFirst()
    {
        await using var server = await JoinAsync("osu-refused", upgrade: false);
        var response = await admin.PostJsonAsync($"/api/v1/linux/servers/{server.Id}/os-updates/upgrade", new OsUpdateUpgradeRequest(OsUpgradeMode.Upgrade, null));
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "os_updates.upgrade");
        Assert.IsFalse(server.Handler.Requests.Any(r => r.Type == AgentMessageTypes.OsUpdateRun), "Nothing went to the server.");
    }

    [TestMethod]
    public async Task AFailedUpgradeIs467UntilMarkedHandledAndRetentionKeepsTheNewest()
    {
        await using var server = await JoinAsync("osu-failed");
        await SendSectionAsync(server, Section(Package("libssl3", "openssl", "3.0.20-1~deb12u2", "3.0.22-1~deb12u2")));
        var runId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-2);
        await server.Session.SendAsync(AgentMessageTypes.OsUpdateResult, new OsUpdateResultMessage(runId, OsUpdateKind.Upgrade, OsUpgradeMode.Upgrade, OsUpdateTrigger.Server,
            "root on the server", OsUpdateState.Failed, 100, OsUpdateSummary.Empty, "E: Sub-process /usr/bin/dpkg returned an error code (1)", at, DateTimeOffset.UtcNow, 0, Sha("")),
            CancellationToken.None);
        await AgentHarness.EventuallyAsync(async () => (await ViewAsync(server)).UpdateFailedMessage is not null);
        var events = await HeartbeatAsync(server, 1);
        Assert.AreEqual(PNMTStatusCodes.LINUX_UPDATE_FAILED, events[0].Code);
        StringAssert.Contains(events[0].Message, "failed (exit 100): E: Sub-process /usr/bin/dpkg returned an error code (1)");
        var dismissed = await (await admin.PostJsonAsync($"/api/v1/linux/servers/{server.Id}/os-updates/runs/{runId}/dismiss", new { })).ReadAsync<OsUpdateRunItem>();
        Assert.IsTrue(dismissed.Dismissed);
        events = await HeartbeatAsync(server, 2);
        Assert.AreEqual(PNMTStatusCodes.LINUX_OK, events[0].Code);

        // Retention: OsUpdates:KeepUpdateLogs=3 here; the newest stay, a running one too.
        await using (var db = app.NewDb())
        {
            for (var i = 0; i < 5; i++)
            {
                db.OsUpdateRuns.Add(OsUpdateRuns.NewRun(server.Id, Guid.NewGuid(), OsUpdateKind.Update, null, OsUpdateTrigger.Schedule, null, DateTime.UtcNow.AddDays(-10 + i))
                    .Also(r => r.State = OsUpdateState.Succeeded));
            }
            db.OsUpdateRuns.Add(OsUpdateRuns.NewRun(server.Id, Guid.NewGuid(), OsUpdateKind.Update, null, OsUpdateTrigger.Schedule, null, DateTime.UtcNow.AddDays(-30)));
            await db.SaveChangesAsync();
            await app.Services.GetRequiredService<OsUpdateRuns>().PruneAsync(db, server.Id, CancellationToken.None);
            var updates = await db.OsUpdateRuns.Where(r => r.SensorId == server.Id && r.Kind == OsUpdateKind.Update).ToListAsync();
            Assert.AreEqual(4, updates.Count, "The newest 3, and the running one.");
            Assert.AreEqual(1, updates.Count(r => r.State == OsUpdateState.Running));
        }
    }

    // --- the high-risk check and acknowledgements (§6.4, §6.5) ----------------------------------------------------------------------

    private static async Task<Guid> SeedAsync(string cve, bool kev, double? epssPercentile = null)
    {
        await using var db = app.NewDb();
        var source = await db.OsAdvisorySources.FirstOrDefaultAsync(s => s.Kind == OsAdvisorySourceKind.DebianTracker);
        if (source is null)
        {
            source = new OsAdvisorySourceEntity { Id = Guid.NewGuid(), Kind = OsAdvisorySourceKind.DebianTracker, Name = "Debian Security Tracker", Url = "https://example.org/json",
                IntervalMinutes = 360, CreatedAt = DateTime.UtcNow, LastSuccessAt = DateTime.UtcNow };
            db.OsAdvisorySources.Add(source);
        }
        db.OsFixes.Add(new OsFixEntity { SourceId = source.Id, Distribution = "debian:bookworm", SourcePackage = "openssl", Cve = cve, FixedVersion = "3.0.22-1~deb12u2",
            Urgency = "not yet assigned" });
        db.OsCveRisks.Add(new OsCveRiskEntity { Cve = cve, Kev = kev, KevAddedAt = kev ? new DateOnly(2026, 11, 18) : null, Epss = epssPercentile is null ? null : 0.6,
            EpssPercentile = epssPercentile, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return source.Id;
    }

    [TestMethod]
    public async Task AHighRiskUpdateIs466UntilAcknowledgedAndStrongerEvidenceEndsTheAcknowledgement()
    {
        var kevCve = $"CVE-2026-{Random.Shared.Next(100_000, 999_999)}";
        var epssCve = $"CVE-2026-{Random.Shared.Next(100_000, 999_999)}";
        await SeedAsync(kevCve, kev: true);
        await SeedAsync(epssCve, kev: false, epssPercentile: 0.995);
        await using var server = await JoinAsync("osu-risk");
        await SendSectionAsync(server, Section(Package("libssl3", "openssl", "3.0.20-1~deb12u2", "3.0.22-1~deb12u2")));
        var evaluator = app.Services.GetRequiredService<OsAdvisoryEvaluator>();
        await evaluator.EvaluateAsync(server.Id, CancellationToken.None);
        var view = await ViewAsync(server);
        Assert.AreEqual(2, view.HighRisk);
        var events = await HeartbeatAsync(server, 1);
        Assert.AreEqual(PNMTStatusCodes.LINUX_HIGH_RISK_UPDATE, events[0].Code);
        StringAssert.StartsWith(events[0].Message, "osu-risk: 1 high-risk update pending: openssl 3.0.22-1~deb12u2 (");
        StringAssert.Contains(events[0].Message, $"{kevCve}: exploited in the wild (CISA KEV since 2026-11-18)");

        var kevFinding = view.Findings.Single(f => f.Cve == kevCve);
        var epssFinding = view.Findings.Single(f => f.Cve == epssCve);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync($"/api/v1/linux/servers/{server.Id}/os-updates/findings/acknowledge",
            new OsFindingAcknowledgeRequest([kevFinding.Id], "", 7))).StatusCode, "A reason is required.");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync($"/api/v1/linux/servers/{server.Id}/os-updates/findings/acknowledge",
            new OsFindingAcknowledgeRequest([kevFinding.Id], "freeze", 31))).StatusCode, "At most AcknowledgeMaxDays.");
        view = await (await admin.PostJsonAsync($"/api/v1/linux/servers/{server.Id}/os-updates/findings/acknowledge",
            new OsFindingAcknowledgeRequest([kevFinding.Id, epssFinding.Id], "Kernel freeze until the 28th", 7))).ReadAsync<OsUpdatesView>();
        Assert.AreEqual(0, view.HighRisk);
        Assert.AreEqual(2, view.HighRiskAcknowledged);
        Assert.AreEqual("Kernel freeze until the 28th", view.Findings.Single(f => f.Cve == kevCve).Acknowledgement?.Reason);
        events = await HeartbeatAsync(server, 2);
        Assert.AreEqual(PNMTStatusCodes.LINUX_OK, events[0].Code, "Every high-risk finding acknowledged: no 466.");

        // The EPSS-only finding enters KEV: its acknowledgement ends, and 466 says why.
        await using (var db = app.NewDb())
        {
            await db.OsCveRisks.Where(r => r.Cve == epssCve).ExecuteUpdateAsync(u => u.SetProperty(r => r.Kev, true).SetProperty(r => r.KevAddedAt, new DateOnly(2026, 11, 21)));
        }
        await evaluator.EvaluateAsync(server.Id, CancellationToken.None);
        events = await HeartbeatAsync(server, 3);
        Assert.AreEqual(PNMTStatusCodes.LINUX_HIGH_RISK_UPDATE, events[0].Code);
        StringAssert.Contains(events[0].Message, "(1 more acknowledged)");
        StringAssert.Contains(events[0].Message, $"(acknowledgement ended: {epssCve}: now in CISA KEV)");

        // The other acknowledgement runs out.
        await using (var db = app.NewDb())
        {
            await db.OsUpdateFindings.Where(f => f.Id == kevFinding.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.AcknowledgedUntil, DateTime.UtcNow.AddMinutes(-1)));
        }
        await evaluator.EvaluateAsync(server.Id, CancellationToken.None);
        view = await ViewAsync(server);
        Assert.AreEqual(2, view.HighRisk);
        Assert.AreEqual("it ran out", view.Findings.Single(f => f.Cve == kevCve).AcknowledgementEndedReason);
        await using (var db = app.NewDb())
        {
            // The two refused requests above are audited too, as failed.
            var audit = await db.AuditLog.Where(a => a.TargetId == server.Id.ToString() && a.Outcome == AuditOutcome.Succeeded).Select(a => new { a.EventType, a.Detail })
                .ToListAsync();
            Assert.AreEqual(2, audit.Count(a => a.EventType == AuditEvents.OsUpdateFindingAcknowledged), "One entry per finding.");
            Assert.AreEqual(2, audit.Count(a => a.EventType == AuditEvents.OsUpdateFindingAcknowledgementEnded));
            Assert.IsTrue(audit.Any(a => a.Detail!.Contains("now in CISA KEV", StringComparison.Ordinal)));
        }

        // Installed: the next section no longer lists openssl, the findings resolve, and the sensor is OK again.
        await server.Session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.OsUpdates, Section()), CancellationToken.None);
        await AgentHarness.EventuallyAsync(async () => (await ViewAsync(server)).Pending == 0);
        await evaluator.EvaluateAsync(server.Id, CancellationToken.None);
        events = await HeartbeatAsync(server, 4);
        Assert.AreEqual(PNMTStatusCodes.LINUX_OK, events[0].Code);
        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.PostJsonAsync($"/api/v1/linux/servers/{server.Id}/os-updates/findings/acknowledge",
            new OsFindingAcknowledgeRequest([kevFinding.Id], "too late", 1))).StatusCode, "A resolved finding can't be acknowledged.");
    }
}
