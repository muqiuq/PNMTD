using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Udp;
using PNMTD.Models.Enums;
using PNMTD.Udp;

namespace PNMTD.Tests.Udp;

/// <summary>
/// UDP nodes end to end (docs/udp-iot-plan.md §9): real datagrams to the listener on loopback, the
/// processor and PostgreSQL behind it, the API in front.
/// </summary>
[TestClass]
public sealed class UdpNodeTests
{
    private static UdpTestHarness udp = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => udp = new UdpTestHarness();

    [ClassCleanup]
    public static void Stop() => udp.Dispose();

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes);

    // --- heartbeats, data, boot ---------------------------------------------------------------

    [TestMethod]
    public async Task AHeartbeatIsAnEventAndGetsItsAck()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        Assert.AreEqual(UdpSecretSource.Generated, sensor.Udp!.KeySource);
        var heartbeat = device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true);
        var ack = await udp.ExchangeAsync(device, heartbeat);
        Assert.AreEqual(PacketType.Ack, ack?.Header.Type);

        var events = await udp.EventsAsync(sensor.Id);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(PNMTStatusCodes.UDP_ALIVE, events[0].Code);
        Assert.AreEqual("Alive", events[0].Message);
        StringAssert.StartsWith(events[0].Source, "udp:127.0.0.1:");

        var last = await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}/udp/last-packets").Result.ReadAsync<List<UdpPacketView>>();
        Assert.AreEqual(PacketType.Heartbeat, last.Single().Type);
        CollectionAssert.AreEqual(heartbeat.Datagram, last[0].Datagram);
        Assert.IsNotNull(last[0].Response, "the ACK is kept with it");
        Assert.AreEqual("DEVICE ID", last[0].Fields[2].Name);
    }

    [TestMethod]
    public async Task WithoutARequestThereIsNoAck()
    {
        var (_, device) = await udp.CreateNodeAsync();
        Assert.IsNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, []), 400));
    }

    [TestMethod]
    public async Task ASteadyStreamKeepsTwoEventRowsAndOnlyTheLastPacket()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        for (var i = 0; i < 6; i++)
        {
            Assert.IsNotNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true)));
        }
        Assert.AreEqual(2, (await udp.EventsAsync(sensor.Id)).Count);
        Assert.AreEqual(1, await udp.WithDbAsync(db => db.UdpLastPackets.CountAsync(p => p.SensorId == sensor.Id)));
        var last = await udp.WithDbAsync(db => db.UdpLastPackets.SingleAsync(p => p.SensorId == sensor.Id));
        Assert.AreEqual(6, last.Fcnt);
    }

    [TestMethod]
    public async Task DataAndBootAreEvents()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        await udp.ExchangeAsync(device, device.Build(PacketType.Data, SecuritySuite.HmacSha256_64, "21.5C"u8, ack: true));
        await udp.ExchangeAsync(device, device.Build(PacketType.Data, SecuritySuite.HmacSha256_32, [0x00, 0xFF], ack: true));
        await udp.ExchangeAsync(device, device.Build(PacketType.Boot, SecuritySuite.HmacSha256_32, Payloads.Boot(new BootPayload(2, "1.4.2")), ack: true));
        var events = await udp.EventsAsync(sensor.Id);
        Assert.AreEqual(PNMTStatusCodes.UDP_DATA, events[0].Code);
        Assert.AreEqual("21.5C", events[0].Message);
        Assert.AreEqual("hex:00FF", events[1].Message);
        Assert.AreEqual(PNMTStatusCodes.UDP_BOOT, events[2].Code);
        Assert.AreEqual("Restarted: watchdog (1.4.2)", events[2].Message);
        var detail = await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}").Result.ReadAsync<SensorDetail>();
        Assert.AreEqual("1.4.2", detail.Udp!.FirmwareVersion);
        Assert.AreEqual("watchdog", detail.Udp.LastBootReason);
        Assert.AreEqual(2, (await udp.WithDbAsync(db => db.UdpLastPackets.CountAsync(p => p.SensorId == sensor.Id))));
    }

    // --- what is refused --------------------------------------------------------------------

    [TestMethod]
    public async Task RejectedPacketsGetNoAnswerAndWriteNothing()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        var good = device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true);
        Assert.IsNotNull(await udp.ExchangeAsync(device, good));
        // A second packet, so that the first is an old one: resending the very last packet is a
        // retransmission, which gets its answer again (§3.9).
        Assert.IsNotNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true)));
        var before = await udp.EventsAsync(sensor.Id);

        var forged = good.Datagram.ToArray();
        forged[^1] ^= 1;
        var stranger = new UdpDevice(device.DeviceId, KeyFamily.HmacSha256, RandomNumberGenerator.GetBytes(16), device.Token);
        var cases = new (string Name, byte[] Datagram)[]
        {
            ("a bad tag", stranger.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true).Datagram),
            ("a replay", good.Datagram),
            ("a suite the node does not allow", device.Build(PacketType.Heartbeat, SecuritySuite.Token32, [], ack: true).Datagram),
            ("a downlink type", PacketCodec.Build(new PacketHeader(PacketType.Ack, SecuritySuite.HmacSha256_32, device.DeviceId, 9), [], device.Key, device.Token, 9)),
            ("junk", [0x40, 0x82]),
        };
        foreach (var (name, datagram) in cases)
        {
            Assert.IsNull(await udp.SendAsync(datagram, 400), name);
        }
        var after = await udp.EventsAsync(sensor.Id);
        Assert.AreEqual(before.Count, after.Count, "no new event");
        Assert.AreEqual(before[^1].Created, after[^1].Created, "no event moved");
        var rejections = await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}/udp/rejections").Result.ReadAsync<List<UdpRejectionView>>();
        CollectionAssert.IsSubsetOf(new[] { UdpRejectReason.BadTag, UdpRejectReason.Replay, UdpRejectReason.SuiteNotAllowed, UdpRejectReason.WrongDirection },
            rejections.Select(r => r.Reason).Distinct().ToList());
    }

    [TestMethod]
    public async Task ARetransmissionGetsTheSameAnswerAndIsProcessedOnce()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        var data = device.Build(PacketType.Data, SecuritySuite.HmacSha256_32, "once"u8, ack: true);
        var first = await udp.SendAsync(data.Datagram);
        var again = await udp.SendAsync(data.Datagram);
        Assert.IsNotNull(first);
        CollectionAssert.AreEqual(first, again);
        Assert.AreEqual(1, (await udp.EventsAsync(sensor.Id)).Count);
    }

    [TestMethod]
    public async Task UnknownDevicesAreListedForTheAdmin()
    {
        var stranger = new UdpDevice(4_000_000_123, KeyFamily.HmacSha256, RandomNumberGenerator.GetBytes(16), [1, 2, 3, 4]);
        Assert.IsNull(await udp.SendAsync(stranger.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true).Datagram, 400));
        var unknown = await udp.Admin.GetAsync("/api/v1/udp/unknown-senders").Result.ReadAsync<List<UdpUnknownSenderItem>>();
        Assert.IsTrue(unknown.Any(u => u.DeviceId == 4_000_000_123 && !u.NodeExists));
    }

    // --- commands ---------------------------------------------------------------------------

    private static async Task<UdpCommandItem> QueueAsync(Guid sensorId, byte[] payload, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["payload"] = B64(payload) };
        foreach (var p in extra?.GetType().GetProperties() ?? [])
        {
            body[p.Name] = p.GetValue(extra);
        }
        var created = await udp.Admin.PostJsonAsync($"/api/v1/sensors/{sensorId}/udp/commands", body).Result.ReadAsync<UdpCommandCreated>();
        return created.Command;
    }

    [TestMethod]
    public async Task ACommandGoesOutWithTheNextUplinkAndItsResultIsLogged()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        var command = await QueueAsync(sensor.Id, [0x02, 0x02, 0x58]);
        Assert.AreEqual(UdpCommandState.Queued, command.State);

        var heartbeat = device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, []);
        var down = await udp.ExchangeAsync(device, heartbeat);
        Assert.AreEqual(PacketType.Command, down?.Header.Type);
        Assert.IsFalse(down!.Header.More);
        var (cmdId, bytes) = Payloads.ReadCommand(down.Payload)!.Value;
        Assert.AreEqual(command.CmdId, cmdId);
        CollectionAssert.AreEqual(new byte[] { 0x02, 0x02, 0x58 }, bytes);

        var result = device.Build(PacketType.CmdResult, SecuritySuite.HmacSha256_32, Payloads.CmdResult(new CmdResultPayload(cmdId, 0, "OK"u8.ToArray())), ack: true);
        Assert.AreEqual(PacketType.Ack, (await udp.ExchangeAsync(device, result))?.Header.Type);

        var commands = await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}/udp/commands").Result.ReadAsync<Paged<UdpCommandItem>>();
        var done = commands.Items.Single();
        Assert.AreEqual(UdpCommandState.Completed, done.State);
        Assert.AreEqual("OK", done.ResultStatusText);
        CollectionAssert.AreEqual("OK"u8.ToArray(), done.Result);

        var log = await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}/udp/commands/{command.Id}/log").Result.ReadAsync<List<UdpLogItem>>();
        CollectionAssert.AreEqual(new[] { UdpLogKind.Queued, UdpLogKind.Sent, UdpLogKind.ResultReceived }, log.Select(l => l.Kind).ToList());
        Assert.AreEqual("test-admin", log[0].UserName);
        Assert.AreEqual(UdpDirection.Down, log[1].Direction);
        Assert.IsNotNull(log[1].Fields);
        Assert.IsTrue(log[1].Fields!.Any(f => f.Name == "CMD_ID"));
        CollectionAssert.AreEqual(result.Datagram, log[2].Datagram);
    }

    [TestMethod]
    public async Task CommandsDrainInOrderWithMoreAndAreResentUntilTheirResult()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        var first = await QueueAsync(sensor.Id, [1], new { maxAttempts = 2 });
        var second = await QueueAsync(sensor.Id, [2], new { delivery = UdpCommandDelivery.FireAndForget });

        var a = await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, []));
        Assert.AreEqual(first.CmdId, a!.Payload[0]);
        Assert.IsTrue(a.Header.More);
        // No result: the same command again with the next uplink, then it fails after its 2 attempts.
        var b = await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, []));
        Assert.AreEqual(first.CmdId, b!.Payload[0]);
        var c = await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, []));
        Assert.AreEqual(second.CmdId, c!.Payload[0], "the first failed, so the second goes out");
        Assert.IsFalse(c.Header.More);
        Assert.IsNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, []), 400), "fire and forget: sent once");

        var states = await udp.WithDbAsync(db => db.UdpCommands.Where(x => x.SensorId == sensor.Id).OrderBy(x => x.Created).Select(x => x.State).ToListAsync());
        CollectionAssert.AreEqual(new[] { UdpCommandState.Failed, UdpCommandState.Sent }, states);
    }

    [TestMethod]
    public async Task ACancelledCommandIsNotSent()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        var command = await QueueAsync(sensor.Id, [9]);
        var cancel = await udp.Admin.DeleteAsync($"/api/v1/sensors/{sensor.Id}/udp/commands/{command.Id}");
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.IsNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, []), 400));
        var again = await udp.Admin.DeleteAsync($"/api/v1/sensors/{sensor.Id}/udp/commands/{command.Id}");
        Assert.AreEqual(System.Net.HttpStatusCode.Conflict, again.StatusCode);
    }

    [TestMethod]
    public async Task ResponsesUseTheReceivedSuiteUnlessConfigured()
    {
        // As received by default; the node's response suite or a command's own suite otherwise (decision 6).
        var (sensor, device) = await udp.CreateNodeAsync(suites: [SecuritySuite.Token32, SecuritySuite.HmacSha256_32, SecuritySuite.HmacSha256_64]);
        var ack = await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.Token32, [], ack: true));
        Assert.AreEqual(SecuritySuite.Token32, ack!.Header.Suite);

        var command = await QueueAsync(sensor.Id, [7], new { suite = SecuritySuite.HmacSha256_64 });
        device.AcceptedSuites.Add(SecuritySuite.HmacSha256_64);
        var down = await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.Token32, []));
        Assert.AreEqual(PacketType.Command, down!.Header.Type);
        Assert.AreEqual(SecuritySuite.HmacSha256_64, down.Header.Suite);
        Assert.AreEqual(command.CmdId, down.Payload[0]);
    }

    [TestMethod]
    public async Task AKeyedResponseSuiteEnforcesTheCounterForTokenUplinks()
    {
        var (_, device) = await udp.CreateNodeAsync(suites: [SecuritySuite.Token32, SecuritySuite.HmacSha256_32], responseSuite: SecuritySuite.HmacSha256_32);
        device.ResponseSuite = SecuritySuite.HmacSha256_32;
        var token = device.Build(PacketType.Heartbeat, SecuritySuite.Token32, [], ack: true);
        var ack = await udp.ExchangeAsync(device, token);
        Assert.AreEqual(SecuritySuite.HmacSha256_32, ack!.Header.Suite);
        // The same counter, other bytes (a replay of an older TOKEN32 packet with A cleared): refused.
        var older = device.Build(PacketType.Heartbeat, SecuritySuite.Token32, [], fcnt: token.Fcnt);
        Assert.IsNull(await udp.SendAsync(older.Datagram, 400));
    }

    [TestMethod]
    public async Task AnUnkeyedUplinkCannotLockTheKeyedSuitesOut()
    {
        // NONE and TOKEN32 have a window of their own (§3.7): a forged NONE heartbeat with a high counter, which anyone who knows
        // the device id can send, must not make the device's signed packets look like replays.
        var (sensor, device) = await udp.CreateNodeAsync(suites: [SecuritySuite.None, SecuritySuite.HmacSha256_32]);
        Assert.IsNotNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true)));
        Assert.IsNotNull(await udp.SendAsync(device.Build(PacketType.Heartbeat, SecuritySuite.None, [], ack: true, fcnt: 40_000).Datagram));

        // The device's next signed heartbeat, counter 2, is as fresh as before.
        var ack = await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true));
        Assert.AreEqual(PacketType.Ack, ack!.Header.Type);
        var row = await udp.WithDbAsync(db => db.UdpNodes.AsNoTracking().SingleAsync(n => n.SensorId == sensor.Id));
        Assert.AreEqual(2, row.FcntHigh, "the keyed suites' window");
        Assert.AreEqual(40_000, row.PlainFcntHigh, "the plain window");

        // Another forged one, far off the plain window: that window starts again, the keyed one doesn't notice.
        Assert.IsNotNull(await udp.SendAsync(device.Build(PacketType.Heartbeat, SecuritySuite.None, [], ack: true, fcnt: 1).Datagram));
        Assert.IsNotNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true)));
        row = await udp.WithDbAsync(db => db.UdpNodes.AsNoTracking().SingleAsync(n => n.SensorId == sensor.Id));
        Assert.AreEqual(3, row.FcntHigh);
    }

    [TestMethod]
    public async Task AnEncryptedAnswerToAnUnkeyedUplinkNeedsANodeWithoutKeyedUplinks()
    {
        // The windows are separate (§3.7), so a NONE uplink's counter could meet a signed one's and repeat an AES-CCM nonce: on a
        // node that also allows keyed uplinks, the encrypted response suite is refused for a NONE uplink and the ack comes as the
        // uplink did. A node that allows no keyed uplink still gets its encrypted acks, bound to the plain window as before.
        var (_, mixed) = await udp.CreateNodeAsync(KeyFamily.Aes, suites: [SecuritySuite.None, SecuritySuite.AesCmac32, SecuritySuite.AesCcm64],
            responseSuite: SecuritySuite.AesCcm64);
        mixed.ResponseSuite = SecuritySuite.AesCcm64;
        var plain = await udp.ExchangeAsync(mixed, mixed.Build(PacketType.Heartbeat, SecuritySuite.None, [], ack: true));
        Assert.AreEqual(SecuritySuite.None, plain!.Header.Suite);
        var signed = await udp.ExchangeAsync(mixed, mixed.Build(PacketType.Heartbeat, SecuritySuite.AesCmac32, [], ack: true));
        Assert.AreEqual(SecuritySuite.AesCcm64, signed!.Header.Suite);

        var (_, unkeyed) = await udp.CreateNodeAsync(KeyFamily.Aes, suites: [SecuritySuite.None], responseSuite: SecuritySuite.AesCcm64);
        unkeyed.ResponseSuite = SecuritySuite.AesCcm64;
        var encrypted = await udp.ExchangeAsync(unkeyed, unkeyed.Build(PacketType.Heartbeat, SecuritySuite.None, [], ack: true));
        Assert.AreEqual(SecuritySuite.AesCcm64, encrypted!.Header.Suite);
    }

    [TestMethod]
    public async Task CommandsAndTheirLogOutliveTheNode()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        var command = await QueueAsync(sensor.Id, [5]);
        await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, []));
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, (await udp.Admin.DeleteAsync($"/api/v1/sensors/{sensor.Id}")).StatusCode);
        var kept = await udp.WithDbAsync(db => db.UdpCommands.SingleAsync(c => c.Id == command.Id));
        Assert.IsNull(kept.SensorId);
        Assert.AreEqual(sensor.Name, kept.NodeName);
        var log = await udp.Admin.GetAsync($"/api/v1/udp/command-log?deviceId={sensor.Udp!.DeviceId}").Result.ReadAsync<Paged<UdpLogItem>>();
        Assert.IsTrue(log.Items.Count >= 2);
        Assert.IsTrue(log.Items.All(l => l.SensorId is null && l.NodeName == sensor.Name));
        Assert.IsFalse(await udp.WithDbAsync(db => db.UdpNodes.AnyAsync(n => n.SensorId == sensor.Id)));
    }

    [TestMethod]
    public async Task TheMaintenanceTaskExpiresCommands()
    {
        var (sensor, _) = await udp.CreateNodeAsync();
        var command = await QueueAsync(sensor.Id, [1]);
        await udp.WithDbAsync(db => db.UdpCommands.Where(c => c.Id == command.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTime.UtcNow.AddMinutes(-1))));
        Assert.IsGreaterThanOrEqualTo(1, await udp.App.Services.GetRequiredService<UdpMaintenanceTask>().RunAsync(CancellationToken.None));
        var kinds = await udp.WithDbAsync(db => db.UdpCommandLog.Where(l => l.CommandId == command.Id).Select(l => l.Kind).ToListAsync());
        CollectionAssert.Contains(kinds, UdpLogKind.Expired);
    }

    // --- keys -------------------------------------------------------------------------------

    [TestMethod]
    public async Task AKeyEnteredManuallyReplacesTheOldOneAtOnce()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        Assert.IsNotNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true)));

        var bad = await udp.Admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}/udp/key", new { key = "00000000000000000000000000000000" });
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        var shortKey = await udp.Admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}/udp/key", new { key = "0102" });
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, shortKey.StatusCode);

        var key = RandomNumberGenerator.GetBytes(16);
        var changed = await udp.Admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}/udp/key",
            new { key = "0x" + string.Join(":", key.Select(b => b.ToString("x2"))) }).Result.ReadAsync<UdpSecretsChanged>();
        Assert.AreEqual(Convert.ToHexString(key), changed.Secrets.Key);

        Assert.IsNull(await udp.SendAsync(device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true).Datagram, 400), "the old key");
        var renewed = new UdpDevice(device.DeviceId, KeyFamily.HmacSha256, key, device.Token);
        Assert.IsNotNull(await udp.ExchangeAsync(renewed, renewed.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true)),
            "the new key, and the counter starts again");

        var detail = await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}").Result.ReadAsync<SensorDetail>();
        Assert.AreEqual(UdpSecretSource.Manual, detail.Udp!.KeySource);
        Assert.AreEqual("test-admin", detail.Udp.KeyChangedBy);
        var audit = await udp.WithDbAsync(db => db.AuditLog.Where(a => a.EventType == global::PNMTD.Audit.AuditEvents.UdpKeyChanged).OrderByDescending(a => a.CreatedAt).FirstAsync());
        Assert.DoesNotContain(Convert.ToHexString(key), audit.Detail!, "never the key itself");
        Assert.DoesNotContain(Convert.ToHexString(key).ToLowerInvariant(), audit.Detail!);
    }

    // --- device ids and editing --------------------------------------------------------------

    [TestMethod]
    public void DeviceIdsAreTriedFromARandomOneOnWithPlusOneAtMostAHundredTimes()
    {
        Assert.AreEqual(255L, global::PNMTD.Services.Domain.UdpNodeService.MaxDeviceId(1));
        Assert.AreEqual(65_535L, global::PNMTD.Services.Domain.UdpNodeService.MaxDeviceId(2));
        Assert.AreEqual(4_294_967_295L, global::PNMTD.Services.Domain.UdpNodeService.MaxDeviceId(4));
        Assert.AreEqual(4_294_967_295L, global::PNMTD.Services.Domain.UdpNodeService.MaxDeviceId(9), "at most 4 bytes");

        var candidates = global::PNMTD.Services.Domain.UdpNodeService.DeviceIdCandidates;
        CollectionAssert.AreEqual(new long[] { 7, 8, 9 }, candidates(7, 255).Take(3).ToList());
        CollectionAssert.AreEqual(new long[] { 254, 255, 1, 2 }, candidates(254, 255).Take(4).ToList(), "past the largest: from 1 on");
        Assert.AreEqual(100, candidates(40_000, 65_535).Count());
        Assert.AreEqual(40_099L, candidates(40_000, 65_535).Last());
        Assert.AreEqual(50, candidates(10, 50).Distinct().Count(), "a range smaller than 100: each id once");
    }

    [TestMethod]
    public async Task ANewNodeGetsARandomFreeDeviceIdOfAtMostTwoBytes()
    {
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var (sensor, device) = await udp.CreateNodeAsync();
            ids.Add(sensor.Udp!.DeviceId);
            Assert.IsNotNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true)));
        }
        Assert.IsTrue(ids.All(id => id is >= 1 and <= 65_535), string.Join(", ", ids));
        Assert.AreEqual(5, ids.Distinct().Count());
        Assert.IsFalse(ids.Zip(ids.Skip(1)).All(p => p.Second == p.First + 1), "not handed out in order");
    }

    [TestMethod]
    public async Task EditingTheSettingsKeepsIdAndKeyUnlessChanged()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        object Request(object udpSettings) => new
        {
            hostId = udp.HostId, type = SensorType.UDP_NODE, name = sensor.Name, enabled = true, interval = 600, gracePeriod = 300,
            udp = udpSettings,
        };

        // As the dialog sends it: no id (unchanged), no key or token (unchanged).
        var edited = await udp.Admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", Request(new
        {
            deviceId = (long?)null, keyFamily = KeyFamily.HmacSha256, allowedSuites = new[] { SecuritySuite.HmacSha256_64 },
            sessionMode = UdpSessionMode.None, ackMode = UdpAckMode.Always, replayProtection = true, responseSuite = (SecuritySuite?)null,
        })).Result.ReadAsync<SensorDetail>();
        Assert.AreEqual(sensor.Udp!.DeviceId, edited.Udp!.DeviceId);
        Assert.AreEqual(sensor.Integration!.Udp!.Key, edited.Integration!.Udp!.Key);
        Assert.AreEqual(UdpAckMode.Always, edited.Udp.AckMode);
        Assert.IsNull(await udp.SendAsync(device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true).Datagram, 400),
            "HMAC-SHA256/32 is no longer allowed");
        Assert.IsNotNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_64, [])),
            "ACK always: an answer without A");

        // Another id and another family: the device needs both, and a new key comes with the family.
        var newId = 4_100_000_000L + Random.Shared.Next(1_000_000);
        var moved = await udp.Admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", Request(new
        {
            deviceId = newId, keyFamily = KeyFamily.Aes, allowedSuites = new[] { SecuritySuite.AesCmac32 },
            sessionMode = UdpSessionMode.None, ackMode = UdpAckMode.OnRequest, replayProtection = true, responseSuite = (SecuritySuite?)null,
        })).Result.ReadAsync<SensorDetail>();
        Assert.AreEqual(newId, moved.Udp!.DeviceId);
        Assert.AreNotEqual(sensor.Integration.Udp.Key, moved.Integration!.Udp!.Key);
        Assert.AreEqual(UdpSecretSource.Generated, moved.Udp.KeySource);
        Assert.IsNull(await udp.SendAsync(device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_64, [], ack: true).Datagram, 400), "the old id");
        var aes = new UdpDevice((uint)newId, KeyFamily.Aes, Convert.FromHexString(moved.Integration.Udp.Key), device.Token);
        Assert.IsNotNull(await udp.ExchangeAsync(aes, aes.Build(PacketType.Heartbeat, SecuritySuite.AesCmac32, [], ack: true)));
    }

    [TestMethod]
    public async Task AKeyGivenAtCreationIsManualAndADuplicateWarns()
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var (first, _) = await udp.CreateNodeAsync(key: key);
        Assert.AreEqual(UdpSecretSource.Manual, first.Udp!.KeySource);
        Assert.AreEqual(key, first.Integration!.Udp!.Key);
        var (second, _) = await udp.CreateNodeAsync(key: key);
        Assert.IsTrue(second.Udp!.Warnings.Any(w => w.Contains("same key")));
    }

    [TestMethod]
    public async Task AViewerSeesNoKeys()
    {
        var (sensor, _) = await udp.CreateNodeAsync();
        using var viewer = udp.App.Client(global::PNMTD.Tests.Roles.Viewer);
        var detail = await viewer.GetAsync($"/api/v1/sensors/{sensor.Id}").Result.ReadAsync<SensorDetail>();
        Assert.IsNull(detail.Integration);
        Assert.IsNotNull(detail.Udp);
        var examples = await viewer.GetAsync($"/api/v1/udp/protocol/examples?sensorId={sensor.Id}").Result.ReadAsync<UdpProtocolExamples>();
        Assert.IsTrue(examples.ReferenceKey);
        Assert.AreEqual("000102030405060708090A0B0C0D0E0F", examples.KeyHex);
    }

    // --- sessions and encryption ------------------------------------------------------------

    [TestMethod]
    public async Task ANodeJoinsAndTalksEncrypted()
    {
        var (sensor, device) = await udp.CreateNodeAsync(KeyFamily.Aes, [SecuritySuite.AesCmac32, SecuritySuite.AesCcm64], UdpSessionMode.JoinRequired);
        Assert.IsNull(await udp.SendAsync(device.Build(PacketType.Heartbeat, SecuritySuite.AesCcm64, [], ack: true).Datagram, 400), "no session yet");

        var join = device.BuildJoin(0x11223344);
        var accept = await udp.ExchangeAsync(device, join);
        Assert.AreEqual(PacketType.JoinAccept, accept?.Header.Type);
        Assert.IsNotNull(device.SessionKey);

        var data = device.Build(PacketType.Data, SecuritySuite.AesCcm64, "secret 21.5C"u8, ack: true);
        Assert.AreEqual(PacketType.Ack, (await udp.ExchangeAsync(device, data))?.Header.Type);
        Assert.AreEqual("secret 21.5C", (await udp.EventsAsync(sensor.Id)).Last().Message);

        // A replayed JOIN creates a pending session nobody can use (the JOIN_ACCEPT is for the
        // recorder, who cannot derive the key); the device's session keeps working.
        Assert.IsNotNull(await udp.SendAsync(join.Datagram));
        await Task.Delay(50);
        var stillFine = device.Build(PacketType.Heartbeat, SecuritySuite.AesCmac32, [], ack: true);
        Assert.IsNotNull(await udp.ExchangeAsync(device, stillFine));

        // Commands go out encrypted when the uplink is.
        await QueueAsync(sensor.Id, "open valve"u8.ToArray());
        var down = await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.AesCcm64, []));
        Assert.AreEqual(PacketType.Command, down!.Header.Type);
        Assert.AreEqual(SecuritySuite.AesCcm64, down.Header.Suite);
        CollectionAssert.AreEqual("open valve"u8.ToArray(), Payloads.ReadCommand(down.Payload)!.Value.Command);
        Assert.IsFalse(down.Packet.Body.Span.IndexOf("open valve"u8) >= 0, "encrypted on the wire");

        var detail = await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}").Result.ReadAsync<SensorDetail>();
        Assert.IsTrue(detail.Udp!.SessionActive);
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, (await udp.Admin.PostAsync($"/api/v1/sensors/{sensor.Id}/udp/reset-session", null)).StatusCode);
        Assert.IsNull(await udp.SendAsync(device.Build(PacketType.Heartbeat, SecuritySuite.AesCmac32, [], ack: true).Datagram, 400), "after a reset: JOIN again");
    }

    // --- firmware updates -------------------------------------------------------------------

    private static async Task<UdpFirmwareItem> UploadAsync(byte[] image, string version)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "File", "app.bin");
        form.Add(new StringContent("Pump controller"), "Name");
        form.Add(new StringContent(version), "Version");
        var uploaded = await udp.Admin.PostAsync("/api/v1/udp/firmware", form).Result.ReadAsync<UdpFirmwareUploaded>();
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(), uploaded.Firmware.Sha256);
        return uploaded.Firmware;
    }

    [TestMethod]
    public async Task AFirmwareUpdateRunsFromTheOfferToApplied()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        var image = RandomNumberGenerator.GetBytes(5000);
        image[0] = 0xE9;
        var firmware = await UploadAsync(image, "1.5.0");
        Assert.AreEqual("ESP32 application image (0xE9)", firmware.Magic);
        var jobs = await udp.Admin.PostJsonAsync("/api/v1/udp/ota-jobs", new { firmwareId = firmware.Id, sensorIds = new[] { sensor.Id }, chunkSize = 448 })
            .Result.ReadAsync<UdpOtaJobsCreated>();
        Assert.AreEqual(1, jobs.Created.Count);

        var offer = await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, []));
        Assert.AreEqual(PacketType.OtaOffer, offer!.Header.Type);
        var o = Payloads.ReadOtaOffer(offer.Payload)!;
        Assert.AreEqual((uint)image.Length, o.Size);
        Assert.AreEqual("1.5.0", o.Version);

        var received = new MemoryStream();
        var lost = 0;
        while (received.Length < o.Size)
        {
            var get = device.Build(PacketType.Ota, SecuritySuite.HmacSha256_32, Payloads.Ota(new OtaPayload(o.UpdateId, OtaOp.Get, (uint)received.Length)));
            if (received.Length == 896 && lost++ == 0)
            {
                // A lost answer: the device sends the same GET again and gets the same chunk.
                _ = await udp.SendAsync(get.Datagram);
            }
            var chunk = await udp.ExchangeAsync(device, get);
            var c = Payloads.ReadOtaChunk(chunk!.Payload)!;
            Assert.AreEqual((uint)received.Length, c.Offset);
            received.Write(c.Data);
        }
        CollectionAssert.AreEqual(SHA256.HashData(image), SHA256.HashData(received.ToArray()));
        var end = await udp.ExchangeAsync(device, device.Build(PacketType.Ota, SecuritySuite.HmacSha256_32, Payloads.Ota(new OtaPayload(o.UpdateId, OtaOp.Get, o.Size))));
        Assert.AreEqual(0, Payloads.ReadOtaChunk(end!.Payload)!.Data.Length, "past the end: empty");

        await udp.ExchangeAsync(device, device.Build(PacketType.Ota, SecuritySuite.HmacSha256_32, Payloads.Ota(new OtaPayload(o.UpdateId, OtaOp.Verified)), ack: true));
        await udp.ExchangeAsync(device, device.Build(PacketType.Boot, SecuritySuite.HmacSha256_32, Payloads.Boot(new BootPayload(4, "1.5.0")), ack: true));

        var job = (await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}/udp/ota-jobs").Result.ReadAsync<List<UdpOtaJobDetail>>()).Single();
        Assert.AreEqual(UdpOtaState.Applied, job.Job.State);
        Assert.AreEqual("1.5.0", job.Job.AppliedVersion);
        Assert.IsGreaterThanOrEqualTo(image.Length, job.Job.BytesServed);
        CollectionAssert.AreEqual(new[] { UdpLogKind.OtaQueued, UdpLogKind.OtaOffered, UdpLogKind.OtaStarted, UdpLogKind.OtaVerified, UdpLogKind.OtaApplied },
            job.Log.Select(l => l.Kind).ToList());
        Assert.AreEqual(0, await udp.WithDbAsync(db => db.UdpLastPackets.CountAsync(p => p.SensorId == sensor.Id && p.Type == PacketType.Ota)),
            "no packet rows for chunks");
        var detail = await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}").Result.ReadAsync<SensorDetail>();
        Assert.AreEqual("1.5.0", detail.Udp!.FirmwareVersion);

        var delete = await udp.Admin.DeleteAsync($"/api/v1/udp/firmware/{firmware.Id}");
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, delete.StatusCode, "no open update uses it any more");
    }

    [TestMethod]
    public async Task ACancelledDownloadIsAbortedAndOtaNeedsAKeyedSuite()
    {
        var (sensor, device) = await udp.CreateNodeAsync(suites: [SecuritySuite.Token32, SecuritySuite.HmacSha256_32]);
        var firmware = await UploadAsync(RandomNumberGenerator.GetBytes(2000), "2.0.0");
        var job = (await udp.Admin.PostJsonAsync("/api/v1/udp/ota-jobs", new { firmwareId = firmware.Id, sensorIds = new[] { sensor.Id } })
            .Result.ReadAsync<UdpOtaJobsCreated>()).Created.Single();

        Assert.IsNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.Token32, []), 400), "no offer to an unkeyed uplink");
        var offer = await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, []));
        var updateId = Payloads.ReadOtaOffer(offer!.Payload)!.UpdateId;
        Assert.IsNull(await udp.SendAsync(device.Build(PacketType.Ota, SecuritySuite.Token32, Payloads.Ota(new OtaPayload(updateId, OtaOp.Get, 0))).Datagram, 400),
            "no chunk for a TOKEN32 GET");
        Assert.IsNotNull(await udp.ExchangeAsync(device, device.Build(PacketType.Ota, SecuritySuite.HmacSha256_32, Payloads.Ota(new OtaPayload(updateId, OtaOp.Get, 0)))));

        Assert.AreEqual(System.Net.HttpStatusCode.Conflict, (await udp.Admin.DeleteAsync($"/api/v1/udp/firmware/{firmware.Id}")).StatusCode, "in use");
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, (await udp.Admin.DeleteAsync($"/api/v1/udp/ota-jobs/{job.Id}")).StatusCode);
        var abort = await udp.ExchangeAsync(device, device.Build(PacketType.Ota, SecuritySuite.HmacSha256_32, Payloads.Ota(new OtaPayload(updateId, OtaOp.Get, 448))));
        Assert.AreEqual(OtaChunkPayload.Abort, Payloads.ReadOtaChunk(abort!.Payload)!.Offset);
    }

    [TestMethod]
    public async Task ADownloadResumesAfterARestartWithANewSession()
    {
        var (sensor, device) = await udp.CreateNodeAsync(KeyFamily.Aes, [SecuritySuite.AesCmac32, SecuritySuite.AesCcm64], UdpSessionMode.JoinRequired);
        Assert.IsNotNull(await udp.ExchangeAsync(device, device.BuildJoin(7)));
        var image = RandomNumberGenerator.GetBytes(1200);
        var firmware = await UploadAsync(image, "3.0.0");
        await udp.Admin.PostJsonAsync("/api/v1/udp/ota-jobs", new { firmwareId = firmware.Id, sensorIds = new[] { sensor.Id } }).Result.ReadAsync<UdpOtaJobsCreated>();
        var offer = Payloads.ReadOtaOffer((await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.AesCcm64, [])))!.Payload)!;
        var first = await udp.ExchangeAsync(device, device.Build(PacketType.Ota, SecuritySuite.AesCcm64, Payloads.Ota(new OtaPayload(offer.UpdateId, OtaOp.Get, 0))));
        var received = Payloads.ReadOtaChunk(first!.Payload)!.Data.ToList();

        // A restart: a new JOIN, and the first packet of the new session is the GET at the first missing offset.
        Assert.IsNotNull(await udp.ExchangeAsync(device, device.BuildJoin(8)));
        while (received.Count < image.Length)
        {
            var chunk = await udp.ExchangeAsync(device, device.Build(PacketType.Ota, SecuritySuite.AesCcm64, Payloads.Ota(new OtaPayload(offer.UpdateId, OtaOp.Get, (uint)received.Count))));
            Assert.AreEqual(SecuritySuite.AesCcm64, chunk!.Header.Suite, "the chunk is encrypted, under the new session");
            received.AddRange(Payloads.ReadOtaChunk(chunk.Payload)!.Data);
        }
        CollectionAssert.AreEqual(image, received.ToArray());
        var kinds = await udp.WithDbAsync(db => db.UdpCommandLog.Where(l => l.SensorId == sensor.Id && l.OtaJobId != null).Select(l => l.Kind).ToListAsync());
        CollectionAssert.DoesNotContain(kinds, UdpLogKind.OtaApplied, "a GET is no report");
        var detail = await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}").Result.ReadAsync<SensorDetail>();
        Assert.IsTrue(detail.Udp!.SessionActive);
        Assert.IsFalse(detail.Udp.SessionPending, "the GET promoted the new session");
    }

    [TestMethod]
    public async Task ANodeWithoutAKeyedSuiteIsRefusedAnUpdate()
    {
        var (sensor, _) = await udp.CreateNodeAsync(suites: [SecuritySuite.Token32]);
        var firmware = await UploadAsync(RandomNumberGenerator.GetBytes(100), "0.1");
        var result = await udp.Admin.PostJsonAsync("/api/v1/udp/ota-jobs", new { firmwareId = firmware.Id, sensorIds = new[] { sensor.Id } })
            .Result.ReadAsync<UdpOtaJobsCreated>();
        Assert.AreEqual(0, result.Created.Count);
        StringAssert.Contains(result.Refused.Single().Reason, "keyed");
    }

    // --- the sensor, the admin page, the docs -----------------------------------------------

    [TestMethod]
    public async Task EverySensorTypeHasNotes()
    {
        var sensor = await udp.Admin.PostJsonAsync("/api/v1/sensors", new { hostId = udp.HostId, type = SensorType.SIMPLE, name = "with notes", notes = "line 1\nline 2" })
            .Result.ReadAsync<SensorDetail>();
        Assert.AreEqual("line 1\nline 2", sensor.Notes);
        var blank = await udp.Admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", new { hostId = udp.HostId, type = SensorType.SIMPLE, name = "with notes", notes = "  " })
            .Result.ReadAsync<SensorDetail>();
        Assert.IsNull(blank.Notes);
        var tooLong = await udp.Admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}", new { hostId = udp.HostId, type = SensorType.SIMPLE, name = "x", notes = new string('n', 4001) });
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [TestMethod]
    public async Task AnUpdateWithoutUdpKeepsTheNodeAsItIs()
    {
        var (sensor, device) = await udp.CreateNodeAsync(suites: [SecuritySuite.HmacSha256_64], ack: UdpAckMode.Always);
        var renamed = await udp.Admin.PutJsonAsync($"/api/v1/sensors/{sensor.Id}",
            new { hostId = udp.HostId, type = SensorType.UDP_NODE, name = "renamed", interval = 600, gracePeriod = 300 }).Result.ReadAsync<SensorDetail>();
        Assert.AreEqual(sensor.Integration!.Udp!.Key, renamed.Integration!.Udp!.Key);
        CollectionAssert.AreEqual(new[] { SecuritySuite.HmacSha256_64 }, renamed.Udp!.AllowedSuites.ToList());
        Assert.AreEqual(UdpAckMode.Always, renamed.Udp.AckMode);
        Assert.IsNotNull(await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_64, [])));
    }

    [TestMethod]
    public async Task TheAdminPageListsEveryNodeAndTheListener()
    {
        var (sensor, device) = await udp.CreateNodeAsync();
        await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true));
        var nodes = await udp.Admin.GetAsync($"/api/v1/udp/nodes?search={sensor.Udp!.DeviceId}&pageSize=500").Result.ReadAsync<Paged<UdpNodeListItem>>();
        var item = nodes.Items.Single(n => n.SensorId == sensor.Id);
        Assert.AreEqual(1, item.Packets24Hours);
        Assert.AreEqual(UdpSecretSource.Generated, item.KeySource);
        var status = await udp.Admin.GetAsync("/api/v1/udp/status").Result.ReadAsync<UdpStatus>();
        Assert.IsTrue(status.Listening);
        Assert.AreEqual(udp.Port, status.Port);
        Assert.IsGreaterThan(0, status.Accepted);
        using var user = udp.App.Client(global::PNMTD.Tests.Roles.User);
        Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/udp/nodes")).StatusCode);
    }

    [TestMethod]
    public async Task TheProtocolPageShowsTheSpecsVectors()
    {
        var examples = await udp.Admin.GetAsync("/api/v1/udp/protocol/examples").Result.ReadAsync<UdpProtocolExamples>();
        Assert.IsTrue(examples.ReferenceKey);
        Assert.AreEqual(12, examples.Vectors.Count);
        Assert.AreEqual("40 82 07 00 2A DF 27 88 7E", examples.Vectors[0].Hex);
        Assert.AreEqual("41 01 01 2C 00 01 5A 17 C3 9E", examples.Vectors[6].Hex);
        Assert.AreEqual("40 8B 07 00 2A B8 25 70 92 47 E1 82 E1", examples.Vectors[11].Hex);
        Assert.AreEqual(12, examples.Suites.Count);

        var built = await udp.Admin.GetAsync("/api/v1/udp/protocol/build?type=Data&suite=HmacSha256_64&fcnt=43&payload=" + Uri.EscapeDataString(B64("21.5C"u8.ToArray())))
            .Result.ReadAsync<UdpBuildResult>();
        Assert.AreEqual("44 03 07 00 2B 32 31 2E 35 43 35 0A B3 32 32 76 BB 45", built.Hex);
        StringAssert.Contains(built.Send, "--suite hmac64");

        var (sensor, device) = await udp.CreateNodeAsync();
        var mine = await udp.Admin.GetAsync($"/api/v1/udp/protocol/examples?sensorId={sensor.Id}").Result.ReadAsync<UdpProtocolExamples>();
        Assert.IsFalse(mine.ReferenceKey);
        var heartbeat = mine.Examples.First(e => e.Title.StartsWith("HEARTBEAT"));
        Assert.IsTrue(PacketCodec.TryParse(heartbeat.Datagram, out var packet, out _));
        Assert.IsNotNull(PacketCodec.Open(packet, device.Key, device.Token, packet.Header.Counter), "built with the node's real key");
    }
}

