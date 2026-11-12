using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PNMTD.Lib.Udp;

/// <summary>
/// The first bytes of a packet (docs/udp-protocol.md §3.2): byte 0 <c>VER TYPE IDL</c>, byte 1
/// <c>A M RSV SUITE</c>, the device id (1–4 bytes) and the low 16 bits of the frame counter.
/// <see cref="IdLength"/> 0 means "as few bytes as the id needs" when building.
/// </summary>
public readonly record struct PacketHeader(
    PacketType Type,
    SecuritySuite Suite,
    uint DeviceId,
    ushort Counter,
    bool AckRequested = false,
    bool More = false,
    int IdLength = 0)
{
    public bool IsDownlink => Type.IsDownlink();

    /// <summary>The bytes the id takes: <see cref="IdLength"/>, or the fewest that hold it.</summary>
    public int EffectiveIdLength => IdLength is >= 1 and <= 4 ? IdLength : MinimalIdLength(DeviceId);

    public int Length => 2 + EffectiveIdLength + 2;

    public static int MinimalIdLength(uint id) => id <= 0xFF ? 1 : id <= 0xFFFF ? 2 : id <= 0xFFFFFF ? 3 : 4;
}

public enum ParseError
{
    None,
    TooShort,
    TooLong,
    BadVersion,
    UnknownType,
    UnknownSuite,
    ReservedBits,
    ZeroDeviceId,
    TagDoesNotFit,
}

/// <summary>A datagram whose structure is valid: header, payload (ciphertext for AES-CCM) and tag. Nothing is verified yet.</summary>
public sealed class ParsedPacket
{
    public required PacketHeader Header { get; init; }

    public required ReadOnlyMemory<byte> Datagram { get; init; }

    public int HeaderLength => Header.Length;

    public int TagLength => Header.Suite.TagLength();

    public ReadOnlyMemory<byte> HeaderBytes => Datagram[..HeaderLength];

    /// <summary>The payload as it travels: encrypted for AES-CCM.</summary>
    public ReadOnlyMemory<byte> Body => Datagram[HeaderLength..(Datagram.Length - TagLength)];

    public ReadOnlyMemory<byte> Tag => Datagram[(Datagram.Length - TagLength)..];

    /// <summary>Every byte before the tag: what a MAC covers.</summary>
    public ReadOnlyMemory<byte> Signed => Datagram[..(Datagram.Length - TagLength)];
}

/// <summary>
/// Parses, builds, verifies and decrypts PNMT-UDP v1 packets (docs/udp-protocol.md §3). No I/O.
/// The MAC of every keyed suite covers <c>PREFIX ‖ P</c>: P are the packet's bytes before the tag,
/// PREFIX the full 32-bit frame counter of the uplink (a downlink: of the uplink it answers) or, for
/// JOIN_ACCEPT, the JOIN's DEV_NONCE (§3.6, §3.8).
/// </summary>
public static class PacketCodec
{
    public const int Version = 1;

    /// <summary>The UDP payload every IPv4 path carries without fragmentation (576 − 60 − 8).</summary>
    public const int MaxDatagram = 508;

    public const int MinDatagram = 5;

