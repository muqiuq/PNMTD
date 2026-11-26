using PNMTD.Lib.Mikrotik;

namespace PNMTD.Lib.Network;

/// <summary>The tables a scan reads, in their order (docs/network-map-plan.md §4.1).</summary>
public enum ScanTable { Interfaces, Ethernet, Bridges, MacTable, Neighbors, Arp, Dhcp, Addresses, Tunnels, Wifi }

/// <summary>
/// The scan's scripts (§4.1): each table is one exec of a one-line script that prints JSON, as the poll does
/// (<see cref="RouterPoll"/>), run as <c>pnmt</c> with the <c>read</c> policy only. Recorded on 7.13 and 7.25.1 (N0):
/// <list type="bullet">
/// <item>The prints take every column, except where a column could hold a secret: the WireGuard prints and the tunnels name
/// their properties (no private key, no preshared key, no IPsec secret). A proplist may name only properties that every
/// version has; an unknown one fails the print.</item>
/// <item>PNMT's own tunnel (the installation's comment) is left out of the WireGuard prints: it is monitoring, not topology.</item>
/// <item>Menus that a router may not have (Wi-Fi in either package, bonding) are parsed at run time inside
/// <c>:do … on-error</c>, so their absence fails nothing.</item>
/// <item><c>/interface ethernet monitor … once</c> runs per port inside <c>:do … on-error</c>: a port that can't be monitored
/// (a CHR's virtio port on some versions) leaves only its own entry out.</item>
/// <item>VLANs (docs/network-suggestion-rules-plan.md §10, V0, two linked CHRs 7.25.1, <c>Fixtures/Network/vlans</c>):
/// neighbor discovery answers on every VLAN interface, each its own entry with the VLAN's name in <c>interface</c> and
/// <c>interface-name</c>. Through a bridge port <c>interface</c> is <c>["ether2","bridge1"]</c>; through a VLAN on a
/// vlan-filtering bridge only <c>["vlan10"]</c> (and without LLDP); through a VLAN that is a bridge's port
/// <c>["vlan10","br10"]</c>, with <c>interface-name</c> as <c>br10/vlan10</c>. A vlan-filtering bridge's MAC table names
/// the member port with <c>vid</c>; with one bridge per VLAN, <c>on-interface</c> is the VLAN interface.</item>
/// </list>
/// </summary>
public static class NetworkScanScript
{
    public static readonly IReadOnlyList<ScanTable> Order = Enum.GetValues<ScanTable>();

    /// <summary>The step's name, as the Scans tab shows it.</summary>
    public static string Step(ScanTable table) => table switch
    {
        ScanTable.Interfaces => "interfaces",
        ScanTable.Ethernet => "Ethernet and SFP",
        ScanTable.Bridges => "bridges and VLANs",
        ScanTable.MacTable => "the MAC table",
        ScanTable.Neighbors => "neighbors",
        ScanTable.Arp => "ARP",
        ScanTable.Dhcp => "DHCP leases",
        ScanTable.Addresses => "addresses",
        ScanTable.Tunnels => "tunnels",
        ScanTable.Wifi => "Wi-Fi clients",
        _ => table.ToString(),
    };

    /// <summary>Without its interfaces, nothing else of a router can be placed: the router's scan fails.</summary>
    public static bool Required(ScanTable table) => table == ScanTable.Interfaces;

    /// <summary>The script of a table, for the installation's comment (<c>pnmt:7f3a</c>).</summary>
    public static string Script(ScanTable table, string tag)
    {
        var t = RouterOsScript.Quote(tag);
        return table switch
        {
            // Without a proplist, 7.13 leaves running, disabled and default-name out, and 7.25.1 default-name (N0).
            ScanTable.Interfaces => ":put [:serialize to=json [/interface/print as-value proplist=name,default-name,type,mac-address,actual-mtu,mtu,running,disabled,comment,slave]]",
            ScanTable.Ethernet => Block(
                ":set ($r->\"ports\") [/interface/ethernet/print as-value]",
                ":local m ({})",
                ":foreach i in=[/interface/ethernet/find] do={ :local n [/interface/ethernet/get $i name]; :do { :set ($m->$n) [/interface/ethernet/monitor $i once as-value] } on-error={} }",
                ":set ($r->\"monitor\") $m"),
            ScanTable.Bridges => Block(
                ":set ($r->\"bridges\") [/interface/bridge/print as-value]",
                ":set ($r->\"ports\") [/interface/bridge/port/print as-value]",
                ":set ($r->\"vlans\") [/interface/vlan/print as-value]",
                ":do { :set ($r->\"bonds\") [[:parse \"/interface/bonding/print as-value\"]] } on-error={}"),
            ScanTable.MacTable => ":put [:serialize to=json [/interface/bridge/host/print as-value]]",
            ScanTable.Neighbors => Block(
                // Without a proplist, 7.25.1 leaves interface-name, discovered-by and platform out; 7.13 has every one named (V0).
                ":set ($r->\"neighbors\") [/ip/neighbor/print as-value proplist=interface,address,address4,mac-address,identity,platform,board,version,interface-name,discovered-by]",
                ":do { :set ($r->\"settings\") [/ip/neighbor/discovery-settings/get] } on-error={}"),
            ScanTable.Arp => ":put [:serialize to=json [/ip/arp/print as-value]]",
            ScanTable.Dhcp => Block(
                ":set ($r->\"servers\") [/ip/dhcp-server/print as-value]",
                ":set ($r->\"leases\") [/ip/dhcp-server/lease/print as-value]"),
            ScanTable.Addresses => ":put [:serialize to=json [/ip/address/print as-value]]",
            ScanTable.Tunnels => Block(
                $":set ($r->\"wireguard\") [/interface/wireguard/print as-value proplist=name,public-key,listen-port where comment!={t}]",
                $":set ($r->\"peers\") [/interface/wireguard/peers/print as-value proplist=interface,public-key,endpoint-address,endpoint-port,allowed-address where comment!={t}]",
                ":do { :set ($r->\"eoip\") [/interface/eoip/print as-value proplist=name,local-address,remote-address] } on-error={}",
                ":do { :set ($r->\"gre\") [/interface/gre/print as-value proplist=name,local-address,remote-address] } on-error={}",
                ":do { :set ($r->\"ipip\") [/interface/ipip/print as-value proplist=name,local-address,remote-address] } on-error={}"),
            ScanTable.Wifi => Block(
                ":do { :set ($r->\"wifi\") [[:parse \"/interface/wifi/registration-table/print as-value\"]] } on-error={}",
                ":do { :set ($r->\"wireless\") [[:parse \"/interface/wireless/registration-table/print as-value\"]] } on-error={}"),
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
    }

    /// <summary>One line: a local array filled by the parts, printed as JSON.</summary>
    private static string Block(params string[] parts) =>
        "{ :local r ({}); " + string.Join("; ", parts) + "; :put [:serialize to=json $r] }";
}
