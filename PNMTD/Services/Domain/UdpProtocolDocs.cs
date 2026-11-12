using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Udp;
using PNMTD.Models.Enums;

namespace PNMTD.Services.Domain;

/// <summary>
/// The Protocol subpage (docs/udp-iot-plan.md §6.5): worked examples and the packet builder, built
/// by the codec the listener uses, so the documentation cannot drift from the implementation.
/// Without a node, or for a Viewer, they use the reference key 00 01 … 0F and token 5A 17 C3 9E.
/// </summary>
public sealed class UdpProtocolDocs(PnmtdDbContext db, UdpEndpoint endpoint)
{
    public static readonly byte[] ReferenceKey = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    public static readonly byte[] ReferenceToken = [0x5A, 0x17, 0xC3, 0x9E];

    private sealed record Node(long DeviceId, KeyFamily Family, IReadOnlyList<SecuritySuite> Allowed, byte[] Key, byte[] Token, bool Reference, UdpSessionMode Session);

    private async Task<Node> NodeAsync(Guid? sensorId, bool withSecrets, CancellationToken ct)
    {
        if (sensorId is not { } id)
        {
            return new Node(7, KeyFamily.HmacSha256, [SecuritySuite.HmacSha256_32, SecuritySuite.HmacSha256_64], ReferenceKey, ReferenceToken, true, UdpSessionMode.None);
        }
        var node = await db.UdpNodes.AsNoTracking().SingleOrDefaultAsync(n => n.SensorId == id, ct) ?? throw ApiException.NotFound("UDP node");
        return new Node(node.DeviceId, node.KeyFamily, SecuritySuites.FromMask(node.AllowedSuites),
            withSecrets ? node.Key : ReferenceKey, withSecrets ? node.Token : ReferenceToken, !withSecrets, node.SessionMode);
    }

    public static IReadOnlyList<UdpSuiteInfo> Suites() => Enum.GetValues<SecuritySuite>()
        .Select(s => new UdpSuiteInfo(s, (byte)s, s.Name(), s.TagLength(), s.Family(), s.IsKeyed(), s.IsEncrypted(), s.IsLegacy())).ToList();

    public static IReadOnlyList<UdpTypeInfo> Types() => Enum.GetValues<PacketType>()
        .Select(t => new UdpTypeInfo(t, (byte)t, t.Name(), t.IsDownlink())).ToList();

