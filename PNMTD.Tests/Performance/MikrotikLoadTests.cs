using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PNMTD.Data;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.WireGuard;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.WireGuard;

namespace PNMTD.Tests.Performance;

/// <summary>
/// The load targets of docs/mikrotik-plan.md §14 (M7): 500 routers at a 5-minute interval, behind a connector
/// with a real router's latency, polled by the real poller; and the gateway's files for 1,000 peers. Opt-in,
/// it takes about a minute: PNMTD_PERF=1.
/// </summary>
[TestClass]
public sealed class MikrotikLoadTests
{
    private const int Routers = 500;

    [TestMethod]
    public async Task Five_hundred_routers_are_polled_well_within_their_interval()
    {
        if (Environment.GetEnvironmentVariable("PNMTD_PERF") != "1")
        {
            Assert.Inconclusive("Opt-in: PNMTD_PERF=1");
        }
        var connector = new LatencyConnector(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(250));
        using var app = new TestApp(settings: new Dictionary<string, string?> { ["Mikrotik:Ssh:MaxConcurrentSessions"] = "8" },
            services: s => s.Replace(ServiceDescriptor.Singleton<IRouterConnector>(connector)));
        var ids = new List<Guid>();
        await using (var db = app.NewDb())
        {
            db.SetKeyValueEntryByEnum(KeyValueKeyEnums.UPLINK_OK, true);
            var host = new HostEntity { Id = Guid.NewGuid(), Name = $"load-{Guid.NewGuid():N}"[..20], Created = DateTime.UtcNow, Enabled = true };
            db.Hosts.Add(host);
            for (var i = 0; i < Routers; i++)
            {
                var sensor = new SensorEntity
                {
                    Id = Guid.NewGuid(), ParentId = host.Id, Type = SensorType.MIKROTIK, Name = $"router {i}", Created = DateTime.UtcNow,
                    Enabled = true, Interval = 300, GracePeriod = 600, SecretWriteToken = $"lw{i}-{Guid.NewGuid():N}"[..32],
                    SecretReadToken = $"lr{i}-{Guid.NewGuid():N}"[..32],
                };
                db.Sensors.Add(sensor);
                db.MikrotikRouters.Add(new MikrotikRouterEntity
                {
                    SensorId = sensor.Id, Mode = MikrotikMode.DirectSsh, Address = $"load-{i}.test", Created = DateTime.UtcNow,
                    ScriptChangedAt = DateTime.UtcNow,
                });
                ids.Add(sensor.Id);
            }
            await db.SaveChangesAsync();
        }
        var poller = app.Services.GetRequiredService<RouterPoller>();

        // The first round: every router's first login, with its first export (the worst case).
        var watch = Stopwatch.StartNew();
        var polled = await poller.RunOnceAsync(CancellationToken.None);
        var first = watch.Elapsed;
        Assert.IsGreaterThanOrEqualTo(Routers, polled);
        Assert.IsLessThanOrEqualTo(8, connector.MaxSessions, "never more sessions than MaxConcurrentSessions");

        // Steady state: two more rounds; the third writes no event rows, only the readings.
        async Task<TimeSpan> RoundAsync()
        {
            await using (var db = app.NewDb())
            {
                await db.MikrotikRouters.Where(r => ids.Contains(r.SensorId))
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.LastPollAt, DateTime.UtcNow.AddMinutes(-10)));
            }
            var w = Stopwatch.StartNew();
            await poller.RunOnceAsync(CancellationToken.None);
            return w.Elapsed;
        }
        await RoundAsync();
        long events, backups;
        await using (var db = app.NewDb())
        {
            events = await db.Events.LongCountAsync(e => ids.Contains(e.SensorId));
            backups = await db.MikrotikBackups.LongCountAsync(b => b.SensorId != null && ids.Contains(b.SensorId.Value));
        }
        var steady = await RoundAsync();
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(events, await db.Events.LongCountAsync(e => ids.Contains(e.SensorId)), "unchanged routers add no event rows (deduplicated)");
            Assert.AreEqual(backups, await db.MikrotikBackups.LongCountAsync(b => b.SensorId != null && ids.Contains(b.SensorId.Value)), "no export due");
            Assert.AreEqual(Routers, await db.MikrotikRouters.CountAsync(r => ids.Contains(r.SensorId) && r.SetupState == MikrotikSetupState.Ready));
            await db.Sensors.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.Enabled, false));
        }
        var measured = $"{Routers} routers, 8 sessions: first round {first.TotalSeconds:0.0} s, steady round {steady.TotalSeconds:0.0} s, " +
            $"at most {connector.MaxSessions} sessions at once";
        Console.WriteLine(measured);
        // The runner shows no output of a passing test: the numbers also go next to the test assembly.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "mikrotik-load.txt"), measured + "\n");
        Assert.IsLessThan(TimeSpan.FromSeconds(300), first, "the first round fits the 5-minute interval");
        Assert.IsLessThan(TimeSpan.FromSeconds(150), steady, "a steady round takes less than half the interval");
    }

    [TestMethod]
    public void The_gateways_files_for_a_thousand_peers_are_built_in_milliseconds()
    {
        var plan = AddressPlan.Default;
        var peers = new List<GatewayPeer>();
        var used = new List<IPAddress>();
        for (var i = 0; i < 1000; i++)
        {
            var address = plan.Allocate(used)!;
            used.Add(address);
            peers.Add(new GatewayPeer(WireGuardKeys.Generate().PublicKey, address.ToString()));
        }
        var access = Enumerable.Range(0, 50).Select(t => new TunnelAccess(peers[950 + t].Address, peers.Take(200).Select(p => p.Address).ToList())).ToList();
        var watch = Stopwatch.StartNew();
        var conf = GatewayConfig.SyncConf(WireGuardKeys.Generate().PrivateKey, 51820, peers);
        var nft = NftRuleset.Build("wg0", plan.AccessAddress.ToString(), [22, 8291, 80, 443], access);
        watch.Stop();
        Assert.AreEqual(1000, conf.Split("[Peer]").Length - 1);
        StringAssert.Contains(nft, "policy drop;");
        Assert.IsLessThan(TimeSpan.FromMilliseconds(500), watch.Elapsed, $"built in {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>Routers that answer like 7.25.1, after a connect and per command latency, counting the sessions open at once.</summary>
    private sealed class LatencyConnector(TimeSpan connect, TimeSpan exec) : IRouterConnector
    {
        private static readonly string Poll = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikrotik", "7.25.1", "poll.json"));
        private static readonly string Export = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikrotik", "7.25.1", "export.rsc"));
        private static readonly RouterHostKey Key = new("ssh-ed25519", "LoadTestHostKeyLoadTestHostKeyLoadTestHostK");
        private int open;
        private int max;

        private TimeSpan Delay { get; } = exec;

        public int MaxSessions => max;

        public async Task<IRouterSession> ConnectAsync(RouterTarget target, CancellationToken ct)
        {
            if (!target.Host.StartsWith("load-", StringComparison.Ordinal))
            {
                throw new RouterSessionException(RouterFailureKind.Unreachable, "SSH: connection timed out");
            }
            var now = Interlocked.Increment(ref open);
            int seen;
            while (now > (seen = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, now, seen) != seen)
            {
            }
            await Task.Delay(connect, ct);
            return new Session(this, target.Host);
        }

        private sealed class Session(LatencyConnector owner, string host) : IRouterSession
        {
            public RouterHostKey HostKey => Key;

            public async Task<RouterExecResult> ExecAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
            {
                await Task.Delay(owner.Delay, ct);
                if (command.StartsWith("/export", StringComparison.Ordinal))
                {
                    return new RouterExecResult(Export, "", 0, false, false, owner.Delay);
                }
                var json = JsonNode.Parse(Poll)!;
                json["identity"] = host;
                return new RouterExecResult(json.ToJsonString(), "", 0, false, false, owner.Delay);
            }

            public Task<byte[]> DownloadAsync(string path, long maxBytes, CancellationToken ct) => throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                Interlocked.Decrement(ref owner.open);
                return ValueTask.CompletedTask;
            }
        }
    }
}
