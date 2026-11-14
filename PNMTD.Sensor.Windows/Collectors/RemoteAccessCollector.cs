using System.ServiceProcess;
using Microsoft.Win32;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Native;
using PNMTD.Sensor.Windows.Resilience;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// <c>remote-access</c> (docs/windows-sensor-plan.md §4.6): RDP, WinRM, SMB, WMI, DCOM, RPC, Remote Registry, OpenSSH, Windows
/// Admin Center, Remote Assistance, Quick Assist, Server Manager's remote management, and others such as third-party remote
/// tools, each with its ports, whether they listen, whether the firewall lets them in, and notes. Every 5 minutes.
/// </summary>
internal sealed class RemoteAccessCollector : Collector
{
    public override string Name => SectionNames.RemoteAccess;

    public override TimeSpan Interval => TimeSpan.FromMinutes(5);

    /// <summary>Third-party remote tools by their services' names (§4.6): extended with each release.</summary>
    private static readonly (string Pattern, string Name)[] RemoteTools =
    [
        ("TeamViewer", "TeamViewer"), ("AnyDesk", "AnyDesk"), ("ScreenConnect", "ScreenConnect (ConnectWise Control)"),
        ("Splashtop", "Splashtop"), ("RustDesk", "RustDesk"), ("tvnserver", "TightVNC"), ("uvnc_service", "UltraVNC"),
        ("vncserver", "RealVNC"), ("winvnc", "VNC"), ("LogMeIn", "LogMeIn"), ("GoToAssist", "GoTo Assist"), ("DameWare", "DameWare"),
        ("BeyondTrust", "BeyondTrust Remote Support"), ("bomgar", "BeyondTrust (Bomgar)"), ("Radmin", "Radmin"), ("NetSupport", "NetSupport"),
        ("Kaseya", "Kaseya"), ("NinjaRMM", "NinjaOne"), ("Datto", "Datto RMM"), ("ZohoAssist", "Zoho Assist"), ("chromoting", "Chrome Remote Desktop"),
        ("parsec", "Parsec"), ("Atera", "Atera"),
    ];

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var services = Isolated.Part(errors, "services", () => ServiceController.GetServices()
            .ToDictionary(s => s.ServiceName, s => (s.Status, s.StartType, s.DisplayName), StringComparer.OrdinalIgnoreCase)) ?? [];
        var listening = Isolated.Part(errors, "listeners", () => Win32.Listeners().Where(l => l.Protocol == "TCP").Select(l => l.Port).ToHashSet()) ?? [];
        var rules = Isolated.Part(errors, "firewall", Firewall.InboundAllowRules) ?? [];
        var profiles = Isolated.Part(errors, "firewall profiles", Firewall.Profiles) ?? [];

        bool Running(string name) => services.TryGetValue(name, out var s) && s.Status == ServiceControllerStatus.Running;
        bool Installed(string name) => services.ContainsKey(name);
        (bool Open, List<string> Profiles) Open(params int[] ports)
        {
            var open = rules.Where(r => ports.Any(p => Firewall.Covers(r, "TCP", p))).ToList();
            var names = open.SelectMany(r => Firewall.ProfileNames(r.Profiles).Split(", ")).Where(n => n.Length > 0)
                .SelectMany(n => n == "All" ? new[] { "Domain", "Private", "Public" } : [n])
                .Where(n => profiles.Any(p => p.Profile == n && p.Enabled) || profiles.Count == 0).Distinct().ToList();
            // A profile whose firewall is off lets everything in.
            names.AddRange(profiles.Where(p => !p.Enabled).Select(p => p.Profile).Except(names));
            return (names.Count > 0, names);
        }
        RemoteFeature Feature(string key, string name, bool installed, bool active, int[] ports, params string[] notes)
        {
            var (open, names) = Open(ports);
            return new RemoteFeature(key, name, installed, active, ports, ports.Any(listening.Contains), active && open, names,
                notes.Where(n => !string.IsNullOrEmpty(n)).ToList());
        }