    public async Task<UdpProtocolExamples> ExamplesAsync(Guid? sensorId, bool withSecrets, CancellationToken ct)
    {
        var node = await NodeAsync(sensorId, withSecrets, ct);
        var examples = new List<UdpExample>();
        var keyed = node.Allowed.FirstOrDefault(s => s.IsKeyed() && !s.IsEncrypted());
        var main = keyed != default ? keyed : node.Allowed.FirstOrDefault();
        var device = new UdpDevice((uint)node.DeviceId, node.Family, node.Key, node.Token);
        if (node.Session == UdpSessionMode.JoinRequired)
        {
            var join = device.BuildJoin(0xA1B2C3D4);
            examples.Add(Example("JOIN", "The first packet after every start (session mode JoinRequired): DEV_NONCE A1 B2 C3 D4, the root key, counter 0.", join.Datagram, true));
            var accept = PacketCodec.Build(new PacketHeader(PacketType.JoinAccept, join.Suite, (uint)node.DeviceId, 0), Payloads.JoinAccept(0x01020304),
                node.Key, node.Token, 0xA1B2C3D4);
            examples.Add(Example("JOIN_ACCEPT", "PNMTD's answer, SERVER_NONCE 01 02 03 04, bound to the JOIN by DEV_NONCE. Both sides derive the session key from the nonces.", accept, false));
            device.SessionKey = SessionKeys.Derive(node.Family, node.Key, 0xA1B2C3D4, 0x01020304);
        }
        var heartbeat = device.Build(PacketType.Heartbeat, main, [], ack: true);
        examples.Add(Example($"HEARTBEAT, {main.Name()}, ACK requested", "The usual packet: 230 \"Alive\" on the sensor, and an ACK because A is set.", heartbeat.Datagram, true));
        var ack = Downlink(device, PacketType.Ack, main, heartbeat, []);
        examples.Add(Example("ACK", "PNMTD's answer: the uplink's counter, echoed, under the same suite.", ack, false));
        var data = device.Build(PacketType.Data, main, "21.5C"u8);
        examples.Add(Example("DATA \"21.5C\"", "Custom data: event 232 with the payload as its message; the last DATA is kept with its bytes.", data.Datagram, true));
        var slot = device.Build(PacketType.Heartbeat, main, []);
        examples.Add(Example("HEARTBEAT, a command waiting", "A heartbeat without A: with a command queued, the response is the COMMAND anyway.", slot.Datagram, true));
        var command = Downlink(device, PacketType.Command, main, slot, Payloads.Command(1, [0x02, 0x02, 0x58]));
        examples.Add(Example("COMMAND, CMD_ID 1, 02 02 58", "A queued command in the response slot of the next uplink; the bytes are the firmware's to interpret.", command, false));
        var result = device.Build(PacketType.CmdResult, main, Payloads.CmdResult(new CmdResultPayload(1, 0, "OK"u8.ToArray())), ack: true);
        examples.Add(Example("CMD_RESULT, CMD_ID 1, OK", "The device's answer: logged permanently with its bytes, and the command is completed.", result.Datagram, true));
        var boot = device.Build(PacketType.Boot, main, Payloads.Boot(new BootPayload(1, "1.0.0")));
        examples.Add(Example("BOOT, power on, 1.0.0", "After a restart: event 231, and INFO becomes the node's firmware version.", boot.Datagram, true));
        if (node.Allowed.FirstOrDefault(s => s.IsEncrypted()) is var encrypted && encrypted != default)
        {
            var secret = device.Build(PacketType.Data, encrypted, "21.5C"u8);
            examples.Add(Example($"DATA, {encrypted.Name()} (encrypted)", "The same data, encrypted: the payload on the wire is ciphertext.", secret.Datagram, true));
        }
        if (keyed != default)
        {
            var get = device.Build(PacketType.Ota, keyed, Payloads.Ota(new OtaPayload(3, OtaOp.Get, 448)));
            examples.Add(Example("OTA GET, update 3, offset 448", "Firmware update: the device pulls the next chunk.", get.Datagram, true));
            var chunk = Downlink(device, PacketType.OtaChunk, keyed, get, Payloads.OtaChunk(new OtaChunkPayload(3, 448, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray())));
            examples.Add(Example("OTA_CHUNK, 16 bytes at 448", "PNMTD's answer (a real chunk carries up to the offer's CHUNK bytes).", chunk, false));
        }
        var vectors = sensorId is null ? Vectors() : [];
        return new UdpProtocolExamples(node.Reference, node.Reference || withSecrets ? UdpViews.Hex(node.Key) : null,
            node.Reference || withSecrets ? UdpViews.Hex(node.Token) : null, node.DeviceId, node.Family, node.Allowed, endpoint.Value,
            PacketCodec.MaxDatagram, Suites(), Types(), examples, vectors);
    }

    private static byte[] Downlink(UdpDevice device, PacketType type, SecuritySuite suite, Uplink uplink, byte[] payload) =>
        PacketCodec.Build(new PacketHeader(type, suite, device.DeviceId, (ushort)uplink.Fcnt), payload, device.SessionKey ?? device.Key, device.Token, uplink.Fcnt);

    private UdpExample Example(string title, string description, byte[] datagram, bool uplink)
    {
        PacketCodec.TryParse(datagram, out var packet, out _);
        var fields = PacketFields.Describe(packet, packet.Header.Suite.IsEncrypted() ? null : packet.Body.ToArray(), true);
        return new UdpExample(title, description, datagram, fields, UdpViews.SpacedHex(datagram), UdpViews.CArray(datagram),
            uplink ? $"echo {UdpViews.SpacedHex(datagram)} | xxd -r -p | nc -u -w1 {endpoint.Host} {endpoint.Port}" : null);
    }

