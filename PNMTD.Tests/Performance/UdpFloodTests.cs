using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PNMTD.Lib.Udp;
using PNMTD.Tests.Udp;

namespace PNMTD.Tests.Performance;

/// <summary>
/// docs/udp-iot-plan.md §9, load: junk and forged packets at about 5,000 datagrams/s while real nodes
/// report. Valid packets keep getting their answers quickly, and the flood writes nothing.
/// <para>
/// The nodes report once before the flood and the clock: a node's first packet loads its state, and the process's
/// first packet also compiles the code and the queries on the way. That was the slowest answer every time, about ten
/// medians (56 ms against 5 ms on a laptop), and on a busy CI runner it took 700 ms, which with 60 answers is the p99
/// on its own (2026-11-26). The bound is about a running server.
/// </para>
/// </summary>
[TestClass]
public sealed class UdpFloodTests
{
    [TestMethod]
    public async Task AFloodDoesNotDelayValidPacketsAndWritesNothing()
    {
        using var udp = new UdpTestHarness();
        var nodes = new List<(Guid SensorId, UdpDevice Device)>();
        for (var i = 0; i < 20; i++)
        {
            var (sensor, device) = await udp.CreateNodeAsync();
            nodes.Add((sensor.Id, device));
        }
        var ids = nodes.Select(n => n.SensorId).ToList();
        int Rows(Data.PnmtdDbContext db) => db.Events.Count(e => ids.Contains(e.SensorId)) + db.UdpLastPackets.Count(p => ids.Contains(p.SensorId))
            + db.UdpCommandLog.Count();

        // Warm, untimed and without the flood: each node's first packet, and the process's.
        foreach (var (_, device) in nodes)
        {
            Assert.IsNotNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true), 5000),
                "a node is answered before the flood");
        }

        using var stop = new CancellationTokenSource();
        var sent = 0;
        var flood = Task.Run(async () =>
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Connect(IPAddress.Loopback, udp.Port);
            var random = new Random(3);
            var forger = new UdpDevice(nodes[0].Device.DeviceId, KeyFamily.HmacSha256, RandomNumberGenerator.GetBytes(16), [1, 2, 3, 4]);
            var clock = Stopwatch.StartNew();
            while (!stop.IsCancellationRequested)
            {
                for (var burst = 0; burst < 50; burst++)
                {
                    byte[] junk;
                    switch (sent % 3)
                    {
                        case 0:
                            junk = new byte[random.Next(1, 120)];
                            random.NextBytes(junk);
                            break;
                        case 1:
                            // A known node, a wrong key: a bad tag.
                            junk = forger.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true).Datagram;
                            break;
                        default:
                            // An unknown device.
                            junk = new UdpDevice((uint)random.Next(1_000_000, 2_000_000), KeyFamily.HmacSha256, new byte[16], [0, 0, 0, 0])
                                .Build(PacketType.Heartbeat, SecuritySuite.None, []).Datagram;
                            break;
                    }
                    await socket.SendAsync(junk);
                    sent++;
                }
                // About 5,000 a second.
                var due = TimeSpan.FromMilliseconds(sent / 5.0);
                if (clock.Elapsed < due)
                {
                    await Task.Delay(due - clock.Elapsed);
                }
            }
        });

        await Task.Delay(300);
        var before = await udp.WithDbAsync(db => Task.FromResult(Rows(db)));
        var answered = new List<(int Round, int Node, double Ms)>();
        var missed = 0;
        for (var round = 0; round < 3; round++)
        {
            for (var n = 0; n < nodes.Count; n++)
            {
                var device = nodes[n].Device;
                var watch = Stopwatch.StartNew();
                var answer = await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true), 2000);
                watch.Stop();
                if (answer is null)
                {
                    missed++;
                }
                else
                {
                    answered.Add((round, n, watch.Elapsed.TotalMilliseconds));
                }
            }
        }
        // However fast the nodes were answered: the flood has been real.
        while (Volatile.Read(ref sent) < 4000 && !flood.IsCompleted)
        {
            await Task.Delay(50);
        }
        stop.Cancel();
        await flood;
        await Task.Delay(500);

        var latencies = answered.Select(a => a.Ms).Order().ToList();
        var p99 = latencies[(int)Math.Ceiling(latencies.Count * 0.99) - 1];
        // Which answers were slow: one stall or many, the first round or all of them.
        var slowest = string.Join(", ", answered.OrderByDescending(a => a.Ms).Take(5).Select(a => $"round {a.Round} node {a.Node}: {a.Ms:0.0} ms"));
        Console.WriteLine($"flood: {sent} datagrams; valid: {latencies.Count} answered, {missed} missed, median {latencies[latencies.Count / 2]:0.0} ms, p99 {p99:0.0} ms; slowest: {slowest}");
        Assert.AreEqual(0, missed, "every valid packet got its answer");
        Assert.IsLessThan(250, p99, $"p99 {p99:0.0} ms (target below 100 ms on a server; slack for test machines); slowest: {slowest}");
        Assert.IsGreaterThanOrEqualTo(4000, sent);

        // Only the valid heartbeats wrote: at most an event and a last packet each, nothing for the flood.
        var after = await udp.WithDbAsync(db => Task.FromResult(Rows(db)));
        Assert.IsLessThanOrEqualTo(before + 2 * nodes.Count * 3, after);
    }
}
