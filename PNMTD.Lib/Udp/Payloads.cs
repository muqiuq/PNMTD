using System.Buffers.Binary;
using System.Text;

namespace PNMTD.Lib.Udp;

/// <summary>The OP of an OTA uplink (docs/udp-protocol.md §3.11).</summary>
public enum OtaOp : byte
{
    Get = 0x01,
    Decline = 0x02,
    Verified = 0x03,
    Failed = 0x04,
    Applied = 0x05,
}

/// <summary>EVENT: <c>CHANNEL(1) CODE(2) MESSAGE</c>.</summary>
public sealed record EventPayload(byte Channel, ushort Code, string Message);

/// <summary>CMD_RESULT: <c>CMD_ID(1) STATUS(1) RESULT</c>.</summary>
public sealed record CmdResultPayload(byte CmdId, byte Status, byte[] Result);

/// <summary>BOOT: <c>REASON(1)? INFO(UTF-8)?</c>.</summary>
public sealed record BootPayload(byte? Reason, string? Info);

/// <summary>OTA (uplink): <c>UPDATE_ID(1) OP(1) ARG</c>.</summary>
public sealed record OtaPayload(byte UpdateId, OtaOp Op, uint Offset = 0, ushort? Length = null, byte Reason = 0, string? Version = null);

/// <summary>OTA_OFFER: <c>UPDATE_ID(1) SIZE(4) CHUNK(2) SHA256(32) VERSION</c>.</summary>
public sealed record OtaOfferPayload(byte UpdateId, uint Size, ushort Chunk, byte[] Sha256, string Version);

/// <summary>OTA_CHUNK: <c>UPDATE_ID(1) OFFSET(4) DATA</c>. <see cref="Abort"/> as the offset ends the update.</summary>
public sealed record OtaChunkPayload(byte UpdateId, uint Offset, byte[] Data)
{
    public const uint Abort = 0xFFFFFFFF;
}

/// <summary>Builds and reads the payloads of §3.4 and §3.11. Readers return null for a payload that is too short.</summary>
public static class Payloads
{
    private static readonly UTF8Encoding Utf8 = new(false, false);

