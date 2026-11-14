using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Native;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// <c>network</c> (docs/windows-sensor-plan.md §4.4): adapters with their addresses, gateways and DNS, teams and virtual
/// switches, routes, listening ports with their processes and services, connections by state, the firewall with the
/// inbound rules for ports that listen, the WinHTTP proxy and the hosts file. On a network change and every 5 minutes.
/// </summary>
internal sealed class NetworkCollector : Collector
{
    public override string Name => SectionNames.Network;

    public override TimeSpan Interval => TimeSpan.FromMinutes(5);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string> { "connectionsByState", "leaseExpires", "pid" };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var wmiAdapters = Isolated.Part(errors, "MSFT_NetAdapter", () => Wmi.Query(Wmi.StandardCimv2,
                "SELECT Name, InterfaceIndex, DriverDescription, DriverVersionString, VlanID, Virtual, HardwareInterface FROM MSFT_NetAdapter")
            .ToDictionary(a => a.Str("Name") ?? "", StringComparer.OrdinalIgnoreCase)) ?? [];
        var profiles = Isolated.Part(errors, "MSFT_NetConnectionProfile", () => Wmi.Query(Wmi.StandardCimv2,
                "SELECT InterfaceAlias, NetworkCategory FROM MSFT_NetConnectionProfile")
            .GroupBy(p => p.Str("InterfaceAlias") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Int("NetworkCategory") switch { 0 => "Public", 1 => "Private", 2 => "Domain", _ => null },
                StringComparer.OrdinalIgnoreCase)) ?? [];
        var leases = Isolated.Part(errors, "Win32_NetworkAdapterConfiguration", () => Wmi.Query(Wmi.Cimv2,
                "SELECT InterfaceIndex, DHCPLeaseExpires FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE")
            .Where(c => c.Int("InterfaceIndex") is not null)
            .ToDictionary(c => c.Int("InterfaceIndex")!.Value, c => c.Date("DHCPLeaseExpires"))) ?? [];

