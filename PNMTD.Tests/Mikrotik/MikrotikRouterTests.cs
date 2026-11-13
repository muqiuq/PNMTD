using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Tests.Mikrotik;

/// <summary>
/// MikroTik routers against a simulated router (docs/mikrotik-plan.md §14): the sensor with its
/// router, the join script, and the poller's states and events, replaying the output M0 recorded.
/// </summary>
[TestClass]
public sealed class MikrotikRouterTests
{
    private static TestApp app = null!;
    private static FakeConnector routers = null!;
    private static HttpClient admin = null!;
    private static HttpClient user = null!;
    private static Guid hostId;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        routers = new FakeConnector();
        app = new TestApp(services: s => s.Replace(ServiceDescriptor.Singleton<IRouterConnector>(routers)));
        admin = app.Client(Roles.Admin);
        user = app.Client(Roles.User);
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"mikrotik-tests-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        hostId = host.Id;
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    private static async Task<SensorDetail> CreateAsync(string address, HttpClient? client = null, MikrotikRouterRequest? request = null, int grace = 600)
    {
        var response = await (client ?? user).PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = hostId,
            Type = SensorType.MIKROTIK,
            Name = $"router {address}",
            Interval = 300,
            GracePeriod = grace,
            Mikrotik = request ?? new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = address, SourceAddress = "192.0.2.10" },
        });
        return await response.ReadAsync<SensorDetail>();
    }

    private static Task<RouterPollOutcome> PollAsync(Guid id) =>
        app.Services.GetRequiredService<RouterPollService>().PollAsync(id, wait: true, CancellationToken.None);

    private static async Task<List<EventEntity>> EventsAsync(Guid id)
    {
        await using var db = app.NewDb();
        return await db.Events.Where(e => e.SensorId == id).OrderBy(e => e.Created).ToListAsync();
    }

    private static async Task<MikrotikRouterEntity> RouterAsync(Guid id)
    {
        await using var db = app.NewDb();
        return await db.MikrotikRouters.SingleAsync(r => r.SensorId == id);
    }

    private static async Task SetLatestAsync(string channel, string version)
    {
        await app.Services.GetRequiredService<MikrotikKeyStore>().GetAsync(CancellationToken.None);
        await using var db = app.NewDb();
        var settings = await db.MikrotikSettings.SingleAsync();
        settings.LatestVersions = $"{{\"{channel}\":{{\"version\":\"{version}\",\"releasedAt\":null}}}}";
        await db.SaveChangesAsync();
    }

    // --- the sensor and its router --------------------------------------------------------------

    [TestMethod]
    public async Task A_router_is_a_sensor_with_its_settings()
    {
        var sensor = await CreateAsync("192.0.2.1");
        Assert.AreEqual(SensorType.MIKROTIK, sensor.Type);
        Assert.IsNotNull(sensor.Mikrotik);
        Assert.AreEqual(MikrotikMode.DirectSsh, sensor.Mikrotik.Mode);
        Assert.AreEqual(MikrotikSetupState.AwaitingRouter, sensor.Mikrotik.SetupState);
        Assert.AreEqual("192.0.2.10", sensor.Mikrotik.SourceAddress);
        Assert.AreEqual(22, sensor.Mikrotik.SshPort);
        Assert.IsNull(sensor.Mikrotik.HostKey);
    }

    [TestMethod]
    public async Task Validation()
    {
        async Task<IReadOnlyList<string>> Errors(SensorRequest request)
        {
            var response = await user.PostJsonAsync("/api/v1/sensors", request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            var problem = await response.ProblemAsync();
            return problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        }
        var base_ = new SensorRequest { HostId = hostId, Type = SensorType.MIKROTIK, Name = "x", Interval = 300 };
        CollectionAssert.Contains((await Errors(base_)).ToList(), "mikrotik");
        CollectionAssert.Contains((await Errors(new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = "x", Interval = 300,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.WireGuardSsh },
        })).ToList(), "mikrotik.mode", "no gateway yet");
        CollectionAssert.Contains((await Errors(Direct("not a host!"))).ToList(), "mikrotik.address");
        CollectionAssert.Contains((await Errors(Direct("192.0.2.1", source: "nonsense"))).ToList(), "mikrotik.sourceAddress");
        CollectionAssert.Contains((await Errors(Direct("192.0.2.1", interval: 30))).ToList(), "interval");
        CollectionAssert.Contains((await Errors(Direct("192.0.2.1", port: 70000))).ToList(), "mikrotik.sshPort");

        SensorRequest Direct(string address, string? source = null, int interval = 300, int port = 22) => new()
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = "x", Interval = interval,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = address, SourceAddress = source, SshPort = port },
        };
    }

    [TestMethod]
    public async Task Diagnostics_and_secrets_are_an_Admins()
    {
        var response = await user.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = "x", Interval = 300,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = "192.0.2.2", Diagnostics = true },
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        var sensor = await CreateAsync("192.0.2.2", admin, new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = "192.0.2.2", Diagnostics = true, IncludeSecrets = true });
        Assert.IsTrue(sensor.Mikrotik!.Diagnostics);
        var script = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/join-script")).ReadAsync<MikrotikScript>();
        StringAssert.Contains(script.Script, "policy=ssh,read,sensitive,test");
    }

    [TestMethod]
    public async Task The_join_script_carries_the_installations_key_and_tag_and_is_audited()
    {
        var sensor = await CreateAsync("192.0.2.3");
        var script = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/join-script")).ReadAsync<MikrotikScript>();
        var installation = await app.Services.GetRequiredService<MikrotikKeyStore>().GetAsync(CancellationToken.None);
        StringAssert.Contains(script.Script, installation.PublicKeys[0]);
        StringAssert.Contains(script.Script, $"comment=\"{installation.Comment}\"");
        StringAssert.Contains(script.Script, "address=192.0.2.10/32");
        Assert.AreEqual("pnmt-join.rsc", script.FileName);
        var viewer = app.Client(Roles.Viewer);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/join-script")).StatusCode);
        var log = await (await admin.GetAsync($"/api/v1/auth-log?type={AuthEvents.MikrotikJoinScriptViewed}")).ReadAsync<Paged<AuthLogItem>>();
        Assert.IsTrue(log.Items.Any(i => i.Detail!.Contains(sensor.Name)), "the view is audit-logged");
        var removal = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/removal-script")).ReadAsync<MikrotikScript>();
        StringAssert.Contains(removal.Script, "/user remove [find where comment~\"^pnmt:\"]");
        // The private key never leaves PNMTD.
        Assert.IsFalse(script.Script.Contains("PRIVATE KEY", StringComparison.Ordinal));
    }

    // --- polls and events ----------------------------------------------------------------------------

    [TestMethod]
    public async Task First_poll_pins_the_host_key_and_writes_240()
    {
        var sensor = await CreateAsync("192.0.2.10");
        routers.Add("192.0.2.10", FakeRouter.From("7.25.1"));
        var outcome = await PollAsync(sensor.Id);
        Assert.IsTrue(outcome.Succeeded, outcome.Message);
        var router = await RouterAsync(sensor.Id);
        Assert.AreEqual(MikrotikSetupState.Ready, router.SetupState);
        Assert.AreEqual(FakeRouter.DefaultHostKey.Fingerprint, router.HostKeyFingerprint);
        Assert.AreEqual("7.25.1", router.Version);
        Assert.AreEqual("r25", router.Identity);
        var events = await EventsAsync(sensor.Id);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_OK, events[0].Code);
        Assert.AreEqual("RouterOS 7.25.1 (stable) · CHR QEMU Standard PC (i440FX + PIIX, 1996)", events[0].Message);
        Assert.AreEqual("mikrotik:192.0.2.10", events[0].Source);
        // PNMTD logged in as pnmt, with its key, and kept to the configured port.
        Assert.AreEqual("pnmt", routers.LastTarget!.Username);
        Assert.AreEqual(1, routers.LastTarget.Credentials.PrivateKeysPem.Count);
        Assert.AreEqual(22, routers.LastTarget.Port);

        // A steady state stays two rows.
        await PollAsync(sensor.Id);
        await PollAsync(sensor.Id);
        Assert.AreEqual(2, (await EventsAsync(sensor.Id)).Count);
    }

    [TestMethod]
    public async Task Restart_update_and_new_version()
    {
        var sensor = await CreateAsync("192.0.2.11");
        var fake = FakeRouter.From("7.25.1");
        routers.Add("192.0.2.11", fake);
        await PollAsync(sensor.Id);

        fake.Set(uptime: "00:00:30", version: "7.25.2");
        await PollAsync(sensor.Id);
        var events = await EventsAsync(sensor.Id);
        CollectionAssert.AreEqual(new[] { 240, 241, 242, 240 }, events.Select(e => e.Code).ToArray());
        StringAssert.StartsWith(events[1].Message!, "Restarted at ");
        Assert.AreEqual("Updated 7.25.1 → 7.25.2", events[2].Message);

        await SetLatestAsync("stable", "7.26");
        await PollAsync(sensor.Id);
        await PollAsync(sensor.Id);
        events = await EventsAsync(sensor.Id);
        Assert.AreEqual(1, events.Count(e => e.Code == PNMTStatusCodes.MIKROTIK_UPDATE_AVAILABLE), "244 once per version");
        Assert.AreEqual("7.26 available on stable (7.25.2 installed)", events.Single(e => e.Code == 244).Message);
        StringAssert.EndsWith(events[^1].Message!, " · 7.26 available");
        var detail = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        Assert.IsTrue(detail.Mikrotik!.UpdateAvailable);
        Assert.AreEqual("7.26", detail.Mikrotik.LatestVersion);
        await SetLatestAsync("stable", "7.25.1");
    }

    [TestMethod]
    public async Task Unreachable_gives_440_after_the_grace_period_only()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Clock.Provider = time;
        var sensor = await CreateAsync("192.0.2.12", grace: 600);
        var fake = FakeRouter.From("7.13");
        routers.Add("192.0.2.12", fake);
        await PollAsync(sensor.Id);

        fake.Failure = (RouterFailureKind.Unreachable, "SSH: connection timed out");
        time.Advance(TimeSpan.FromMinutes(5));
        await PollAsync(sensor.Id);
        Assert.IsFalse((await EventsAsync(sensor.Id)).Any(e => e.Code == PNMTStatusCodes.MIKROTIK_UNREACHABLE), "within the grace period: no 440");
        // The grace period counts from the first failed poll.
        time.Advance(TimeSpan.FromMinutes(9));
        await PollAsync(sensor.Id);
        Assert.IsFalse((await EventsAsync(sensor.Id)).Any(e => e.Code == PNMTStatusCodes.MIKROTIK_UNREACHABLE), "9 minutes of failures: still within 10");
        time.Advance(TimeSpan.FromMinutes(2));
        await PollAsync(sensor.Id);
        var unreachable = (await EventsAsync(sensor.Id)).Single(e => e.Code == PNMTStatusCodes.MIKROTIK_UNREACHABLE);
        StringAssert.Contains(unreachable.Message!, "SSH: connection timed out");
        StringAssert.StartsWith(unreachable.Message!, "Unreachable since ");

        fake.Failure = null;
        time.Advance(TimeSpan.FromMinutes(5));
        var back = await PollAsync(sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_OK, back.Code);
        Assert.IsNull((await RouterAsync(sensor.Id)).FailingSince);
    }

    [TestMethod]
    public async Task A_changed_host_key_is_refused_at_once_until_an_Admin_accepts_it()
    {
        var sensor = await CreateAsync("192.0.2.13");
        var fake = FakeRouter.From("7.13");
        routers.Add("192.0.2.13", fake);
        await PollAsync(sensor.Id);

        fake.HostKey = new RouterHostKey("ssh-ed25519", "AnotherRouterAnotherKey0000000000000000000000");
        await PollAsync(sensor.Id);
        var events = await EventsAsync(sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_ACCESS_DENIED, events[^1].Code);
        StringAssert.StartsWith(events[^1].Message!, "Host key changed");
        var router = await RouterAsync(sensor.Id);
        Assert.AreEqual(MikrotikSetupState.NeedsAttention, router.SetupState);
        Assert.AreEqual(fake.HostKey.Fingerprint, router.PendingHostKeyFingerprint);
        Assert.AreEqual(FakeRouter.DefaultHostKey.Fingerprint, router.HostKeyFingerprint, "the pinned key stays");

        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.PostAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/host-key/accept", null)).StatusCode);
        var accepted = await (await admin.PostAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/host-key/accept", null)).ReadAsync<MikrotikRouterDetail>();
        Assert.AreEqual(fake.HostKey.Fingerprint, accepted.HostKey!.Fingerprint);
        Assert.IsNull(accepted.PendingHostKey);
        await PollAsync(sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_OK, (await EventsAsync(sensor.Id))[^1].Code);
    }

    [TestMethod]
    public async Task A_refused_login_is_441_but_not_before_the_first_login()
    {
        var sensor = await CreateAsync("192.0.2.14");
        var fake = FakeRouter.From("7.13");
        fake.Failure = (RouterFailureKind.AuthenticationFailed, "SSH: login refused");
        routers.Add("192.0.2.14", fake);
        await PollAsync(sensor.Id);
        Assert.AreEqual(0, (await EventsAsync(sensor.Id)).Count, "waiting for the join script: no events");
        var router = await RouterAsync(sensor.Id);
        Assert.AreEqual(MikrotikSetupState.AwaitingRouter, router.SetupState);
        Assert.AreEqual("SSH: login refused", router.LastError);

        fake.Failure = null;
        await PollAsync(sensor.Id);
        fake.Failure = (RouterFailureKind.AuthenticationFailed, "SSH: login refused");
        await PollAsync(sensor.Id);
        var events = await EventsAsync(sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_ACCESS_DENIED, events[^1].Code);
        Assert.AreEqual("SSH login as pnmt refused", events[^1].Message);
    }

    [TestMethod]
    public async Task Below_the_floor_is_441()
    {
        var sensor = await CreateAsync("192.0.2.15");
        var fake = FakeRouter.From("7.13");
        fake.Set(version: "7.12.1");
        routers.Add("192.0.2.15", fake);
        await PollAsync(sensor.Id);
        var events = await EventsAsync(sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_ACCESS_DENIED, events[^1].Code);
        Assert.AreEqual("RouterOS 7.12.1 is below 7.13", events[^1].Message);
    }

    [TestMethod]
    public async Task Unexpected_output_counts_as_a_failed_poll()
    {
        var sensor = await CreateAsync("192.0.2.16", grace: 1);
        var fake = FakeRouter.From("7.13");
        routers.Add("192.0.2.16", fake);
        await PollAsync(sensor.Id);
        fake.Output = "bad command name serialize (line 1 column 3)";
        await Task.Delay(1100);
        await PollAsync(sensor.Id);
        await Task.Delay(1100);
        var outcome = await PollAsync(sensor.Id);
        Assert.IsFalse(outcome.Succeeded);
        var events = await EventsAsync(sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_UNREACHABLE, events[^1].Code);
        StringAssert.Contains(events[^1].Message!, "The router answered: bad command name serialize");
        Assert.AreEqual("r13", (await RouterAsync(sensor.Id)).Identity, "the last reading stays");
    }

    [TestMethod]
    public async Task Script_outdated_when_the_group_lacks_an_opt_in()
    {
        var sensor = await CreateAsync("192.0.2.17");
        routers.Add("192.0.2.17", FakeRouter.From("7.25.1"));
        await PollAsync(sensor.Id);
        var detail = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        Assert.IsTrue(detail.Mikrotik!.ScriptOutdated, "the fixture's router has another key than this installation's");
        StringAssert.Contains(detail.Mikrotik.ScriptDifferences[0], "SSH key");

        var request = new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = detail.Name, Interval = 300, GracePeriod = 600,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = "192.0.2.17", SourceAddress = "192.0.2.10", Diagnostics = true },
        };
        var updated = await (await admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", request)).ReadAsync<SensorDetail>();
        CollectionAssert.Contains(updated.Mikrotik!.ScriptDifferences.ToList(), "Diagnostics are on, but the group pnmt-read lacks the policy test.");
    }

    [TestMethod]
    public async Task The_admin_page_lists_the_fleet_and_the_status()
    {
        var sensor = await CreateAsync("192.0.2.18");
        routers.Add("192.0.2.18", FakeRouter.From("7.13"));
        await PollAsync(sensor.Id);
        var fleet = await (await admin.GetAsync("/api/v1/mikrotik/routers?search=192.0.2.18")).ReadAsync<Paged<MikrotikFleetItem>>();
        Assert.AreEqual(1, fleet.TotalCount);
        Assert.AreEqual("7.13", fleet.Items[0].Version);
        Assert.AreEqual(SensorState.Ok, fleet.Items[0].State);
        var status = await (await admin.GetAsync("/api/v1/mikrotik/status")).ReadAsync<MikrotikStatus>();
        Assert.IsTrue(status.Routers >= 1);
        Assert.AreEqual(4, status.Tag.Length);
        StringAssert.StartsWith(status.SshKeys[0].PublicKey, "ssh-ed25519 ");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/mikrotik/routers")).StatusCode);
    }

    [TestMethod]
    public async Task Poll_now_through_the_API()
    {
        var sensor = await CreateAsync("192.0.2.19");
        routers.Add("192.0.2.19", FakeRouter.From("7.13"));
        var detail = await (await user.PostAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/poll", null)).ReadAsync<MikrotikRouterDetail>();
        Assert.AreEqual(MikrotikSetupState.Ready, detail.SetupState);
        Assert.AreEqual("7.13", detail.Version);
        Assert.AreEqual(171L, detail.Resources!.UptimeSeconds);
    }

    [TestMethod]
    public async Task Changing_the_address_unpins_the_host_key()
    {
        var sensor = await CreateAsync("192.0.2.20");
        routers.Add("192.0.2.20", FakeRouter.From("7.13"));
        await PollAsync(sensor.Id);
        Assert.IsNotNull((await RouterAsync(sensor.Id)).HostKeyFingerprint);
        var request = new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = sensor.Name, Interval = 300,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = "192.0.2.21", SourceAddress = "192.0.2.10" },
        };
        var updated = await (await user.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", request)).ReadAsync<SensorDetail>();
        Assert.IsNull(updated.Mikrotik!.HostKey);
        Assert.AreEqual(MikrotikSetupState.AwaitingRouter, updated.Mikrotik.SetupState);
    }

    // --- the simulated router -------------------------------------------------------------------------

    internal sealed class FakeRouter
    {
        public static readonly RouterHostKey DefaultHostKey = new("ssh-ed25519", "LOQGlmOf3uLSAul2M1/UhP8sxm6fFGiEDYO1VLE+dQA");

        public string Output { get; set; } = "";

        public RouterHostKey HostKey { get; set; } = DefaultHostKey;

        public (RouterFailureKind Kind, string Message)? Failure { get; set; }

        public static FakeRouter From(string version) => new()
        {
            Output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikrotik", version, "poll.json")),
        };

        /// <summary>Changes the recorded reading as a router would: a new uptime (a restart), another version.</summary>
        public void Set(string? uptime = null, string? version = null)
        {
            var json = JsonNode.Parse(Output)!;
            if (uptime is not null)
            {
                json["resource"]!["uptime"] = uptime;
            }
            if (version is not null)
            {
                json["version"] = "v" + version;
                json["resource"]!["version"] = version;
            }
            Output = json.ToJsonString();
        }
    }

    internal sealed class FakeConnector : IRouterConnector
    {
        private readonly ConcurrentDictionary<string, FakeRouter> byHost = new();

        public RouterTarget? LastTarget { get; private set; }

        public void Add(string host, FakeRouter router) => byHost[host] = router;

        public Task<IRouterSession> ConnectAsync(RouterTarget target, CancellationToken ct)
        {
            LastTarget = target;
            if (!byHost.TryGetValue(target.Host, out var router))
            {
                throw new RouterSessionException(RouterFailureKind.Unreachable, "SSH: connection timed out");
            }
            if (router.Failure is { } failure)
            {
                throw new RouterSessionException(failure.Kind, failure.Message);
            }
            if (target.PinnedHostKey is { } pinned && pinned != router.HostKey)
            {
                throw new RouterSessionException(RouterFailureKind.HostKeyChanged, $"Host key changed: the router presented {router.HostKey}", router.HostKey);
            }
            return Task.FromResult<IRouterSession>(new Session(router));
        }

        private sealed class Session(FakeRouter router) : IRouterSession
        {
            public RouterHostKey HostKey => router.HostKey;

            public Task<RouterExecResult> ExecAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken ct) =>
                Task.FromResult(new RouterExecResult(router.Output, "", 0, false, false, TimeSpan.FromMilliseconds(5)));

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