/// <summary>The listener's own limits, with the default settings.</summary>
[TestClass]
public sealed class UdpRateLimitTests
{
    [TestMethod]
    public async Task ANodeIsRateLimited()
    {
        using var udp = new UdpTestHarness(new Dictionary<string, string?>
        {
            ["Udp:RateLimit:PerNodeBurst"] = "3",
            ["Udp:RateLimit:PerNodePerMinute"] = "1",
        });
        var (sensor, device) = await udp.CreateNodeAsync();
        var answers = 0;
        for (var i = 0; i < 6; i++)
        {
            if (await udp.ExchangeAsync(device, device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true), 300) is not null)
            {
                answers++;
            }
        }
        Assert.AreEqual(3, answers);
        var rejections = await udp.Admin.GetAsync($"/api/v1/sensors/{sensor.Id}/udp/rejections").Result.ReadAsync<List<UdpRejectionView>>();
        Assert.IsTrue(rejections.All(r => r.Reason == UdpRejectReason.RateLimited));
        Assert.AreEqual(3, rejections.Count);
    }
}

/// <summary>§3.8: one JOIN per node per 10 seconds, with the default setting.</summary>
[TestClass]
public sealed class UdpJoinIntervalTests
{
    [TestMethod]
    public async Task ASecondJoinWithinTenSecondsIsRefused()
    {
        using var udp = new UdpTestHarness(new Dictionary<string, string?> { ["Udp:JoinIntervalSeconds"] = "10" });
        var (_, device) = await udp.CreateNodeAsync(KeyFamily.Aes, [SecuritySuite.AesCmac32], UdpSessionMode.JoinRequired);
        var join = device.BuildJoin(1);
        Assert.IsNotNull(await udp.ExchangeAsync(device, join));
        Assert.IsNotNull(await udp.SendAsync(join.Datagram), "the identical JOIN again is a retransmission: the same JOIN_ACCEPT");
        Assert.IsNull(await udp.SendAsync(device.BuildJoin(2).Datagram, 400), "a new JOIN within 10 s");
    }
}

