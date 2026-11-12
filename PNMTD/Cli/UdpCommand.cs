using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Udp;
using PNMTD.Models.Db;

namespace PNMTD.Cli;

/// <summary>
/// `pnmtd udp …` (docs/udp-iot-plan.md §7): <c>send</c> one packet and show both directions,
/// <c>node</c> a simulated device (heartbeats, commands, JOIN, firmware updates), and
/// <c>reset-counter</c> on the server. The packets come from the same codec as the listener's.
/// </summary>
internal static class UdpCommand
{
    public const string Usage = """
          udp send --server <host:port> --id <device id> --key <hex> [--token <hex>] [--suite hmac32]
                   [--family hmacsha256|aes|hmacsha1|hmacmd5] [--type heartbeat|data|event|cmd_result|boot|ota]
                   [--text <text> | --hex <hex>] [--ack] [--counter <n> | --counter-file <path>]
                   [--session-key <hex>] [--wait <ms>]
                                                send one packet, show it and the answer, decoded and verified
          udp node --server <host:port> --id <device id> --key <hex> [--token <hex>] [--suite hmac32]
                   [--family …] [--interval 10] [--count <n>] [--join] [--counter-file <path>]
                   [--version 1.0.0] [--result ok|fail|ignore] [--ota-dir <dir>]
                   [--ota-fail hash|decline|rollback] [--ota-drop <percent>]
                                                a simulated device: BOOT, then heartbeats; answers
                                                commands, joins, downloads firmware updates
          udp reset-counter <device id>         the node's next packet sets a new counter window
        """;

    public static int Run(string[] args, Func<PnmtdDbContext> database)
    {
        return args switch
        {
            ["send", .. var rest] => Send(new CommandLine.Options(rest)),
            ["node", .. var rest] => new Simulator(new CommandLine.Options(rest)).RunAsync().GetAwaiter().GetResult(),
            ["reset-counter", var id] => ResetCounter(id, database),
            _ => throw new CommandLineException($"Unknown udp command: {string.Join(' ', args)}"),
        };
    }

    // --- shared options ---------------------------------------------------------------------

    private sealed record Target(IPEndPoint Server, UdpDevice Device, SecuritySuite Suite, string? CounterFile);

    private static Target Parse(CommandLine.Options o)
    {
        var server = Endpoint(o.Required("--server"));
        var id = uint.Parse(o.Required("--id"), CultureInfo.InvariantCulture);
        var suite = SecuritySuites.FromCliName(o.Optional("--suite") ?? "hmac32") ?? throw new CommandLineException("Unknown --suite; one of: "
            + string.Join(", ", Enum.GetValues<SecuritySuite>().Select(s => s.CliName())));
        var family = o.Optional("--family") is { } f
            ? Enum.TryParse<KeyFamily>(f, true, out var parsed) ? parsed : throw new CommandLineException("--family: hmacsha256, aes, hmacsha1 or hmacmd5.")
            : suite.Family() ?? KeyFamily.HmacSha256;
        var key = Hex(o.Optional("--key") ?? (suite.IsKeyed() ? throw new CommandLineException("--key <32 hex digits> is required for a keyed suite.") : "00000000000000000000000000000000"));
        var token = Hex(o.Optional("--token") ?? "00000000");
        var device = new UdpDevice(id, family, key, token);
        if (o.Optional("--session-key") is { } session)
        {
            device.SessionKey = Hex(session);
        }
        var counterFile = o.Optional("--counter-file");
        if (o.Optional("--counter") is { } counter)
        {
            device.NextCounter = uint.Parse(counter, CultureInfo.InvariantCulture);
        }
        else if (counterFile is not null && File.Exists(counterFile))
        {
            device.NextCounter = uint.Parse(File.ReadAllText(counterFile).Trim(), CultureInfo.InvariantCulture);
        }
        return new Target(server, device, suite, counterFile);
    }

    private static IPEndPoint Endpoint(string text)
    {
        var at = text.LastIndexOf(':');
        if (at <= 0 || !int.TryParse(text[(at + 1)..], out var port))
        {
            throw new CommandLineException("--server is host:port, for example 127.0.0.1:5028.");
        }
        var host = text[..at].Trim('[', ']');
        var address = IPAddress.TryParse(host, out var ip) ? ip : Dns.GetHostAddresses(host).First();
        return new IPEndPoint(address, port);
    }

