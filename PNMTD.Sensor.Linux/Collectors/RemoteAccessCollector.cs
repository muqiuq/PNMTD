using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>A unit's states, from <c>list-units</c> and <c>list-unit-files</c> for services and sockets.</summary>
internal sealed record UnitState(string Name, string? Load, string? Active, string? FileState)
{
    public bool IsActive => Active is "active" or "activating" or "reloading";

    public bool Exists => Load is "loaded" || FileState is not (null or "" or "not-found");
}

/// <summary>
/// <c>remote-access</c> (docs/linux-sensor-plan.md §5.5 → <see cref="LinuxRemoteAccessSection"/>): sshd's effective settings (<c>sshd -T</c>, and
/// Ubuntu's <c>ssh.socket</c>), the other ways in of §5.5's table, detected by their units, sockets and listeners (<c>ss -H -tulpn</c>), each with
/// its ports, whether they listen beyond localhost, the firewall's verdict (<see cref="FirewallVerdict"/>), notes, and the overview line. Every
/// 5 minutes.
/// </summary>
internal sealed partial class RemoteAccessCollector : Collector
{
    public override string Name => SectionNames.RemoteAccess;

    public override TimeSpan Interval => TimeSpan.FromMinutes(5);

    /// <summary>The tools whose services count as third-party remote access (§5.5): highlighted, a list extended with each release.</summary>
    private static readonly (string Key, string Name, string[] Units)[] RemoteTools =
    [
        ("teamviewer", "TeamViewer", ["teamviewerd.service"]),
        ("anydesk", "AnyDesk", ["anydesk.service"]),
        ("rustdesk", "RustDesk", ["rustdesk.service"]),
        ("screenconnect", "ConnectWise ScreenConnect", ["connectwisecontrol-*", "screenconnect-*"]),
        ("nomachine", "NoMachine", ["nxserver.service"]),
        ("splashtop", "Splashtop", ["SRStreamer.service", "splashtop-streamer.service"]),
        ("meshcentral", "MeshCentral agent", ["meshagent.service"]),
    ];

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var units = Isolated.Part(errors, "units", Units) ?? new Dictionary<string, UnitState>(StringComparer.Ordinal);
        var listeners = Isolated.Part(errors, "listeners", () => Tool.Exists("ss")
            ? NetworkCollector.ParseSs(Tool.Text("ss", ["-H", "-tulpn"], TimeSpan.FromSeconds(15)))
            : throw new NotAvailableException("ss (iproute2) is not installed")) ?? [];
        var firewall = Isolated.Part(errors, "firewall", FirewallVerdict.Read) ?? FirewallVerdict.Fixed("unknown", PortVerdict.IsUnknown);
        var sshd = Isolated.Part(errors, "sshd -T", () => Settings(units));