    public static bool TryParse(ReadOnlyMemory<byte> datagram, out ParsedPacket packet, out ParseError error)
    {
        packet = null!;
        var span = datagram.Span;
        if (span.Length < MinDatagram)
        {
            error = ParseError.TooShort;
            return false;
        }
        if (span.Length > MaxDatagram)
        {
            error = ParseError.TooLong;
            return false;
        }
        if (span[0] >> 6 != Version)
        {
            error = ParseError.BadVersion;
            return false;
        }
        var typeValue = (byte)((span[0] >> 2) & 0xF);
        if (!PacketTypes.IsDefined(typeValue))
        {
            error = ParseError.UnknownType;
            return false;
        }
        if ((span[1] & 0x30) != 0)
        {
            error = ParseError.ReservedBits;
            return false;
        }
        var suiteValue = (byte)(span[1] & 0xF);
        if (!SecuritySuites.IsDefined(suiteValue))
        {
            error = ParseError.UnknownSuite;
            return false;
        }
        var idLength = (span[0] & 0x3) + 1;
        var suite = (SecuritySuite)suiteValue;
        var headerLength = 2 + idLength + 2;
        if (span.Length < headerLength + suite.TagLength())
        {
            error = span.Length < headerLength ? ParseError.TooShort : ParseError.TagDoesNotFit;
            return false;
        }
        uint id = 0;
        for (var i = 0; i < idLength; i++)
        {
            id = (id << 8) | span[2 + i];
        }
        if (id == 0)
        {
            error = ParseError.ZeroDeviceId;
            return false;
        }
        var header = new PacketHeader((PacketType)typeValue, suite, id,
            BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2 + idLength, 2)),
            (span[1] & 0x80) != 0, (span[1] & 0x40) != 0, idLength);
        packet = new ParsedPacket { Header = header, Datagram = datagram };
        error = ParseError.None;
        return true;
    }

    /// <summary>The header bytes alone (no payload, no tag).</summary>
    public static byte[] HeaderBytes(PacketHeader header)
    {
        if (header.DeviceId == 0)
        {
            throw new ArgumentException("Device id 0 is invalid.", nameof(header));
        }
        var idLength = header.EffectiveIdLength;
        if (idLength < PacketHeader.MinimalIdLength(header.DeviceId))
        {
            throw new ArgumentException($"Device id {header.DeviceId} does not fit in {idLength} bytes.", nameof(header));
        }
        var bytes = new byte[header.Length];
        bytes[0] = (byte)((Version << 6) | ((byte)header.Type << 2) | (idLength - 1));
        bytes[1] = (byte)((header.AckRequested ? 0x80 : 0) | (header.More ? 0x40 : 0) | (byte)header.Suite);
        for (var i = 0; i < idLength; i++)
        {
            bytes[2 + i] = (byte)(header.DeviceId >> (8 * (idLength - 1 - i)));
        }
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2 + idLength), header.Counter);
        return bytes;
    }

    /// <summary>
    /// A complete packet: header, payload (encrypted for AES-CCM) and tag.
    /// <paramref name="prefix"/> is FCNT32 (§3.6), or DEV_NONCE for JOIN_ACCEPT.
    /// </summary>
    public static byte[] Build(PacketHeader header, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key, ReadOnlySpan<byte> token, uint prefix)
    {
        var headerBytes = HeaderBytes(header);
        var suite = header.Suite;
        byte[] body;
        byte[] tag;
        if (suite.IsEncrypted())
        {
            (body, tag) = AesModes.CcmEncrypt(RequireKey(key), Nonce(header.IsDownlink, header.DeviceId, prefix), headerBytes,
                payload, suite.TagLength());
        }
        else
        {
            body = payload.ToArray();
            var signed = Concat(headerBytes, body);
            tag = Tag(suite, key, token, prefix, signed);
        }
        var packet = Concat(headerBytes, body, tag);
        if (packet.Length > MaxDatagram)
        {
            throw new ArgumentException($"The packet would be {packet.Length} bytes; at most {MaxDatagram}.", nameof(payload));
        }
        return packet;
    }

    /// <summary>
    /// Verifies the tag (in constant time) and returns the plaintext payload, decrypted for AES-CCM.
    /// Null when the tag does not verify.
    /// </summary>
    public static byte[]? Open(ParsedPacket packet, ReadOnlySpan<byte> key, ReadOnlySpan<byte> token, uint prefix)
    {
        var suite = packet.Header.Suite;
        if (suite == SecuritySuite.None)
        {
            return packet.Body.ToArray();
        }
        if (suite.IsKeyed() && key.Length != SecuritySuites.KeyLength)
        {
            return null;
        }
        if (suite.IsEncrypted())
        {
            return AesModes.CcmDecrypt(key, Nonce(packet.Header.IsDownlink, packet.Header.DeviceId, prefix),
                packet.HeaderBytes.Span, packet.Body.Span, packet.Tag.Span);
        }
        if (suite == SecuritySuite.Token32 && token.Length != SecuritySuites.TokenLength)
        {
            return null;
        }
        var expected = Tag(suite, key, token, prefix, packet.Signed.Span);
        return CryptographicOperations.FixedTimeEquals(expected, packet.Tag.Span) ? packet.Body.ToArray() : null;
    }

    /// <summary>The tag of a MAC suite (or TOKEN32's token) over <c>PREFIX ‖ signed</c>.</summary>
    public static byte[] Tag(SecuritySuite suite, ReadOnlySpan<byte> key, ReadOnlySpan<byte> token, uint prefix, ReadOnlySpan<byte> signed)
    {
        if (suite == SecuritySuite.None)
        {
            return [];
        }
        if (suite == SecuritySuite.Token32)
        {
            if (token.Length != SecuritySuites.TokenLength)
            {
                throw new ArgumentException("TOKEN32 needs a 4-byte token.", nameof(token));
            }
            return token.ToArray();
        }
        if (suite.IsEncrypted())
        {
            throw new ArgumentException("AES-CCM's tag comes from the encryption.", nameof(suite));
        }
        var input = new byte[4 + signed.Length];
        BinaryPrimitives.WriteUInt32BigEndian(input, prefix);
        signed.CopyTo(input.AsSpan(4));
        var k = RequireKey(key);
        var full = suite switch
        {
            SecuritySuite.HmacSha256_32 or SecuritySuite.HmacSha256_64 => HMACSHA256.HashData(k, input),
            SecuritySuite.HmacSha1_32 or SecuritySuite.HmacSha1_64 => HMACSHA1.HashData(k, input),
            SecuritySuite.HmacMd5_32 or SecuritySuite.HmacMd5_64 => HMACMD5.HashData(k, input),
            SecuritySuite.AesCmac32 or SecuritySuite.AesCmac64 => AesModes.Cmac(k, input),
            _ => throw new ArgumentOutOfRangeException(nameof(suite)),
        };
        return full[..suite.TagLength()];
    }

    /// <summary>§3.6: <c>DIR ‖ DEVICE_ID(4) ‖ FCNT32(4) ‖ 00 00 00 00</c>.</summary>
    public static byte[] Nonce(bool downlink, uint deviceId, uint fcnt32)
    {
        var nonce = new byte[13];
        nonce[0] = (byte)(downlink ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(1), deviceId);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(5), fcnt32);
        return nonce;
    }

    private static ReadOnlySpan<byte> RequireKey(ReadOnlySpan<byte> key) =>
        key.Length == SecuritySuites.KeyLength ? key : throw new ArgumentException("A keyed suite needs the node's 16-byte key.", nameof(key));

    private static byte[] Concat(params ReadOnlySpan<byte[]> parts)
    {
        var length = 0;
        foreach (var part in parts)
        {
            length += part.Length;
        }
        var result = new byte[length];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }
        return result;
    }

    /// <summary>The payload as text when it is printable UTF-8 (tabs and line breaks allowed), else <c>hex:</c> and its bytes (§4.2).</summary>
    public static string Render(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return "";
        }
        try
        {
            var text = new UTF8Encoding(false, true).GetString(payload);
            if (text.All(c => !char.IsControl(c) || c is '\t' or '\n' or '\r'))
            {
                return text;
            }
        }
        catch (DecoderFallbackException)
        {
        }
        return "hex:" + Convert.ToHexString(payload);
    }
}

