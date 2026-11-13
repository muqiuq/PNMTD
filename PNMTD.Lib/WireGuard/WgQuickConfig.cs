using System.Globalization;
using System.Text;

namespace PNMTD.Lib.WireGuard;

/// <summary>
/// A custom tunnel's configuration file (docs/mikrotik-plan.md §3.5), in wg-quick's format: for the WireGuard
/// apps on Windows, macOS, Linux, Android and iOS, or <c>wg-quick up</c>. It routes only the tunnel network,
/// so the device's other traffic is untouched.
/// </summary>
public static class WgQuickConfig
{
    public static string Build(string tunnelName, string instance, string privateKey, string address, int mtu,
        string gatewayPublicKey, string endpoint, string network, int keepaliveSeconds)
    {
        var sb = new StringBuilder();
        sb.Append("# PNMT tunnel \"").Append(OneLine(tunnelName)).Append("\" (").Append(OneLine(instance)).Append(").\n");
        sb.Append("# Contains a private key: keep it like a password.\n");
        sb.Append("[Interface]\n");
        sb.Append("PrivateKey = ").Append(privateKey).Append('\n');
        sb.Append("Address = ").Append(address).Append("/32\n");
        sb.Append("MTU = ").Append(mtu.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append('\n');
        sb.Append("[Peer]\n");
        sb.Append("PublicKey = ").Append(gatewayPublicKey).Append('\n');
        sb.Append("Endpoint = ").Append(endpoint).Append('\n');
        sb.Append("# Only the tunnel network: the device's other traffic is untouched.\n");
        sb.Append("AllowedIPs = ").Append(network).Append('\n');
        sb.Append("PersistentKeepalive = ").Append(keepaliveSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    /// <summary>A file name for the download: letters, digits, dashes; the WireGuard apps name the tunnel after it (at most 15 characters on Linux).</summary>
    public static string FileName(string tunnelName)
    {
        var name = new string(tunnelName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (name.Length == 0)
        {
            name = "pnmt";
        }
        return (name.Length > 15 ? name[..15] : name) + ".conf";
    }

    private static string OneLine(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