    /// <summary>Hex with spaces, colons and 0x allowed.</summary>
    private static byte[] Hex(string text) =>
        Services.Domain.UdpViews.ParseHex(text) ?? throw new CommandLineException($"Not hex: {text}");

    private static PacketType Type(string name) => name.ToLowerInvariant() switch
    {
        "heartbeat" => PacketType.Heartbeat,
        "data" => PacketType.Data,
        "event" => PacketType.Event,
        "cmd_result" or "cmd-result" or "result" => PacketType.CmdResult,
        "boot" => PacketType.Boot,
        "ota" => PacketType.Ota,
        _ => throw new CommandLineException("--type: heartbeat, data, event, cmd_result, boot or ota (JOIN: pnmtd udp node --join)."),
    };

    private static void SaveCounter(Target t)
    {
        if (t.CounterFile is { } path)
        {
            File.WriteAllText(path, t.Device.NextCounter.ToString(CultureInfo.InvariantCulture));
        }
    }

    // --- send -------------------------------------------------------------------------------

    private static int Send(CommandLine.Options o)
    {
        var t = Parse(o);
        var type = Type(o.Optional("--type") ?? "heartbeat");
        var payload = o.Optional("--hex") is { } hex ? Hex(hex) : Encoding.UTF8.GetBytes(o.Optional("--text") ?? "");
        var ack = o.Flag("--ack");
        var wait = int.Parse(o.Optional("--wait") ?? "2000", CultureInfo.InvariantCulture);
        o.EnsureAllUsed();

        var uplink = t.Device.Build(type, t.Suite, payload, ack);
        SaveCounter(t);
        using var socket = new UdpClient(t.Server.AddressFamily);
        socket.Connect(t.Server);
        socket.Send(uplink.Datagram);
        Console.WriteLine($"→ {t.Server}  {type.Name()} {t.Suite.Name()} FCNT {uplink.Fcnt}, {uplink.Datagram.Length} bytes");
        Print(uplink.Datagram, payload, true);
        socket.Client.ReceiveTimeout = wait;
        try
        {
            IPEndPoint? from = null;
            var answer = socket.Receive(ref from);
            var downlink = t.Device.Open(answer, uplink, out var why);
            Console.WriteLine();
            Console.WriteLine($"← {from}  {answer.Length} bytes{(downlink is null ? $": REFUSED ({why})" : $", {downlink.Header.Type.Name()} verified")}");
            Print(answer, downlink?.Payload, downlink is not null);
            return downlink is null ? 1 : 0;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            Console.WriteLine();
            Console.WriteLine($"← no answer within {wait} ms{(ack ? "" : " (none asked for: --ack)")}");
            return ack ? 1 : 0;
        }
    }