/// <summary>`pnmtd udp send` and `pnmtd udp node` (docs/udp-iot-plan.md §7) against a real listener.</summary>
[TestClass]
public sealed class UdpCliTests
{
    [TestMethod]
    public async Task TheSimulatedNodeAnswersCommandsAndInstallsAnUpdate()
    {
        using var udp = new UdpTestHarness();
        var (sensor, device) = await udp.CreateNodeAsync();
        var key = Convert.ToHexString(device.Key);
        await udp.Admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/udp/commands", new { payload = Convert.ToBase64String("reboot"u8.ToArray()) })
            .Result.ReadAsync<UdpCommandCreated>();
        var image = RandomNumberGenerator.GetBytes(3000);
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(image), "file", "app.bin" },
            { new StringContent("Sim"), "name" },
            { new StringContent("2.0.0"), "version" },
        };
        var firmware = (await udp.Admin.PostAsync("/api/v1/udp/firmware", form).Result.ReadAsync<UdpFirmwareUploaded>()).Firmware;
        await udp.Admin.PostJsonAsync("/api/v1/udp/ota-jobs", new { firmwareId = firmware.Id, sensorIds = new[] { sensor.Id } }).Result.ReadAsync<UdpOtaJobsCreated>();

        var dir = Directory.CreateTempSubdirectory("pnmtd-sim-");
        var exit = await Task.Run(() => global::PNMTD.Cli.UdpCommand.Run(
            ["node", "--server", $"127.0.0.1:{udp.Port}", "--id", device.DeviceId.ToString(), "--key", key, "--suite", "hmac32",
             "--interval", "0.2", "--count", "2", "--ota-dir", dir.FullName, "--counter-file", Path.Combine(dir.FullName, "fcnt")],
            () => throw new InvalidOperationException()));
        Assert.AreEqual(0, exit);

        var command = await udp.WithDbAsync(db => db.UdpCommands.SingleAsync(c => c.SensorId == sensor.Id));
        Assert.AreEqual(UdpCommandState.Completed, command.State);
        CollectionAssert.AreEqual("reboot"u8.ToArray(), command.Result, "the simulator echoes the command");
        var job = await udp.WithDbAsync(db => db.UdpOtaJobs.SingleAsync(j => j.SensorId == sensor.Id));
        Assert.AreEqual(UdpOtaState.Applied, job.State);
        CollectionAssert.AreEqual(image, await File.ReadAllBytesAsync(Path.Combine(dir.FullName, $"update-{job.UpdateId}-2.0.0.bin")));
        Assert.IsTrue(uint.Parse(await File.ReadAllTextAsync(Path.Combine(dir.FullName, "fcnt"))) > 8, "the counter file moved on");

        var send = await Task.Run(() => global::PNMTD.Cli.UdpCommand.Run(
            ["send", "--server", $"127.0.0.1:{udp.Port}", "--id", device.DeviceId.ToString(), "--key", key, "--type", "data", "--text", "21.5C",
             "--ack", "--counter-file", Path.Combine(dir.FullName, "fcnt")],
            () => throw new InvalidOperationException()));
        Assert.AreEqual(0, send, "the ACK verified");
        Assert.AreEqual("21.5C", (await udp.EventsAsync(sensor.Id)).Last().Message);
        dir.Delete(true);
    }
}

