using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Cli;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Tests.Agents;
using static PNMTD.Tests.KeyManager.SshKeyTestKit;

namespace PNMTD.Tests.KeyManager;

/// <summary>
/// Removing keys (docs/linux-sensor-plan.md §6.5, §14, L6), through the simulator: refused without keys.remove.v1 (and nothing sent);
/// the dialog's preview with its refusals and warnings; sent, applied, recorded, audited and worded once "by PNMT"; pending for an
/// offline server and sent when it connects; expired after its days; and a user's offboarding, one removal per server and account,
/// with the agent's answer after its ack.
/// </summary>
[TestClass]
public sealed class SshKeyRemovalTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;

    private const string DeployFile = "/home/deploy/.ssh/authorized_keys";

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Agents:JoinRateLimitPerMinute"] = "1000",
            ["Agents:HelloTimeoutSeconds"] = "2",
        });
        admin = app.Client(Roles.Admin);
        hostId = await AgentHarness.CreateHostAsync(admin, "key-removal-tests");
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

    private sealed record Running(AgentSimulator Simulator, SimulatedAgentState State, CancellationTokenSource Stop, Task Task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            await Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>A simulated Linux server, connected, its ssh-keys section in the key manager.</summary>
    private static async Task<Running> SimulateAsync(bool allowKeyRemoval, SimulatedAgentState? state = null, string hostname = "sim-keys")
    {
        var simulator = new AgentSimulator(new Uri("http://localhost/"), app.Client(), ct => AgentHarness.ConnectAsync(app, ct), TextWriter.Null, AgentPlatform.Linux)
        {
            AllowKeyRemoval = allowKeyRemoval,
        };
        // The sensor is named as the server, so the audit's lines are told apart.
        state ??= await simulator.JoinAsync((await AgentHarness.TokenAsync(admin, hostId, hostname, AgentPlatform.Linux)).Token, null, hostname, CancellationToken.None);
        var stop = new CancellationTokenSource();
        var task = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        var hub = app.Services.GetRequiredService<AgentHub>();
        await AgentHarness.EventuallyAsync(() => Task.FromResult(hub.Connection(state.SensorId)?.Capabilities.Contains(AgentCapabilities.KeysRemove) == allowKeyRemoval
            && hub.Connection(state.SensorId)!.Capabilities.Contains(AgentCapabilities.Keys)));
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.SshKeyAuthorizations.AnyAsync(a => a.SensorId == state.SensorId);
        });
        return new Running(simulator, state, stop, task);
    }

    private static async Task<List<SshKeyRemovalEntity>> RemovalsAsync(Guid sensorId)
    {
        await using var db = app.NewDb();
        return await db.SshKeyRemovals.AsNoTracking().Where(r => r.SensorId == sensorId).OrderBy(r => r.RequestedAt).ToListAsync();
    }

    [TestMethod]
    public async Task WithoutKeysRemoveTheServerRefusesAndNothingIsSent()
    {
        await using var running = await SimulateAsync(allowKeyRemoval: false, hostname: "sim-no-removal");
        var item = new SshKeyRemovalTarget(running.State.SensorId, "deploy", DeployFile, SimulatedLinux.CiRunner.Fingerprint);
        var preview = await (await admin.PostJsonAsync("/api/v1/ssh-keys/remove/preview", new SshKeyRemovalPreviewRequest([item]))).ReadAsync<SshKeyRemovalPreview>();
        Assert.AreEqual((0, 1, 1), (preview.Allowed, preview.Refused, preview.Confirm));
        StringAssert.Contains(preview.Items.Single().Refused, "\"ssh_keys: remove: true\" in /etc/pnmt-agent/config.yaml");

        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/ssh-keys/remove", new SshKeyRemoveRequest([item], SshKeyRemovalReason.Server, 2))).StatusCode,
            "confirm is the number of keys");
        var result = await (await admin.PostJsonAsync("/api/v1/ssh-keys/remove", new SshKeyRemoveRequest([item], SshKeyRemovalReason.Server, 1))).ReadAsync<SshKeyRemoveResult>();
        Assert.AreEqual((0, 0, 1), (result.Sent, result.Pending, result.Refused));
        Assert.AreEqual(SshKeyRemovalState.Refused, result.Items.Single().State);
        CollectionAssert.DoesNotContain(running.Simulator.Requests, AgentMessageTypes.RemoveSshKey);
        Assert.AreEqual(SshKeyRemovalState.Refused, (await RemovalsAsync(running.State.SensorId)).Single().State, "recorded, as refused");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.User).PostJsonAsync("/api/v1/ssh-keys/remove/preview", new SshKeyRemovalPreviewRequest([item]))).StatusCode);
    }

    [TestMethod]
    public async Task ARemovalIsSentAppliedRecordedAndAudited()
    {
        await using var running = await SimulateAsync(allowKeyRemoval: true, hostname: "sim-removal");
        var sensor = running.State.SensorId;
        var laptop = new SshKeyRemovalTarget(sensor, "deploy", DeployFile, SimulatedLinux.PhilippLaptop.Fingerprint);
        var rootKey = new SshKeyRemovalTarget(sensor, "root", "/root/.ssh/authorized_keys", SimulatedLinux.PhilippLaptop.Fingerprint);
        var ci = new SshKeyRemovalTarget(sensor, "deploy", DeployFile, SimulatedLinux.CiRunner.Fingerprint);

        // The dialog: root's file isn't removable here (only deploy and www); removing both of deploy's keys leaves it none.
        var preview = await (await admin.PostJsonAsync("/api/v1/ssh-keys/remove/preview", new SshKeyRemovalPreviewRequest([laptop, rootKey, ci])))
            .ReadAsync<SshKeyRemovalPreview>();
        Assert.AreEqual((2, 1), (preview.Allowed, preview.Refused));
        StringAssert.Contains(preview.Items.Single(i => i.Item.Account == "root").Refused, "only for deploy, www");
        CollectionAssert.Contains(preview.Items.Single(i => i.Item == laptop).Warnings.ToList(), "deploy would have no key left on sim-removal.");
        preview = await (await admin.PostJsonAsync("/api/v1/ssh-keys/remove/preview", new SshKeyRemovalPreviewRequest([laptop]))).ReadAsync<SshKeyRemovalPreview>();
        Assert.AreEqual((1, 1), (preview.Items.Single().KeysLeft, preview.Allowed));
        Assert.AreEqual(0, preview.Items.Single().Warnings.Count, string.Join("; ", preview.Items.Single().Warnings));

        // Sent to the connected agent, which answers its key-removal before the ack: removed, with the backup.
        var result = await (await admin.PostJsonAsync("/api/v1/ssh-keys/remove", new SshKeyRemoveRequest([laptop], SshKeyRemovalReason.Server, 1))).ReadAsync<SshKeyRemoveResult>();
        Assert.AreEqual((1, 0, 0), (result.Sent, result.Pending, result.Refused));
        CollectionAssert.Contains(running.Simulator.Requests, AgentMessageTypes.RemoveSshKey);
        var removal = await AgentHarness.EventuallyAsync(async () => (await RemovalsAsync(sensor)).Single(), r => r.State == SshKeyRemovalState.Removed);
        Assert.AreEqual(1, removal.LinesRemoved);
        StringAssert.StartsWith(removal.Backup, "/var/lib/pnmt-agent/state/key-backups/deploy/");
        Assert.AreEqual("test-admin", removal.RequestedByName);
        Assert.AreEqual($"unregistered key ssh-ed25519 {SshKeys.Short(SimulatedLinux.PhilippLaptop.Fingerprint)} 'philipp@laptop'", removal.KeyName);

        // The agent sent its ssh-keys section again: the key is gone from deploy's file, worded once, "by PNMT".
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return !await db.SshKeyAuthorizations.AnyAsync(a => a.SensorId == sensor && a.Account == "deploy" && a.Fingerprint == SimulatedLinux.PhilippLaptop.Fingerprint);
        });
        var line = $"unregistered key ssh-ed25519 {SshKeys.Short(SimulatedLinux.PhilippLaptop.Fingerprint)} 'philipp@laptop' removed from deploy's authorized_keys by PNMT (test-admin)";
        await using (var db = app.NewDb())
        {
            var changes = await db.AgentChanges.AsNoTracking().Where(c => c.SensorId == sensor && c.Section == SectionNames.SshKeys).Select(c => c.Summary).ToListAsync();
            CollectionAssert.Contains(changes, line);
            Assert.IsFalse(changes.Any(c => c.Contains("outside PNMT")), string.Join("\n", changes));
            var log = await db.AgentLog.AsNoTracking().Where(l => l.SensorId == sensor && l.Kind == AgentLogKind.Request).Select(l => l.Message).ToListAsync();
            Assert.IsTrue(log.Any(l => l.StartsWith("Key removal: ") && l.Contains("removed (1 line, backup /var/lib/pnmt-agent/state/key-backups/deploy/")), string.Join("\n", log));
            var audit = await db.AuditLog.AsNoTracking().Where(l => l.EventType == AuditEvents.SshKeyRemoved && l.Detail!.Contains(removal.ServerName)).Select(l => l.Detail!)
                .ToListAsync();
            Assert.AreEqual(2, audit.Count, "asked for, and done: " + string.Join("\n", audit));
        }
        var events = await AgentHarness.EventsAsync(app, sensor);
        Assert.IsTrue(events.Any(e => e.Code == PNMTStatusCodes.LINUX_CHANGED && e.Message == line), string.Join("\n", events.Select(e => e.Message)));

        // The history; the key is gone from that file, so another removal there is refused by PNMT.
        var history = await (await admin.GetAsync($"/api/v1/ssh-key-removals?sensorId={sensor}&fingerprint={Uri.EscapeDataString(SimulatedLinux.PhilippLaptop.Fingerprint)}"))
            .ReadAsync<Paged<SshKeyRemovalItem>>();
        Assert.AreEqual((SshKeyRemovalState.Removed, sensor), (history.Items.Single().State, history.Items.Single().Server?.Id));
        Assert.AreEqual(1, (await (await admin.GetAsync($"/api/v1/ssh-key-removals?batchId={result.BatchId}&state=Removed")).ReadAsync<Paged<SshKeyRemovalItem>>()).TotalCount);
        StringAssert.Contains((await (await admin.PostJsonAsync("/api/v1/ssh-keys/remove/preview", new SshKeyRemovalPreviewRequest([laptop]))).ReadAsync<SshKeyRemovalPreview>())
            .Items.Single().Refused, "doesn't have this key");
    }

    [TestMethod]
    public async Task ARemovalForAnOfflineServerIsPendingSentWhenItConnectsAndExpiresAfterItsDays()
    {
        SimulatedAgentState state;
        await using (var first = await SimulateAsync(allowKeyRemoval: true, hostname: "sim-offline"))
        {
            state = first.State;
        }
        var hub = app.Services.GetRequiredService<AgentHub>();
        await AgentHarness.EventuallyAsync(() => Task.FromResult(!hub.IsConnected(state.SensorId)));
        var ci = new SshKeyRemovalTarget(state.SensorId, "deploy", DeployFile, SimulatedLinux.CiRunner.Fingerprint);
        var preview = await (await admin.PostJsonAsync("/api/v1/ssh-keys/remove/preview", new SshKeyRemovalPreviewRequest([ci]))).ReadAsync<SshKeyRemovalPreview>();
        CollectionAssert.Contains(preview.Items.Single().Warnings.ToList(), "sim-offline is offline: the removal waits until it connects, at most 7 days.");
        var result = await (await admin.PostJsonAsync("/api/v1/ssh-keys/remove", new SshKeyRemoveRequest([ci], SshKeyRemovalReason.Unregistered, 1))).ReadAsync<SshKeyRemoveResult>();
        Assert.AreEqual((0, 1, 0), (result.Sent, result.Pending, result.Refused));

        // It connects: the removal goes, and is applied.
        await using (var again = await SimulateAsync(allowKeyRemoval: true, state))
        {
            var removal = await AgentHarness.EventuallyAsync(async () => (await RemovalsAsync(state.SensorId)).Single(), r => r.State == SshKeyRemovalState.Removed);
            Assert.IsNotNull(removal.SentAt);
            CollectionAssert.Contains(again.Simulator.Requests, AgentMessageTypes.RemoveSshKey);
        }

        // Another one, while it is away: after Agents__KeyRemovalPendingDays it expires, and isn't sent any more.
        await AgentHarness.EventuallyAsync(() => Task.FromResult(!hub.IsConnected(state.SensorId)));
        var laptop = new SshKeyRemovalTarget(state.SensorId, "deploy", DeployFile, SimulatedLinux.PhilippLaptop.Fingerprint);
        var pending = await (await admin.PostJsonAsync("/api/v1/ssh-keys/remove", new SshKeyRemoveRequest([laptop], SshKeyRemovalReason.Key, 1))).ReadAsync<SshKeyRemoveResult>();
        Assert.AreEqual(SshKeyRemovalState.Pending, pending.Items.Single().State);
        var id = pending.Items.Single().Id;
        await using (var db = app.NewDb())
        {
            await db.SshKeyRemovals.Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.RequestedAt, DateTime.UtcNow.AddDays(-7).AddMinutes(-1)));
        }
        await app.Services.GetRequiredService<AgentMaintenanceTask>().RunAsync(CancellationToken.None);
        var expired = (await RemovalsAsync(state.SensorId)).Single(r => r.Id == id);
        Assert.AreEqual(SshKeyRemovalState.Expired, expired.State);
        Assert.AreEqual("The server didn't connect within 7 days: nothing was removed.", expired.Message);
        await using (var third = await SimulateAsync(allowKeyRemoval: true, state))
        {
            await Task.Delay(500);
            CollectionAssert.DoesNotContain(third.Simulator.Requests, AgentMessageTypes.RemoveSshKey);
        }
    }

    /// <summary>An agent that removes keys as asked, answering each key-removal after its ack.</summary>
    private sealed class RemovingHandler : IAgentSessionHandler
    {
        public ConcurrentQueue<RemoveSshKeyRequest> Removals { get; } = new();

        public Task OnConfigAsync(AgentConfig config, CancellationToken ct) => Task.CompletedTask;

        public Task<Ack> OnRequestAsync(AgentSession session, SignedContent request, CancellationToken ct)
        {
            if (request.Type == AgentMessageTypes.RemoveSshKey)
            {
                var removal = AgentJson.Deserialize<RemoveSshKeyRequest>(request.Body)!;
                Removals.Enqueue(removal);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    await session.SendAsync(AgentMessageTypes.KeyRemoval, new SshKeyRemovalResult(request.RequestId, removal.RemovalId, SshKeyRemovalOutcome.Removed, 1,
                        $"/var/lib/pnmt-agent/state/key-backups/{removal.Account}/x-authorized_keys", "beef", null), CancellationToken.None);
                });
            }
            return Task.FromResult(new Ack(request.RequestId, true));
        }

        public void OnIgnored(string type, string reason)
        {
        }
    }

    [TestMethod]
    public async Task OffboardingRetiresAUsersKeysAndRemovesThemFromEveryServerAndAccount()
    {
        var created = await (await admin.PostJsonAsync("/api/v1/users", new CreateUserRequest { Username = $"offboarded{Random.Shared.Next(100_000)}", Role = Roles.User }))
            .ReadAsync<CreatedUser>();
        var user = created.User;
        var key = NewKey("offboarded@laptop");
        var other = NewKey("someone-else@laptop");
        var registered = (await (await admin.PostJsonAsync("/api/v1/ssh-keys", new SshKeyRegisterRequest(key.Line, "Offboarded, laptop", null, user.Id, null, SshKeyKind.Person, null)))
            .ReadAsync<List<SshKeyItem>>()).Single();

        var joined = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null, AgentPlatform.Linux)).Token, "offboard-01", AgentPlatform.Linux);
        await using var session = await AgentHarness.OpenAsync(app, joined, rights: RemovalRights(), capabilities: LinuxCapabilities(true));
        var handler = new RemovingHandler();
        using var stop = new CancellationTokenSource();
        var receiving = session.RunAsync(handler, stop.Token);
        await SendKeysAsync(app, session, joined.Grant.SensorId, Section(Root(key, other), Deploy(key)));

        // Deactivated: the strip offers offboarding while the keys are authorized.
        await admin.PutJsonAsync($"/api/v1/users/{user.Id}", new UpdateUserRequest { DisplayName = "Offboarded", Role = Roles.User, IsActive = false });
        var summary = await (await admin.GetAsync("/api/v1/ssh-keys/summary")).ReadAsync<SshKeySummary>();
        Assert.AreEqual((1, 2), (summary.DeactivatedUsers.Single(d => d.User.Id == user.Id).Keys, summary.DeactivatedUsers.Single(d => d.User.Id == user.Id).Authorizations));

        // The preview: root and deploy on the one server; root would keep the other key.
        var preview = await (await admin.PostJsonAsync($"/api/v1/ssh-keys/users/{user.Id}/removal-items", new { })).ReadAsync<SshKeyRemovalPreview>();
        CollectionAssert.AreEquivalent(new[] { "root", "deploy" }, preview.Items.Select(i => i.Item.Account).ToList());
        Assert.AreEqual(2, preview.Allowed);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync($"/api/v1/ssh-keys/users/{user.Id}/offboard", new SshKeyOffboardRequest(1))).StatusCode);
        var offboarded = await (await admin.PostJsonAsync($"/api/v1/ssh-keys/users/{user.Id}/offboard", new SshKeyOffboardRequest(2))).ReadAsync<SshKeyOffboardResult>();
        Assert.AreEqual(1, offboarded.Retired);
        Assert.AreEqual((2, 0, 0), (offboarded.Removal.Sent, offboarded.Removal.Pending, offboarded.Removal.Refused));
        Assert.IsTrue(offboarded.Removal.Items.All(i => i.Reason == SshKeyRemovalReason.Offboarding));
        CollectionAssert.AreEquivalent(new[] { "root", "deploy" }, handler.Removals.Select(r => r.Account).ToList(), "one removal per server and account");
        Assert.IsTrue(handler.Removals.All(r => r.Fingerprint == key.Fingerprint));

        // The results came after the acks: recorded all the same.
        await AgentHarness.EventuallyAsync(async () => (await RemovalsAsync(joined.Grant.SensorId)).Count(r => r.State == SshKeyRemovalState.Removed) == 2);
        Assert.AreEqual(SshKeyState.Retired, (await (await admin.GetAsync($"/api/v1/ssh-keys/{registered.Id}")).ReadAsync<SshKeyDetail>()).Key.State);
        stop.Cancel();
        await receiving.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
    }
}