    private static void Print(byte[] datagram, byte[]? plaintext, bool verified)
    {
        for (var i = 0; i < datagram.Length; i += 16)
        {
            var line = datagram.AsSpan(i, Math.Min(16, datagram.Length - i));
            var hex = string.Join(' ', line.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            var ascii = new string(line.ToArray().Select(b => b is >= 0x20 and < 0x7F ? (char)b : '·').ToArray());
            Console.WriteLine($"  {i:X4}  {hex,-47}  {ascii}");
        }
        if (PacketCodec.TryParse(datagram, out var packet, out _))
        {
            foreach (var field in PacketFields.Describe(packet, plaintext, verified))
            {
                Console.WriteLine($"  {field.Offset,4}+{field.Length,-3} {field.Name,-18} {field.Value}");
            }
        }
    }

    // --- reset-counter ----------------------------------------------------------------------

    private static int ResetCounter(string id, Func<PnmtdDbContext> database)
    {
        var deviceId = long.Parse(id, CultureInfo.InvariantCulture);
        using var db = database();
        var node = db.UdpNodes.Include(n => n.Sensor).SingleOrDefault(n => n.DeviceId == deviceId);
        if (node is null)
        {
            Console.Error.WriteLine($"No UDP node with device id {deviceId}.");
            return 1;
        }
        node.FcntHigh = null;
        node.FcntWindow = 0;
        node.LastUplinkFcnt = null;
        node.LastUplinkDigest = null;
        node.LastUplinkAt = null;
        node.LastResponse = null;
        db.AuthLog.Add(new AuthLogEntity
        {
            Id = Guid.NewGuid(),
            CreatedAt = Clock.UtcNow,
            Username = "",
            EventType = AuthEvents.UdpCounterReset,
            Detail = $"cli: node {node.Sensor.Name} ({node.DeviceId}): counter window reset",
        });
        db.SaveChanges();
        Console.WriteLine($"Node {node.Sensor.Name} ({deviceId}): the next packet sets a new counter window.");
        return 0;
    }

    // --- node: the simulated device -------------------------------------------------------

    private sealed class Simulator(CommandLine.Options o)
    {
        private readonly Target t = Parse(o);
        private readonly TimeSpan interval = TimeSpan.FromSeconds(double.Parse(o.Optional("--interval") ?? "10", CultureInfo.InvariantCulture));
        private readonly int? count = o.Optional("--count") is { } c ? int.Parse(c, CultureInfo.InvariantCulture) : null;
        private readonly bool join = o.Flag("--join");
        private readonly string result = (o.Optional("--result") ?? "ok").ToLowerInvariant();
        private readonly string? otaDir = o.Optional("--ota-dir");
        private readonly string? otaFail = o.Optional("--ota-fail")?.ToLowerInvariant();
        private readonly double otaDrop = double.Parse(o.Optional("--ota-drop") ?? "0", CultureInfo.InvariantCulture) / 100;
        private string version = o.Optional("--version") ?? "1.0.0";
        private readonly Random random = new();
        private readonly Dictionary<byte, byte[]> results = [];
        private UdpClient socket = null!;

        public async Task<int> RunAsync()
        {
            o.EnsureAllUsed();
            socket = new UdpClient(t.Server.AddressFamily);
            socket.Connect(t.Server);
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };
            Log($"device {t.Device.DeviceId} → {t.Server}, {t.Suite.Name()}, every {interval.TotalSeconds:0.#} s");
            try
            {
                if (join && !await JoinAsync(stop.Token))
                {
                    return 1;
                }
                await UplinkAsync(PacketType.Boot, Payloads.Boot(new BootPayload(1, version)), ack: true, stop.Token);
                for (var sent = 0; count is null || sent < count; sent++)
                {
                    await Task.Delay(interval, stop.Token);
                    await UplinkAsync(PacketType.Heartbeat, [], ack: true, stop.Token);
                }
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            finally
            {
                socket.Dispose();
            }
        }

        private void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {message}");

        private async Task<Downlink?> ExchangeAsync(Uplink uplink, CancellationToken ct, int timeoutMs = 3000, int retries = 2)
        {
            SaveCounter(t);
            for (var attempt = 0; attempt <= retries; attempt++)
            {
                await socket.SendAsync(uplink.Datagram, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutMs);
                try
                {
                    while (true)
                    {
                        var answer = await socket.ReceiveAsync(timeout.Token);
                        if (t.Device.Open(answer.Buffer, uplink, out var why) is { } downlink)
                        {
                            return downlink;
                        }
                        Log($"ignored a datagram: {why}");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // No answer: the identical datagram again (§3.9).
                }
            }
            return null;
        }

        private async Task<bool> JoinAsync(CancellationToken ct)
        {
            foreach (var wait in new[] { 0, 10, 30, 60 })
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                var uplink = t.Device.BuildJoin(BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)));
                if (await ExchangeAsync(uplink, ct, retries: 0) is { Header.Type: PacketType.JoinAccept })
                {
                    Log("joined: a new session key, counter 1");
                    return true;
                }
                Log("no JOIN_ACCEPT");
            }
            return false;
        }

        private async Task UplinkAsync(PacketType type, byte[] payload, bool ack, CancellationToken ct)
        {
            var uplink = t.Device.Build(type, t.Suite, payload, ack);
            var downlink = await ExchangeAsync(uplink, ct, retries: ack ? 2 : 0);
            Log($"{type.Name()} FCNT {uplink.Fcnt} → {downlink?.Header.Type.Name() ?? "no answer"}{(downlink?.Header.More == true ? " (more waiting)" : "")}");
            if (downlink is not null)
            {
                await HandleAsync(downlink, ct);
            }
        }

