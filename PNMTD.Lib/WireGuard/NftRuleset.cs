using System.Globalization;
using System.Text;

namespace PNMTD.Lib.WireGuard;

/// <summary>A custom tunnel's reach (§3.5): its address, and the router addresses it may reach on the management ports.</summary>
public sealed record TunnelAccess(string Address, IReadOnlyList<string> RouterAddresses);

/// <summary>
/// The gateway's firewall (docs/mikrotik-plan.md §3.4, §3.5), as one nftables file that replaces the
/// table atomically (<c>nft -f</c>). Deny by default:
/// <list type="bullet">
/// <item>input from wg0: only replies to the gateway's own connections (PNMTD's SSH through SOCKS). Nothing
/// listens on the tunnel addresses.</item>
/// <item>forward: only custom tunnels to the management ports of the routers they may reach, and the
/// replies. Routers reach nothing, and neither do tunnels each other.</item>
/// <item>NAT: custom tunnels' traffic to routers leaves with the access address (.2), so a router tells it
/// from PNMTD's (.1), and the <c>pnmt</c> user, bound to .1, is out of their reach.</item>
/// </list>
/// The gateway loads the version without peers before wg0 exists, so forwarding is never open.
/// </summary>
public static class NftRuleset
{
    public const string Table = "pnmt";

    public static string Build(string wireGuardInterface, string accessAddress, IReadOnlyList<int> managementPorts, IEnumerable<TunnelAccess> tunnels)
    {
        var wg = Quote(wireGuardInterface);
        var ports = string.Join(", ", managementPorts.Distinct().Order().Select(p => p.ToString(CultureInfo.InvariantCulture)));
        var sb = new StringBuilder();
        // Declaring the table first makes the delete succeed whether or not it exists: one atomic replace.
        sb.Append("table inet ").Append(Table).Append(" {}\n");
        sb.Append("delete table inet ").Append(Table).Append('\n');
        sb.Append("table inet ").Append(Table).Append(" {\n");
        sb.Append("  chain input {\n");
        sb.Append("    type filter hook input priority 0; policy accept;\n");
        sb.Append("    iifname ").Append(wg).Append(" ct state established,related accept\n");
        sb.Append("    iifname ").Append(wg).Append(" drop\n");
        sb.Append("  }\n");
        sb.Append("  chain forward {\n");
        sb.Append("    type filter hook forward priority 0; policy drop;\n");
        sb.Append("    ct state established,related accept\n");
        foreach (var tunnel in tunnels.OrderBy(t => t.Address, StringComparer.Ordinal))
        {
            if (tunnel.RouterAddresses.Count == 0 || ports.Length == 0)
            {
                continue;
            }
            sb.Append("    iifname ").Append(wg).Append(" oifname ").Append(wg)
                .Append(" ip saddr ").Append(tunnel.Address)
                .Append(" ip daddr { ").Append(string.Join(", ", tunnel.RouterAddresses.Distinct().Order(StringComparer.Ordinal))).Append(" }")
                .Append(" tcp dport { ").Append(ports).Append(" } accept\n");
        }
        sb.Append("  }\n");
        sb.Append("  chain postrouting {\n");
        sb.Append("    type nat hook postrouting priority 100; policy accept;\n");
        sb.Append("    iifname ").Append(wg).Append(" oifname ").Append(wg).Append(" snat ip to ").Append(accessAddress).Append('\n');
        sb.Append("  }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "", StringComparison.Ordinal) + "\"";
}
