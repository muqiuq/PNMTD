using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Cli;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Tests.Agents;

namespace PNMTD.Tests.Linux;

/// <summary>
/// Linux servers, PNMTD's side (docs/linux-sensor-plan.md §14, L3 and L4): heartbeats (260, 261, 461 for the clock, problems and a
/// refused config.yaml; 462 after its days), the rights from the hello, the typed sections stored once per hash (the viewer's 403 for
/// access, journal and ssh-keys), logins (stored once, 264 with the key, 464 for failures against accounts and never for invalid
/// users, not in sudoers and lockouts), the Linux differs with 263 and 463, and the fleet's Linux fields.
/// </summary>
[TestClass]
public sealed class LinuxAgentTests
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
            ["Agents:MaxMessageBytes"] = "262144",
        });
        admin = app.Client(Roles.Admin);
        hostId = await AgentHarness.CreateHostAsync(admin, "linux-tests");
    }

    /// <summary>The servers' state changes would crowd other classes' dashboards (the newest 15 changes): their host goes off.</summary>
    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            await db.Hosts.Where(h => h.Id == hostId).ExecuteUpdateAsync(u => u.SetProperty(h => h.Enabled, false));
        }
        SimulatedLinux.Override = null;
        app.Dispose();
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    private static async Task<AgentHarness.Joined> JoinAsync(string hostname) =>
        await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null, AgentPlatform.Linux)).Token, hostname, AgentPlatform.Linux);

    private static AgentRightsSummary Rights(string source, string? problem = null, bool keyRemoval = false) =>
        new(source, problem, keyRemoval, keyRemoval ? ["deploy", "www"] : [], keyRemoval ? 1 : 0, [], false, [], 0, 60, false, false);

    private static Heartbeat Beat(AgentSession session, DateTimeOffset boot, int problems = 0, string? shutdown = null) =>
        AgentHarness.Heartbeat(session, boot) with { ProblemsSinceLast = problems, LastShutdown = shutdown, LowestVolume = "/" };

    [TestMethod]
    public async Task HeartbeatsWrite260ThenARestart261AndAClockOffOrProblems461()
    {
        var joined = await JoinAsync("web-beat");
        await using var session = await AgentHarness.OpenAsync(app, joined);
        var boot = DateTimeOffset.UtcNow.AddDays(-2);
        await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, boot), CancellationToken.None);
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count == 1);
        Assert.AreEqual(PNMTStatusCodes.LINUX_OK, events[0].Code);

        // The boot time moved: 261, saying whether the last shutdown was clean.
        await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, boot.AddDays(1), shutdown: "clean shutdown at 2026-11-13 22:10"), CancellationToken.None);
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.LINUX_RESTARTED);
        StringAssert.Matches(events[0].Message, new System.Text.RegularExpressions.Regex(@"^web-beat: restarted at .* UTC; clean shutdown at 2026-11-13 22:10$"));
        await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, boot.AddDays(1).AddHours(3)), CancellationToken.None);
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId),
            e => e[0].Code == PNMTStatusCodes.LINUX_RESTARTED && e[0].Message!.EndsWith("whether the last shutdown was clean is not known", StringComparison.Ordinal));
        var restarted = boot.AddDays(1).AddHours(3);

        // The clock: 461, without Windows' Kerberos remark.
        await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, restarted) with { AgentTime = DateTimeOffset.UtcNow.AddMinutes(7).AddSeconds(5) },
            CancellationToken.None);
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.LINUX_AGENT_PROBLEM);
        StringAssert.Contains(events[0].Message, "the clock is off by 7 min");
        Assert.IsFalse(events[0].Message!.Contains("Kerberos"), events[0].Message);

        // Problems in three heartbeats in a row: 461.
        for (var i = 0; i < 2; i++)
        {
            await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, restarted, problems: 1), CancellationToken.None);
        }
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.LINUX_OK);
        await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, restarted, problems: 2), CancellationToken.None);
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.LINUX_AGENT_PROBLEM);
        Assert.AreEqual("web-beat: the agent reported problems in 3 heartbeats in a row (see the Agent tab)", events[0].Message);
        Assert.AreEqual("linux-agent", events[0].Source);
    }

    [TestMethod]
    public async Task ARefusedConfigYamlWrites461AndTheRightsAreKeptAndTheirChangesLogged()
    {
        var joined = await JoinAsync("web-rights");
        await using (var session = await AgentHarness.OpenAsync(app, joined, rights: Rights("refused", "/etc/pnmt-agent/config.yaml is writable by others")))
        {
            await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, DateTimeOffset.UtcNow.AddDays(-1)), CancellationToken.None);
            var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count == 1);
            Assert.AreEqual(PNMTStatusCodes.LINUX_AGENT_PROBLEM, events[0].Code);
            Assert.AreEqual("web-rights: /etc/pnmt-agent/config.yaml is refused (/etc/pnmt-agent/config.yaml is writable by others): it grants PNMT nothing",
                events[0].Message);
            var detail = await (await admin.GetAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}")).ReadAsync<AgentServerDetail>();
            Assert.AreEqual("refused", detail.Rights?.Source);
            Assert.IsFalse(detail.Server.KeyRemovalAllowed);
        }

        // root fixed the file and allowed removing keys: the next hello says so, the log says what changed, the heartbeat is OK again.
        await using (var session = await AgentHarness.OpenAsync(app, joined, rights: Rights("config.yaml", keyRemoval: true)))
        {
            await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, DateTimeOffset.UtcNow.AddDays(-1)), CancellationToken.None);
            await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.LINUX_OK);
        }
        // The same rights again (a restart, an update): no change logged, although jsonb gives the stored text back normalised.
        await using (var session = await AgentHarness.OpenAsync(app, joined, rights: Rights("config.yaml", keyRemoval: true)))
        {
            await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, DateTimeOffset.UtcNow.AddDays(-1)), CancellationToken.None);
            await AgentHarness.EventuallyAsync(async () =>
            {
                await using var connected = app.NewDb();
                return await connected.AgentLog.CountAsync(l => l.SensorId == joined.Grant.SensorId && l.Kind == AgentLogKind.Connected) == 3;
            });
        }
        await using var db = app.NewDb();
        var log = await db.AgentLog.AsNoTracking().Where(l => l.SensorId == joined.Grant.SensorId).OrderBy(l => l.At).ToListAsync();
        CollectionAssert.Contains(log.Select(l => l.Message).ToList(), "Rights changed on the server: key removal allowed for deploy, www (1 protected keys); scripts off.");
        Assert.AreEqual(1, log.Count(l => l.Message.StartsWith("Rights changed", StringComparison.Ordinal)), "only the real change");
        StringAssert.Contains(log.First(l => l.Kind == AgentLogKind.Connected).Message, "rights on the server: config.yaml refused (/etc/pnmt-agent/config.yaml is writable by others): everything off");
        var item = (await (await admin.GetAsync("/api/v1/agents/servers?search=web-rights&keyRemoval=true")).ReadAsync<Paged<AgentServerItem>>()).Items.Single();
        Assert.IsTrue(item.KeyRemovalAllowed);
        Assert.AreEqual("Debian GNU/Linux 12 (bookworm)", item.Distribution, "the hello's pretty name until a system section comes");
        Assert.AreEqual("6.1.0-28-amd64", item.KernelRelease);
    }

    [TestMethod]
    public async Task SectionsAreStoredOncePerHashAndServedTyped()
    {
        var created = await AgentHarness.TokenAsync(admin, hostId, "sections", AgentPlatform.Linux);
        var simulator = new AgentSimulator(new Uri("http://localhost/"), app.Client(), ct => AgentHarness.ConnectAsync(app, ct), TextWriter.Null, AgentPlatform.Linux);
        var state = await simulator.JoinAsync(created.Token, null, "sim-sections", CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var running = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        var rows = await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.AgentSections.AsNoTracking().Where(s => s.SensorId == state.SensorId).ToListAsync();
        }, r => r.Count == SectionNames.Linux.Count);
        var viewer = app.Client(Roles.Viewer);
        var user = app.Client(Roles.User);
        var url = $"/api/v1/linux/servers/{state.SensorId}/sections";

        var system = await (await viewer.GetAsync($"{url}/system")).ReadAsync<LinuxSectionView>();
        Assert.AreEqual("sim-sections", system.System?.Hostname);
        Assert.AreEqual("6.1.0-28-amd64", system.System?.Os.KernelRelease);
        Assert.AreEqual(3, system.System?.Updates?.Security);
        Assert.IsNull(system.Hardware);
        var hardware = await (await viewer.GetAsync($"{url}/hardware")).ReadAsync<LinuxSectionView>();
        CollectionAssert.Contains(hardware.Hardware!.Volumes.Select(v => v.Mount).ToList(), "/srv/data");
        var network = await (await viewer.GetAsync($"{url}/network")).ReadAsync<LinuxSectionView>();
        Assert.AreEqual(22, network.Network!.Listeners.Single(l => l.Services.Contains("ssh.service")).Port);
        var services = await (await viewer.GetAsync($"{url}/services")).ReadAsync<LinuxSectionView>();
        Assert.AreEqual(LinuxServiceClass.Unpackaged, services.Services!.Services.Single(s => s.Name == "sync-daemon.service").Class);
        var remote = await (await viewer.GetAsync($"{url}/remote-access")).ReadAsync<LinuxSectionView>();
        Assert.AreEqual("prohibit-password", remote.RemoteAccess!.Sshd!.PermitRootLogin);

        // Logins, the journal and the keys: the User role.
        foreach (var name in new[] { SectionNames.Access, SectionNames.Journal, SectionNames.SshKeys })
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync($"{url}/{name}")).StatusCode, name);
        }
        var access = await (await user.GetAsync($"{url}/access")).ReadAsync<LinuxSectionView>();
        Assert.IsTrue(access.Access!.Logging.JournalPersistent);
        var journal = await (await user.GetAsync($"{url}/journal")).ReadAsync<LinuxSectionView>();
        Assert.AreEqual(3, journal.Journal!.Entries.Count);
        var keys = await (await user.GetAsync($"{url}/ssh-keys")).ReadAsync<LinuxSectionView>();
        var rootKeys = keys.SshKeys!.Files.Single(f => f.Account == "root").Keys;
        CollectionAssert.Contains(rootKeys.Single(k => k.Fingerprint == SimulatedLinux.OldRsa.Fingerprint).Notes.ToList(), "RSA 1024 bits: refused by OpenSSH");
        CollectionAssert.Contains(rootKeys.Single(k => k.Fingerprint == SimulatedLinux.PhilippLaptop.Fingerprint).Notes.ToList(), "root's key without from=");

        // The same hash again only moves the times; unknown names, Windows' names and a Windows server are 404.
        var changedAt = rows.Single(r => r.Section == SectionNames.Hardware).ChangedAt;
        Assert.IsTrue((await (await user.PostJsonAsync($"/api/v1/agents/servers/{state.SensorId}/collect", new AgentCollectRequest([SectionNames.Hardware])))
            .ReadAsync<AgentRequestResult>()).Ok);
        var again = await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.AgentSections.AsNoTracking().SingleAsync(s => s.SensorId == state.SensorId && s.Section == SectionNames.Hardware);
        }, r => r.ReceivedAt > rows.Single(x => x.Section == SectionNames.Hardware).ReceivedAt);
        Assert.AreEqual(changedAt, again.ChangedAt);
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.GetAsync($"{url}/event-log")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.GetAsync($"{url}/nonsense")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/windows/servers/{state.SensorId}/sections/services")).StatusCode);
        var windows = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null)).Token, "SRV-NOT-LINUX");
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/linux/servers/{windows.Grant.SensorId}/sections/system")).StatusCode);

        // The fleet and the detail: the distribution's point release, the kernel, the security updates pending.
        var detail = await (await viewer.GetAsync($"/api/v1/agents/servers/{state.SensorId}")).ReadAsync<AgentServerDetail>();
        CollectionAssert.AreEquivalent(SectionNames.Linux.ToList(), detail.Sections.Select(s => s.Name).ToList());
        Assert.AreEqual("Debian 12.8", detail.Server.Distribution);
        Assert.AreEqual(3, detail.Server.SecurityUpdates);
        Assert.AreEqual("defaults", detail.Rights?.Source);
        Assert.IsTrue((await (await viewer.GetAsync("/api/v1/agents/servers?platform=Linux&securityUpdates=true&pageSize=500")).ReadAsync<Paged<AgentServerItem>>())
            .Items.Any(i => i.SensorId == state.SensorId));
        Assert.AreEqual("SSH (root: key only) · fail2ban", detail.Server.RemoteAccess);

        // The simulator's logins: root's and deploy's key logins page (264), the sudo, the file transfers and the scanners don't.
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, state.SensorId),
            e => e.Count(x => x.Code == PNMTStatusCodes.LINUX_ADMIN_LOGIN) == 2);
        CollectionAssert.Contains(events.Select(e => e.Message).ToList(), $"root: SSH from 10.0.4.17 with key {SshKeys.Short(SimulatedLinux.PhilippLaptop.Fingerprint)}");
        Assert.IsFalse(events.Any(e => e.Code == PNMTStatusCodes.LINUX_LOGIN_FAILURES), "4 wrong passwords and the invalid users are below the threshold or never count");
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task ALinuxAgentsWindowsSectionIsNotStored()
    {
        var joined = await JoinAsync("web-unknown");
        await using var session = await AgentHarness.OpenAsync(app, joined);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.EventLog, SimulatedSections.EventLog()), CancellationToken.None);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.Journal, SimulatedLinux.Journal()), CancellationToken.None);
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.AgentSections.AnyAsync(s => s.SensorId == joined.Grant.SensorId && s.Section == SectionNames.Journal);
        });
        await using var db = app.NewDb();
        Assert.IsFalse(await db.AgentSections.AnyAsync(s => s.SensorId == joined.Grant.SensorId && s.Section == SectionNames.EventLog));
        Assert.IsTrue(await db.AgentLog.AnyAsync(l => l.SensorId == joined.Grant.SensorId && l.Kind == AgentLogKind.Error && l.Message.Contains("unknown section")));
    }

    private static LoginRecord Login(long id, string user, LogonKind kind, LogonOutcome outcome = LogonOutcome.Success, string? from = "10.0.4.17",
        string? method = "publickey", string? fingerprint = null, int count = 1, string? reason = null, bool admin = false, string? target = null,
        string? tty = null, DateTimeOffset? at = null, IReadOnlyList<string>? tried = null)
    {
        var when = at ?? DateTimeOffset.UtcNow.AddSeconds(-20);
        return new LoginRecord(LinuxLogins.Channel, id, when, count > 1 ? when.AddSeconds(15) : null, count, outcome, reason, user, null, admin, kind, method, from,
            from is null ? null : 51000, tty, null, null, null, fingerprint, target, null, tried);
    }

    [TestMethod]
    public async Task LoginsAreStoredOnceAndAnAdminsSshLoginWrites264WithItsKey()
    {
        var joined = await JoinAsync("web-logins");
        await using var session = await AgentHarness.OpenAsync(app, joined);
        var key = SimulatedLinux.PhilippLaptop.Fingerprint;
        var ssh = Login(7001, "root", LogonKind.Ssh, fingerprint: key, admin: true);
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch(
        [
            ssh,
            Login(7002, "philipp", LogonKind.Sudo, from: null, method: null, admin: true, target: "root", tty: "pts/0"),
            Login(7003, "bkp", LogonKind.Sftp, from: "10.0.1.30", fingerprint: SimulatedLinux.Backup.Fingerprint, count: 6),
            Login(7004, "root", LogonKind.Console, from: null, method: "password", admin: true, tty: "tty1", at: DateTimeOffset.UtcNow.AddHours(-3)),
        ]), CancellationToken.None);
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count >= 1);
        Assert.AreEqual(PNMTStatusCodes.LINUX_ADMIN_LOGIN, events[0].Code);
        Assert.AreEqual($"root: SSH from 10.0.4.17 with key {SshKeys.Short(key)}", events[0].Message);
        StringAssert.StartsWith(events[0].Message, "root: SSH from 10.0.4.17 with key " + key[..16]);
        Assert.AreEqual(1, events.Count, "sudo and file transfers never page; a console login of three hours ago is a backlog");

        // The same record again, with its logoff: updated, not added; no second 264.
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch([ssh with { LoggedOffAt = DateTimeOffset.UtcNow }]), CancellationToken.None);
        var url = $"/api/v1/agents/servers/{joined.Grant.SensorId}/logins";
        var user = app.Client(Roles.User);
        var logins = await AgentHarness.EventuallyAsync(async () => await (await user.GetAsync(url)).ReadAsync<Paged<AgentLoginItem>>(),
            p => p.Items.Any(l => l.LoggedOffAt is not null));
        Assert.AreEqual(4, logins.TotalCount);
        Assert.AreEqual(1, (await AgentHarness.EventsAsync(app, joined.Grant.SensorId)).Count);

        // The filters: a kind of Linux's own, and the key (with or without "SHA256:").
        var sudo = await (await user.GetAsync($"{url}?kind=Sudo")).ReadAsync<Paged<AgentLoginItem>>();
        Assert.AreEqual("root", sudo.Items.Single().TargetUser);
        var sftp = await (await user.GetAsync($"{url}?kind=Sftp")).ReadAsync<Paged<AgentLoginItem>>();
        Assert.AreEqual(6, sftp.Items.Single().Count);
        var withKey = await (await user.GetAsync($"{url}?keyFingerprint={Uri.EscapeDataString(key[7..])}")).ReadAsync<Paged<AgentLoginItem>>();
        Assert.AreEqual(1, withKey.TotalCount);
        Assert.AreEqual(key, withKey.Items.Single().KeyFingerprint);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.Viewer).GetAsync(url)).StatusCode);

        // The other ways in, worded; a registered key's name comes with the key manager (L5).
        var none = new Dictionary<string, SshKeyName>();
        Assert.AreEqual("root: console (tty1, password)", AgentAccessService.LinuxAdminMessage(new AgentLoginEntity
        {
            User = "root", Kind = LogonKind.Console, Workstation = "tty1", AuthPackage = "password",
        }, none));
        Assert.AreEqual("root: SSH from 10.0.4.17 (password)", AgentAccessService.LinuxAdminMessage(new AgentLoginEntity
        {
            User = "root", Kind = LogonKind.Ssh, SourceAddress = "10.0.4.17", AuthPackage = "password",
        }, none));
        Assert.AreEqual($"root: SSH from 10.0.4.17 with key Philipp, laptop (philipp)", AgentAccessService.LinuxAdminMessage(new AgentLoginEntity
        {
            User = "root", Kind = LogonKind.Ssh, SourceAddress = "10.0.4.17", AuthPackage = "publickey", KeyFingerprint = key,
        }, new Dictionary<string, SshKeyName> { [key] = new("Philipp, laptop", "philipp") }));
    }

    [TestMethod]
    public async Task FailuresAgainstAnAccountWrite464AndInvalidUsersNever()
    {
        var joined = await JoinAsync("web-failures");
        await (await app.Client(Roles.User).PutJsonAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/settings",
            new AgentServerSettings(true, true, 5, false))).ReadAsync<AgentServerDetail>();
        await using var session = await AgentHarness.OpenAsync(app, joined);

        // The internet's scanners: hundreds of tries for accounts that don't exist, from one source. Never 464.
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch(
        [
            Login(8001, LinuxLogins.InvalidUser, LogonKind.Ssh, LogonOutcome.Failure, "198.51.100.23", "password", count: 300, reason: LinuxLogins.InvalidUser,
                tried: ["admin", "oracle", "test"]),
            Login(8002, LinuxLogins.InvalidUser, LogonKind.Ssh, LogonOutcome.Failure, "198.51.100.24", "password", count: 40, reason: LinuxLogins.InvalidUser),
        ]), CancellationToken.None);
        await Task.Delay(400);
        Assert.AreEqual(0, (await AgentHarness.EventsAsync(app, joined.Grant.SensorId)).Count, "invalid users never page");

        // Wrong passwords for root: 3, then 2 more from the same source reach the threshold of 5.
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch([Login(8003, "root", LogonKind.Ssh, LogonOutcome.Failure, "203.0.113.9", "password", count: 3,
            reason: LinuxLogins.WrongPassword, admin: true)]), CancellationToken.None);
        await Task.Delay(300);
        Assert.AreEqual(0, (await AgentHarness.EventsAsync(app, joined.Grant.SensorId)).Count, "below the threshold");
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch([Login(8004, "deploy", LogonKind.Ssh, LogonOutcome.Failure, "203.0.113.9", "password", count: 2,
            reason: LinuxLogins.WrongPassword)]), CancellationToken.None);
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count >= 1);
        Assert.AreEqual(PNMTStatusCodes.LINUX_LOGIN_FAILURES, events[0].Code);
        Assert.AreEqual("Failed logins from 203.0.113.9: at least 5 in 10 min (users: deploy, root)", events[0].Message);

        // sudo has no source: for one user. A refusal for sudoers and a lockout page on their own.
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch(
        [
            Login(8005, "philipp", LogonKind.Sudo, LogonOutcome.Failure, null, null, count: 5, reason: LinuxLogins.WrongPassword, tty: "pts/0"),
            Login(8006, "www", LogonKind.Sudo, LogonOutcome.Failure, null, null, reason: LinuxLogins.NotInSudoers, tty: "pts/2"),
            Login(8007, "jdoe", LogonKind.Ssh, LogonOutcome.Failure, "203.0.113.50", "password", reason: LinuxLogins.Lockout),
        ]), CancellationToken.None);
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Any(x => x.Message == "jdoe locked (from 203.0.113.50)"));
        var messages = events.Where(e => e.Code == PNMTStatusCodes.LINUX_LOGIN_FAILURES).Select(e => e.Message!).ToList();
        CollectionAssert.Contains(messages, "Failed logins for philipp: at least 5 in 10 min (Sudo)");
        CollectionAssert.Contains(messages, "www: not in sudoers (sudo on pts/2)");
        Assert.IsFalse(messages.Any(m => m.Contains("198.51.100") || m.Contains(LinuxLogins.InvalidUser)), string.Join("\n", messages));
        var stored = await (await app.Client(Roles.User).GetAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/logins?outcome=Failure&pageSize=50"))
            .ReadAsync<Paged<AgentLoginItem>>();
        CollectionAssert.AreEqual(new[] { "admin", "oracle", "test" }, stored.Items.Single(l => l.SourceAddress == "198.51.100.23").TriedUsers!.ToList(), "stored, with the names tried");
    }

    [TestMethod]
    public async Task SecurityUpdatesWrite462AfterTheirDaysWithTheSwitchOnAndNoneWithItOff()
    {
        var joined = await JoinAsync("web-updates");
        var settingsUrl = $"/api/v1/agents/servers/{joined.Grant.SensorId}/settings";
        var user = app.Client(Roles.User);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await user.PutJsonAsync(settingsUrl, new AgentServerSettings(true, true, 10, false, true, true, 0))).StatusCode);
        var detail = await (await user.PutJsonAsync(settingsUrl, new AgentServerSettings(true, true, 10, false, true, true, 7))).ReadAsync<AgentServerDetail>();
        Assert.IsTrue(detail.Settings.SecurityUpdateEvents);
        Assert.AreEqual(7, detail.Settings.SecurityUpdateDays);
        await using var session = await AgentHarness.OpenAsync(app, joined);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.System, SimulatedLinux.System("web-updates")), CancellationToken.None);
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.Agents.AnyAsync(a => a.SensorId == joined.Grant.SensorId && a.SecurityUpdatesSince != null && a.SecurityUpdates == 3);
        });
        var boot = DateTimeOffset.UtcNow.AddDays(-1);
        await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, boot), CancellationToken.None);
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e.Count == 1);
        Assert.AreEqual(PNMTStatusCodes.LINUX_OK, events[0].Code, "pending for less than 7 days");

        var since = DateTime.UtcNow.AddDays(-9);
        await using (var db = app.NewDb())
        {
            await db.Agents.Where(a => a.SensorId == joined.Grant.SensorId).ExecuteUpdateAsync(u => u.SetProperty(a => a.SecurityUpdatesSince, since));
        }
        await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, boot), CancellationToken.None);
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.LINUX_SECURITY_UPDATES);
        Assert.AreEqual($"web-updates: 3 security updates pending since {since:yyyy-MM-dd}, more than 7 days: libssl3, openssl, linux-image-6.1.0-29-amd64", events[0].Message);

        // Stable while it lasts: the same message collapses by the dedupe.
        for (var i = 0; i < 3; i++)
        {
            await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, boot), CancellationToken.None);
        }
        await Task.Delay(400);
        Assert.AreEqual(2, (await AgentHarness.EventsAsync(app, joined.Grant.SensorId)).Count(e => e.Code == PNMTStatusCodes.LINUX_SECURITY_UPDATES));

        // The switch off: none.
        await (await user.PutJsonAsync(settingsUrl, new AgentServerSettings(true, true, 10, false, true, false, 7))).ReadAsync<AgentServerDetail>();
        await session.SendAsync(AgentMessageTypes.Heartbeat, Beat(session, boot), CancellationToken.None);
        await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId), e => e[0].Code == PNMTStatusCodes.LINUX_OK);

        // Installed: no longer pending.
        var system = SimulatedLinux.System("web-updates");
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.System, system with
        {
            Updates = system.Updates! with { Security = 0, SecurityPackages = [], Upgradable = 11 },
        }), CancellationToken.None);
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.Agents.AnyAsync(a => a.SensorId == joined.Grant.SensorId && a.SecurityUpdatesSince == null && a.SecurityUpdates == 0);
        });
    }

    [TestMethod]
    public async Task AChangedSectionWritesReadableChangesAnd263AndACriticalEntry463()
    {
        var joined = await JoinAsync("web-changes");
        await (await app.Client(Roles.User).PutJsonAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/settings",
            new AgentServerSettings(true, true, 10, true))).ReadAsync<AgentServerDetail>();
        await using var session = await AgentHarness.OpenAsync(app, joined);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.Services, SimulatedLinux.Services()), CancellationToken.None);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.RemoteAccess, SimulatedLinux.RemoteAccess()), CancellationToken.None);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.Journal, SimulatedLinux.Journal()), CancellationToken.None);
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.AgentSections.CountAsync(s => s.SensorId == joined.Grant.SensorId) == 3;
        });

        var services = SimulatedLinux.Services();
        var added = services with
        {
            Services = [.. services.Services, new LinuxServiceEntry("teamviewerd.service", "TeamViewer remote control daemon", "loaded", "active", "running", "enabled",
                "root", 4410, "/opt/teamviewer/tv_bin/teamviewerd", "/etc/systemd/system/teamviewerd.service", "admin", "teamviewer", "TeamViewer", null,
                LinuxServiceClass.ThirdParty, false, false)],
        };
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.Services, added), CancellationToken.None);
        var remote = SimulatedLinux.RemoteAccess();
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.RemoteAccess, remote with
        {
            Sshd = remote.Sshd! with { PasswordAuthentication = true },
        }), CancellationToken.None);
        var critical = new JournalSection([new JournalEntry(DateTimeOffset.UtcNow, 2, "crit", "smartd.service", "smartd", 611, "b1", "Device: /dev/sda, FAILED SMART self-check. BACK UP DATA NOW!",
            "s=b1;i=99"), .. SimulatedLinux.Journal().Entries], true);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.Journal, critical), CancellationToken.None);

        var changes = await AgentHarness.EventuallyAsync(async () =>
            await (await app.Client(Roles.Viewer).GetAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/changes")).ReadAsync<Paged<AgentChangeItem>>(),
            p => p.TotalCount >= 2);
        CollectionAssert.IsSubsetOf(new[] { "Service teamviewerd.service added (third-party, enabled, running)", "sshd: password authentication turned on" },
            changes.Items.Select(c => c.Summary).ToList());
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId),
            e => e.Count(x => x.Code == PNMTStatusCodes.LINUX_CHANGED) == 2 && e.Any(x => x.Code == PNMTStatusCodes.LINUX_CRITICAL_ENTRY));
        Assert.AreEqual("smartd.service (crit): Device: /dev/sda, FAILED SMART self-check. BACK UP DATA NOW!",
            events.Single(e => e.Code == PNMTStatusCodes.LINUX_CRITICAL_ENTRY).Message);
        Assert.IsTrue(events.All(e => e.Source == "linux-agent"));
    }

    [TestMethod]
    public void TheLinuxDiffersNameWhatChanged()
    {
        static string Json<T>(T value) => AgentJson.Serialize(value);
        List<string> Lines(string section, object before, object after) =>
            PNMTD.Linux.LinuxDiffers.Compare(section, Json(before), Json(after)).Select(c => c.Summary).ToList();

        var system = SimulatedLinux.System("web-01");
        var updated = system with
        {
            Os = system.Os with { KernelRelease = "6.1.0-29-amd64", DebianVersion = "12.9", Distribution = "Debian 12.9" },
            Administrators = [.. system.Administrators, new LinuxAdmin("jdoe", "group sudo")],
            Accounts = system.Accounts.Select(a => a.Name == "deploy" ? a with { PasswordSet = true } : a).ToList(),
            Security = system.Security! with { AppArmor = system.Security.AppArmor! with { Enabled = false }, Firewall = system.Security.Firewall! with { InputPolicy = "accept" } },
        };
        var lines = Lines(SectionNames.System, system, updated);
        CollectionAssert.Contains(lines, "Kernel updated: 6.1.0-28-amd64 → 6.1.0-29-amd64");
        CollectionAssert.Contains(lines, "Point release: Debian 12.8 → Debian 12.9");
        CollectionAssert.Contains(lines, "jdoe added to sudo");
        CollectionAssert.Contains(lines, "deploy can now log in with a password");
        CollectionAssert.Contains(lines, "AppArmor turned off");
        CollectionAssert.Contains(lines, "Firewall input policy: drop → accept");
        Assert.IsTrue(PNMTD.Linux.LinuxDiffers.Compare(SectionNames.System, Json(system), Json(updated)).All(c => c.Notable));

        var services = SimulatedLinux.Services();
        var cron = services with { Cron = [.. services.Cron, new CronEntry("/etc/cron.d/miner", "root", "*/5 * * * *", "/tmp/.x/run", null, "ffff0000aaaa1111")] };
        CollectionAssert.Contains(Lines(SectionNames.Services, services, cron), "Cron entry added for root: */5 * * * * /tmp/.x/run (/etc/cron.d/miner)");
        // An on-demand service without User= is root's whether it runs or not (found in the lab: systemd-timedated flipped).
        var stopped = services with { Services = services.Services.Select(e => e.User == "root" ? e with { User = null } : e).ToList() };
        Assert.AreEqual(0, Lines(SectionNames.Services, services, stopped).Count(l => l.Contains("runs as", StringComparison.Ordinal)));

        var access = SimulatedLinux.Access();
        var volatileJournal = access with { Logging = access.Logging with { JournalPersistent = false, SshdLogLevel = "VERBOSE" } };
        lines = Lines(SectionNames.Access, access, volatileJournal);
        CollectionAssert.Contains(lines, "The journal is no longer persistent");
        CollectionAssert.Contains(lines, "sshd's LogLevel: INFO → VERBOSE");

        // The shared records, compared as on Windows, with Linux's word for the proxy.
        var network = SimulatedLinux.Network();
        var moved = network with { Adapters = network.Adapters.Select(a => a.Name == "ens18" ? a with { Gateways = ["10.0.2.254"] } : a).ToList(), Proxy = "http://proxy:3128" };
        lines = Lines(SectionNames.Network, network, moved);
        CollectionAssert.Contains(lines, "Gateway on ens18: 10.0.2.1 → 10.0.2.254");
        CollectionAssert.Contains(lines, "Proxy: direct → http://proxy:3128");
        // The ssh-keys section's own lines (L5): a key file's mode and owner, CA keys. Whether PNMT may remove a key there is no change.
        Assert.AreEqual(0, Lines(SectionNames.SshKeys, SimulatedLinux.SshKeys("web-01"), SimulatedLinux.SshKeys("web-01", keyRemoval: true)).Count);
        var keys = SimulatedLinux.SshKeys("web-01");
        var loose = keys with
        {
            Files = keys.Files.Select(f => f.Account == "root" ? f with { Mode = "0664", Problems = ["writable by group or others"] } : f).ToList(),
            CaKeys = [new CaKey("/etc/ssh/ca.pub", "ssh-ed25519", 256, SimulatedLinux.ClusterNode.Fingerprint, "ca@corp")],
        };
        lines = Lines(SectionNames.SshKeys, keys, loose);
        CollectionAssert.Contains(lines, "root's /root/.ssh/authorized_keys: mode 0600 → 0664");
        CollectionAssert.Contains(lines, "root's /root/.ssh/authorized_keys: writable by group or others");
        CollectionAssert.Contains(lines, $"CA key added: ssh-ed25519 {SshKeys.Short(SimulatedLinux.ClusterNode.Fingerprint)} 'ca@corp' (/etc/ssh/ca.pub): certificates it signs get in");
    }
}