    public static byte[] Event(EventPayload e)
    {
        var message = Utf8.GetBytes(e.Message);
        var bytes = new byte[3 + message.Length];
        bytes[0] = e.Channel;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(1), e.Code);
        message.CopyTo(bytes, 3);
        return bytes;
    }

    public static EventPayload? ReadEvent(ReadOnlySpan<byte> p) =>
        p.Length < 3 ? null : new EventPayload(p[0], BinaryPrimitives.ReadUInt16BigEndian(p[1..]), Utf8.GetString(p[3..]));

    public static byte[] CmdResult(CmdResultPayload r) => [r.CmdId, r.Status, .. r.Result];

    public static CmdResultPayload? ReadCmdResult(ReadOnlySpan<byte> p) =>
        p.Length < 2 ? null : new CmdResultPayload(p[0], p[1], p[2..].ToArray());

    public static byte[] Boot(BootPayload b) =>
        b.Reason is { } reason ? [reason, .. Utf8.GetBytes(b.Info ?? "")] : [];

    public static BootPayload ReadBoot(ReadOnlySpan<byte> p) =>
        p.IsEmpty ? new BootPayload(null, null) : new BootPayload(p[0], p.Length > 1 ? Utf8.GetString(p[1..]) : null);

    public static byte[] Join(uint devNonce) => U32(devNonce);

    public static uint? ReadJoin(ReadOnlySpan<byte> p) => p.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(p) : null;

    public static byte[] JoinAccept(uint serverNonce) => U32(serverNonce);

    public static uint? ReadJoinAccept(ReadOnlySpan<byte> p) => p.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(p) : null;

    public static byte[] Command(byte cmdId, ReadOnlySpan<byte> command) => [cmdId, .. command];

    public static (byte CmdId, byte[] Command)? ReadCommand(ReadOnlySpan<byte> p) => p.IsEmpty ? null : (p[0], p[1..].ToArray());

    public static byte[] Ota(OtaPayload o) => o.Op switch
    {
        OtaOp.Get when o.Length is { } length => [o.UpdateId, (byte)o.Op, .. U32(o.Offset), (byte)(length >> 8), (byte)length],
        OtaOp.Get => [o.UpdateId, (byte)o.Op, .. U32(o.Offset)],
        OtaOp.Decline or OtaOp.Failed => [o.UpdateId, (byte)o.Op, o.Reason],
        OtaOp.Applied => [o.UpdateId, (byte)o.Op, .. Utf8.GetBytes(o.Version ?? "")],
        _ => [o.UpdateId, (byte)o.Op],
    };

    /// <summary>Null for a malformed OTA uplink: unknown OP, or a GET that is not 6 or 8 bytes.</summary>
    public static OtaPayload? ReadOta(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2 || !Enum.IsDefined((OtaOp)p[1]))
        {
            return null;
        }
        var op = (OtaOp)p[1];
        return op switch
        {
            OtaOp.Get when p.Length == 6 => new OtaPayload(p[0], op, BinaryPrimitives.ReadUInt32BigEndian(p[2..])),
            OtaOp.Get when p.Length == 8 => new OtaPayload(p[0], op, BinaryPrimitives.ReadUInt32BigEndian(p[2..]),
                BinaryPrimitives.ReadUInt16BigEndian(p[6..])),
            OtaOp.Get => null,
            OtaOp.Decline or OtaOp.Failed => new OtaPayload(p[0], op, Reason: p.Length > 2 ? p[2] : (byte)0),
            OtaOp.Applied => new OtaPayload(p[0], op, Version: p.Length > 2 ? Utf8.GetString(p[2..]) : null),
            _ => new OtaPayload(p[0], op),
        };
    }

    public static byte[] OtaOffer(OtaOfferPayload o)
    {
        if (o.Sha256.Length != 32)
        {
            throw new ArgumentException("SHA-256 is 32 bytes.", nameof(o));
        }
        return [o.UpdateId, .. U32(o.Size), (byte)(o.Chunk >> 8), (byte)o.Chunk, .. o.Sha256, .. Utf8.GetBytes(o.Version)];
    }

    public static OtaOfferPayload? ReadOtaOffer(ReadOnlySpan<byte> p) =>
        p.Length < 39
            ? null
            : new OtaOfferPayload(p[0], BinaryPrimitives.ReadUInt32BigEndian(p[1..]), BinaryPrimitives.ReadUInt16BigEndian(p[5..]),
                p.Slice(7, 32).ToArray(), Utf8.GetString(p[39..]));

    public static byte[] OtaChunk(OtaChunkPayload c) => [c.UpdateId, .. U32(c.Offset), .. c.Data];

    public static OtaChunkPayload? ReadOtaChunk(ReadOnlySpan<byte> p) =>
        p.Length < 5 ? null : new OtaChunkPayload(p[0], BinaryPrimitives.ReadUInt32BigEndian(p[1..]), p[5..].ToArray());

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>BOOT's REASON in words (§3.4).</summary>
    public static string BootReason(byte? reason) => reason switch
    {
        null or 0 => "unknown",
        1 => "power on",
        2 => "watchdog",
        3 => "brown-out",
        4 => "software reset or update",
        5 => "reset by a command",
        >= 128 => $"device-specific {reason}",
        _ => $"reserved {reason}",
    };

    /// <summary>CMD_RESULT's STATUS in words (§3.4).</summary>
    public static string CommandStatus(byte status) => status switch
    {
        0 => "OK",
        1 => "unknown command",
        2 => "invalid argument",
        3 => "failed",
        4 => "busy",
        >= 128 => $"device-specific {status}",
        _ => $"reserved {status}",
    };

    /// <summary>DECLINE's REASON (§3.11).</summary>
    public static string DeclineReason(byte reason) => reason switch
    {
        1 => "not enough space",
        2 => "battery low",
        3 => "busy",
        4 => "already installed",
        5 => "wrong target",
        >= 128 => $"device-specific {reason}",
        _ => $"reason {reason}",
    };

    /// <summary>FAILED's REASON (§3.11).</summary>
    public static string FailReason(byte reason) => reason switch
    {
        1 => "hash mismatch",
        2 => "write error",
        3 => "image rejected",
        4 => "install failed",
        5 => "rolled back after the restart",
        >= 128 => $"device-specific {reason}",
        _ => $"reason {reason}",
    };
}

/// <summary>One field of a packet for an annotated hex dump: where it is and what it means.</summary>
public sealed record PacketField(int Offset, int Length, string Name, string Value);