        var features = new List<RemoteFeature>();
        var rdp = Isolated.Part(errors, "RDP", () =>
        {
            using var ts = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
            using var tcp = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
            var allowed = ts?.GetValue("fDenyTSConnections") is int deny && deny == 0;
            var port = tcp?.GetValue("PortNumber") is int p ? p : 3389;
            var nla = tcp?.GetValue("UserAuthentication") is int ua && ua == 1;
            return Feature("rdp", "Remote Desktop (RDP)", Installed("TermService"), allowed && Running("TermService"), [port],
                allowed && !nla ? "NLA off" : "", port != 3389 ? $"port {port}" : "");
        });
        Add(features, rdp);
        Add(features, Isolated.Part(errors, "WinRM", () =>
        {
            var listeners = new List<int>();
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WSMAN\Listener"))
            {
                foreach (var name in key?.GetSubKeyNames() ?? [])
                {
                    listeners.Add(name.Contains("HTTPS", StringComparison.OrdinalIgnoreCase) ? 5986 : 5985);
                }
            }
            if (listeners.Count == 0)
            {
                listeners.Add(5985);
            }
            return Feature("winrm", "WinRM / PowerShell remoting", Installed("WinRM"), Running("WinRM") && listeners.Any(listening.Contains),
                listeners.Distinct().ToArray(), listeners.Contains(5986) ? "HTTPS listener" : "");
        }));
        Add(features, Isolated.Part(errors, "SMB", () =>
        {
            var smb1 = Isolated.Part(errors, "SMB1", () => Wmi.First(Wmi.Smb, "SELECT EnableSMB1Protocol, RequireSecuritySignature FROM MSFT_SmbServerConfiguration"));
            var shares = Isolated.Part(errors, "shares", () => Wmi.Query(Wmi.Cimv2, "SELECT Name FROM Win32_Share").Select(s => s.Str("Name") ?? "").ToList()) ?? [];
            return Feature("smb", "SMB file sharing", Installed("LanmanServer"), Running("LanmanServer") && listening.Contains(445), [445],
                smb1?.Bool("EnableSMB1Protocol") == true ? "SMB1 on" : "", smb1?.Bool("RequireSecuritySignature") == false ? "signing not required" : "",
                shares.Count > 0 ? $"shares: {string.Join(", ", shares.Take(20))}" : "");
        }));
        Add(features, Isolated.Part(errors, "WMI", () =>
        {
            var wmiRules = rules.Where(r => r.Group?.Contains("Windows Management Instrumentation", StringComparison.OrdinalIgnoreCase) == true
                || r.Name.Contains("WMI", StringComparison.OrdinalIgnoreCase)).ToList();
            return new RemoteFeature("wmi", "WMI (remote)", Installed("Winmgmt"), Running("Winmgmt") && wmiRules.Count > 0, [135], listening.Contains(135),
                wmiRules.Count > 0, wmiRules.SelectMany(r => Firewall.ProfileNames(r.Profiles).Split(", ")).Distinct().ToList(), []);
        }));
        Add(features, Isolated.Part(errors, "DCOM", () =>
        {
            using var ole = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Ole");
            var enabled = string.Equals(ole?.GetValue("EnableDCOM") as string, "Y", StringComparison.OrdinalIgnoreCase);
            return Feature("dcom", "DCOM", true, enabled, [135]);
        }));
        Add(features, Isolated.Part(errors, "RPC", () => Feature("rpc", "RPC endpoint mapper", true, listening.Contains(135) && Open(135).Open, [135])));
        Add(features, Isolated.Part(errors, "Remote Registry", () =>
        {
            var installed = services.TryGetValue("RemoteRegistry", out var s);
            var active = installed && (s.Status == ServiceControllerStatus.Running || s.StartType is ServiceStartMode.Automatic);
            return new RemoteFeature("remote-registry", "Remote Registry", installed, active, [], false, false, [],
                installed ? [$"{s.Status}, {s.StartType}"] : []);
        }));
        Add(features, Isolated.Part(errors, "OpenSSH", () =>
        {
            var port = 22;
            var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "sshd_config");
            if (File.Exists(config))
            {
                var line = File.ReadLines(config).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Port ", StringComparison.OrdinalIgnoreCase));
                if (line is not null && int.TryParse(line[5..].Trim(), out var p))
                {
                    port = p;
                }
            }
            return Feature("openssh", "OpenSSH Server", Installed("sshd"), Running("sshd"), [port], port != 22 ? $"port {port}" : "");
        }));
        Add(features, Isolated.Part(errors, "Windows Admin Center", () =>
        {
            var name = new[] { "ServerManagementGateway", "WindowsAdminCenter" }.FirstOrDefault(Installed);
            return Feature("wac", "Windows Admin Center", name is not null, name is not null && Running(name), [443, 6516]);
        }));
        Add(features, Isolated.Part(errors, "Remote Assistance", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Remote Assistance");
            var allowed = key?.GetValue("fAllowToGetHelp") is int a && a == 1;
            var full = key?.GetValue("fAllowFullControl") is int f && f == 1;
            return new RemoteFeature("remote-assistance", "Remote Assistance", key is not null, allowed, [], false, false, [], allowed && full ? ["full control allowed"] : []);
        }));
        Add(features, Isolated.Part(errors, "Quick Assist", () =>
        {
            var installed = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "quickassist.exe"))
                || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"))
                    && Directory.EnumerateDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"), "MicrosoftCorporationII.QuickAssist*").Any();
            return new RemoteFeature("quick-assist", "Quick Assist", installed, installed, [], false, false, [], installed ? ["outbound only, started by a logged-on user"] : []);
        }));
        Add(features, Isolated.Part(errors, "Server Manager", () =>
        {
            var winrm = features.FirstOrDefault(f => f.Key == "winrm");
            return new RemoteFeature("server-manager", "Server Manager remote management", winrm?.Installed ?? false, winrm is { Active: true, FirewallOpen: true },
                winrm?.Ports ?? [], winrm?.Listening ?? false, winrm?.FirewallOpen ?? false, winrm?.FirewallProfiles ?? [], []);
        }));
        foreach (var (service, name, port) in new[] { ("TlntSvr", "Telnet server", 23), ("WMSVC", "IIS management service", 8172), ("FTPSVC", "FTP server", 21), ("SNMP", "SNMP", 161) })
        {
            if (Installed(service))
            {
                Add(features, Isolated.Part(errors, name, () => Feature(service.ToLowerInvariant(), name, true, Running(service), [port])));
            }
        }
        foreach (var (pattern, name) in RemoteTools)
        {
            var matches = services.Where(s => s.Key.Contains(pattern, StringComparison.OrdinalIgnoreCase) || s.Value.DisplayName.Contains(pattern, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 0)
            {
                var running = matches.Any(m => m.Value.Status == ServiceControllerStatus.Running);
                features.Add(new RemoteFeature($"tool:{pattern.ToLowerInvariant()}", name, true, running, [], false, false, [], [$"service {matches[0].Key}"]));
            }
        }
        var active = features.Where(f => f.Active && f.Key is "rdp" or "winrm" or "smb" or "openssh" or "wac" || f.Active && f.Key.StartsWith("tool:", StringComparison.Ordinal))
            .Select(f => f.Key switch { "rdp" => "RDP", "winrm" => "WinRM", "smb" => "SMB", "openssh" => "SSH", "wac" => "WAC", _ => f.Name })
            .ToList();
        return new RemoteAccessSection(features, active.Count == 0 ? "none open" : $"{string.Join(" · ", active)} open");
    }

    private static void Add(List<RemoteFeature> features, RemoteFeature? feature)
    {
        if (feature is not null)
        {
            features.Add(feature);
        }
    }
}
