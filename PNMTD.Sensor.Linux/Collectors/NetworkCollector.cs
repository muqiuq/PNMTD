using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>An interface as <c>ip -j -d link</c> and <c>ip -j addr</c> describe it.</summary>
internal sealed record IpLink(int Index, string Name, IReadOnlyList<string> Flags, int? Mtu, string? OperState, string? LinkType, string? Address, string? Kind,
    string? SlaveKind, string? Master, int? VlanId, string? BondMode);

internal sealed record IpAddress(string Interface, string Family, string Local, int Prefix, bool Dynamic, long? ValidSeconds, string? Scope);

internal sealed record IpRoute(string Destination, string? Gateway, string? Device, int? Metric, string? Protocol, string? Type);

/// <summary>
/// <c>network</c> (docs/linux-sensor-plan.md §5.3 → the Windows record, <see cref="NetworkSection"/>): the adapters from <c>ip -j -d
/// link</c> and <c>addr</c> and <c>/sys/class/net</c> (kind, MAC, state, MTU, speed, driver, addresses, DHCP and its lease best effort,
/// DNS from systemd-resolved or /etc/resolv.conf), bonds and bridges, routes (<c>ip -j route</c>, the main table), listeners
/// (<c>ss -H -tulpn</c>, with the systemd unit from each PID's cgroup), connections by state, the proxy and /etc/hosts' own entries.
/// Containers' and VMs' ports on a bridge (veth, tap, Proxmox's fw*) come and go with every guest and are left out. On a netlink
/// change, every 5 minutes, and every 6 hours.
/// </summary>
internal sealed partial class NetworkCollector : Collector
{
    public override string Name => SectionNames.Network;

    public override TimeSpan Interval => TimeSpan.FromMinutes(5);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string> { "connectionsByState", "leaseExpires", "pid" };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    private const long Forever = 4294967295;

    /// <summary>/proc/net/tcp's states, named as Windows' TcpState (what the Windows agent reports).</summary>
    private static readonly Dictionary<string, string> TcpStates = new(StringComparer.Ordinal)
    {
        ["01"] = "Established", ["02"] = "SynSent", ["03"] = "SynReceived", ["04"] = "FinWait1", ["05"] = "FinWait2", ["06"] = "TimeWait",
        ["07"] = "Closed", ["08"] = "CloseWait", ["09"] = "LastAck", ["0B"] = "Closing",
    };

    [GeneratedRegex(@"\(""([^""]+)"",pid=(\d+)")]
    private static partial Regex SsProcess();

    [GeneratedRegex(@"^Link \d+ \(([^)]+)\):\s*(.*)$")]
    private static partial Regex ResolvectlLink();

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        if (!Tool.Exists("ip"))
        {
            errors.Add("adapters: ip (iproute2) is not installed");
        }
        var links = Isolated.Part(errors, "ip link", () => ParseLinks(Tool.Text("ip", ["-j", "-d", "link", "show"]))) ?? [];
        var addresses = Isolated.Part(errors, "ip addr", () => ParseAddresses(Tool.Text("ip", ["-j", "addr", "show"]))) ?? [];
        var routes = Isolated.Part(errors, "ip route", () => ParseRoutes(Tool.Text("ip", ["-j", "route", "show", "table", "main"]), "inet")
            .Concat(ParseRoutes(Tool.Text("ip", ["-j", "-6", "route", "show", "table", "main"]), "inet6")).ToList()) ?? [];
        var dns = Isolated.Part(errors, "DNS", Dns) ?? new DnsServers([], null, new Dictionary<string, (List<string>, string?)>());
        var shown = links.Where(l => !Hidden(l, links)).ToList();

