using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace PNMTD.Lib.WireGuard;

/// <summary>
/// The tunnel network (docs/mikrotik-plan.md §3.2): one IPv4 prefix per installation, default
/// 10.253.0.0/16. Its first /30 is the server block: .1, the server address PNMTD's SSH connections come
/// from, and .2, the access address of custom tunnels. Peers get the next free addresses after it,
/// up to the last one before the broadcast address.
/// </summary>
public sealed record AddressPlan
{
    public const string DefaultPrefix = "10.253.0.0/16";

    public IPAddress Network { get; }

    public int Length { get; }

    private AddressPlan(IPAddress network, int length)
    {
        Network = network;
        Length = length;
    }

    public static AddressPlan Default => Parse(DefaultPrefix);

    /// <summary>"10.253.0.0/16": IPv4, a length from 8 to 28 (room for the server block and peers). Host bits are cleared.</summary>
    public static bool TryParse(string? text, out AddressPlan plan, out string? error)
    {
        plan = null!;
        error = null;
        if (text is null || !IPNetwork.TryParse(text.Trim(), out var network))
        {
            error = "An IPv4 network such as 10.253.0.0/16.";
            return false;
        }
        if (network.BaseAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "IPv4 only: the tunnel network has no IPv6 (§1).";
            return false;
        }
        if (network.PrefixLength is < 8 or > 28)
        {
            error = "A prefix length from /8 to /28.";
            return false;
        }
        plan = new AddressPlan(network.BaseAddress, network.PrefixLength);
        return true;
    }

    public static AddressPlan Parse(string text) =>
        TryParse(text, out var plan, out var error) ? plan : throw new FormatException(error);

    public override string ToString() => $"{Network}/{Length}";

    private uint Base => BinaryPrimitives.ReadUInt32BigEndian(Network.GetAddressBytes());

    private uint Size => 1u << (32 - Length);

    private static IPAddress At(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    private static uint Value(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.MapToIPv4().GetAddressBytes());

    /// <summary>.1: where PNMTD's SSH connections come from; the gateway's address on wg0.</summary>
    public IPAddress ServerAddress => At(Base + 1);

    /// <summary>.2: where custom tunnels' traffic comes from, after the gateway rewrote it (§3.5).</summary>
    public IPAddress AccessAddress => At(Base + 2);

    /// <summary>The first /30, the only part routers route into the tunnel.</summary>
    public string ServerBlock => $"{Network}/30";

    /// <summary>How many peers fit.</summary>
    public long Capacity => Size - 5;

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && (Value(address) & Mask) == Base;

    private uint Mask => Length == 0 ? 0 : uint.MaxValue << (32 - Length);

    /// <summary>A peer address: after the server block, before the broadcast address.</summary>
    public bool IsPeerAddress(IPAddress address) =>
        Contains(address) && Value(address) - Base is var offset && offset >= 4 && offset < Size - 1;

    /// <summary>The lowest free peer address, or null when the network is full.</summary>
    public IPAddress? Allocate(IEnumerable<IPAddress> used)
    {
        var taken = used.Where(Contains).Select(a => Value(a) - Base).ToHashSet();
        for (uint offset = 4; offset < Size - 1; offset++)
        {
            if (!taken.Contains(offset))
            {
                return At(Base + offset);
            }
        }
        return null;
    }

    /// <summary>The address at the same place in another prefix (§3.3: a new prefix re-addresses every peer); null when it does not fit.</summary>
    public IPAddress? Move(IPAddress address, AddressPlan to)
    {
        var offset = Value(address) - Base;
        return offset >= 4 && offset < to.Size - 1 ? At(to.Base + offset) : null;
    }
}
