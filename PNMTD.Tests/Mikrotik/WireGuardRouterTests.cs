using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.WireGuard;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.WireGuard;
using static PNMTD.Tests.Mikrotik.MikrotikRouterTests;

namespace PNMTD.Tests.Mikrotik;

/// <summary>Routers in the WireGuard modes (docs/mikrotik-plan.md §3, §14), with the gateway's side simulated in the database.</summary>
[TestClass]
public sealed class WireGuardRouterTests
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
        app = new TestApp(settings: new Dictionary<string, string?> { ["WireGuard:Enabled"] = "true" },
            services: s => s.Replace(ServiceDescriptor.Singleton<IRouterConnector>(routers)));
        admin = app.Client(Roles.Admin);
        user = app.Client(Roles.User);
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"wg-tests-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        hostId = host.Id;
        await SetNetworkAsync(host: "vpn.example.com", port: 51999);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<WireGuardNetworkSaved> SetNetworkAsync(string? host = null, int? port = null, string prefix = "10.253.0.0/16", string? confirm = null)
    {
        var current = await (await admin.GetAsync("/api/v1/wireguard/network")).ReadAsync<WireGuardNetworkView>();
        return await (await admin.PutJsonAsync("/api/v1/wireguard/network", new WireGuardNetworkRequest
        {
            Prefix = prefix,
            ConfirmPrefix = confirm,
            ExternalHost = host ?? current.ExternalHost,
            ExternalPort = port ?? current.ExternalPort,
            KeepaliveSeconds = current.KeepaliveSeconds,
            Mtu = current.Mtu,
            StaleHandshakeSeconds = current.StaleHandshakeSeconds,
            ManagementPorts = current.ManagementPorts,
        })).ReadAsync<WireGuardNetworkSaved>();
    }

    private static async Task<SensorDetail> CreateAsync(MikrotikMode mode, string name)
    {
        var sensor = await (await user.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = name, Interval = 300, GracePeriod = 600,
            Mikrotik = new MikrotikRouterRequest { Mode = mode },
        })).ReadAsync<SensorDetail>();
        return sensor;
    }

    private static async Task<WgPeerEntity> PeerAsync(Guid sensorId)
    {
        await using var db = app.NewDb();
        return await db.WgPeers.SingleAsync(p => p.SensorId == sensorId);
    }

    /// <summary>What the gateway writes: its heartbeat, and a peer's handshake.</summary>
    private static async Task GatewayAsync(bool up, Guid? peerId = null, DateTime? handshake = null)
    {
        await using var db = app.NewDb();
        await db.WgNetwork.ExecuteUpdateAsync(u => u.SetProperty(n => n.GatewaySeenAt, up ? Clock.UtcNow : Clock.UtcNow.AddMinutes(-10)));
        if (peerId is { } id)
        {
            await db.WgPeerStatus.Where(s => s.PeerId == id).ExecuteDeleteAsync();
            db.WgPeerStatus.Add(new WgPeerStatusEntity { PeerId = id, LatestHandshake = handshake, Endpoint = "203.0.113.7:13231", UpdatedAt = Clock.UtcNow });
            await db.SaveChangesAsync();
        }
    }

    private static Task<RouterPollOutcome> PollAsync(Guid id) =>
        app.Services.GetRequiredService<RouterPollService>().PollAsync(id, wait: true, CancellationToken.None);

    [TestMethod]
    public async Task A_tunnel_router_gets_an_address_keys_and_a_join_script_with_them()
    {
        var sensor = await CreateAsync(MikrotikMode.WireGuardSsh, "tunnel router");
        var peer = await PeerAsync(sensor.Id);
        Assert.IsTrue(AddressPlan.Default.IsPeerAddress(IPAddress.Parse(peer.Address)));
        Assert.AreEqual(MikrotikMode.WireGuardSsh, sensor.Mikrotik!.Mode);

        var script = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/join-script")).ReadAsync<MikrotikScript>();
        var network = app.Services.GetRequiredService<WireGuardNetwork>();
        var privateKey = network.Unprotect(peer.PrivateKeyProtected)!;
        Assert.AreEqual(peer.PublicKey, WireGuardKeys.PublicKey(privateKey));
        StringAssert.Contains(script.Script, $"private-key=\"{privateKey}\"");
        StringAssert.Contains(script.Script, "endpoint-address=\"vpn.example.com\" endpoint-port=51999 allowed-address=10.253.0.0/30");
        StringAssert.Contains(script.Script, $"/ip address add address={peer.Address}/32 interface=pnmt");
        StringAssert.Contains(script.Script, "/user add name=pnmt group=pnmt-read address=10.253.0.1/32");
        var status = await (await admin.GetAsync("/api/v1/wireguard/status")).ReadAsync<WireGuardStatus>();
        StringAssert.Contains(script.Script, $"public-key=\"{status.GatewayPublicKey}\"");

        // Regenerating makes the old key useless.
        var detail = await (await user.PostAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/regenerate-keys", null)).ReadAsync<MikrotikRouterDetail>();
        Assert.AreNotEqual(peer.PublicKey, detail.Tunnel!.PublicKey);
        Assert.AreEqual(peer.Address, detail.Tunnel.Address, "the address stays");

        // Direct SSH has no tunnel: the peer goes.
        var updated = await (await user.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = sensor.Name, Interval = 300,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = "192.0.2.99" },
        })).ReadAsync<SensorDetail>();
        Assert.IsNull(updated.Mikrotik!.Tunnel);
        await using var db = app.NewDb();
        Assert.IsFalse(await db.WgPeers.AnyAsync(p => p.SensorId == sensor.Id));
    }

    [TestMethod]
    public async Task WireGuard_only_follows_the_handshake_and_waits_while_the_gateway_is_down()
    {
        var sensor = await CreateAsync(MikrotikMode.WireGuardOnly, "tunnel only");
        var peer = await PeerAsync(sensor.Id);
        await GatewayAsync(up: true, peer.Id, handshake: null);
        var waiting = await PollAsync(sensor.Id);
        Assert.AreEqual(0, waiting.Events.Count, "before the first handshake: waiting for the router");
        Assert.AreEqual("No WireGuard handshake yet", waiting.Message);

        await GatewayAsync(up: true, peer.Id, handshake: Clock.UtcNow.AddSeconds(-30));
        var up = await PollAsync(sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.MIKROTIK_OK, up.Code);
        Assert.AreEqual("Tunnel up (WireGuard only)", up.Message);
        var detail = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        Assert.IsTrue(detail.Mikrotik!.Tunnel!.Up);
        Assert.AreEqual(MikrotikSetupState.Ready, detail.Mikrotik.SetupState);

        await GatewayAsync(up: false);
        var down = await PollAsync(sensor.Id);
        Assert.IsTrue(down.Skipped, "no events while the gateway is down");
        StringAssert.Contains(down.Message, "gateway is down");

        // Just restarted: a tunnel without a fresh handshake is reconnecting, not down.
        await GatewayAsync(up: true, peer.Id, handshake: Clock.UtcNow.AddMinutes(-20));
        await using (var db = app.NewDb())
        {
            await db.WgNetwork.ExecuteUpdateAsync(u => u.SetProperty(n => n.GatewayStartedAt, Clock.UtcNow));
        }
        var settling = await PollAsync(sensor.Id);
        Assert.IsTrue(settling.Skipped);
        StringAssert.Contains(settling.Message, "reconnecting");
        await using (var db = app.NewDb())
        {
            await db.WgNetwork.ExecuteUpdateAsync(u => u.SetProperty(n => n.GatewayStartedAt, Clock.UtcNow.AddHours(-1)));
        }
        var stale = await PollAsync(sensor.Id);
        Assert.IsFalse(stale.Skipped, "after the settling period the handshake counts");
        StringAssert.StartsWith(stale.Message, "No WireGuard handshake since");
    }

    [TestMethod]
    public async Task WireGuard_and_SSH_logs_in_through_the_gateways_SOCKS()
    {
        var sensor = await CreateAsync(MikrotikMode.WireGuardSsh, "tunnel ssh");
        var peer = await PeerAsync(sensor.Id);
        routers.Add(peer.Address, FakeRouter.From("7.25.1"));

        await GatewayAsync(up: true, peer.Id, handshake: Clock.UtcNow.AddMinutes(-30));
        var stale = await PollAsync(sensor.Id);
        StringAssert.StartsWith(stale.Message, "No WireGuard handshake since");

        await GatewayAsync(up: true, peer.Id, handshake: Clock.UtcNow.AddSeconds(-20));
        var outcome = await PollAsync(sensor.Id);
        Assert.IsTrue(outcome.Succeeded, outcome.Message);
        Assert.AreEqual(peer.Address, routers.LastTarget!.Host);
        Assert.AreEqual(22, routers.LastTarget.Port);
        Assert.AreEqual("pnmtd-wg", routers.LastTarget.Proxy!.Host);
        Assert.AreEqual(1080, routers.LastTarget.Proxy.Port);
        Assert.AreEqual("pnmtd", routers.LastTarget.Proxy.Username);
        await using var db = app.NewDb();
        var events = await db.Events.Where(e => e.SensorId == sensor.Id).ToListAsync();
        Assert.IsTrue(events.All(e => e.Source == $"mikrotik:{peer.Address}"));

        // The fixture's router has another tunnel key and dials port 51820: the script is outdated, and says why.
        var detail = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        Assert.IsTrue(detail.Mikrotik!.ScriptDifferences.Any(d => d.Contains("tunnel key", StringComparison.Ordinal)));
        Assert.IsTrue(detail.Mikrotik.ScriptDifferences.Any(d => d.Contains("dials port 51820; the endpoint's port is now 51999", StringComparison.Ordinal)),
            string.Join("; ", detail.Mikrotik.ScriptDifferences));
    }

    /// <summary>Without PNMT's firewall rules, the router's firewall is the admin's and isn't compared (docs/mikrotik-join-options-plan.md §2.2, §2.4).</summary>
    [TestMethod]
    public async Task Without_firewall_rules_the_firewall_is_not_compared()
    {
        var sensor = await CreateAsync(MikrotikMode.WireGuardSsh, "own firewall");
        var peer = await PeerAsync(sensor.Id);
        var router = FakeRouter.From("7.25.1");
        // The access address of the network in effect: another test of this class may have changed the prefix.
        await using (var db = app.NewDb())
        {
            var network = await app.Services.GetRequiredService<WireGuardNetwork>().SettingsAsync(db, CancellationToken.None);
            router.FirewallSources(network.Plan.ServerAddress.ToString(), network.Plan.AccessAddress.ToString());
        }
        routers.Add(peer.Address, router);
        await GatewayAsync(up: true, peer.Id, handshake: Clock.UtcNow.AddSeconds(-20));
        Assert.IsTrue((await PollAsync(sensor.Id)).Succeeded);
        const string stillAccepts = "The router still accepts custom tunnels on its management ports.";
        var detail = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        CollectionAssert.Contains(detail.Mikrotik!.ScriptDifferences.ToList(), stillAccepts);
        Assert.IsTrue(detail.Mikrotik.Differences.Single(d => d.Text == stillAccepts).Admin);

        var script = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/join-script?firewall=false")).ReadAsync<MikrotikScript>();
        Assert.AreEqual(new MikrotikScriptOptions(false, true), script.Options);
        StringAssert.Contains(script.Script, "# Options: no firewall rules.");
        Assert.IsFalse(script.Script.Split('\n').Any(l => !l.StartsWith('#') && l.Contains("/ip firewall", StringComparison.Ordinal)));
        Assert.IsTrue(script.Notes.Any(n => n.Contains("interface list LAN", StringComparison.Ordinal)), string.Join("; ", script.Notes));
        var without = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        Assert.IsFalse(without.Mikrotik!.ScriptDifferences.Contains(stillAccepts), string.Join("; ", without.Mikrotik.ScriptDifferences));

        // Without removals as well, the text says that only a hand removes the rule.
        await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/join-script?firewall=true&removals=false");
        var byHand = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        CollectionAssert.Contains(byHand.Mikrotik!.ScriptDifferences.ToList(),
            stillAccepts + " The join script removes nothing: remove the rule by hand, or acknowledge it.");
    }

    /// <summary>The fleet and the overview compare with everything the Setup tab compares with (docs/mikrotik-join-options-plan.md §3.5).</summary>
    [TestMethod]
    public async Task The_fleet_counts_what_the_Setup_tab_shows()
    {
        var sensor = await CreateAsync(MikrotikMode.WireGuardOnly, $"fleet {Guid.NewGuid():N}");
        await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/join-script");
        Assert.AreEqual(HttpStatusCode.OK, (await user.PostAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/regenerate-keys", null)).StatusCode);
        var detail = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        Assert.IsTrue(detail.Mikrotik!.ScriptOutdated, "new keys after the script was shown");
        var fleet = await (await admin.GetAsync($"/api/v1/mikrotik/routers?search={Uri.EscapeDataString(sensor.Name)}")).ReadAsync<Paged<MikrotikFleetItem>>();
        Assert.IsTrue(fleet.Items.Single().ScriptOutdated, "before, the fleet compared WireGuard only routers with nothing");
        var outdated = (await (await admin.GetAsync("/api/v1/mikrotik/status")).ReadAsync<MikrotikStatus>()).ScriptsOutdated;

        var difference = detail.Mikrotik.Differences.Single();
        Assert.IsFalse(difference.Admin);
        await (await user.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/differences/acknowledge",
            new MikrotikDifferenceAcknowledgeRequest([difference.Id], "The router gets its new keys at the next visit"))).ReadAsync<MikrotikRouterDetail>();
        fleet = await (await admin.GetAsync($"/api/v1/mikrotik/routers?search={Uri.EscapeDataString(sensor.Name)}")).ReadAsync<Paged<MikrotikFleetItem>>();
        Assert.IsFalse(fleet.Items.Single().ScriptOutdated);
        Assert.AreEqual(outdated - 1, (await (await admin.GetAsync("/api/v1/mikrotik/status")).ReadAsync<MikrotikStatus>()).ScriptsOutdated);
    }

    [TestMethod]
    public async Task Network_settings_are_validated_and_a_new_prefix_needs_confirming()
    {
        var sensor = await CreateAsync(MikrotikMode.WireGuardOnly, "readdressed");
        var before = await PeerAsync(sensor.Id);
        var unconfirmed = await admin.PutJsonAsync("/api/v1/wireguard/network", new WireGuardNetworkRequest { Prefix = "10.254.0.0/16" });
        Assert.AreEqual(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        var problem = await unconfirmed.ProblemAsync();
        Assert.IsTrue(problem.GetProperty("errors").TryGetProperty("confirmPrefix", out _));

        var saved = await SetNetworkAsync(prefix: "10.254.0.0/16", confirm: "10.254.0.0/16");
        Assert.AreEqual("10.254.0.0/16", saved.Network.Prefix);
        CollectionAssert.Contains(saved.NeedNewScripts.ToList(), "readdressed");
        var after = await PeerAsync(sensor.Id);
        Assert.AreEqual(before.Address.Replace("10.253.", "10.254."), after.Address, "the same place in the new prefix");
        await SetNetworkAsync(prefix: "10.253.0.0/16", confirm: "10.253.0.0/16");

        var bad = await admin.PutJsonAsync("/api/v1/wireguard/network", new WireGuardNetworkRequest { Prefix = "10.253.0.0/16", Mtu = 9000, KeepaliveSeconds = 5 });
        var errors = (await bad.ProblemAsync()).GetProperty("errors");
        Assert.IsTrue(errors.TryGetProperty("mtu", out _));
        Assert.IsTrue(errors.TryGetProperty("keepaliveSeconds", out _));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/wireguard/network")).StatusCode);
    }

    [TestMethod]
    public async Task Custom_tunnels_reach_only_routers_with_management_access()
    {
        // Two routers: one with management access (an Admin's setting), one without.
        var open = await (await admin.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = "managed router", Interval = 300,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.WireGuardOnly, ManagementAccess = true },
        })).ReadAsync<SensorDetail>();
        var closed = await CreateAsync(MikrotikMode.WireGuardOnly, "closed router");
        var openPeer = await PeerAsync(open.Id);

        var tunnel = await (await admin.PostJsonAsync("/api/v1/wireguard/tunnels", new WireGuardTunnelRequest
        {
            Name = "alice-laptop", OwnerUserId = TestUsers.User, AccessAllRouters = true,
        })).ReadAsync<WireGuardTunnelItem>();
        Assert.IsTrue(AddressPlan.Default.IsPeerAddress(IPAddress.Parse(tunnel.Address)));
        CollectionAssert.Contains(tunnel.Reaches.Select(r => r.SensorId).ToList(), open.Id);
        CollectionAssert.DoesNotContain(tunnel.Reaches.Select(r => r.SensorId).ToList(), closed.Id);
        Assert.AreEqual(openPeer.Address, tunnel.Reaches.Single(r => r.SensorId == open.Id).Address);
        Assert.AreEqual("test-user", tunnel.Owner!.Name);

        // The management rule is in the router's join script.
        var script = await (await admin.GetAsync($"/api/v1/sensors/{open.Id}/mikrotik/join-script")).ReadAsync<MikrotikScript>();
        StringAssert.Contains(script.Script, "src-address=10.253.0.2 protocol=tcp dst-port=22,8291,80,443 action=accept");

        // The file: wg-quick, only the tunnel network routed.
        var config = await (await admin.GetAsync($"/api/v1/wireguard/tunnels/{tunnel.Id}/config")).ReadAsync<WireGuardTunnelConfig>();
        Assert.AreEqual("alice-laptop.conf", config.FileName);
        StringAssert.Contains(config.Content, $"Address = {tunnel.Address}/32");
        StringAssert.Contains(config.Content, "Endpoint = vpn.example.com:51999");
        StringAssert.Contains(config.Content, "AllowedIPs = 10.253.0.0/16");
        var network = app.Services.GetRequiredService<WireGuardNetwork>();
        var privateKey = config.Content.Split('\n').Single(l => l.StartsWith("PrivateKey = ", StringComparison.Ordinal))["PrivateKey = ".Length..];
        Assert.AreEqual(tunnel.PublicKey, WireGuardKeys.PublicKey(privateKey));

        // The owner downloads it under Account; others do not see it; Users cannot manage tunnels.
        var mine = await (await user.GetAsync("/api/v1/account/tunnels")).ReadAsync<List<WireGuardTunnelItem>>();
        Assert.IsTrue(mine.Any(t => t.Id == tunnel.Id));
        Assert.AreEqual(HttpStatusCode.OK, (await user.GetAsync($"/api/v1/account/tunnels/{tunnel.Id}/config")).StatusCode);
        var viewer = app.Client(Roles.Viewer);
        Assert.IsFalse((await (await viewer.GetAsync("/api/v1/account/tunnels")).ReadAsync<List<WireGuardTunnelItem>>()).Any());
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/account/tunnels/{tunnel.Id}/config")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/wireguard/tunnels")).StatusCode);
        var log = await (await admin.GetAsync($"/api/v1/audit-log?type={AuditEvents.WireGuardTunnelDownloaded}")).ReadAsync<Paged<AuditLogItem>>();
        Assert.IsTrue(log.Items.Count(i => i.Detail!.Contains("alice-laptop")) >= 2);

        // Selected routers only, and an expired tunnel is not downloadable by its owner.
        var updated = await (await admin.PutJsonAsync($"/api/v1/wireguard/tunnels/{tunnel.Id}", new WireGuardTunnelRequest
        {
            Name = "alice-laptop", OwnerUserId = TestUsers.User, RouterIds = [closed.Id], ExpiresAt = Clock.UtcNow.AddSeconds(-1),
        })).ReadAsync<WireGuardTunnelItem>();
        Assert.AreEqual(0, updated.Reaches.Count, "the selected router has no management access");
        Assert.IsTrue(updated.Expired);
        Assert.AreEqual(HttpStatusCode.Conflict, (await user.GetAsync($"/api/v1/account/tunnels/{tunnel.Id}/config")).StatusCode);

        var regenerated = await (await admin.PostAsync($"/api/v1/wireguard/tunnels/{tunnel.Id}/regenerate-keys", null)).ReadAsync<WireGuardTunnelItem>();
        Assert.AreNotEqual(tunnel.PublicKey, regenerated.PublicKey);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/wireguard/tunnels/{tunnel.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task Empty_variables_from_compose_are_not_set()
    {
        // docker-compose.yml passes ${PNMTD_WG_EXTERNAL_PORT:-} as an empty string when .env leaves it empty.
        using var empty = new TestApp(settings: new Dictionary<string, string?>
        {
            ["WireGuard:Enabled"] = "true",
            ["WireGuard:ExternalHost"] = "",
            ["WireGuard:ExternalPort"] = "",
        });
        var view = await (await empty.Client(Roles.Admin).GetAsync("/api/v1/wireguard/network")).ReadAsync<WireGuardNetworkView>();
        Assert.IsFalse(view.ExternalHostLocked);
        Assert.IsFalse(view.ExternalPortLocked);
        Assert.AreNotEqual(WgSettingSource.Environment, view.ExternalPortSource);
    }

    [TestMethod]
    public async Task The_environment_wins_over_the_admin_page()
    {
        using var locked = new TestApp(settings: new Dictionary<string, string?>
        {
            ["WireGuard:Enabled"] = "true",
            ["WireGuard:ExternalHost"] = "env.example.com",
            ["WireGuard:ExternalPort"] = "40000",
        });
        var client = locked.Client(Roles.Admin);
        var view = await (await client.GetAsync("/api/v1/wireguard/network")).ReadAsync<WireGuardNetworkView>();
        Assert.IsTrue(view.ExternalHostLocked);
        Assert.AreEqual("env.example.com", view.ExternalHostInEffect);
        Assert.AreEqual(WgSettingSource.Environment, view.ExternalHostSource);
        Assert.AreEqual(40000, view.ExternalPortInEffect);
        var refused = await client.PutJsonAsync("/api/v1/wireguard/network", new WireGuardNetworkRequest
        {
            Prefix = view.Prefix, ExternalHost = "other.example.com", KeepaliveSeconds = view.KeepaliveSeconds, Mtu = view.Mtu,
            StaleHandshakeSeconds = view.StaleHandshakeSeconds, ManagementPorts = view.ManagementPorts,
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "PNMTD_WG_EXTERNAL_HOST");
        var status = await (await client.GetAsync("/api/v1/wireguard/status")).ReadAsync<WireGuardStatus>();
        Assert.AreEqual("env.example.com:40000", status.Endpoint);
    }
}