        var features = new List<LinuxRemoteFeature>();
        void Add(LinuxRemoteFeature? feature)
        {
            if (feature is not null)
            {
                features.Add(feature);
            }
        }
        Add(Isolated.Part(errors, "OpenSSH", () => Ssh(sshd, units, listeners, firewall)));
        Add(Isolated.Part(errors, "Proxmox VE", () => Simple("proxmox-ui", "Proxmox VE web UI", units, ["pveproxy.service"], [("tcp", 8006)], listeners, firewall,
            ProxmoxNotes())));
        Add(Simple("spice", "Proxmox VE SPICE proxy", units, ["spiceproxy.service"], [("tcp", 3128)], listeners, firewall));
        Add(Simple("cockpit", "Cockpit", units, ["cockpit.socket", "cockpit.service"], [("tcp", 9090)], listeners, firewall));
        Add(Simple("webmin", "Webmin", units, ["webmin.service"], [("tcp", 10000)], listeners, firewall));
        Add(Simple("usermin", "Usermin", units, ["usermin.service"], [("tcp", 20000)], listeners, firewall));
        Add(Simple("xrdp", "xrdp", units, ["xrdp.service"], [("tcp", 3389)], listeners, firewall));
        Add(Isolated.Part(errors, "VNC", () => Vnc(units, listeners, firewall)));
        Add(Isolated.Part(errors, "Telnet, rsh", () => Legacy(units, listeners, firewall)));
        Add(Isolated.Part(errors, "SNMP", () => Simple("snmp", "SNMP", units, ["snmpd.service"], [("udp", 161)], listeners, firewall, SnmpNotes())));
        Add(Isolated.Part(errors, "Samba", () => Simple("samba", "Samba (SMB)", units, ["smbd.service"], [("tcp", 445), ("tcp", 139)], listeners, firewall, SambaNotes())));
        Add(Isolated.Part(errors, "NFS", () => Simple("nfs", "NFS server", units, ["nfs-server.service", "nfs-kernel-server.service"], [("tcp", 2049), ("udp", 2049)],
            listeners, firewall, NfsNotes())));
        Add(Isolated.Part(errors, "Docker", () => Docker(units, listeners, firewall)));
        Add(Isolated.Part(errors, "WireGuard", () => WireGuard(listeners, firewall)));
        Add(Simple("openvpn", "OpenVPN", units, ["openvpn.service", "openvpn@*", "openvpn-server@*", "openvpn-client@*"], [], listeners, firewall, process: "openvpn"));
        Add(Simple("tailscale", "Tailscale", units, ["tailscaled.service"], [], listeners, firewall, process: "tailscaled"));
        Add(Simple("zerotier", "ZeroTier", units, ["zerotier-one.service"], [], listeners, firewall, process: "zerotier-one"));
        Add(Simple("netbird", "NetBird", units, ["netbird.service"], [], listeners, firewall, process: "netbird"));
        foreach (var (key, name, patterns) in RemoteTools)
        {
            Add(Simple(key, name, units, patterns, [], listeners, firewall, highlight: true));
        }
        Add(Isolated.Part(errors, "fail2ban", () => Fail2ban(units)));