        var adapters = Isolated.Part(errors, "adapters", () => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => Adapter(n, wmiAdapters.GetValueOrDefault(n.Name), profiles.GetValueOrDefault(n.Name), leases))
            .ToList()) ?? [];

        var teams = Isolated.Part(errors, "NIC teams", () =>
        {
            var members = Wmi.Query(Wmi.StandardCimv2, "SELECT Name, Team FROM MSFT_NetLbfoTeamMember")
                .GroupBy(m => m.Str("Team") ?? "").ToDictionary(g => g.Key, g => g.Select(m => m.Str("Name") ?? "").ToList());
            return Wmi.Query(Wmi.StandardCimv2, "SELECT Name, TeamingMode FROM MSFT_NetLbfoTeam")
                .Select(t => new TeamInfo(t.Str("Name") ?? "", members.GetValueOrDefault(t.Str("Name") ?? "") ?? [],
                    t.Int("TeamingMode") switch { 0 => "Static", 1 => "Switch independent", 2 => "LACP", _ => null }))
                .ToList();
        }) ?? [];
        var switches = Optional(() => Wmi.Query(Wmi.Virtualization, "SELECT ElementName FROM Msvm_VirtualEthernetSwitch")
            .Select(s => s.Str("ElementName") ?? "").Where(s => s.Length > 0).ToList()) ?? [];

        var routes = Isolated.Part(errors, "routes", Routes) ?? [];
        var services = Isolated.Part(errors, "services by process", () => Wmi.Query(Wmi.Cimv2, "SELECT Name, ProcessId FROM Win32_Service WHERE ProcessId > 0")
            .GroupBy(s => s.Int("ProcessId") ?? 0).ToDictionary(g => g.Key, g => g.Select(s => s.Str("Name") ?? "").Order().ToList())) ?? [];
        var listeners = Isolated.Part(errors, "listeners", () => Win32.Listeners()
            .DistinctBy(l => (l.Protocol, l.Address, l.Port))
            .OrderBy(l => l.Port).ThenBy(l => l.Protocol)
            .Select(l => new ListenerInfo(l.Protocol, l.Address.ToString(), l.Port, l.Pid, ProcessName(l.Pid), services.GetValueOrDefault(l.Pid) ?? []))
            .ToList()) ?? [];
        var connections = Isolated.Part(errors, "connections", () => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .GroupBy(c => c.State.ToString()).ToDictionary(g => g.Key, g => g.Count())) ?? [];

        var firewall = Isolated.Part(errors, "firewall", () =>
        {
            var rules = Firewall.InboundAllowRules();
            var relevant = rules.Where(r => listeners.Any(l => Firewall.Covers(r, l.Protocol, l.Port)))
                .Select(r => new FirewallRuleInfo(r.Name, r.Group, Firewall.ProtocolName(r.Protocol), r.Ports, Firewall.ProfileNames(r.Profiles), r.Program,
                    r.RemoteAddresses is "*" ? null : r.RemoteAddresses))
                .OrderBy(r => r.Name).Take(500).ToList();
            return new FirewallInfo(Firewall.Profiles(), relevant);
        });

        return new NetworkSection(adapters, teams, switches, routes, listeners, connections, firewall,
            Isolated.Part(errors, "proxy", Proxy), Isolated.Part(errors, "hosts file", HostsEntries) ?? []);
    }

    private static AdapterInfo Adapter(NetworkInterface n, System.Management.ManagementBaseObject? wmi, string? profile,
        IReadOnlyDictionary<int, DateTimeOffset?> leases)
    {
        var properties = n.GetIPProperties();
        int? index = null;
        int? mtu = null;
        try
        {
            var v4 = properties.GetIPv4Properties();
            index = v4.Index;
            mtu = v4.Mtu;
        }
        catch (NetworkInformationException)
        {
        }
        var dhcp = false;
        try
        {
            dhcp = properties.GetIPv4Properties().IsDhcpEnabled;
        }
        catch (NetworkInformationException)
        {
        }
        var addresses = properties.UnicastAddresses
            .Where(a => !IPAddress.IsLoopback(a.Address))
            .Select(a => $"{a.Address}/{a.PrefixLength}")
            .ToList();
        var mac = n.GetPhysicalAddress().GetAddressBytes() is { Length: > 0 } bytes ? string.Join(':', bytes.Select(b => b.ToString("X2"))) : null;
        var isVirtual = wmi?.Bool("Virtual") ?? (wmi?.Bool("HardwareInterface") == false);
        return new AdapterInfo(
            n.Name,
            n.Description,
            n.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => isVirtual ? "Virtual" : "Ethernet",
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ppp => "PPP",
                _ => n.NetworkInterfaceType.ToString(),
            },
            mac,
            n.OperationalStatus.ToString(),
            n.Speed > 0 ? n.Speed : null,
            mtu,
            dhcp,
            properties.DhcpServerAddresses.FirstOrDefault()?.ToString(),
            index is { } i ? leases.GetValueOrDefault(i) : null,
            addresses,
            properties.GatewayAddresses.Select(g => g.Address.ToString()).Where(g => g is not ("0.0.0.0" or "::")).ToList(),
            properties.DnsAddresses.Select(d => d.ToString()).ToList(),
            string.IsNullOrEmpty(properties.DnsSuffix) ? null : properties.DnsSuffix,
            properties.IsDynamicDnsEnabled,
            wmi?.Int("VlanID") is > 0 and var vlan ? vlan : null,
            wmi?.Str("DriverDescription"),
            wmi?.Str("DriverVersionString"),
            profile,
            isVirtual);
    }

    /// <summary>The default routes, and the routes that are not on-link (§4.4), from both stores.</summary>
    private static List<RouteInfo> Routes() => Wmi.Query(Wmi.StandardCimv2, "SELECT DestinationPrefix, NextHop, InterfaceAlias, RouteMetric, Store FROM MSFT_NetRoute")
        .Select(r => new
        {
            Destination = r.Str("DestinationPrefix") ?? "",
            Gateway = r.Str("NextHop") ?? "",
            Interface = r.Str("InterfaceAlias"),
            Metric = r.Int("RouteMetric"),
            Persistent = r.Int("Store") == 0,
        })
        .Where(r => r.Destination is "0.0.0.0/0" or "::/0" || r.Gateway is not ("0.0.0.0" or "::" or ""))
        .GroupBy(r => (r.Destination, r.Gateway, r.Interface))
        .Select(g => new RouteInfo(g.Key.Destination, g.Key.Gateway, g.Key.Interface, g.First().Metric, g.Any(r => r.Persistent)))
        .OrderBy(r => r.Destination).Take(500)
        .ToList();

    private static string? ProcessName(int pid)
    {
        if (pid is 0 or 4)
        {
            return pid == 4 ? "System" : null;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The WinHTTP proxy, as <c>netsh winhttp show proxy</c> reports it; null for a direct connection.</summary>
    private static string? Proxy()
    {
        using var process = Process.Start(new ProcessStartInfo("netsh", "winhttp show proxy")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null || !process.WaitForExit(5000))
        {
            return null;
        }
        var lines = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var server = lines.FirstOrDefault(l => l.Contains("Proxy Server", StringComparison.OrdinalIgnoreCase));
        return server?.Split(':', 2).ElementAtOrDefault(1)?.Trim() is { Length: > 0 } value ? value : null;
    }

    /// <summary>The hosts file's entries that aren't comments.</summary>
    private static List<string> HostsEntries()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        return File.Exists(path)
            ? File.ReadLines(path).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).Take(200).ToList()
            : [];
    }

    private static T? Optional<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is NotAvailableException or System.Management.ManagementException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