public static class PacketFields
{
    /// <summary>
    /// The fields of a packet, in order. <paramref name="plaintext"/> is the opened payload (for
    /// AES-CCM the decrypted one), so the payload's own fields can be named too.
    /// </summary>
    public static IReadOnlyList<PacketField> Describe(ParsedPacket packet, byte[]? plaintext, bool? tagVerified)
    {
        var h = packet.Header;
        var idLength = h.EffectiveIdLength;
        var fields = new List<PacketField>
        {
            new(0, 1, "VER · TYPE · IDL", $"version 1, {h.Type.Name()} (0x{(byte)h.Type:X}), {idLength}-byte id"),
            new(1, 1, "A · M · SUITE", string.Join(", ", new[]
            {
                h.AckRequested ? "ACK requested" : null,
                h.More ? "more waiting" : null,
                $"{h.Suite.Name()} ({(byte)h.Suite})",
            }.Where(s => s is not null))),
            new(2, idLength, "DEVICE ID", h.DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(2 + idLength, 2, "COUNTER", h.IsDownlink ? $"{h.Counter} (the uplink's, echoed)" : h.Counter.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        var at = packet.HeaderLength;
        var body = packet.Body.Length;
        if (body > 0)
        {
            if (h.Suite.IsEncrypted() || plaintext is null)
            {
                fields.Add(new(at, body, "PAYLOAD", h.Suite.IsEncrypted() ? "encrypted" : $"{body} bytes"));
            }
            else
            {
                fields.AddRange(DescribePayload(h.Type, plaintext, at));
            }
        }
        if (packet.TagLength > 0)
        {
            var name = h.Suite == SecuritySuite.Token32 ? "TOKEN" : "TAG";
            var state = tagVerified switch { true => "verified", false => "does not verify", null => "not checked" };
            fields.Add(new(packet.Datagram.Length - packet.TagLength, packet.TagLength, name, $"{h.Suite.Name()}, {state}"));
        }
        return fields;
    }

    private static IEnumerable<PacketField> DescribePayload(PacketType type, byte[] p, int at)
    {
        switch (type)
        {
            case PacketType.Event when Payloads.ReadEvent(p) is { } e:
                yield return new(at, 1, "CHANNEL", e.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture));
                yield return new(at + 1, 2, "CODE", e.Code.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (p.Length > 3)
                {
                    yield return new(at + 3, p.Length - 3, "MESSAGE", e.Message);
                }
                break;
            case PacketType.CmdResult when Payloads.ReadCmdResult(p) is { } r:
                yield return new(at, 1, "CMD_ID", r.CmdId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                yield return new(at + 1, 1, "STATUS", $"{r.Status} ({Payloads.CommandStatus(r.Status)})");
                if (r.Result.Length > 0)
                {
                    yield return new(at + 2, r.Result.Length, "RESULT", PacketCodec.Render(r.Result));
                }
                break;
            case PacketType.Command when p.Length > 0:
                yield return new(at, 1, "CMD_ID", p[0].ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (p.Length > 1)
                {
                    yield return new(at + 1, p.Length - 1, "COMMAND", PacketCodec.Render(p.AsSpan(1)));
                }
                break;
            case PacketType.Boot when p.Length > 0:
                yield return new(at, 1, "REASON", $"{p[0]} ({Payloads.BootReason(p[0])})");
                if (p.Length > 1)
                {
                    yield return new(at + 1, p.Length - 1, "INFO", Encoding.UTF8.GetString(p, 1, p.Length - 1));
                }
                break;
            case PacketType.Join when p.Length == 4:
                yield return new(at, 4, "DEV_NONCE", Convert.ToHexString(p));
                break;
            case PacketType.JoinAccept when p.Length == 4:
                yield return new(at, 4, "SERVER_NONCE", Convert.ToHexString(p));
                break;
            case PacketType.Ota when Payloads.ReadOta(p) is { } o:
                yield return new(at, 1, "UPDATE_ID", o.UpdateId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                yield return new(at + 1, 1, "OP", o.Op.ToString().ToUpperInvariant());
                if (p.Length > 2)
                {
                    yield return new(at + 2, p.Length - 2, "ARG", o.Op switch
                    {
                        OtaOp.Get => o.Length is { } l ? $"offset {o.Offset}, {l} bytes" : $"offset {o.Offset}",
                        OtaOp.Decline => Payloads.DeclineReason(o.Reason),
                        OtaOp.Failed => Payloads.FailReason(o.Reason),
                        _ => o.Version ?? "",
                    });
                }
                break;
            case PacketType.OtaOffer when Payloads.ReadOtaOffer(p) is { } offer:
                yield return new(at, 1, "UPDATE_ID", offer.UpdateId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                yield return new(at + 1, 4, "SIZE", $"{offer.Size} bytes");
                yield return new(at + 5, 2, "CHUNK", $"{offer.Chunk} bytes");
                yield return new(at + 7, 32, "SHA256", Convert.ToHexString(offer.Sha256).ToLowerInvariant());
                if (p.Length > 39)
                {
                    yield return new(at + 39, p.Length - 39, "VERSION", offer.Version);
                }
                break;
            case PacketType.OtaChunk when Payloads.ReadOtaChunk(p) is { } chunk:
                yield return new(at, 1, "UPDATE_ID", chunk.UpdateId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                yield return new(at + 1, 4, "OFFSET", chunk.Offset == OtaChunkPayload.Abort ? "abort" : chunk.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (chunk.Data.Length > 0)
                {
                    yield return new(at + 5, chunk.Data.Length, "DATA", $"{chunk.Data.Length} bytes");
                }
                break;
            default:
                yield return new(at, p.Length, "PAYLOAD", PacketCodec.Render(p));
                break;
        }
    }
}