/// <summary>examples/udp/pnmt_udp.py against the real listener, where Python is installed.</summary>
[TestClass]
public sealed class UdpPythonClientTests
{
    [TestMethod]
    public async Task ThePythonReferenceClientGetsItsAckAndACommand()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PNMTD.slnx")))
        {
            root = root.Parent;
        }
        var script = Path.Combine(root!.FullName, "examples", "udp", "pnmt_udp.py");
        string Run(params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-I", script }.Concat(args))
            {
                start.ArgumentList.Add(a);
            }
            try
            {
                using var process = System.Diagnostics.Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit(20_000);
                return output;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Inconclusive("python3 is not installed here.");
                return "";
            }
        }

        using var udp = new UdpTestHarness();
        var (sensor, device) = await udp.CreateNodeAsync();
        // One counter file for both runs: a second run starting at 1 again would be a replay (§3.7).
        var counter = Path.Combine(Path.GetTempPath(), $"pnmt-py-{Guid.NewGuid():N}");
        var common = new[] { "--server", $"127.0.0.1:{udp.Port}", "--id", device.DeviceId.ToString(), "--key", Convert.ToHexString(device.Key),
            "--counter-file", counter };
        StringAssert.Contains(Run([.. common, "heartbeat", "--ack"]), "<- ACK");
        await udp.Admin.PostJsonAsync($"/api/v1/sensors/{sensor.Id}/udp/commands", new { payload = Convert.ToBase64String("hi"u8.ToArray()) })
            .Result.ReadAsync<UdpCommandCreated>();
        var output = Run([.. common, "heartbeat"]);
        File.Delete(counter);
        StringAssert.Contains(output, "COMMAND");
        var command = await udp.WithDbAsync(db => db.UdpCommands.SingleAsync(c => c.SensorId == sensor.Id));
        Assert.AreEqual(UdpCommandState.Completed, command.State, output);
    }
}