/// <summary>The frame counter window of a node (§3.7): the highest accepted FCNT32 and a 64-bit bitmap below it.</summary>
public readonly record struct CounterState(long? High, ulong Window)
{
    public static readonly CounterState Empty = new(null, 0);
}

public enum CounterVerdict
{
    /// <summary>Higher than everything before, or the first packet.</summary>
    New,
    /// <summary>Within the window and not seen yet.</summary>
    Reordered,
    Replay,
    Jump,
}

public static class FrameCounter
{
    public const int WindowSize = 64;

    public const long MaxGap = 16384;

    public const long Max = uint.MaxValue;

    /// <summary>The full counter closest to <paramref name="high"/> whose low 16 bits are <paramref name="wire"/>. Negative: before 0, a replay.</summary>
    public static long Reconstruct(long? high, ushort wire)
    {
        if (high is not { } h)
        {
            return wire;
        }
        var d = (int)((wire - (h & 0xFFFF)) & 0xFFFF);
        if (d >= 32768)
        {
            d -= 65536;
        }
        return h + d;
    }

    public static CounterVerdict Check(CounterState state, long full)
    {
        if (full < 0)
        {
            return CounterVerdict.Replay;
        }
        if (full > Max)
        {
            return CounterVerdict.Jump;
        }
        if (state.High is not { } high)
        {
            return CounterVerdict.New;
        }
        if (full > high)
        {
            return full - high > MaxGap ? CounterVerdict.Jump : CounterVerdict.New;
        }
        var behind = high - full;
        if (behind >= WindowSize)
        {
            return CounterVerdict.Replay;
        }
        return (state.Window & (1UL << (int)behind)) != 0 ? CounterVerdict.Replay : CounterVerdict.Reordered;
    }

    public static CounterState Accept(CounterState state, long full)
    {
        if (state.High is not { } high)
        {
            return new CounterState(full, 1);
        }
        if (full > high)
        {
            var shift = full - high;
            var window = shift >= WindowSize ? 0 : state.Window << (int)shift;
            return new CounterState(full, window | 1);
        }
        return state with { Window = state.Window | (1UL << (int)(high - full)) };
    }
}

/// <summary>The session key of a JOIN (§3.8), from the root key and both nonces.</summary>
public static class SessionKeys
{
    private static readonly byte[] Label = "PNMT-UDP1 SK"u8.ToArray();

    public static byte[] Derive(KeyFamily family, ReadOnlySpan<byte> rootKey, uint devNonce, uint serverNonce)
    {
        if (family == KeyFamily.Aes)
        {
            var block = new byte[16];
            block[0] = 0x01;
            BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(1), devNonce);
            BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(5), serverNonce);
            return AesModes.EncryptBlock(rootKey, block);
        }
        var input = new byte[Label.Length + 8];
        Label.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(Label.Length), devNonce);
        BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(Label.Length + 4), serverNonce);
        var full = family switch
        {
            KeyFamily.HmacSha1 => HMACSHA1.HashData(rootKey, input),
            KeyFamily.HmacMd5 => HMACMD5.HashData(rootKey, input),
            _ => HMACSHA256.HashData(rootKey, input),
        };
        return full[..16];
    }
}
