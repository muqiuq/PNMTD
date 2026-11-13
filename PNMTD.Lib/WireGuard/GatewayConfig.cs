using System.Globalization;
using System.Text;

namespace PNMTD.Lib.WireGuard;

/// <summary>A peer as the gateway configures it: its key and its one address.</summary>
public sealed record GatewayPeer(string PublicKey, string Address);

/// <summary>What <c>wg show wg0 dump</c> says about a peer (docs/mikrotik-plan.md §3.2).</summary>
public sealed record PeerState(string PublicKey, string? Endpoint, DateTime? LatestHandshake, long RxBytes, long TxBytes);

/// <summary>
/// The gateway's WireGuard side (§3.4): the file <c>wg syncconf</c> takes (wg's format, not wg-quick's:
/// no Address, no DNS), and the parser of <c>wg show … dump</c>. Peers have no endpoint: the gateway learns
/// it from each handshake. No keepalive either: the routers keep the tunnel open.
/// </summary>
public static class GatewayConfig
{
    public static string SyncConf(string privateKey, int listenPort, IEnumerable<GatewayPeer> peers)
    {
        var sb = new StringBuilder();
        sb.Append("[Interface]\n");
        sb.Append("PrivateKey = ").Append(privateKey).Append('\n');
        sb.Append("ListenPort = ").Append(listenPort.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var peer in peers.OrderBy(p => p.PublicKey, StringComparer.Ordinal))
        {
            sb.Append("\n[Peer]\n");
            sb.Append("PublicKey = ").Append(peer.PublicKey).Append('\n');
            sb.Append("AllowedIPs = ").Append(peer.Address).Append("/32\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// <c>wg show wg0 dump</c>: the interface's line first (private key, public key, port, fwmark), then one
    /// tab-separated line per peer: public key, preshared key, endpoint, allowed IPs, latest handshake (Unix
    /// seconds, 0 for never), received, sent, persistent keepalive.
    /// </summary>
    public static IReadOnlyList<PeerState> ParseDump(string dump)
    {
        var peers = new List<PeerState>();
        foreach (var (line, index) in dump.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select((l, i) => (l.TrimEnd('\r'), i)))
        {
            var f = line.Split('\t');
            if (index == 0 && f.Length == 4)
            {
                continue;
            }
            if (f.Length < 8)
            {
                continue;
            }
            var handshake = long.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? DateTime.UnixEpoch.AddSeconds(seconds)
                : (DateTime?)null;
            peers.Add(new PeerState(f[0], f[2] == "(none)" ? null : f[2], handshake, Number(f[5]), Number(f[6])));
        }
        return peers;
    }

    private static long Number(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>A WireGuard key: 32 bytes in Base64 (44 characters with the padding).</summary>
    public static bool IsKey(string? text)
    {
        if (text is not { Length: 44 } || !text.EndsWith('='))
        {
            return false;
        }
        Span<byte> bytes = stackalloc byte[33];
        return Convert.TryFromBase64String(text, bytes, out var written) && written == 32;
    }
}
