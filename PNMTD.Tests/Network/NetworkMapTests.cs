using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PNMTD.Api.V1.Contracts;
using PNMTD.Agents;
using PNMTD.Audit;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Network;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Network;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Network;

/// <summary>
/// The network map against simulated routers (docs/network-map-plan.md §12): the scan, one router at a time, its skips and
/// failed tables; the analysis' suggestions and interfaces in the database; connections, suggestions and assignments through
/// the API with their rights and audit; the schedule; the servers' MACs.
/// </summary>
[TestClass]
public sealed class NetworkMapTests
{
    private static TestApp app = null!;
    private static ScanConnector routers = null!;
    private static HttpClient admin = null!;
    private static Guid siteId;
    private static Guid hostId;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        routers = new ScanConnector();
        // The worker's loop off: each test runs the queue itself, in order.
        app = new TestApp(settings: new Dictionary<string, string?> { ["NetworkMap:Scan:Background"] = "false" },
            services: s => s.Replace(ServiceDescriptor.Singleton<IRouterConnector>(routers)));
        admin = app.Client(Roles.Admin);
        var site = await (await admin.PostJsonAsync("/api/v1/locations", new { name = $"Site {Guid.NewGuid():N}"[..20] })).ReadAsync<JsonElement>();
        siteId = site.GetProperty("id").GetGuid();
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"netmap-{Guid.NewGuid():N}", LocationId = siteId })).ReadAsync<HostDetail>();
        hostId = host.Id;
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    // --- the simulated routers ---------------------------------------------------------------------------------------------

    /// <summary>A router that answers each table of the scan with what the test gave it.</summary>
    internal sealed class ScanRouter
    {
        public Dictionary<ScanTable, string> Tables { get; } = [];

        public Dictionary<ScanTable, string> Errors { get; } = [];

        public List<string> Commands { get; } = [];

        public static readonly string[] ArrayTables = [nameof(ScanTable.Interfaces), nameof(ScanTable.MacTable), nameof(ScanTable.Arp), nameof(ScanTable.Addresses)];

        public string Answer(ScanTable table) => Tables.TryGetValue(table, out var output) ? output : ArrayTables.Contains(table.ToString()) ? "[]" : "{}";
    }

    internal sealed class ScanConnector : IRouterConnector
    {
        private readonly ConcurrentDictionary<string, ScanRouter> byHost = new();

        public void Add(string host, ScanRouter router) => byHost[host] = router;

        public static ScanTable? TableOf(string command) =>
            command.Contains("/interface/print as-value proplist") ? ScanTable.Interfaces
            : command.Contains("/interface/ethernet/print") ? ScanTable.Ethernet
            : command.Contains("/interface/bridge/host/print") ? ScanTable.MacTable
            : command.Contains("/interface/bridge/print") ? ScanTable.Bridges
            : command.Contains("/ip/neighbor/print") ? ScanTable.Neighbors
            : command.Contains("/ip/arp/print") ? ScanTable.Arp
            : command.Contains("/ip/dhcp-server/print") ? ScanTable.Dhcp
            : command.Contains("/ip/address/print") ? ScanTable.Addresses
            : command.Contains("/interface/wireguard/print") ? ScanTable.Tunnels
            : command.Contains("registration-table") ? ScanTable.Wifi
            : null;

        public Task<IRouterSession> ConnectAsync(RouterTarget target, CancellationToken ct) =>
            byHost.TryGetValue(target.Host, out var router)
                ? Task.FromResult<IRouterSession>(new Session(router))
                : throw new RouterSessionException(RouterFailureKind.Unreachable, "SSH: connection timed out");

        private sealed class Session(ScanRouter router) : IRouterSession
        {
            public RouterHostKey HostKey => new("ssh-ed25519", "LOQGlmOf3uLSAul2M1/UhP8sxm6fFGiEDYO1VLE+dQA");

            public Task<RouterExecResult> ExecAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
            {
                router.Commands.Add(command);
                if (TableOf(command) is not { } table)
                {
                    return Task.FromResult(new RouterExecResult("bad command name", "", 1, false, false, TimeSpan.Zero));
                }
                return Task.FromResult(router.Errors.TryGetValue(table, out var error)
                    ? new RouterExecResult(error, "", 1, false, false, TimeSpan.Zero)
                    : new RouterExecResult(router.Answer(table), "", 0, false, false, TimeSpan.FromMilliseconds(3)));
            }

            public Task<byte[]> DownloadAsync(string path, long maxBytes, CancellationToken ct) => throw new NotSupportedException();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static int addresses;

    /// <summary>A Direct SSH router on the test's host, Ready as after its first login, and its simulated twin.</summary>
    private static async Task<(Guid Id, string Name, ScanRouter Router)> RouterAsync(string name, Action<ScanRouter>? setup = null, Guid? host = null,
        MikrotikMode mode = MikrotikMode.DirectSsh, bool ready = true)
    {
        var address = $"198.51.100.{Interlocked.Increment(ref addresses)}";
        var full = $"{name}-{Guid.NewGuid():N}"[..(name.Length + 9)];
        var sensor = await (await admin.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = host ?? hostId, Type = SensorType.MIKROTIK, Name = full, Interval = 300,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = address, SourceAddress = "192.0.2.10" },
        })).ReadAsync<SensorDetail>();
        await using (var db = app.NewDb())
        {
            var row = await db.MikrotikRouters.SingleAsync(r => r.SensorId == sensor.Id);
            row.SetupState = ready ? MikrotikSetupState.Ready : MikrotikSetupState.AwaitingRouter;
            row.Identity = full;
            row.Mode = mode;
            await db.SaveChangesAsync();
        }
        var router = new ScanRouter();
        setup?.Invoke(router);
        routers.Add(address, router);
        return (sensor.Id, full, router);
    }

    private static async Task<NetworkScanRow> StartAsync(object request, HttpClient? client = null)
    {
        var response = await (client ?? admin).PostJsonAsync("/api/v1/network-map/scans", request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<NetworkScanRow>();
    }

    private static async Task<NetworkScanDetail> RunAsync(Guid scanId)
    {
        await app.Services.GetRequiredService<NetworkScanner>().RunHereAsync(CancellationToken.None);
        return await (await admin.GetAsync($"/api/v1/network-map/scans/{scanId}")).ReadAsync<NetworkScanDetail>();
    }

    private static string Interfaces(params (string Name, string Mac, string Type)[] ports) =>
        JsonSerializer.Serialize(ports.Select(p => new Dictionary<string, object>
        {
            ["name"] = p.Name, ["default-name"] = p.Name, ["type"] = p.Type, ["mac-address"] = p.Mac, ["running"] = true, ["disabled"] = false,
        }));

    private static string Monitor(params (string Port, string Rate)[] ports) => JsonSerializer.Serialize(new
    {
        ports = ports.Select(p => new { name = p.Port }),
        monitor = ports.ToDictionary(p => p.Port, p => new Dictionary<string, object> { ["name"] = p.Port, ["status"] = "link-ok", ["rate"] = p.Rate }),
    });

    private static string Neighbors(string port, string identity, string mac, string theirPort) => JsonSerializer.Serialize(new
    {
        neighbors = new[] { new Dictionary<string, object> { ["interface"] = port, ["identity"] = identity, ["mac-address"] = mac, ["interface-name"] = theirPort, ["discovered-by"] = "mndp" } },
        settings = new { protocol = new[] { "mndp" } },
    });

    private static int macs;

    private static string NewMac()
    {
        var n = Interlocked.Increment(ref macs);
        var r = Random.Shared.Next(0, 0xFFFF);
        return $"00:1B:{(r >> 8) & 0xFF:X2}:{r & 0xFF:X2}:{(n >> 8) & 0xFF:X2}:{n & 0xFF:X2}";
    }

    // --- the scan ----------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task A_scan_reads_every_table_and_stores_the_router()
    {
        var mac = NewMac();
        var seen = NewMac();
        var (id, name, router) = await RouterAsync("gw", r =>
        {
            r.Tables[ScanTable.Interfaces] = Interfaces(("ether1", mac, "ether"), ("ether2", NewMac(), "ether"));
            r.Tables[ScanTable.Ethernet] = Monitor(("ether1", "1Gbps"), ("ether2", "100Mbps"));
            r.Tables[ScanTable.MacTable] = $$"""[{"mac-address":"{{seen}}","on-interface":"ether2","bridge":"bridge"}]""";
            r.Tables[ScanTable.Dhcp] = $$"""{"servers":[],"leases":[{"address":"192.168.88.20","mac-address":"{{seen}}","host-name":"cam-1"}]}""";
        });
        var scan = await StartAsync(new { scope = "Router", sensorId = id });
        Assert.AreEqual(NetworkScanState.Queued, scan.State);
        Assert.AreEqual(name, scan.ScopeName);
        var done = await RunAsync(scan.Id);
        Assert.AreEqual(NetworkScanState.Done, done.Scan.State, JsonSerializer.Serialize(done));
        var item = done.Routers.Single();
        Assert.AreEqual(NetworkScanItemState.Succeeded, item.State, item.Error);
        Assert.AreEqual(2, item.Counts!["ports"]);
        Assert.AreEqual(1, item.Counts["macs"]);
        Assert.AreEqual(NetworkScanScript.Order.Count, router.Commands.Count, "one exec per table");

        await using var db = app.NewDb();
        var ports = await db.NetworkRouterPorts.Where(p => p.SensorId == id).OrderBy(p => p.Name).ToListAsync();
        CollectionAssert.AreEqual(new[] { "baset-1g", "baset-100m" }, ports.Select(p => p.DetectedType).ToArray());
        var snapshot = await db.NetworkRouterSnapshots.SingleAsync(s => s.SensorId == id);
        Assert.AreEqual(1, snapshot.ScanNumber);
        Assert.AreEqual(NetworkScanScript.Order.Count, NetworkScanStore.ReadRaw(snapshot.Raw).Count);
        var row = await db.NetworkInterfaces.SingleAsync(i => i.Mac == seen);
        Assert.AreEqual((id, "ether2", "cam-1"), (row.AttachedSensorId!.Value, row.AttachedPort, row.HostName), "the analysis ran at the run's end");
        CollectionAssert.Contains(row.Ips, "192.168.88.20");
        Assert.AreEqual(1, await db.NetworkRouterPorts.CountAsync(p => p.SensorId == id && p.Name == "ether2" && p.MacCount == 1));
    }

    [TestMethod]
    public async Task A_failed_table_keeps_the_last_rows_and_failed_interfaces_fail_the_router()
    {
        var seen = NewMac();
        var (id, _, router) = await RouterAsync("keep", r =>
        {
            r.Tables[ScanTable.Interfaces] = Interfaces(("ether1", NewMac(), "ether"));
            r.Tables[ScanTable.MacTable] = $$"""[{"mac-address":"{{seen}}","on-interface":"ether1","bridge":"bridge"}]""";
        });
        await RunAsync((await StartAsync(new { scope = "Router", sensorId = id })).Id);
        router.Errors[ScanTable.MacTable] = "input does not match any value of interface";
        var second = await RunAsync((await StartAsync(new { scope = "Router", sensorId = id })).Id);
        var item = second.Routers.Single();
        Assert.AreEqual(NetworkScanItemState.Succeeded, item.State);
        StringAssert.Contains(item.TableErrors!["MacTable"], "input does not match");
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(1, await db.NetworkObservations.CountAsync(o => o.SensorId == id && o.Source == ObservationSource.BridgeHost && o.Mac == seen),
                "the MAC table's rows of the first scan stay");
            Assert.AreEqual(2, (await db.NetworkRouterSnapshots.SingleAsync(s => s.SensorId == id)).ScanNumber);
        }
        router.Errors[ScanTable.Interfaces] = "not allowed (9)";
        var third = await RunAsync((await StartAsync(new { scope = "Router", sensorId = id })).Id);
        Assert.AreEqual(NetworkScanItemState.Failed, third.Routers.Single().State);
        StringAssert.StartsWith(third.Routers.Single().Error, "interfaces:");
        Assert.AreEqual(1, third.Scan.Failed);
    }

    [TestMethod]
    public async Task Routers_that_cannot_be_scanned_are_skipped_with_their_reason()
    {
        var location = await (await admin.PostJsonAsync("/api/v1/locations", new { name = $"Skips {Guid.NewGuid():N}"[..20] })).ReadAsync<JsonElement>();
        var locationId = location.GetProperty("id").GetGuid();
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"skip-{Guid.NewGuid():N}", LocationId = locationId })).ReadAsync<HostDetail>();
        var (_, wgName, _) = await RouterAsync("wgonly", host: host.Id, mode: MikrotikMode.WireGuardOnly);
        var (_, newName, _) = await RouterAsync("fresh", host: host.Id, ready: false);
        var (_, okName, _) = await RouterAsync("fine", r => r.Tables[ScanTable.Interfaces] = Interfaces(("ether1", NewMac(), "ether")), host: host.Id);
        var scan = await StartAsync(new { scope = "Location", locationId });
        Assert.AreEqual(3, scan.Total);
        var done = await RunAsync(scan.Id);
        Assert.AreEqual("WireGuard only: there is no login.", done.Routers.Single(r => r.RouterName == wgName).Error);
        StringAssert.StartsWith(done.Routers.Single(r => r.RouterName == newName).Error, "Not ready yet");
        Assert.AreEqual(NetworkScanItemState.Succeeded, done.Routers.Single(r => r.RouterName == okName).State);
        Assert.AreEqual((1, 2, 0), (done.Scan.Succeeded, done.Scan.Skipped, done.Scan.Failed));
    }

    [TestMethod]
    public async Task One_router_at_a_time_and_a_routers_own_scan_goes_first()
    {
        var location = await (await admin.PostJsonAsync("/api/v1/locations", new { name = $"Order {Guid.NewGuid():N}"[..20] })).ReadAsync<JsonElement>();
        var locationId = location.GetProperty("id").GetGuid();
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"order-{Guid.NewGuid():N}", LocationId = locationId })).ReadAsync<HostDetail>();
        var a = await RouterAsync("a", r => r.Tables[ScanTable.Interfaces] = Interfaces(("ether1", NewMac(), "ether")), host: host.Id);
        var b = await RouterAsync("b", r => r.Tables[ScanTable.Interfaces] = Interfaces(("ether1", NewMac(), "ether")), host: host.Id);
        var scanner = app.Services.GetRequiredService<NetworkScanner>();
        var many = await StartAsync(new { scope = "Location", locationId });
        var one = await StartAsync(new { scope = "Router", sensorId = b.Id });
        var again = await admin.PostJsonAsync("/api/v1/network-map/scans", new { scope = "Router", sensorId = b.Id });
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode, "the same scope is queued");
        Assert.AreEqual(one.Id, (await again.ProblemAsync()).GetProperty("scanId").GetGuid());

        Assert.IsTrue(await scanner.RunNextAsync(CancellationToken.None));
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(NetworkScanItemState.Succeeded, (await db.NetworkScanItems.SingleAsync(i => i.ScanId == one.Id)).State, "the router's own run first");
            Assert.AreEqual(2, await db.NetworkScanItems.CountAsync(i => i.ScanId == many.Id && i.State == NetworkScanItemState.Queued));
        }
        var status = await (await admin.GetAsync("/api/v1/network-map/scans/status")).ReadAsync<NetworkScanStatus>();
        Assert.IsTrue(status.Queued.Any(s => s.Id == many.Id) || status.Running?.Id == many.Id);
        var cancelled = await (await admin.PostJsonAsync($"/api/v1/network-map/scans/{many.Id}/cancel", new { })).ReadAsync<NetworkScanRow>();
        Assert.AreEqual(NetworkScanState.Cancelled, cancelled.State);
        await scanner.RunHereAsync(CancellationToken.None);
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(2, await db.NetworkScanItems.CountAsync(i => i.ScanId == many.Id && i.State == NetworkScanItemState.Cancelled));
        }
        _ = a;
    }

    [TestMethod]
    public async Task A_routers_scan_needs_Commands_Run_and_every_other_Manage()
    {
        var (id, _, _) = await RouterAsync("rights", r => r.Tables[ScanTable.Interfaces] = Interfaces(("ether1", NewMac(), "ether")));
        var operatorClient = app.ClientWithRights("Commands:Run");
        await StartAsync(new { scope = "Router", sensorId = id }, operatorClient);
        var location = await operatorClient.PostJsonAsync("/api/v1/network-map/scans", new { scope = "Location", locationId = siteId });
        Assert.AreEqual(HttpStatusCode.Forbidden, location.StatusCode);
        Assert.AreEqual("missing_right", (await location.ProblemAsync()).GetProperty("code").GetString());
        var viewer = app.ClientWithRights("NetworkMap:Edit");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.PostJsonAsync("/api/v1/network-map/scans", new { scope = "All" })).StatusCode);
        await app.Services.GetRequiredService<NetworkScanner>().RunHereAsync(CancellationToken.None);
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.NetworkScanStarted && l.Subjects.Contains(id)));
    }

    // --- suggestions, connections, interfaces --------------------------------------------------------------------------------

    [TestMethod]
    public async Task Two_routers_become_a_suggestion_then_a_confirmed_connection()
    {
        var macA = NewMac();
        var macB = NewMac();
        string? nameA = null, nameB = null;
        var a = await RouterAsync("left", r =>
        {
            r.Tables[ScanTable.Interfaces] = Interfaces(("sfp-sfpplus1", macA, "ether"));
            r.Tables[ScanTable.Ethernet] = """{"ports":[{"name":"sfp-sfpplus1"}],"monitor":{"sfp-sfpplus1":{"status":"link-ok","rate":"10Gbps","sfp-module-present":true,"sfp-connector-type":"LC","sfp-wavelength":"1310nm"}}}""";
        });
        nameA = a.Name;
        var b = await RouterAsync("right", r =>
        {
            r.Tables[ScanTable.Interfaces] = Interfaces(("sfp-sfpplus3", macB, "ether"));
            r.Tables[ScanTable.Neighbors] = Neighbors("sfp-sfpplus3", nameA, macA, "sfp-sfpplus1");
        });
        nameB = b.Name;
        a.Router.Tables[ScanTable.Neighbors] = Neighbors("sfp-sfpplus1", nameB, macB, "sfp-sfpplus3");
        await RunAsync((await StartAsync(new { scope = "Router", sensorId = a.Id })).Id);
        await RunAsync((await StartAsync(new { scope = "Router", sensorId = b.Id })).Id);

        var page = await (await admin.GetAsync($"/api/v1/network-map/suggestions?deviceId={a.Id}")).ReadAsync<Paged<NetworkSuggestionItem>>();
        var s = page.Items.Single();
        Assert.AreEqual((Confidence.High, "smf-10g"), (s.Confidence, s.Type));
        CollectionAssert.AreEquivalent(new[] { "sfp-sfpplus1", "sfp-sfpplus3" }, new[] { s.A.Interface, s.B.Interface });
        Assert.AreEqual(NetworkDeviceKind.Router, s.A.Kind);

        var viewer = app.ClientWithRights("NetworkMap:Details");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.PostJsonAsync($"/api/v1/network-map/suggestions/{s.Id}/accept", new { })).StatusCode);
        var connection = await (await admin.PostJsonAsync($"/api/v1/network-map/suggestions/{s.Id}/accept", new { notes = "Building A to B" })).ReadAsync<NetworkConnectionItem>();
        Assert.AreEqual(("smf-10g", "Fiber SMF 10G", true, NetworkConnectionSource.Suggestion), (connection.Type, connection.TypeLabel, connection.Physical, connection.Source));

        await app.Services.GetRequiredService<NetworkAnalysis>().RunAsync(CancellationToken.None);
        var after = await (await admin.GetAsync($"/api/v1/network-map/connections/{connection.Id}")).ReadAsync<NetworkConnectionItem>();
        Assert.AreEqual(NetworkScanEvidence.Confirmed, after.Scan);
        Assert.AreEqual(0, (await (await admin.GetAsync($"/api/v1/network-map/suggestions?deviceId={a.Id}")).ReadAsync<Paged<NetworkSuggestionItem>>()).TotalCount);

        // The map draws it; the path follows it.
        var graph = await (await admin.GetAsync($"/api/v1/network-map/graph")).ReadAsync<NetworkGraph>();
        var edge = graph.Edges.Single(e => e.Id == connection.Id);
        Assert.AreEqual(($"router:{a.Id}", $"router:{b.Id}"), (edge.Source, edge.Target).Item1 == $"router:{a.Id}" ? (edge.Source, edge.Target) : (edge.Target, edge.Source));
        Assert.IsTrue(graph.Groups.Any(g => g.LocationId == siteId), "the routers' site is a circle");
        var path = await (await admin.GetAsync($"/api/v1/network-map/path?from=router:{a.Id}&to=router:{b.Id}")).ReadAsync<NetworkPath>();
        Assert.AreEqual(("10G", 1), (path.Slowest, path.SinglePoints.Count));
        Assert.AreEqual(connection.Id, path.Hops.Single().ConnectionId);

        // Two scans without it: unconfirmed (§5.5). While b's stored neighbor still shows it, a's scan alone confirms it.
        a.Router.Tables.Remove(ScanTable.Neighbors);
        b.Router.Tables.Remove(ScanTable.Neighbors);
        await RunAsync((await StartAsync(new { scope = "Router", sensorId = a.Id })).Id);
        Assert.AreEqual(NetworkScanEvidence.Confirmed, (await (await admin.GetAsync($"/api/v1/network-map/connections/{connection.Id}")).ReadAsync<NetworkConnectionItem>()).Scan);
        await RunAsync((await StartAsync(new { scope = "Router", sensorId = b.Id })).Id);
        Assert.AreEqual(NetworkScanEvidence.Confirmed, (await (await admin.GetAsync($"/api/v1/network-map/connections/{connection.Id}")).ReadAsync<NetworkConnectionItem>()).Scan,
            "one miss is no warning");
        await RunAsync((await StartAsync(new { scope = "Router", sensorId = a.Id })).Id);
        Assert.AreEqual(NetworkScanEvidence.Unconfirmed, (await (await admin.GetAsync($"/api/v1/network-map/connections/{connection.Id}")).ReadAsync<NetworkConnectionItem>()).Scan);

        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.NetworkSuggestionAccepted && l.Subjects.Contains(a.Id)));
    }

    [TestMethod]
    public async Task Connections_are_made_changed_and_deleted_by_hand()
    {
        var (router, _, _) = await RouterAsync("hand", r => r.Tables[ScanTable.Interfaces] = Interfaces(("ether3", NewMac(), "ether")));
        var server = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"srv-{Guid.NewGuid():N}", LocationId = siteId })).ReadAsync<HostDetail>();
        var request = new { a = new { sensorId = router, @interface = "ether3" }, b = new { hostId = server.Id, @interface = "eno1" }, type = "baset-10g", notes = "patch 14" };
        var bad = await admin.PostJsonAsync("/api/v1/network-map/connections", new { a = new { sensorId = router }, b = new { sensorId = router }, type = "warp-9" });
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.ProblemAsync()).GetProperty("errors");
        Assert.IsTrue(errors.TryGetProperty("type", out _) && errors.TryGetProperty("b", out _));

        var made = await (await admin.PostJsonAsync("/api/v1/network-map/connections", request)).ReadAsync<NetworkConnectionItem>();
        Assert.AreEqual((NetworkScanEvidence.Manual, NetworkConnectionSource.Manual), (made.Scan, made.Source));
        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.PostJsonAsync("/api/v1/network-map/connections", request)).StatusCode, "a pair of ends exists once");
        var changed = await (await admin.PutJsonAsync($"/api/v1/network-map/connections/{made.Id}", new { type = "dac-10g" })).ReadAsync<NetworkConnectionItem>();
        Assert.AreEqual("DAC 10G", changed.TypeLabel);
        var mine = await (await admin.GetAsync($"/api/v1/network-map/connections?deviceId={server.Id}")).ReadAsync<Paged<NetworkConnectionItem>>();
        Assert.AreEqual(made.Id, mine.Items.Single().Id);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/network-map/connections/{made.Id}")).StatusCode);

        await using var db = app.NewDb();
        var events = await db.AuditLog.Where(l => l.TargetId == made.Id.ToString()).Select(l => l.EventType).ToListAsync();
        CollectionAssert.IsSubsetOf(new[] { AuditEvents.NetworkConnectionCreated, AuditEvents.NetworkConnectionUpdated, AuditEvents.NetworkConnectionDeleted }, events);
    }

    [TestMethod]
    public async Task A_server_is_matched_by_its_MAC_and_an_assignment_brings_a_suggestion()
    {
        var serverMac = NewMac();
        var printerMac = NewMac();
        var (router, _, _) = await RouterAsync("edge", r =>
        {
            r.Tables[ScanTable.Interfaces] = Interfaces(("ether4", NewMac(), "ether"), ("ether5", NewMac(), "ether"));
            r.Tables[ScanTable.Ethernet] = Monitor(("ether4", "1Gbps"), ("ether5", "1Gbps"));
            r.Tables[ScanTable.MacTable] = $$"""[{"mac-address":"{{serverMac}}","on-interface":"ether4"},{"mac-address":"{{printerMac}}","on-interface":"ether5"}]""";
        });
        // A server: a WINDOWS sensor with its network section, as the agent sends it.
        var server = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"srv-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        var agentSensor = Guid.NewGuid();
        await using (var db = app.NewDb())
        {
            db.Sensors.Add(new SensorEntity { Id = agentSensor, ParentId = server.Id, Name = "Windows", Type = SensorType.WINDOWS, Enabled = true, Created = Clock.UtcNow,
                SecretWriteToken = Guid.NewGuid().ToString("N"), SecretReadToken = Guid.NewGuid().ToString("N") });
            await db.SaveChangesAsync();
            var section = new NetworkSection([new AdapterInfo("Ethernet0", null, "Ethernet", serverMac.ToLowerInvariant().Replace(':', '-'), "Up", 1_000_000_000, 1500,
                false, null, null, [], [], [], null, null, null, null, null, null, false)], [], [], [], [], new Dictionary<string, int>(), null, null, []);
            await AgentSections.StoreAsync(db, agentSensor, new SectionMessage(SectionNames.Network, 1, "h1", DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(section, AgentJson.Options)), CancellationToken.None);
            await db.SaveChangesAsync();
            Assert.AreEqual(serverMac, (await db.AgentAdapterMacs.SingleAsync(m => m.SensorId == agentSensor)).Mac, "the agent's MAC, normalised");
        }
        await RunAsync((await StartAsync(new { scope = "Router", sensorId = router })).Id);

        var page = await (await admin.GetAsync($"/api/v1/network-map/interfaces?routerId={router}")).ReadAsync<Paged<NetworkInterfaceItem>>();
        var serverRow = page.Items.Single(i => i.Mac == serverMac);
        Assert.AreEqual((MatchKind.ServerAdapter, server.Id, "Ethernet0"), (serverRow.Match, serverRow.MatchHostId!.Value, serverRow.MatchInterface));
        Assert.AreEqual(MatchKind.None, page.Items.Single(i => i.Mac == printerMac).Match);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.ClientWithRights("NetworkMap:View").GetAsync("/api/v1/network-map/interfaces")).StatusCode,
            "the interfaces need Details");
        var found = await (await admin.GetAsync($"/api/v1/network-map/interfaces?search={printerMac[..8].ToLowerInvariant()}")).ReadAsync<Paged<NetworkInterfaceItem>>();
        Assert.IsTrue(found.Items.Any(i => i.Mac == printerMac), "a MAC prefix finds it");

        var suggestions = await (await admin.GetAsync($"/api/v1/network-map/suggestions?deviceId={router}")).ReadAsync<Paged<NetworkSuggestionItem>>();
        var toServer = suggestions.Items.Single();
        Assert.AreEqual((Confidence.High, "baset-1g", "Ethernet0"), (toServer.Confidence, toServer.Type, toServer.B.SensorId is null ? toServer.B.Interface : toServer.A.Interface));

        var printer = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"printer-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        var assigned = await (await admin.PutJsonAsync($"/api/v1/network-map/interfaces/{printerMac}/assignment", new { hostId = printer.Id })).ReadAsync<NetworkInterfaceItem>();
        Assert.AreEqual((MatchKind.Manual, printer.Id), (assigned.Match, assigned.MatchHostId!.Value));
        await app.Services.GetRequiredService<NetworkAnalysis>().RunAsync(CancellationToken.None);
        var now = await (await admin.GetAsync($"/api/v1/network-map/suggestions?deviceId={printer.Id}")).ReadAsync<Paged<NetworkSuggestionItem>>();
        Assert.AreEqual(1, now.TotalCount, "the assigned interface is a host now");

        var dismissed = await (await admin.PostJsonAsync($"/api/v1/network-map/suggestions/{now.Items[0].Id}/dismiss", new { })).ReadAsync<NetworkSuggestionsDecided>();
        Assert.AreEqual(1, dismissed.Done);
        await app.Services.GetRequiredService<NetworkAnalysis>().RunAsync(CancellationToken.None);
        Assert.AreEqual(0, (await (await admin.GetAsync($"/api/v1/network-map/suggestions?deviceId={printer.Id}")).ReadAsync<Paged<NetworkSuggestionItem>>()).TotalCount,
            "dismissed stays dismissed while its key comes back");

        var device = await (await admin.GetAsync($"/api/v1/network-map/devices/Router/{router}")).ReadAsync<NetworkDeviceDetail>();
        Assert.AreEqual(2, device.Ports.Count);
        Assert.AreEqual(2, device.InterfacesSeen);
        Assert.IsNotNull(device.Interfaces);
        var asViewer = await (await app.ClientWithRights("NetworkMap:View").GetAsync($"/api/v1/network-map/devices/Router/{router}")).ReadAsync<NetworkDeviceDetail>();
        Assert.IsNull(asViewer.Interfaces, "the interfaces are Details");
        var hostPanel = await (await admin.GetAsync($"/api/v1/network-map/devices/Host/{server.Id}")).ReadAsync<NetworkDeviceDetail>();
        Assert.AreEqual(("Windows", "Ethernet0"), (hostPanel.Os, hostPanel.Adapters.Single().Adapter));
    }

    // --- the schedule --------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task The_schedule_is_off_until_turned_on_and_waits_for_a_first_full_scan()
    {
        var schedule = app.Services.GetRequiredService<NetworkScanSchedule>();
        await using (var db = app.NewDb())
        {
            await db.NetworkScans.Where(s => s.Scope == NetworkScanScope.All).ExecuteDeleteAsync();
        }
        var settings = await (await admin.GetAsync("/api/v1/network-map/settings")).ReadAsync<NetworkMapSettings>();
        Assert.AreEqual((false, 14, 2), (settings.ScanScheduleEnabled, settings.ScanIntervalDays, settings.ScanHour), "off at first, 14 days at 02:00");
        Assert.IsNull(await schedule.RunOnceAsync(CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PutJsonAsync("/api/v1/network-map/settings", new { scanScheduleEnabled = true, scanIntervalDays = 0, scanHour = 25, retentionDays = 90 })).StatusCode);
        await admin.PutJsonAsync("/api/v1/network-map/settings", new { scanScheduleEnabled = true, scanIntervalDays = 14, scanHour = 2, retentionDays = 90 });
        Assert.IsNull(await schedule.RunOnceAsync(CancellationToken.None), "no full scan yet: the first one is started by hand");
        var status = await (await admin.GetAsync("/api/v1/network-map/scans/status")).ReadAsync<NetworkScanStatus>();
        Assert.IsTrue(status.ScheduleEnabled);
        Assert.IsNull(status.NextScheduledAt);

        await using (var db = app.NewDb())
        {
            db.NetworkScans.Add(new NetworkScanEntity
            {
                Id = Guid.NewGuid(), Scope = NetworkScanScope.All, ScopeName = NetworkScanQueue.AllRouters, State = NetworkScanState.Done,
                CreatedAt = Clock.UtcNow.AddDays(-3), FinishedAt = Clock.UtcNow.AddDays(-3),
            });
            await db.SaveChangesAsync();
        }
        Assert.IsNull(await schedule.RunOnceAsync(CancellationToken.None), "3 days ago: not due");
        await using (var db = app.NewDb())
        {
            await db.NetworkScans.Where(s => s.Scope == NetworkScanScope.All).ExecuteUpdateAsync(u => u.SetProperty(s => s.CreatedAt, Clock.UtcNow.AddDays(-16)));
        }
        var started = await schedule.RunOnceAsync(CancellationToken.None);
        Assert.IsNotNull(started);
        Assert.AreEqual(NetworkScanTrigger.Schedule, started.Trigger);
        Assert.IsNull(await schedule.RunOnceAsync(CancellationToken.None), "one run at a time");

        await using (var db = app.NewDb())
        {
            await db.NetworkScanItems.Where(i => i.ScanId == started.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.State, NetworkScanItemState.Cancelled));
            await db.NetworkScans.Where(s => s.Id == started.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.State, NetworkScanState.Cancelled));
        }
        await admin.PutJsonAsync("/api/v1/network-map/settings", new { scanScheduleEnabled = false, scanIntervalDays = 14, scanHour = 2, retentionDays = 90 });
    }

    [TestMethod]
    public void The_next_scheduled_run_is_at_the_hour_after_the_interval()
    {
        var settings = new NetworkMapSettingsEntity { ScanScheduleEnabled = true, ScanIntervalDays = 14, ScanHour = 2 };
        var zone = Clock.TimeZone;
        try
        {
            Clock.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
            var last = new DateTime(2026, 11, 2, 15, 30, 0, DateTimeKind.Utc);
            Assert.AreEqual(new DateTime(2026, 11, 16, 1, 0, 0, DateTimeKind.Utc), NetworkScanQueue.NextScheduled(last, settings), "02:00 in Zurich is 01:00 UTC");
            Assert.IsNull(NetworkScanQueue.NextScheduled(null, settings));
            Assert.IsNull(NetworkScanQueue.NextScheduled(last, new NetworkMapSettingsEntity()), "off");
        }
        finally
        {
            Clock.TimeZone = zone;
        }
    }

    // --- saved positions (docs/network-map-positions-plan.md §7) -------------------------------------------------------------

    [TestMethod]
    public async Task Router_positions_are_saved_cleared_and_reset_with_Edit()
    {
        var (a, _, _) = await RouterAsync("pos-a");
        var (b, _, _) = await RouterAsync("pos-b");
        var ping = await V1.CreateSensorAsync(admin, hostId, SensorType.PING, parameters: "192.0.2.9", interval: 60);
        var viewer = app.ClientWithRights("NetworkMap:View");
        var editor = app.ClientWithRights("NetworkMap:Edit");
        const string url = "/api/v1/network-map/positions";
        object Body(params (Guid Id, double X, double Y)[] p) => new { positions = p.Select(x => new { sensorId = x.Id, x = x.X, y = x.Y }) };

        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.PutJsonAsync(url, Body((a, 1, 2)))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.DeleteAsync(url)).StatusCode);
        foreach (var bad in new[] { Body(), Body((a, 1e9, 0)), Body((a, 1, 2), (a, 3, 4)), Body((ping.Id, 1, 2)) })
        {
            var refused = await editor.PutJsonAsync(url, bad);
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, await refused.Content.ReadAsStringAsync());
        }

        // Added, then updated; a router deleted meanwhile is skipped, not refused.
        Assert.AreEqual(new NetworkPositionsResult(1, 0), await (await editor.PutJsonAsync(url, Body((a, 10.5, -20)))).ReadAsync<NetworkPositionsResult>());
        Assert.AreEqual(new NetworkPositionsResult(2, 1),
            await (await editor.PutJsonAsync(url, Body((a, 30.25, 40), (b, -1, 2), (Guid.NewGuid(), 5, 5)))).ReadAsync<NetworkPositionsResult>());
        var graph = await (await viewer.GetAsync("/api/v1/network-map/graph")).ReadAsync<NetworkGraph>();
        var node = graph.Nodes.Single(n => n.SensorId == a);
        Assert.AreEqual((30.25, 40.0), (node.X!.Value, node.Y!.Value), "saved points come back exactly");
        Assert.AreEqual(2, graph.SavedPositions);
        var device = await (await viewer.GetAsync($"/api/v1/network-map/devices/Router/{a}")).ReadAsync<NetworkDeviceDetail>();
        Assert.AreEqual(30.25, device.Position?.X);
        Assert.IsFalse(string.IsNullOrEmpty(device.Position?.UpdatedByName));

        // One back to automatic; a second time there is nothing to clear.
        Assert.AreEqual(HttpStatusCode.NoContent, (await editor.DeleteAsync($"{url}/{a}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await editor.DeleteAsync($"{url}/{a}")).StatusCode);
        graph = await (await viewer.GetAsync("/api/v1/network-map/graph")).ReadAsync<NetworkGraph>();
        Assert.IsNull(graph.Nodes.Single(n => n.SensorId == a).X);

        // Deleting the router deletes its position; Edit resets all.
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/sensors/{b}")).StatusCode);
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.NetworkRouterPositions.AnyAsync(p => p.SensorId == b));
        }
        await editor.PutJsonAsync(url, Body((a, 1, 1)));
        Assert.AreEqual(HttpStatusCode.NoContent, (await editor.DeleteAsync(url)).StatusCode);

        await using var check = app.NewDb();
        Assert.AreEqual(0, await check.NetworkRouterPositions.CountAsync());
        var events = await check.AuditLog.Where(l => l.Subjects.Contains(a)).Select(l => new { l.EventType, Summary = l.Detail }).ToListAsync();
        CollectionAssert.IsSubsetOf(new[] { AuditEvents.NetworkPositionsSaved, AuditEvents.NetworkPositionCleared, AuditEvents.NetworkPositionsReset },
            events.Select(e => e.EventType).ToList());
        Assert.IsTrue(events.Any(e => e.Summary == $"Saved the position of {node.Name} on the network map"), string.Join("; ", events.Select(e => e.Summary)));
    }

    [TestMethod]
    public async Task The_backfill_writes_the_MACs_of_servers_that_reported_before()
    {
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"old-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        var sensor = Guid.NewGuid();
        var mac = NewMac();
        await using var db = app.NewDb();
        db.Sensors.Add(new SensorEntity { Id = sensor, ParentId = host.Id, Name = "Linux", Type = SensorType.LINUX, Enabled = true, Created = Clock.UtcNow,
            SecretWriteToken = Guid.NewGuid().ToString("N"), SecretReadToken = Guid.NewGuid().ToString("N") });
        var section = new NetworkSection([new AdapterInfo("eno1", null, "ethernet", mac, "up", 10_000_000_000, 1500, true, null, null, [], [], [], null, null, null,
            null, null, null, false)], [], [], [], [], new Dictionary<string, int>(), null, null, []);
        db.AgentSections.Add(new AgentSectionEntity
        {
            SensorId = sensor, Section = SectionNames.Network, SchemaVersion = 1, Hash = "x", CollectedAt = Clock.UtcNow, ReceivedAt = Clock.UtcNow,
            ChangedAt = Clock.UtcNow, Content = AgentSections.Compress(JsonSerializer.Serialize(section, AgentJson.Options)),
        });
        await db.SaveChangesAsync();
        var migration = new PNMTD.Data.CustomMigrations.CustomMigration_AgentAdapterMacs();
        Assert.IsTrue(migration.ShouldRun(db));
        migration.Run(db);
        var row = await db.AgentAdapterMacs.SingleAsync(m => m.SensorId == sensor);
        Assert.AreEqual((mac, "eno1", 10_000_000_000L), (row.Mac, row.Adapter, row.SpeedBps!.Value));
    }
}
