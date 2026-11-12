namespace PNMTD.Lib.Udp;

/// <summary>An uplink as a device built it: the bytes, and what a downlink must answer to.</summary>
public sealed record Uplink(byte[] Datagram, uint Fcnt, PacketType Type, SecuritySuite Suite, uint? DevNonce = null);

/// <summary>A verified downlink: its header and its (decrypted) payload.</summary>
public sealed record Downlink(PacketHeader Header, byte[] Payload, ParsedPacket Packet);

/// <summary>
/// The protocol as a node speaks it (docs/udp-protocol.md §3.15), without I/O: the simulator
/// (<c>pnmtd udp node</c>), the tests and the Protocol page's examples build their packets here.
/// </summary>
public sealed class UdpDevice(uint deviceId, KeyFamily family, byte[] key, byte[] token)
{
    public uint DeviceId { get; } = deviceId;

    public KeyFamily Family { get; } = family;

    public byte[] Key { get; } = key;

    public byte[] Token { get; } = token;

    /// <summary>After a JOIN_ACCEPT: the session key for every keyed suite but JOIN's.</summary>
    public byte[]? SessionKey { get; set; }

    /// <summary>The counter of the next new uplink (§3.7). Starts at 1.</summary>
    public uint NextCounter { get; set; } = 1;

    /// <summary>0: the fewest bytes the id needs.</summary>
    public int IdLength { get; set; }

    /// <summary>A suite other than the uplink's that the node's responses may use (the node's response suite, §3.5).</summary>
    public SecuritySuite? ResponseSuite { get; set; }

    /// <summary>Further downlink suites the device is configured to accept, for commands with a suite of their own (§3.10).</summary>
    public HashSet<SecuritySuite> AcceptedSuites { get; } = [];

    private byte[] KeyFor(SecuritySuite suite, bool join) => suite.IsKeyed() && !join ? SessionKey ?? Key : Key;

    /// <summary>A new uplink with the next counter, or with <paramref name="fcnt"/> as given (tests).</summary>
    public Uplink Build(PacketType type, SecuritySuite suite, ReadOnlySpan<byte> payload, bool ack = false, uint? fcnt = null)
    {
        if (type is PacketType.Join || type.IsDownlink())
        {
            throw new ArgumentException($"{type.Name()} is not built this way.", nameof(type));
        }
        var counter = fcnt ?? NextCounter++;
        var header = new PacketHeader(type, suite, DeviceId, (ushort)counter, ack, IdLength: IdLength);
        return new Uplink(PacketCodec.Build(header, payload, KeyFor(suite, false), Token, counter), counter, type, suite);
    }

    /// <summary>A JOIN (§3.8): counter 0, the root key, the family's 64-bit MAC suite.</summary>
    public Uplink BuildJoin(uint devNonce)
    {
        var suite = Family.JoinSuite();
        var header = new PacketHeader(PacketType.Join, suite, DeviceId, 0, IdLength: IdLength);
        return new Uplink(PacketCodec.Build(header, Payloads.Join(devNonce), Key, Token, 0), 0, PacketType.Join, suite, devNonce);
    }

    /// <summary>
    /// The downlink that answers <paramref name="uplink"/>, verified as §3.15 says: a downlink type, the
    /// uplink's suite or the response suite, the uplink's counter echoed, the tag. Null otherwise.
    /// A JOIN_ACCEPT also switches the device to the new session (counter 1).
    /// </summary>
    public Downlink? Open(ReadOnlyMemory<byte> datagram, Uplink uplink, out string? why)
    {
        if (!PacketCodec.TryParse(datagram, out var packet, out var error))
        {
            why = $"not a PNMT-UDP packet: {error}";
            return null;
        }
        var h = packet.Header;
        if (!h.IsDownlink)
        {
            why = $"{h.Type.Name()} is not a downlink";
            return null;
        }
        if (h.DeviceId != DeviceId)
        {
            why = $"for device {h.DeviceId}";
            return null;
        }
        if (h.Suite != uplink.Suite && h.Suite != ResponseSuite && !AcceptedSuites.Contains(h.Suite))
        {
            why = $"suite {h.Suite.Name()} is neither the uplink's nor one the device accepts";
            return null;
        }
        if (h.Counter != (ushort)uplink.Fcnt)
        {
            why = $"answers counter {h.Counter}, not {(ushort)uplink.Fcnt}";
            return null;
        }
        var join = h.Type == PacketType.JoinAccept;
        if (join && uplink.DevNonce is null)
        {
            why = "a JOIN_ACCEPT without a JOIN";
            return null;
        }
        var prefix = join ? uplink.DevNonce!.Value : uplink.Fcnt;
        var payload = PacketCodec.Open(packet, KeyFor(h.Suite, join), Token, prefix);
        if (payload is null)
        {
            why = "the tag does not verify";
            return null;
        }
        if (join)
        {
            if (Payloads.ReadJoinAccept(payload) is not { } serverNonce)
            {
                why = "a JOIN_ACCEPT without SERVER_NONCE";
                return null;
            }
            SessionKey = SessionKeys.Derive(Family, Key, uplink.DevNonce!.Value, serverNonce);
            NextCounter = 1;
        }
        why = null;
        return new Downlink(h, payload, packet);
    }
}