        var adapters = Isolated.Part(errors, "adapters", () => shown.Where(l => l.LinkType != "loopback")
            .Select(l => Adapter(l, addresses.Where(a => a.Interface == l.Name).ToList(), routes, dns))
            .ToList()) ?? [];
        var teams = links.Where(l => l.Kind == "bond")
            .Select(b => new TeamInfo(b.Name, links.Where(l => l.Master == b.Name).Select(l => l.Name).Order(StringComparer.Ordinal).ToList(),
                b.BondMode ?? BondingMode(b.Name)))
            .ToList();
        var bridges = shown.Where(l => l.Kind == "bridge")
            .Select(b => links.Where(l => l.Master == b.Name && !Hidden(l, links)).Select(l => l.Name).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } ports
                ? $"{b.Name}: {string.Join(", ", ports)}"
                : b.Name)
            .ToList();
        var routeInfos = routes.Where(r => r.Type is null or "unicast")
            .Where(r => !r.Destination.StartsWith("fe80:", StringComparison.Ordinal) && !r.Destination.StartsWith("ff00:", StringComparison.Ordinal))
            .Select(r => new RouteInfo(r.Destination, r.Gateway ?? "", r.Device, r.Metric, r.Protocol is "static" or "boot"))
            .ToList();
        var listeners = Isolated.Part(errors, "listeners", Listeners) ?? [];
        var connections = Isolated.Part(errors, "connections", Connections) ?? [];
        return new NetworkSection(adapters, teams, bridges, routeInfos, listeners, connections, null, Isolated.Part(errors, "proxy", Proxy),
            Isolated.Part(errors, "hosts file", () => HostsEntries(File.ReadAllText("/etc/hosts"), Tool.ReadValue("/proc/sys/kernel/hostname"))) ?? []);
    }

    /// <summary>A guest's port on a bridge (veth, tap, Proxmox's fwpr/fwln), or Proxmox's per-guest firewall bridge (fwbr): left out.</summary>
    public static bool Hidden(IpLink link, IReadOnlyList<IpLink> all) =>
        link.Name.StartsWith("fwbr", StringComparison.Ordinal)
        || link.Master is not null && (link.Name.StartsWith("veth", StringComparison.Ordinal) || link.Name.StartsWith("tap", StringComparison.Ordinal)
            || link.Name.StartsWith("fwpr", StringComparison.Ordinal) || link.Name.StartsWith("fwln", StringComparison.Ordinal))
            && all.Any(l => l.Name == link.Master && l.Kind == "bridge");

    // --- adapters -------------------------------------------------------------------------------------------------------

    private static AdapterInfo Adapter(IpLink link, IReadOnlyList<IpAddress> addresses, IReadOnlyList<IpRoute> routes, DnsServers dns)
    {
        var sys = Path.Combine("/sys/class/net", link.Name);
        var driver = new DirectoryInfo(Path.Combine(sys, "device", "driver")).LinkTarget is { } target ? Path.GetFileName(target) : null;
        var kind = link.Kind switch
        {
            "vlan" => "VLAN",
            "bridge" => "Bridge",
            "bond" => "Bond",
            "wireguard" => "WireGuard",
            "veth" => "veth",
            "tun" => "tun",
            { } other => other,
            null when Directory.Exists(Path.Combine(sys, "wireless")) || Directory.Exists(Path.Combine(sys, "phy80211")) => "Wi-Fi",
            null when link.LinkType == "ether" => "Ethernet",
            null when link.LinkType == "none" => "tun",
            _ => link.LinkType ?? "unknown",
        };
        var up = link.OperState == "UP" || link.OperState is "UNKNOWN" or null && link.Flags.Contains("UP") && link.Flags.Contains("LOWER_UP");
        var speed = long.TryParse(Tool.ReadValue(Path.Combine(sys, "speed")), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mbps) && mbps > 0
            ? mbps * 1_000_000 : (long?)null;
        var dhcp = addresses.FirstOrDefault(a => a.Family == "inet" && a.Dynamic);
        var (linkDns, suffix) = dns.PerLink.TryGetValue(link.Name, out var own) && own.Servers.Count > 0 ? own
            : (routes.Any(r => r.Device == link.Name && r.Destination is "0.0.0.0/0" or "::/0") ? dns.Global : [], dns.Suffix);
        return new AdapterInfo(
            link.Name,
            driver ?? link.Kind,
            kind,
            link.Address is { Length: > 0 } mac && link.LinkType != "none" ? mac.ToUpperInvariant() : null,
            up ? "Up" : "Down",
            speed,
            link.Mtu,
            dhcp is not null,
            dhcp is null ? null : Isolated.Part([], "lease", () => DhcpServer(link)),
            dhcp?.ValidSeconds is { } valid && valid != Forever ? DateTimeOffset.UtcNow.AddSeconds(valid) : null,
            addresses.Select(a => $"{a.Local}/{a.Prefix}").ToList(),
            routes.Where(r => r.Device == link.Name && r.Gateway is not null && r.Destination is "0.0.0.0/0" or "::/0").Select(r => r.Gateway!).Distinct().ToList(),
            linkDns,
            suffix,
            null,
            link.VlanId,
            driver,
            driver is null ? null : Tool.ReadValue($"/sys/module/{driver}/version"),
            null,
            !Directory.Exists(Path.Combine(sys, "device")));
    }

    /// <summary><c>ip -j -d link show</c>.</summary>
    public static List<IpLink> ParseLinks(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(e =>
        {
            var info = e.TryGetProperty("linkinfo", out var li) ? li : default;
            var data = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("info_data", out var d) ? d : default;
            return new IpLink(Int(e, "ifindex") ?? 0, Str(e, "ifname") ?? "?",
                e.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Array ? flags.EnumerateArray().Select(f => f.GetString() ?? "").ToList() : [],
                Int(e, "mtu"), Str(e, "operstate"), Str(e, "link_type"), Str(e, "address"),
                info.ValueKind == JsonValueKind.Object ? Str(info, "info_kind") : null,
                info.ValueKind == JsonValueKind.Object ? Str(info, "info_slave_kind") : null,
                Str(e, "master"),
                data.ValueKind == JsonValueKind.Object ? Int(data, "id") : null,
                data.ValueKind == JsonValueKind.Object ? Str(data, "mode") : null);
        }).ToList();
    }

    /// <summary><c>ip -j addr show</c>: every address with its prefix, whether DHCP or SLAAC gave it, and how long it is valid.</summary>
    public static List<IpAddress> ParseAddresses(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<IpAddress>();
        foreach (var link in document.RootElement.EnumerateArray())
        {
            var name = Str(link, "ifname") ?? "?";
            if (!link.TryGetProperty("addr_info", out var infos) || infos.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var a in infos.EnumerateArray())
            {
                if (Str(a, "local") is { } local && Str(a, "family") is { } family && Int(a, "prefixlen") is { } prefix)
                {
                    result.Add(new IpAddress(name, family, local, prefix, a.TryGetProperty("dynamic", out var dyn) && dyn.ValueKind == JsonValueKind.True,
                        a.TryGetProperty("valid_life_time", out var v) && v.TryGetInt64(out var seconds) ? seconds : null, Str(a, "scope")));
                }
            }
        }
        return result;
    }

    /// <summary><c>ip -j route show table main</c>: <c>default</c> becomes 0.0.0.0/0 or ::/0, a host route gets its /32 or /128.</summary>
    public static List<IpRoute> ParseRoutes(string json, string family)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(r =>
        {
            var destination = Str(r, "dst") ?? "?";
            destination = destination == "default" ? family == "inet6" ? "::/0" : "0.0.0.0/0"
                : destination.Contains('/') ? destination : destination + (family == "inet6" ? "/128" : "/32");
            return new IpRoute(destination, Str(r, "gateway"), Str(r, "dev"), Int(r, "metric"), Str(r, "protocol"), Str(r, "type"));
        }).ToList();
    }

    /// <summary>A bond's mode from /proc/net/bonding when <c>ip -d</c> didn't say.</summary>
    private static string? BondingMode(string bond) => Tool.ReadFile($"/proc/net/bonding/{bond}")?.Split('\n')
        .FirstOrDefault(l => l.StartsWith("Bonding Mode:", StringComparison.Ordinal))?["Bonding Mode:".Length..].Trim();

    /// <summary>The DHCP server of an interface's lease, best effort: systemd-networkd's, dhclient's, or NetworkManager's lease file.</summary>
    private static string? DhcpServer(IpLink link)
    {
        if (Tool.ReadFile($"/run/systemd/netif/leases/{link.Index}") is { } networkd
            && SystemCollector.KeyValues(networkd).GetValueOrDefault("SERVER_ADDRESS") is { } server)
        {
            return server;
        }
        foreach (var folder in new[] { "/var/lib/dhcp", "/var/lib/dhclient" })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }
            foreach (var file in Directory.GetFiles(folder, "*.leases").Where(f => Path.GetFileName(f).Contains(link.Name, StringComparison.Ordinal)
                         || Path.GetFileName(f) == "dhclient.leases"))
            {
                var identifiers = Regex.Matches(Tool.ReadFile(file) ?? "", @"option dhcp-server-identifier ([0-9.]+);");
                if (identifiers.Count > 0)
                {
                    return identifiers[^1].Groups[1].Value;
                }
            }
        }
        if (Directory.Exists("/var/lib/NetworkManager"))
        {
            foreach (var file in Directory.GetFiles("/var/lib/NetworkManager", $"*-{link.Name}.lease"))
            {
                if (SystemCollector.KeyValues(Tool.ReadFile(file) ?? "").GetValueOrDefault("SERVER_ADDRESS") is { } nm)
                {
                    return nm;
                }
            }
        }
        return null;
    }

    // --- DNS ------------------------------------------------------------------------------------------------------------

    private sealed record DnsServers(List<string> Global, string? Suffix, Dictionary<string, (List<string> Servers, string? Suffix)> PerLink);

    /// <summary>
    /// The DNS servers (§5.3): per link from systemd-resolved (<c>resolvectl dns</c> and <c>domain</c>) where it runs, as on Ubuntu;
    /// otherwise /etc/resolv.conf's (resolved's upstream list instead of its 127.0.0.53 stub), for the adapters with a default route.
    /// </summary>
    private static DnsServers Dns()
    {
        var perLink = new Dictionary<string, (List<string>, string?)>(StringComparer.Ordinal);
        var resolvConf = Tool.ReadFile("/etc/resolv.conf") ?? "";
        var (servers, suffix) = ParseResolvConf(resolvConf);
        if (servers.All(s => s.StartsWith("127.0.0.53", StringComparison.Ordinal)) && Tool.ReadFile("/run/systemd/resolve/resolv.conf") is { } upstream)
        {
            (servers, var upstreamSuffix) = ParseResolvConf(upstream);
            suffix ??= upstreamSuffix;
        }
        if (Directory.Exists("/run/systemd/resolve") && Tool.Exists("resolvectl"))
        {
            try
            {
                var serversByLink = ParseResolvectl(Tool.Text("resolvectl", ["dns"], TimeSpan.FromSeconds(10)));
                var domains = ParseResolvectl(Tool.Text("resolvectl", ["domain"], TimeSpan.FromSeconds(10)));
                foreach (var (link, list) in serversByLink)
                {
                    perLink[link] = (list, domains.GetValueOrDefault(link)?.FirstOrDefault());
                }
            }
            catch (Exception e) when (e is NotAvailableException or InvalidOperationException or TimeoutException)
            {
                // resolved isn't running: resolv.conf's servers stand.
            }
        }
        return new DnsServers(servers, suffix, perLink);
    }

    /// <summary>resolv.conf's <c>nameserver</c> lines and its first <c>search</c> (or <c>domain</c>) suffix.</summary>
    public static (List<string> Servers, string? Suffix) ParseResolvConf(string text)
    {
        var servers = new List<string>();
        string? suffix = null;
        foreach (var line in text.Split('\n').Select(l => l.Trim()))
        {
            var fields = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0] == "nameserver")
            {
                servers.Add(fields[1]);
            }
            else if (fields.Length >= 2 && fields[0] is "search" or "domain" && fields[1] != ".")
            {
                suffix ??= fields[1];
            }
        }
        return (servers.Distinct(StringComparer.Ordinal).ToList(), suffix);
    }

    /// <summary><c>resolvectl dns</c> or <c>domain</c>: <c>Link 2 (eth0): 10.0.0.1 fe80::1</c> per link.</summary>
    public static Dictionary<string, List<string>> ParseResolvectl(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (ResolvectlLink().Match(line.Trim()) is { Success: true } m)
            {
                result[m.Groups[1].Value] = m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(v => v != "~.").ToList();
            }
        }
        return result;
    }

    // --- listeners and connections --------------------------------------------------------------------------------------

    private static List<ListenerInfo> Listeners()
    {
        if (!Tool.Exists("ss"))
        {
            throw new NotAvailableException("ss (iproute2) is not installed");
        }
        var units = new Dictionary<int, List<string>>();
        return ParseSs(Tool.Text("ss", ["-H", "-tulpn"], TimeSpan.FromSeconds(15)))
            .Select(l => l with
            {
                Services = l.Pid is { } pid ? units.TryGetValue(pid, out var known) ? known : units[pid] = UnitsOf(pid) : [],
            })
            .ToList();
    }

    /// <summary>
    /// <c>ss -H -tulpn</c>'s lines: protocol, address (<c>*</c> for any, without brackets and <c>%interface</c>), port, and the first
    /// process with its PID; one entry per protocol, address and port.
    /// </summary>
    public static List<ListenerInfo> ParseSs(string output)
    {
        var listeners = new List<ListenerInfo>();
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || fields[0] is not ("tcp" or "udp"))
            {
                continue;
            }
            var local = fields[4];
            var colon = local.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(local[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                continue;
            }
            var address = local[..colon].Trim('[', ']');
            var percent = address.IndexOf('%');
            address = percent >= 0 ? address[..percent] : address;
            var process = SsProcess().Match(string.Join(' ', fields.Skip(6)));
            listeners.Add(new ListenerInfo(fields[0].ToUpperInvariant(), address, port,
                process.Success ? int.Parse(process.Groups[2].Value, CultureInfo.InvariantCulture) : null,
                process.Success ? process.Groups[1].Value : null, []));
        }
        return listeners.DistinctBy(l => (l.Protocol, l.Address, l.Port)).OrderBy(l => l.Port).ThenBy(l => l.Protocol, StringComparer.Ordinal)
            .ThenBy(l => l.Address, StringComparer.Ordinal).ToList();
    }

    /// <summary>The systemd service a process runs in, from <c>/proc/&lt;pid&gt;/cgroup</c> (<c>0::/system.slice/ssh.service</c>).</summary>
    private static List<string> UnitsOf(int pid) => UnitFromCgroup(Tool.ReadFile($"/proc/{pid}/cgroup")) is { } unit ? [unit] : [];

    public static string? UnitFromCgroup(string? cgroup) => cgroup?.Split('\n')
        .Select(l => l.Split(':', 3)).Where(p => p.Length == 3)
        .Select(p => p[2].Split('/').LastOrDefault(s => s.EndsWith(".service", StringComparison.Ordinal)))
        .FirstOrDefault(u => u is not null);

    /// <summary>TCP connections by state from /proc/net/tcp and tcp6, without the listeners.</summary>
    private static Dictionary<string, int> Connections()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            foreach (var (state, count) in CountTcpStates(Tool.ReadFile(file) ?? ""))
            {
                counts[state] = counts.GetValueOrDefault(state) + count;
            }
        }
        return counts;
    }

    public static Dictionary<string, int> CountTcpStates(string text) => text.Split('\n').Skip(1)
        .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(f => f.Length > 3 && TcpStates.ContainsKey(f[3]))
        .GroupBy(f => TcpStates[f[3]], StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    // --- proxy and hosts ------------------------------------------------------------------------------------------------

    /// <summary>The proxy (§5.3): /etc/environment's <c>http_proxy</c> and <c>https_proxy</c>, and apt's <c>Acquire::http(s)::Proxy</c>.</summary>
    private static string? Proxy()
    {
        var parts = new List<string>();
        foreach (var line in (Tool.ReadFile("/etc/environment") ?? "").Split('\n').Select(l => l.Trim()))
        {
            var pair = line.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().ToLowerInvariant() is "http_proxy" or "https_proxy" && pair[1].Trim('"', '\'', ' ') is { Length: > 0 } value)
            {
                parts.Add($"{pair[0].Trim().ToLowerInvariant()}={value}");
            }
        }
        if (Tool.Exists("apt-config"))
        {
            foreach (var line in Tool.Text("apt-config", ["dump"], TimeSpan.FromSeconds(15)).Split('\n').Select(l => l.Trim()))
            {
                if (Regex.Match(line, @"^Acquire::(https?)::Proxy ""([^""]+)"";$") is { Success: true } m && m.Groups[2].Value is not ("DIRECT" or "false"))
                {
                    parts.Add($"apt {m.Groups[1].Value}: {m.Groups[2].Value}");
                }
            }
        }
        return parts.Count == 0 ? null : string.Join("; ", parts.Distinct(StringComparer.Ordinal));
    }

    /// <summary>/etc/hosts without comments and Debian's defaults (localhost, the IPv6 names, 127.0.1.1 for this host).</summary>
    public static List<string> HostsEntries(string text, string? hostname)
    {
        string[] defaults = ["localhost", "localhost.localdomain", "localhost4", "localhost4.localdomain4", "localhost6", "localhost6.localdomain6",
            "ip6-localhost", "ip6-loopback", "ip6-localnet", "ip6-mcastprefix", "ip6-allnodes", "ip6-allrouters", "ip6-allhosts"];
        return text.Split('\n')
            .Select(l => (l.Contains('#') ? l[..l.IndexOf('#')] : l).Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length >= 2)
            .Where(f => !f.Skip(1).All(n => defaults.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .Where(f => !(f[0] == "127.0.1.1" && hostname is not null && f.Skip(1).Any(n => n.Split('.')[0].Equals(hostname.Split('.')[0], StringComparison.OrdinalIgnoreCase))))
            .Select(f => string.Join(' ', f))
            .Take(500)
            .ToList();
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
