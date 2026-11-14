using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>
/// <c>system</c> (docs/linux-sensor-plan.md §5.2 → <see cref="LinuxSystemSection"/>): identity, OS and the version check, uptime and
/// the last shutdown, updates (<c>apt-get -s dist-upgrade</c>, a simulation, never <c>apt update</c>), time, security (firewall,
/// AppArmor, Secure Boot, LUKS, fail2ban), local administrators, sudo's rules, accounts, and the sessions now. Every 15 minutes; each
/// part on its own, a missing tool <i>not available</i>.
/// </summary>
internal sealed partial class SystemCollector : Collector
{
    public override string Name => SectionNames.System;

    public override TimeSpan Interval => TimeSpan.FromMinutes(15);

    /// <summary>apt-get's simulation takes seconds on a large system, pveversion one.</summary>
    public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string> { "sessions", "listsUpdatedAt", "unattendedLastRun", "banned", "totalBanned" };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    [GeneratedRegex(@"^Inst (\S+) (?:\[[^\]]*\] )?\((\S+) (.*?)(?: \[[^\]]*\])?\)")]
    private static partial Regex AptInst();

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var hostname = Tool.ReadValue("/proc/sys/kernel/hostname") ?? Environment.MachineName;
        var os = Isolated.Part(errors, "os-release", LinuxOs.Read)
            ?? new LinuxOsInfo("Linux", "linux", [], null, null, null, Environment.OSVersion.VersionString, null, AgentInfo.Architecture, false, null, null);
        var boot = Isolated.Part(errors, "boot time", () => (DateTimeOffset?)LinuxFacts.BootTime()) ?? DateTimeOffset.UtcNow;

        var users = Isolated.Part(errors, "/etc/passwd", () => Accounts.ParsePasswd(File.ReadAllText("/etc/passwd"))) ?? [];
        var groups = Isolated.Part(errors, "/etc/group", () => Accounts.ParseGroup(File.ReadAllText("/etc/group"))) ?? [];
        var shadow = Isolated.Part(errors, "/etc/shadow", () => Accounts.ParseShadow(File.ReadAllText("/etc/shadow"), DateOnly.FromDateTime(DateTime.UtcNow)));
        var rules = Isolated.Part(errors, "sudoers", Accounts.ReadSudoRules) ?? [];