    /// <summary>The vectors of docs/udp-protocol.md §3.14, built live (a test pins them to the spec).</summary>
    public IReadOnlyList<UdpExample> Vectors()
    {
        UdpExample V(string title, PacketType type, SecuritySuite suite, uint id, uint fcnt, bool ack, byte[] payload) =>
            Example(title, "", PacketCodec.Build(new PacketHeader(type, suite, id, (ushort)fcnt, ack), payload, ReferenceKey, ReferenceToken, fcnt), !type.IsDownlink());
        return
        [
            V("#1 HEARTBEAT, A, HMAC-SHA256/32, FCNT 42", PacketType.Heartbeat, SecuritySuite.HmacSha256_32, 7, 42, true, []),
            V("#2 ACK for #1", PacketType.Ack, SecuritySuite.HmacSha256_32, 7, 42, false, []),
            V("#3 DATA \"21.5C\", HMAC-SHA256/64, FCNT 43", PacketType.Data, SecuritySuite.HmacSha256_64, 7, 43, false, "21.5C"u8.ToArray()),
            V("#4 HEARTBEAT, HMAC-SHA256/32, FCNT 44", PacketType.Heartbeat, SecuritySuite.HmacSha256_32, 7, 44, false, []),
            V("#5 COMMAND for #4, CMD_ID 1, 02 02 58", PacketType.Command, SecuritySuite.HmacSha256_32, 7, 44, false, [1, 2, 2, 0x58]),
            V("#6 CMD_RESULT, CMD_ID 1, OK, A, FCNT 45", PacketType.CmdResult, SecuritySuite.HmacSha256_32, 7, 45, true, [1, 0, (byte)'O', (byte)'K']),
            V("#7 HEARTBEAT, TOKEN32, id 300, FCNT 1", PacketType.Heartbeat, SecuritySuite.Token32, 300, 1, false, []),
            V("#8 HEARTBEAT, NONE, FCNT 1", PacketType.Heartbeat, SecuritySuite.None, 7, 1, false, []),
            V("#9 HEARTBEAT, A, HMAC-MD5/32, FCNT 42", PacketType.Heartbeat, SecuritySuite.HmacMd5_32, 7, 42, true, []),
            V("#10 HEARTBEAT, A, HMAC-MD5/64, FCNT 42", PacketType.Heartbeat, SecuritySuite.HmacMd5_64, 7, 42, true, []),
            V("#11 HEARTBEAT, A, HMAC-SHA1/32, FCNT 42", PacketType.Heartbeat, SecuritySuite.HmacSha1_32, 7, 42, true, []),
            V("#12 HEARTBEAT, A, HMAC-SHA1/64, FCNT 42", PacketType.Heartbeat, SecuritySuite.HmacSha1_64, 7, 42, true, []),
        ];
    }

    /// <summary><c>GET /udp/protocol/build</c>: builds a packet, never sends it.</summary>
    public async Task<UdpBuildResult> BuildAsync(Guid? sensorId, PacketType type, SecuritySuite suite, long fcnt, bool ack, string? payloadBase64,
        bool withSecrets, CancellationToken ct)
    {
        byte[] payload = [];
        var v = new ApiValidator();
        if (!string.IsNullOrEmpty(payloadBase64))
        {
            try
            {
                payload = Convert.FromBase64String(payloadBase64);
            }
            catch (FormatException)
            {
                v.Check(false, "payload", "Base64.");
            }
        }
        v.Check(Enum.IsDefined(type), "type", "A packet type.");
        v.Check(Enum.IsDefined(suite), "suite", "A suite.");
        v.Check(fcnt is >= 0 and <= uint.MaxValue, "fcnt", "Between 0 and 4294967295.");
        v.Check(payload.Length <= 480, "payload", "At most 480 bytes.");
        v.Check(type != PacketType.Join || payload.Length == 4, "payload", "A JOIN carries DEV_NONCE: 4 bytes.");
        v.ThrowIfInvalid();
        var node = await NodeAsync(sensorId, withSecrets, ct);
        new ApiValidator().Check(suite.BelongsTo(node.Family), "suite", $"{suite} does not belong to the node's family {node.Family}.").ThrowIfInvalid();
        var header = new PacketHeader(type, suite, (uint)node.DeviceId, (ushort)(type == PacketType.Join ? 0 : fcnt), ack && !type.IsDownlink());
        var prefix = type == PacketType.Join ? 0u : (uint)fcnt;
        byte[] datagram;
        try
        {
            datagram = PacketCodec.Build(header, payload, node.Key, node.Token, prefix);
        }
        catch (ArgumentException ex)
        {
            throw ApiException.Invalid("payload", ex.Message);
        }
        PacketCodec.TryParse(datagram, out var packet, out _);
        var send = $"pnmtd udp send --server {endpoint.Value} --id {node.DeviceId} --family {node.Family} --suite {suite.CliName()} "
            + $"--key {(node.Reference || withSecrets ? UdpViews.Hex(node.Key) : "<key>")} "
            + (suite == SecuritySuite.Token32 ? $"--token {UdpViews.Hex(node.Token)} " : "")
            + $"--type {type.Name().ToLowerInvariant()} --counter {fcnt}"
            + (payload.Length > 0 ? $" --hex {UdpViews.Hex(payload)}" : "") + (header.AckRequested ? " --ack" : "");
        return new UdpBuildResult(node.Reference, datagram, PacketFields.Describe(packet, suite.IsEncrypted() ? null : payload, true),
            UdpViews.SpacedHex(datagram), UdpViews.CArray(datagram), send);
    }
}