        private async Task HandleAsync(Downlink downlink, CancellationToken ct)
        {
            switch (downlink.Header.Type)
            {
                case PacketType.Command when Payloads.ReadCommand(downlink.Payload) is { } received:
                    var (cmdId, command) = received;
                    Log($"COMMAND {cmdId}: {PacketCodec.Render(command)}");
                    if (result == "ignore")
                    {
                        return;
                    }
                    // A repeat of a command it ran already gets the stored result (§3.10).
                    if (!results.TryGetValue(cmdId, out var stored))
                    {
                        stored = Payloads.CmdResult(new CmdResultPayload(cmdId, result == "fail" ? (byte)3 : (byte)0, command));
                        results[cmdId] = stored;
                    }
                    await UplinkAsync(PacketType.CmdResult, stored, ack: !downlink.Header.More, ct);
                    break;
                case PacketType.OtaOffer when Payloads.ReadOtaOffer(downlink.Payload) is { } offer:
                    await UpdateAsync(offer, ct);
                    break;
                default:
                    if (downlink.Header.More)
                    {
                        await UplinkAsync(PacketType.Heartbeat, [], ack: true, ct);
                    }
                    break;
            }
        }

        private async Task UpdateAsync(OtaOfferPayload offer, CancellationToken ct)
        {
            Log($"OTA_OFFER {offer.UpdateId}: {offer.Version}, {offer.Size} bytes in chunks of {offer.Chunk}");
            if (otaFail == "decline")
            {
                await UplinkAsync(PacketType.Ota, Payloads.Ota(new OtaPayload(offer.UpdateId, OtaOp.Decline, Reason: 3)), ack: true, ct);
                return;
            }
            var image = new MemoryStream();
            while (image.Length < offer.Size)
            {
                var get = t.Device.Build(PacketType.Ota, t.Suite, Payloads.Ota(new OtaPayload(offer.UpdateId, OtaOp.Get, (uint)image.Length)));
                Downlink? chunk = null;
                for (var attempt = 0; attempt < 5 && chunk is null; attempt++)
                {
                    chunk = await ExchangeAsync(get, ct, timeoutMs: 2000, retries: 0);
                    if (chunk is not null && random.NextDouble() < otaDrop)
                    {
                        chunk = null; // pretend the answer was lost: the same GET again
                    }
                }
                if (chunk is null || Payloads.ReadOtaChunk(chunk.Payload) is not { } c)
                {
                    Log("OTA: no answer, giving up for now");
                    return;
                }
                if (c.Offset == OtaChunkPayload.Abort)
                {
                    Log("OTA: aborted by the server");
                    return;
                }
                image.Write(c.Data);
                if (c.Data.Length == 0)
                {
                    break;
                }
            }
            var bytes = image.ToArray();
            var hashOk = SHA256.HashData(bytes).AsSpan().SequenceEqual(offer.Sha256) && otaFail != "hash";
            Log($"OTA: {bytes.Length} bytes, SHA-256 {(hashOk ? "matches" : "does NOT match")}");
            if (!hashOk)
            {
                await UplinkAsync(PacketType.Ota, Payloads.Ota(new OtaPayload(offer.UpdateId, OtaOp.Failed, Reason: 1)), ack: true, ct);
                return;
            }
            if (otaDir is not null)
            {
                Directory.CreateDirectory(otaDir);
                var path = Path.Combine(otaDir, $"update-{offer.UpdateId}-{offer.Version}.bin");
                await File.WriteAllBytesAsync(path, bytes, ct);
                Log($"OTA: written to {path}");
            }
            await UplinkAsync(PacketType.Ota, Payloads.Ota(new OtaPayload(offer.UpdateId, OtaOp.Verified)), ack: true, ct);
            Log("OTA: restarting");
            if (otaFail == "rollback")
            {
                await UplinkAsync(PacketType.Boot, Payloads.Boot(new BootPayload(4, version)), ack: true, ct);
                await UplinkAsync(PacketType.Ota, Payloads.Ota(new OtaPayload(offer.UpdateId, OtaOp.Failed, Reason: 5)), ack: true, ct);
                return;
            }
            version = offer.Version;
            await UplinkAsync(PacketType.Boot, Payloads.Boot(new BootPayload(4, version)), ack: true, ct);
            await UplinkAsync(PacketType.Ota, Payloads.Ota(new OtaPayload(offer.UpdateId, OtaOp.Applied, Version: version)), ack: true, ct);
        }
    }
}