        return new LinuxSystemSection(
            hostname,
            Isolated.Part(errors, "fqdn", () => LinuxPlatform.Fqdn(hostname)),
            Isolated.Part(errors, "realm", LinuxPlatform.Realm),
            Tool.ReadValue("/etc/machine-id"),
            Isolated.Part(errors, "virtualisation", Virtualization),
            Isolated.Part(errors, "containers", Containers) ?? [],
            Isolated.Part(errors, "Proxmox VE", () => Proxmox(hostname)),
            os,
            boot,
            Isolated.Part(errors, "last shutdown", LinuxFacts.LastShutdown),
            Isolated.Part(errors, "updates", () => Updates(os, errors)),
            Isolated.Part(errors, "time", Time),
            Isolated.Part(errors, "security", () => Security(errors)),
            Accounts.Administrators(users, groups, rules),
            rules,
            Accounts.List(users, groups, shadow, Accounts.UidMin(Tool.ReadFile("/etc/login.defs"))),
            Isolated.Part(errors, "sessions", () => LinuxFacts.Sessions(boot)) ?? []);
    }

    // --- identity ---------------------------------------------------------------------------------------------------

    /// <summary><c>systemd-detect-virt</c>: kvm, vmware, microsoft, podman, lxc, … or none (its exit code 1).</summary>
    private static string? Virtualization()
    {
        var result = Tool.Run("systemd-detect-virt", [], TimeSpan.FromSeconds(10));
        return result.Output.Trim() is { Length: > 0 } kind ? kind : null;
    }

    /// <summary>Docker and Podman: installed (the program), running (its unit or socket active).</summary>
    private static List<ContainerRuntime> Containers()
    {
        var docker = Tool.Exists("docker") || Tool.Exists("dockerd");
        var podman = Tool.Exists("podman");
        if (!docker && !podman)
        {
            return [];
        }
        var states = Tool.Run("systemctl", ["is-active", "docker.service", "podman.service", "podman.socket"], TimeSpan.FromSeconds(10)).Output
            .Split('\n').Select(l => l.Trim()).ToList();
        string State(int i) => i < states.Count ? states[i] : "";
        var result = new List<ContainerRuntime>();
        if (docker)
        {
            result.Add(new ContainerRuntime("Docker", true, State(0) == "active"));
        }
        if (podman)
        {
            result.Add(new ContainerRuntime("Podman", true, State(1) == "active" || State(2) == "active"));
        }
        return result;
    }

    /// <summary>Proxmox VE (§4.8): its version, the cluster's name (corosync.conf) and nodes (/etc/pve/nodes), and this node.</summary>
    private static ProxmoxInfo? Proxmox(string hostname)
    {
        if (!LinuxOs.IsProxmox())
        {
            return null;
        }
        var cluster = Tool.ReadFile("/etc/pve/corosync.conf")?.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("cluster_name:", StringComparison.Ordinal))?["cluster_name:".Length..].Trim();
        var nodes = Directory.Exists("/etc/pve/nodes") ? Directory.GetDirectories("/etc/pve/nodes").Select(Path.GetFileName).Select(n => n!).Order().ToList() : [];
        return new ProxmoxInfo(LinuxOs.ProxmoxVersion(), cluster, hostname.Split('.')[0], nodes);
    }

    // --- updates ----------------------------------------------------------------------------------------------------

    private static LinuxUpdatesInfo? Updates(LinuxOsInfo os, List<string> errors)
    {
        if (!Tool.Exists("apt-get"))
        {
            throw new NotAvailableException("apt-get is not installed");
        }
        var simulation = Tool.Text("apt-get", ["-s", "-q", "-o", "Debug::NoLocking=1", "dist-upgrade"], TimeSpan.FromSeconds(90));
        var (upgradable, security) = ParseAptSimulation(simulation);
        var reasons = Isolated.Part(errors, "reboot pending", LinuxFacts.RebootReasons) ?? [];
        if (Isolated.Part(errors, "needrestart", NeedrestartKernel) is { } kernel)
        {
            reasons.Add(kernel);
        }
        var periodic = Isolated.Part(errors, "apt-config", AptPeriodic) ?? [];
        return new LinuxUpdatesInfo(upgradable, security.Count, security.Take(200).ToList(), ListsUpdatedAt(),
            File.Exists("/usr/bin/unattended-upgrade"), periodic.GetValueOrDefault("APT::Periodic::Unattended-Upgrade") is { } u && u != "0",
            LastWrite("/var/log/unattended-upgrades/unattended-upgrades.log") ?? LastWrite("/var/lib/apt/periodic/unattended-upgrades-stamp"),
            reasons.Count > 0, reasons, os.Id == "ubuntu" ? Esm() : null);
    }

    /// <summary>
    /// <c>apt-get -s dist-upgrade</c>'s <c>Inst</c> lines: every package that would be installed or upgraded, and those from a
    /// security archive by name (<c>Debian-Security:12/stable-security</c>, <c>Ubuntu:24.04/noble-security</c>).
    /// </summary>
    public static (int Upgradable, List<string> Security) ParseAptSimulation(string output)
    {
        var upgradable = 0;
        var security = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            if (AptInst().Match(line.TrimEnd()) is not { Success: true } match)
            {
                continue;
            }
            upgradable++;
            var origins = match.Groups[3].Value;
            if (origins.Contains("-security", StringComparison.OrdinalIgnoreCase) || origins.Contains("Security:", StringComparison.OrdinalIgnoreCase))
            {
                security.Add(match.Groups[1].Value);
            }
        }
        return (upgradable, security.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>needrestart's verdict on the kernel (<c>NEEDRESTART-KSTA: 3</c>: a newer one is installed), when it is installed.</summary>
    private static string? NeedrestartKernel()
    {
        if (!Tool.Exists("needrestart"))
        {
            return null;
        }
        var output = Tool.Run("needrestart", ["-b", "-k"], TimeSpan.FromSeconds(60)).Output;
        var status = output.Split('\n').FirstOrDefault(l => l.StartsWith("NEEDRESTART-KSTA:", StringComparison.Ordinal))?["NEEDRESTART-KSTA:".Length..].Trim();
        return status is "2" or "3" ? "needrestart: the running kernel is outdated" : null;
    }

    /// <summary>apt's settings, <c>APT::Periodic::…</c> among them (<c>apt-config dump</c>: <c>Name "value";</c>).</summary>
    private static Dictionary<string, string> AptPeriodic() => Tool.Text("apt-config", ["dump"], TimeSpan.FromSeconds(15)).Split('\n')
        .Select(l => l.Trim()).Where(l => l.EndsWith("\";", StringComparison.Ordinal) && l.Contains(" \"", StringComparison.Ordinal))
        .Select(l => (Name: l[..l.IndexOf(' ')], Value: l[(l.IndexOf(" \"", StringComparison.Ordinal) + 2)..^2]))
        .GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

    /// <summary>When the package lists were last refreshed: the newest Release file in /var/lib/apt/lists.</summary>
    private static DateTimeOffset? ListsUpdatedAt()
    {
        const string lists = "/var/lib/apt/lists";
        if (!Directory.Exists(lists))
        {
            return null;
        }
        var newest = Directory.GetFiles(lists).Where(f => f.EndsWith("Release", StringComparison.Ordinal))
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        return newest == default ? null : new DateTimeOffset(newest, TimeSpan.Zero);
    }

    /// <summary>Ubuntu Pro's ESM: its sources are there.</summary>
    private static bool? Esm() =>
        Directory.Exists("/etc/apt/sources.list.d") && Directory.GetFiles("/etc/apt/sources.list.d", "ubuntu-esm-*").Length > 0 ? true
        : Tool.Exists("pro") ? false : null;

    private static DateTimeOffset? LastWrite(string path) => File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;

    // --- time -------------------------------------------------------------------------------------------------------

    /// <summary><c>timedatectl show</c>: the zone, NTP on and synchronised; the service that keeps the time; the zone from /etc without a bus.</summary>
    private static LinuxTimeInfo Time()
    {
        string? zone = null;
        bool? ntp = null, synchronized = null;
        try
        {
            var values = KeyValues(Tool.Text("timedatectl", ["show"], TimeSpan.FromSeconds(10)));
            zone = values.GetValueOrDefault("Timezone");
            ntp = YesNo(values.GetValueOrDefault("NTP"));
            synchronized = YesNo(values.GetValueOrDefault("NTPSynchronized"));
        }
        catch (NotAvailableException)
        {
        }
        zone ??= Tool.ReadValue("/etc/timezone") ?? LocaltimeZone() ?? TimeZoneInfo.Local.Id;
        string[] services = ["systemd-timesyncd", "chrony", "chronyd", "ntpsec", "ntp", "openntpd"];
        string? service = null;
        try
        {
            var states = Tool.Run("systemctl", ["is-active", .. services.Select(s => s + ".service")], TimeSpan.FromSeconds(10)).Output.Split('\n');
            service = services.Where((_, i) => i < states.Length && states[i].Trim() == "active").FirstOrDefault();
        }
        catch (NotAvailableException)
        {
        }
        return new LinuxTimeInfo(zone, ntp, synchronized, service);
    }

    private static string? LocaltimeZone()
    {
        var target = new FileInfo("/etc/localtime").LinkTarget;
        var marker = target?.IndexOf("zoneinfo/", StringComparison.Ordinal) ?? -1;
        return marker >= 0 ? target![(marker + "zoneinfo/".Length)..] : null;
    }

    // --- security ---------------------------------------------------------------------------------------------------

    private static LinuxSecurityInfo Security(List<string> errors) => new(
        Isolated.Part(errors, "firewall", Firewall),
        Isolated.Part(errors, "AppArmor", AppArmor),
        Isolated.Part(errors, "Secure Boot", SecureBoot),
        Isolated.Part(errors, "LUKS", () => BlockDevices.Encrypted(BlockDevices.Read())) ?? [],
        Isolated.Part(errors, "fail2ban", Fail2ban));

    /// <summary>
    /// The firewall in use (§5.2): ufw or firewalld when active; else nftables when a chain hooks input; else iptables (legacy) with rules;
    /// else none. The input chain's default policy, and notes.
    /// </summary>
    private static LinuxFirewallInfo Firewall()
    {
        var notes = new List<string>();
        if (Tool.Exists("ufw"))
        {
            var status = Tool.Run("ufw", ["status", "verbose"], TimeSpan.FromSeconds(15));
            if (status.Ok && status.Output.Contains("Status: active", StringComparison.Ordinal))
            {
                var policy = Regex.Match(status.Output, @"Default:\s*(\w+)\s*\(incoming\)") is { Success: true } m ? m.Groups[1].Value : null;
                return new LinuxFirewallInfo("ufw", true, policy, notes);
            }
            notes.Add("ufw is installed and inactive");
        }
        if (Tool.Exists("firewall-cmd") && Tool.Run("systemctl", ["is-active", "firewalld.service"], TimeSpan.FromSeconds(10)).Output.Trim() == "active")
        {
            return new LinuxFirewallInfo("firewalld", true, null, notes);
        }
        if (Tool.Exists("nft"))
        {
            var ruleset = Tool.Run("nft", ["-j", "list", "ruleset"], TimeSpan.FromSeconds(15));
            if (!ruleset.Ok && ruleset.Errors.Contains("Operation not permitted", StringComparison.Ordinal))
            {
                throw new NotAvailableException("nft: not permitted here (a container without CAP_NET_ADMIN)");
            }
            if (ruleset.Ok && ParseNftInput(ruleset.Output) is { } nft)
            {
                notes.AddRange(nft.Tables.Select(t => $"nftables table {t}"));
                if (nft.Hooked)
                {
                    return new LinuxFirewallInfo("nftables", true, nft.Policy, notes);
                }
            }
        }
        if (Tool.Exists("iptables-legacy") || (!Tool.Exists("nft") && Tool.Exists("iptables")))
        {
            var rules = Tool.Run(Tool.Exists("iptables-legacy") ? "iptables-legacy" : "iptables", ["-S", "INPUT"], TimeSpan.FromSeconds(15));
            if (rules.Ok)
            {
                var lines = rules.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var policy = lines.FirstOrDefault(l => l.StartsWith("-P INPUT ", StringComparison.Ordinal))?["-P INPUT ".Length..];
                if (lines.Any(l => l.StartsWith("-A INPUT", StringComparison.Ordinal)) || policy is "DROP" or "REJECT")
                {
                    return new LinuxFirewallInfo("iptables", true, policy?.ToLowerInvariant(), notes);
                }
            }
        }
        return new LinuxFirewallInfo("none", false, null, notes);
    }

    /// <summary><c>nft -j list ruleset</c>: the tables, whether a base chain hooks input, and its policy (drop wins over accept).</summary>
    public static (bool Hooked, string? Policy, List<string> Tables)? ParseNftInput(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("nftables", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var tables = new List<string>();
            string? policy = null;
            var hooked = false;
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("table", out var table))
                {
                    tables.Add($"{Str(table, "family")} {Str(table, "name")}");
                }
                if (item.TryGetProperty("chain", out var chain) && Str(chain, "hook") == "input")
                {
                    hooked = true;
                    var p = Str(chain, "policy");
                    policy = policy == "drop" ? policy : p ?? policy;
                }
            }
            return (hooked, policy, tables);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>AppArmor (§5.2): on or off, and the profiles enforcing and complaining (securityfs, readable by root).</summary>
    private static AppArmorInfo? AppArmor()
    {
        var enabled = Tool.ReadValue("/sys/module/apparmor/parameters/enabled");
        if (enabled is null)
        {
            return null;
        }
        var profiles = Tool.ReadFile("/sys/kernel/security/apparmor/profiles")?.Split('\n') ?? [];
        return new AppArmorInfo(enabled == "Y", profiles.Count(p => p.EndsWith("(enforce)", StringComparison.Ordinal)),
            profiles.Count(p => p.EndsWith("(complain)", StringComparison.Ordinal)));
    }

    /// <summary>Secure Boot: UEFI's SecureBoot variable (its fifth byte); false on a BIOS machine; null when efivarfs can't be read.</summary>
    private static bool? SecureBoot()
    {
        if (!Directory.Exists("/sys/firmware/efi"))
        {
            return false;
        }
        const string variable = "/sys/firmware/efi/efivars/SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c";
        if (!File.Exists(variable))
        {
            return null;
        }
        var bytes = File.ReadAllBytes(variable);
        return bytes.Length >= 5 ? bytes[4] == 1 : null;
    }

    /// <summary>fail2ban (§5.2): running, its jails, and the addresses banned now and in all.</summary>
    internal static Fail2banInfo? Fail2ban()
    {
        if (!Tool.Exists("fail2ban-client"))
        {
            return null;
        }
        if (!Tool.Run("fail2ban-client", ["ping"], TimeSpan.FromSeconds(10)).Ok)
        {
            return new Fail2banInfo(false, []);
        }
        var status = Tool.Text("fail2ban-client", ["status"], TimeSpan.FromSeconds(10));
        var jails = Regex.Match(status, @"Jail list:\s*(.*)") is { Success: true } m
            ? m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new Fail2banInfo(true, jails.Take(50).Select(jail =>
        {
            var text = Tool.Run("fail2ban-client", ["status", jail], TimeSpan.FromSeconds(10)).Output;
            int Count(string label) => Regex.Match(text, label + @":\s*(\d+)") is { Success: true } c ? int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            return new Fail2banJail(jail, Count("Currently banned"), Count("Total banned"));
        }).ToList());
    }

    // --- helpers ----------------------------------------------------------------------------------------------------

    /// <summary><c>Key=value</c> lines (systemd's <c>show</c>).</summary>
    public static Dictionary<string, string> KeyValues(string text) => text.Split('\n')
        .Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
        .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.Ordinal);

    private static bool? YesNo(string? value) => value switch { "yes" => true, "no" => false, _ => null };

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