        var summary = Summary(features, sshd);
        var settings = sshd?.Settings;
        return new LinuxRemoteAccessSection(settings, features, summary, firewall.Kind);
    }

    // --- units ------------------------------------------------------------------------------------------------------

    /// <summary>Every service and socket with its states: loaded ones from <c>list-units</c>, the rest from <c>list-unit-files</c>.</summary>
    private static Dictionary<string, UnitState> Units()
    {
        var result = new Dictionary<string, UnitState>(StringComparer.Ordinal);
        var listed = Tool.Run("systemctl", ["list-units", "--type=service,socket", "--all", "--output=json", "--no-pager"], TimeSpan.FromSeconds(20));
        var units = listed.Ok && listed.Output.TrimStart().StartsWith('[') ? ServicesCollector.ParseUnitsJson(listed.Output) : null;
        units ??= ServicesCollector.ParseUnitsTable(Tool.Text("systemctl", ["list-units", "--type=service,socket", "--all", "--plain", "--no-legend", "--no-pager"],
            TimeSpan.FromSeconds(20)), ".service", ".socket");
        foreach (var u in units)
        {
            result[u.Name] = new UnitState(u.Name, u.Load, u.Active, null);
        }
        var files = Tool.Run("systemctl", ["list-unit-files", "--type=service,socket", "--output=json", "--no-pager"], TimeSpan.FromSeconds(20));
        if (files.Ok && files.Output.TrimStart().StartsWith('['))
        {
            using var document = JsonDocument.Parse(files.Output);
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.TryGetProperty("unit_file", out var n) && n.GetString() is { } name)
                {
                    var state = e.TryGetProperty("state", out var s) ? s.GetString() : null;
                    result[name] = result.TryGetValue(name, out var known) ? known with { FileState = state } : new UnitState(name, null, null, state);
                }
            }
        }
        return result;
    }

    /// <summary>The units a feature names: exact names, or prefixes with <c>*</c> (<c>openvpn@*</c>, <c>screenconnect-*</c>).</summary>
    private static List<UnitState> Find(IReadOnlyDictionary<string, UnitState> units, IEnumerable<string> patterns) => patterns.SelectMany(p =>
        p.EndsWith('*')
            ? units.Values.Where(u => u.Name.StartsWith(p[..^1], StringComparison.Ordinal) && !u.Name.EndsWith("@.service", StringComparison.Ordinal))
            : units.TryGetValue(p, out var u) ? [u] : []).Distinct().ToList();

    // --- the features -----------------------------------------------------------------------------------------------

    private static bool Loopback(string address) => address.StartsWith("127.", StringComparison.Ordinal) || address == "::1";

    /// <summary>A feature found by its units, ports and, optionally, a process name in the listeners; null when nothing of it is there.</summary>
    private static LinuxRemoteFeature? Simple(string key, string name, IReadOnlyDictionary<string, UnitState> units, IEnumerable<string> patterns,
        IReadOnlyList<(string Protocol, int Port)> ports, IReadOnlyList<ListenerInfo> listeners, FirewallVerdict firewall, IEnumerable<string>? notes = null,
        string? process = null, bool highlight = false)
    {
        var found = Find(units, patterns);
        var byProcess = process is null ? [] : listeners.Where(l => l.Process == process).ToList();
        var installed = found.Any(u => u.Exists) || byProcess.Count > 0;
        if (!installed)
        {
            return null;
        }
        var active = found.Any(u => u.IsActive) || byProcess.Count > 0;
        var wanted = ports.Concat(byProcess.Select(l => (l.Protocol.ToLowerInvariant(), l.Port))).Distinct().ToList();
        return Feature(key, name, true, active, wanted, listeners, firewall, notes?.ToList() ?? [], highlight);
    }

    private static LinuxRemoteFeature Feature(string key, string name, bool installed, bool active, IReadOnlyList<(string Protocol, int Port)> ports,
        IReadOnlyList<ListenerInfo> listeners, FirewallVerdict firewall, List<string> notes, bool highlight)
    {
        var listening = ports.Where(p => listeners.Any(l => l.Port == p.Port && l.Protocol.Equals(p.Protocol, StringComparison.OrdinalIgnoreCase) && !Loopback(l.Address)))
            .ToList();
        var local = ports.Where(p => !listening.Contains(p) && listeners.Any(l => l.Port == p.Port && l.Protocol.Equals(p.Protocol, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (local.Count > 0)
        {
            notes.Add($"Listens on localhost only: {string.Join(", ", local.Select(p => p.Port))}");
        }
        var verdicts = (listening.Count > 0 ? listening : ports).Select(p => (p.Port, Verdict: firewall.For(p.Protocol, p.Port))).ToList();
        var verdict = verdicts.Count == 0 ? PortVerdict.Unknown
            : verdicts.Any(v => v.Verdict.Verdict == PortVerdict.Open) ? PortVerdict.Open
            : verdicts.Any(v => v.Verdict.Verdict == PortVerdict.Unknown) ? PortVerdict.Unknown
            : PortVerdict.Filtered;
        foreach (var (port, v) in verdicts.Where(v => v.Verdict.OnlyFrom.Count > 0))
        {
            notes.Add($"The firewall lets {port} in only from {string.Join(", ", v.OnlyFrom.Take(5))}");
        }
        return new LinuxRemoteFeature(key, name, installed, active, ports.Select(p => p.Port).Distinct().Order().ToList(), listening.Count > 0, verdict, notes, highlight);
    }

    private sealed record SshdView(SshdSettings Settings, Dictionary<string, List<string>> Raw, List<int> SocketPorts);

    /// <summary>sshd's settings (§5.5): <c>sshd -T</c>, the Match blocks from its files, the version, and Ubuntu's ssh.socket.</summary>
    private static SshdView? Settings(IReadOnlyDictionary<string, UnitState> units)
    {
        if (!Sshd.Installed)
        {
            return null;
        }
        var raw = Sshd.Effective();
        var socket = Sshd.Socket();
        var files = Sshd.ConfigFiles();
        var settings = new SshdSettings(
            Sshd.Ports(raw),
            Sshd.Words(raw, "listenaddress"),
            Sshd.One(raw, "permitrootlogin"),
            Sshd.Yes(raw, "passwordauthentication"),
            Sshd.Yes(raw, "kbdinteractiveauthentication") || Sshd.Yes(raw, "challengeresponseauthentication"),
            Sshd.Yes(raw, "pubkeyauthentication", fallback: true),
            Sshd.Words(raw, "allowusers"),
            Sshd.Words(raw, "allowgroups"),
            Sshd.Words(raw, "denyusers"),
            Sshd.Words(raw, "denygroups"),
            Sshd.Yes(raw, "allowagentforwarding"),
            Sshd.One(raw, "allowtcpforwarding"),
            Sshd.Yes(raw, "x11forwarding"),
            int.TryParse(Sshd.One(raw, "maxauthtries"), NumberStyles.None, CultureInfo.InvariantCulture, out var tries) ? tries : null,
            Sshd.One(raw, "loglevel"),
            Sshd.HasMatchBlocks(files),
            Sshd.Path(raw, "authorizedkeyscommand"),
            Sshd.Path(raw, "trustedusercakeys") is not null,
            socket is { } s && (s.Active || s.Enabled),
            Sshd.Version());
        return new SshdView(settings, raw, socket?.Ports ?? []);
    }

    /// <summary>How root may log in: no, key only, forced commands only, or with a password.</summary>
    public static string RootLogin(string? permitRootLogin, bool passwords) => permitRootLogin switch
    {
        "no" => "no",
        "prohibit-password" or "without-password" => "key only",
        "forced-commands-only" => "forced commands only",
        "yes" => passwords ? "password" : "key only",
        _ => "unknown",
    };

    private static LinuxRemoteFeature? Ssh(SshdView? sshd, IReadOnlyDictionary<string, UnitState> units, IReadOnlyList<ListenerInfo> listeners, FirewallVerdict firewall)
    {
        var found = Find(units, ["ssh.service", "sshd.service", "ssh.socket"]);
        if (sshd is null && found.All(u => !u.Exists))
        {
            return null;
        }
        var notes = new List<string>();
        var ports = new List<(string, int)>();
        if (sshd is { } view)
        {
            var s = view.Settings;
            ports.AddRange(s.Ports.Concat(view.SocketPorts).Distinct().Select(p => ("tcp", p)));
            var passwords = s.PasswordAuthentication || s.KbdInteractiveAuthentication;
            notes.Add(RootLogin(s.PermitRootLogin, passwords) switch
            {
                "no" => "Root login off",
                "key only" => "Root login allowed, key only",
                "forced commands only" => "Root login: forced commands only",
                "password" => "Root login allowed with a password",
                _ => $"Root login: {s.PermitRootLogin}",
            });
            if (s.PasswordAuthentication)
            {
                notes.Add("Password authentication on");
            }
            if (s.KbdInteractiveAuthentication)
            {
                notes.Add("Keyboard-interactive on");
            }
            if (s.AllowUsers.Count > 0)
            {
                notes.Add($"AllowUsers: {string.Join(", ", s.AllowUsers.Take(20))}");
            }
            if (s.AllowGroups.Count > 0)
            {
                notes.Add($"AllowGroups: {string.Join(", ", s.AllowGroups.Take(20))}");
            }
            var forwarding = new List<string>();
            if (s.AllowAgentForwarding)
            {
                forwarding.Add("agent");
            }
            if (s.AllowTcpForwarding is { } tcp && tcp != "no")
            {
                forwarding.Add("TCP");
            }
            if (s.X11Forwarding)
            {
                forwarding.Add("X11");
            }
            if (forwarding.Count > 0)
            {
                var list = string.Join(", ", forwarding);
                notes.Add($"{char.ToUpperInvariant(list[0])}{list[1..]} forwarding on");
            }
            if (s.MaxAuthTries is { } max)
            {
                notes.Add($"MaxAuthTries {max}");
            }
            if (s.AuthorizedKeysCommand is { } command)
            {
                notes.Add($"AuthorizedKeysCommand set ({command}): keys from outside the files");
            }
            if (s.TrustedUserCaKeys)
            {
                notes.Add("TrustedUserCAKeys set: certificates its CAs sign get in");
            }
            if (s.MatchBlocks)
            {
                notes.Add("Match blocks: settings differ per user or address");
            }
            if (s.SocketActivated)
            {
                notes.Add("Started on demand by ssh.socket");
            }
            if (s.Version is { } version)
            {
                notes.Add(version);
            }
        }
        else
        {
            ports.Add(("tcp", 22));
        }
        var active = found.Any(u => u.IsActive) || listeners.Any(l => l.Process is "sshd" or "sshd-session");
        return Feature("ssh", "OpenSSH server", true, active, ports, listeners, firewall, notes, false);
    }

    /// <summary>Proxmox VE's realms (<c>/etc/pve/domains.cfg</c>) and the ones that require a second factor, where readable.</summary>
    private static List<string> ProxmoxNotes()
    {
        var notes = new List<string>();
        if (Tool.ReadFile("/etc/pve/domains.cfg") is not { } text)
        {
            return notes;
        }
        var realms = new List<string>();
        var twoFactor = new List<string>();
        string? current = null;
        foreach (var raw in text.Split('\n'))
        {
            if (Regex.Match(raw, @"^(\w+):\s*(\S+)") is { Success: true } realm)
            {
                current = realm.Groups[2].Value;
                realms.Add(realm.Groups[1].Value == realm.Groups[2].Value ? realm.Groups[1].Value.ToUpperInvariant() : $"{realm.Groups[2].Value} ({realm.Groups[1].Value.ToUpperInvariant()})");
            }
            else if (current is not null && raw.TrimStart().StartsWith("tfa ", StringComparison.Ordinal))
            {
                twoFactor.Add(current);
            }
        }
        if (realms.Count > 0)
        {
            notes.Add($"Realms: {string.Join(", ", realms)}");
        }
        if (twoFactor.Count > 0)
        {
            notes.Add($"Two-factor required for {string.Join(", ", twoFactor)}");
        }
        return notes;
    }

    private static LinuxRemoteFeature? Vnc(IReadOnlyDictionary<string, UnitState> units, IReadOnlyList<ListenerInfo> listeners, FirewallVerdict firewall)
    {
        string[] processes = ["Xvnc", "x11vnc", "Xtigervnc", "Xtightvnc", "wayvnc", "vino-server", "Xvnc-core"];
        var found = Find(units, ["x11vnc.service", "vncserver@*", "tigervncserver@*", "tightvncserver.service", "wayvnc.service"]);
        var byPort = listeners.Where(l => l.Protocol == "TCP" && l.Port is >= 5900 and <= 5999 || l.Process is { } p && processes.Contains(p)).ToList();
        if (found.All(u => !u.Exists) && byPort.Count == 0)
        {
            return null;
        }
        return Feature("vnc", "VNC server", true, found.Any(u => u.IsActive) || byPort.Count > 0,
            byPort.Select(l => ("tcp", l.Port)).DefaultIfEmpty(("tcp", 5900)).Distinct().ToList(), listeners, firewall, [], false);
    }

    /// <summary>Telnet, rsh, rlogin, rexec (§5.5): inetd's and xinetd's configuration, their daemons, ports 23 and 512–514. Always highlighted.</summary>
    private static LinuxRemoteFeature? Legacy(IReadOnlyDictionary<string, UnitState> units, IReadOnlyList<ListenerInfo> listeners, FirewallVerdict firewall)
    {
        var services = new List<string>();
        foreach (var line in (Tool.ReadFile("/etc/inetd.conf") ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var name = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
            if (name is "telnet" or "shell" or "login" or "exec")
            {
                services.Add($"{name} in /etc/inetd.conf");
            }
        }
        foreach (var name in new[] { "telnet", "rsh", "rlogin", "rexec" })
        {
            if (Tool.ReadFile($"/etc/xinetd.d/{name}") is { } text && !Regex.IsMatch(text, @"^\s*disable\s*=\s*yes", RegexOptions.Multiline))
            {
                services.Add($"{name} in /etc/xinetd.d");
            }
        }
        var found = Find(units, ["telnet.socket", "telnetd.socket", "inetutils-telnetd.service", "rsh.socket", "rlogin.socket", "rexec.socket"]);
        var ports = listeners.Where(l => l.Protocol == "TCP" && l.Port is 23 or 512 or 513 or 514).ToList();
        if (services.Count == 0 && found.All(u => !u.Exists) && ports.Count == 0)
        {
            return null;
        }
        return Feature("telnet", "Telnet, rsh, rlogin, rexec", true, ports.Count > 0 || found.Any(u => u.IsActive),
            ports.Select(l => ("tcp", l.Port)).DefaultIfEmpty(("tcp", 23)).Distinct().ToList(), listeners, firewall, services, true);
    }

    /// <summary>SNMP's v1/v2c communities: whether there are any, never their names (§5.5).</summary>
    private static List<string> SnmpNotes()
    {
        var text = Tool.ReadFile("/etc/snmp/snmpd.conf") ?? "";
        var communities = text.Split('\n').Select(l => l.Trim()).Count(l => Regex.IsMatch(l, @"^(rocommunity6?|rwcommunity6?|com2sec6?)\s", RegexOptions.IgnoreCase));
        return communities > 0 ? [$"v1/v2c communities present ({communities})"] : [];
    }

    /// <summary>Samba's shares and whether SMB1 is allowed (<c>server min protocol</c> NT1 or older).</summary>
    private static List<string> SambaNotes()
    {
        if (Tool.ReadFile("/etc/samba/smb.conf") is not { } text)
        {
            return [];
        }
        var notes = new List<string>();
        var shares = Regex.Matches(text, @"^\s*\[([^\]]+)\]", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim())
            .Where(s => !s.Equals("global", StringComparison.OrdinalIgnoreCase) && !s.Equals("printers", StringComparison.OrdinalIgnoreCase) && s != "print$").ToList();
        if (shares.Count > 0)
        {
            notes.Add($"{shares.Count} shares: {string.Join(", ", shares.Take(10))}");
        }
        if (Regex.IsMatch(text, @"^\s*(server )?min protocol\s*=\s*(NT1|LANMAN1|LANMAN2|CORE|COREPLUS)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            notes.Add("SMB1 on");
        }
        return notes;
    }

    /// <summary>NFS's exports, and whether one goes to everyone (<c>*</c>).</summary>
    private static List<string> NfsNotes()
    {
        var exports = (Tool.ReadFile("/etc/exports") ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var notes = new List<string>();
        if (exports.Count > 0)
        {
            notes.Add($"{exports.Count} exports");
        }
        if (exports.Any(l => Regex.IsMatch(l, @"\s\*(\(|\s|$)")))
        {
            notes.Add("Exports to * (everyone)");
        }
        return notes;
    }

    /// <summary>Docker's API over TCP (§5.5): <c>-H tcp://</c> in dockerd's command line or <c>hosts</c> in daemon.json. Root-equivalent access.</summary>
    private static LinuxRemoteFeature? Docker(IReadOnlyDictionary<string, UnitState> units, IReadOnlyList<ListenerInfo> listeners, FirewallVerdict firewall)
    {
        if (!units.TryGetValue("docker.service", out var docker) || !docker.Exists)
        {
            return null;
        }
        var exec = Tool.Run("systemctl", ["show", "-p", "ExecStart", "docker.service"], TimeSpan.FromSeconds(10)).Output;
        var daemon = Tool.ReadFile("/etc/docker/daemon.json") ?? "";
        var hosts = Regex.Matches(exec + "\n" + daemon, @"tcp://[^\s"",;\]]+").Select(m => m.Value).Distinct().ToList();
        var byProcess = listeners.Where(l => l.Process == "dockerd").ToList();
        if (hosts.Count == 0 && byProcess.Count == 0)
        {
            return null;
        }
        var tls = exec.Contains("--tlsverify", StringComparison.Ordinal) || Regex.IsMatch(daemon, @"""tlsverify""\s*:\s*true");
        var ports = hosts.Select(h => Regex.Match(h, @":(\d+)$") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : tls ? 2376 : 2375)
            .Concat(byProcess.Select(l => l.Port)).Distinct().Select(p => ("tcp", p)).ToList();
        var notes = new List<string> { "Root-equivalent access" };
        if (!tls)
        {
            notes.Add("Without TLS");
        }
        return Feature("docker-api", "Docker API over TCP", true, docker.IsActive, ports, listeners, firewall, notes, true);
    }

    /// <summary>WireGuard's interfaces (<c>DEVTYPE=wireguard</c>), and their ports from <c>wg show</c> when wireguard-tools are installed.</summary>
    private static LinuxRemoteFeature? WireGuard(IReadOnlyList<ListenerInfo> listeners, FirewallVerdict firewall)
    {
        if (!Directory.Exists("/sys/class/net"))
        {
            return null;
        }
        var interfaces = Directory.GetDirectories("/sys/class/net")
            .Where(d => (Tool.ReadFile(Path.Combine(d, "uevent")) ?? "").Contains("DEVTYPE=wireguard", StringComparison.Ordinal))
            .Select(Path.GetFileName).Select(n => n!).Order(StringComparer.Ordinal).ToList();
        if (interfaces.Count == 0)
        {
            return null;
        }
        var ports = new List<(string, int)>();
        if (Tool.Exists("wg"))
        {
            foreach (var line in Tool.Run("wg", ["show", "all", "listen-port"], TimeSpan.FromSeconds(10)).Output.Split('\n'))
            {
                var f = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (f.Length == 2 && int.TryParse(f[1], out var port) && port > 0)
                {
                    ports.Add(("udp", port));
                }
            }
        }
        var up = interfaces.Any(i => Tool.ReadValue($"/sys/class/net/{i}/operstate") is "up" or "unknown");
        return Feature("wireguard", "WireGuard", true, up, ports, listeners, firewall, [$"Interfaces: {string.Join(", ", interfaces)}"], false);
    }

    /// <summary>fail2ban (§5.5): a mitigation, its jails and how many are banned.</summary>
    private static LinuxRemoteFeature? Fail2ban(IReadOnlyDictionary<string, UnitState> units)
    {
        if (!units.TryGetValue("fail2ban.service", out var unit) || !unit.Exists)
        {
            return null;
        }
        var info = SystemCollector.Fail2ban();
        var notes = new List<string> { "A mitigation: bans sources after failed logins" };
        if (info is { Jails.Count: > 0 })
        {
            notes.Add($"Jails: {string.Join(", ", info.Jails.Select(j => $"{j.Name} ({j.Banned} banned)"))}");
        }
        return new LinuxRemoteFeature("fail2ban", "fail2ban", true, unit.IsActive || info?.Running == true, [], false, PortVerdict.Unknown, notes);
    }

    /// <summary>The overview (§5.5): "SSH (root: key only) · Proxmox UI · WireGuard", the active ways in.</summary>
    private static string Summary(IReadOnlyList<LinuxRemoteFeature> features, SshdView? sshd)
    {
        var parts = new List<string>();
        foreach (var f in features.Where(f => f.Active && f.Key != "fail2ban"))
        {
            parts.Add(f.Key switch
            {
                "ssh" when sshd is { } s => $"SSH (root: {RootLogin(s.Settings.PermitRootLogin, s.Settings.PasswordAuthentication || s.Settings.KbdInteractiveAuthentication)})",
                "ssh" => "SSH",
                "proxmox-ui" => "Proxmox UI",
                "spice" => "SPICE proxy",
                _ => f.Name,
            });
        }
        return parts.Count == 0 ? "No remote access found" : string.Join(" · ", parts);
    }
}
