using System.Security.Cryptography;
using System.Text;
using PNMTD.Lib.Agent;

namespace PNMTD.Cli;

/// <summary>A key of the canned ssh-keys section: its type, the public key (Base64), fingerprint, size and comment.</summary>
public sealed record SimulatedKey(string Type, string Key, string Fingerprint, int? Bits, string Comment);

/// <summary>
/// The simulator's canned Linux data (docs/linux-sensor-plan.md §5, §9.4): the eight sections of a Debian 12 web server (and, for the
/// seed, of a Proxmox VE 9 node), its logins (SSH key logins with their fingerprints, a sudo, a file transfer aggregate, failures
/// against an existing account, the internet's invalid users), and the rights its config.yaml would announce. The dates are fixed,
/// so the same data gives the same hash.
/// </summary>
public static class SimulatedLinux
{
    private static readonly DateTimeOffset Boot = DateTimeOffset.UtcNow.AddDays(-9).AddMinutes(-42);

    /// <summary>Fixed dates: the same data must give the same hash.</summary>
    private static readonly DateTimeOffset Stamp = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>What the hello and the join say (§4.8): os-release, the edition its ID, the build the kernel's release.</summary>
    public static AgentOs Os { get; } = new("Debian GNU/Linux 12 (bookworm)", "debian", "12", "6.1.0-28-amd64", "bookworm", true);

    /// <summary>Proxmox VE 9 on Debian 13, for the seed.</summary>
    public static AgentOs ProxmoxOs { get; } = new("Debian GNU/Linux 13 (trixie)", "proxmox", "13", "6.14.11-4-pve", "trixie", true);

    /// <summary>What a Linux agent announces: the ssh-keys section, and removal and scripts only where config.yaml allows them.</summary>
    public static IReadOnlyList<string> Capabilities(bool keyRemoval, bool scripts) =>
    [
        AgentCapabilities.Collect, AgentCapabilities.Access, AgentCapabilities.DiagPing, AgentCapabilities.DiagDns, AgentCapabilities.Keys,
        .. keyRemoval ? [AgentCapabilities.KeysRemove] : Array.Empty<string>(),
        .. scripts ? [AgentCapabilities.Scripts] : Array.Empty<string>(),
    ];

    /// <summary>
    /// The rights of §4.6, as a config.yaml that allows key removal (for deploy and www, one protected key) and scripts (as root and
    /// deploy) would announce them; without either, the defaults (no file).
    /// </summary>
    public static AgentRightsSummary Rights(bool keyRemoval, bool scripts) => new(
        keyRemoval || scripts ? "config.yaml" : "defaults", null, keyRemoval, keyRemoval ? ["deploy", "www"] : [], keyRemoval ? 1 : 0, [], scripts,
        scripts ? ["root", "deploy"] : [], 0, 60, false, false);

    /// <summary>Changes what the next sections report, for the tests of the differs: a section's data by name and host name.</summary>
    public static Func<string, string, object?>? Override { get; set; }

    public static IReadOnlyDictionary<string, object> All(string hostname, bool proxmox = false, bool keyRemoval = false)
    {
        var sections = new Dictionary<string, object>
        {
            [SectionNames.System] = System(hostname, proxmox),
            [SectionNames.Hardware] = Hardware(proxmox),
            [SectionNames.Network] = Network(proxmox),
            [SectionNames.Services] = Services(proxmox),
            [SectionNames.RemoteAccess] = RemoteAccess(proxmox),
            [SectionNames.Access] = Access(),
            [SectionNames.Journal] = Journal(),
            [SectionNames.SshKeys] = SshKeys(hostname, proxmox, keyRemoval),
        };
        foreach (var name in sections.Keys.ToList())
        {
            if (Override?.Invoke(name, hostname) is { } replaced)
            {
                sections[name] = replaced;
            }
        }
        return sections;
    }

    public static IReadOnlyDictionary<string, string> Hashes(string hostname, bool keyRemoval = false) =>
        All(hostname, keyRemoval: keyRemoval).ToDictionary(p => p.Key, p => Hash(AgentJson.ToElement(p.Value)));

    public static async Task SendAsync(AgentSession session, string hostname, IReadOnlyList<string> names, bool keyRemoval, CancellationToken ct)
    {
        foreach (var (name, data) in All(hostname, keyRemoval: keyRemoval).Where(p => names.Count == 0 || names.Contains(p.Key)))
        {
            var element = AgentJson.ToElement(data);
            await session.SendAsync(AgentMessageTypes.Section, new SectionMessage(name, SectionNames.SchemaVersion, Hash(element), DateTimeOffset.UtcNow, element), ct);
        }
    }

    public static string Hash(System.Text.Json.JsonElement element) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element.GetRawText())), 0, 16).ToLowerInvariant();

    /// <summary>A heartbeat (§5.3): the fullest real file system, a pending reboot (a newer kernel), and the last shutdown, clean.</summary>
    public static Heartbeat Heartbeat(string hostname, int configVersion, string agentVersion, bool keyRemoval = false) => new(DateTimeOffset.UtcNow, Boot,
        3.5 + Random.Shared.NextDouble() * 6, 38.4, "/", 61.2, [new SessionInfo("philipp", "Ssh", "active", "10.0.4.17")], true, Hashes(hostname, keyRemoval),
        configVersion, agentVersion, 0, hostname, $"clean shutdown at {Boot.AddMinutes(-1).ToLocalTime():yyyy-MM-dd HH:mm}");

    // --- system (§5.2) ------------------------------------------------------------------------------------------

    public static LinuxSystemSection System(string hostname, bool proxmox = false) => new(
        hostname, $"{hostname}.corp.example", null, proxmox ? "9f3e1d2c4b5a69788796a5b4c3d2e1f0" : "4b1c9a2e7f3d4e5a9c0b1d2e3f4a5b6c",
        proxmox ? null : "kvm",
        proxmox ? [] : [new ContainerRuntime("docker", true, true), new ContainerRuntime("podman", false, false)],
        proxmox ? new ProxmoxInfo("9.0.10", "zrh-cluster", hostname, [hostname, "pve-02"]) : null,
        proxmox
            ? new LinuxOsInfo("Debian GNU/Linux 13 (trixie)", "debian", [], "13", "trixie", "13.1", "6.14.11-4-pve", "#1 SMP PREEMPT_DYNAMIC PMX 6.14.11-4", "x64", true,
                "Proxmox VE 9.0.10", "257 (257.8-1~deb13u2)")
            : new LinuxOsInfo("Debian GNU/Linux 12 (bookworm)", "debian", [], "12", "bookworm", "12.8", "6.1.0-28-amd64",
                "#1 SMP PREEMPT_DYNAMIC Debian 6.1.119-1 (2024-11-22)", "x64", true, "Debian 12.8", "252 (252.31-1~deb12u1)"),
        Boot,
        new LinuxShutdownInfo(Boot.AddMinutes(-1), true, "e2a4c6f8a0b2c4d6e8f0a2b4c6d8e0f2"),
        proxmox
            ? new LinuxUpdatesInfo(6, 0, [], Stamp.AddHours(-3), false, false, null, false, [])
            : new LinuxUpdatesInfo(14, 3, ["libssl3", "openssl", "linux-image-6.1.0-29-amd64"], Stamp.AddHours(-5), true, true, Stamp.AddHours(-18), true,
                ["linux-image-6.1.0-29-amd64 is newer than the running kernel"]),
        new LinuxTimeInfo("Europe/Zurich", true, true, proxmox ? "chrony" : "systemd-timesyncd"),
        new LinuxSecurityInfo(
            proxmox ? new LinuxFirewallInfo("pve-firewall", true, null, ["Proxmox VE's own rules: the verdict per port is unknown"])
                : new LinuxFirewallInfo("nftables", true, "drop", []),
            new AppArmorInfo(true, proxmox ? 12 : 23, 0),
            proxmox ? true : false,
            proxmox ? [] : [new EncryptedVolume("/dev/vdb1", "/srv/data", "LUKS2")],
            proxmox ? null : new Fail2banInfo(true, [new Fail2banJail("sshd", 3, 412)])),
        proxmox ? [new LinuxAdmin("root", "uid 0")]
            : [new LinuxAdmin("deploy", "group sudo"), new LinuxAdmin("deploy", "sudoers"), new LinuxAdmin("philipp", "group sudo"), new LinuxAdmin("root", "uid 0")],
        proxmox ? [] : [new SudoRule("%sudo", "/etc/sudoers", 50, false), new SudoRule("deploy", "/etc/sudoers.d/deploy", 1, true)],
        proxmox
            ? [new LinuxAccount("root", 0, 0, "/root", "/bin/bash", true, ["root"], true, false, false)]
            :
            [
                new LinuxAccount("root", 0, 0, "/root", "/bin/bash", true, ["root"], false, null, false),
                new LinuxAccount("philipp", 1000, 1000, "/home/philipp", "/bin/bash", true, ["philipp", "adm", "sudo"], true, false, false),
                new LinuxAccount("deploy", 1001, 1001, "/home/deploy", "/bin/bash", true, ["deploy", "sudo", "docker"], false, false, false),
                new LinuxAccount("bkp", 1002, 1002, "/home/bkp", "/bin/sh", true, ["bkp"], false, false, false),
                new LinuxAccount("www-data", 33, 33, "/var/www", "/usr/sbin/nologin", false, ["www-data"], false, null, true),
            ],
        [new SessionInfo("philipp", "Ssh", "active", "10.0.4.17", Stamp.AddMinutes(-20))]);

    // --- hardware and network: the shared records (§5.3) ------------------------------------------------------

    private const long GiB = 1024L * 1024 * 1024;

    public static HardwareSection Hardware(bool proxmox = false) => proxmox
        ? new HardwareSection(
            new MachineInfo("Supermicro", "SYS-1029P-WTR", "S35XXYZ0012", "3.4", Stamp.AddYears(-3), "UEFI"),
            new TpmInfo(true, "2.0", true),
            [new ProcessorInfo("Intel(R) Xeon(R) Silver 4214R CPU @ 2.40GHz", 12, 24, 3500), new ProcessorInfo("Intel(R) Xeon(R) Silver 4214R CPU @ 2.40GHz", 12, 24, 3500)],
            new MemoryInfo(256 * GiB, Enumerable.Range(0, 8).Select(i => new MemoryModule($"P{i / 4 + 1}-DIMM{(char)('A' + i % 4)}1", 32 * GiB, 2933, "Samsung", "M393A4K40DB2-CVF")).ToList()),
            [
                new DiskInfo("nvme0n1", "SAMSUNG MZQL2960HCJR-00A07", 960 * 1_000_000_000L, "NVMe", "nvme", "OK", null, false, "S64FNE0R801234"),
                new DiskInfo("nvme1n1", "SAMSUNG MZQL2960HCJR-00A07", 960 * 1_000_000_000L, "NVMe", "nvme", "OK", null, false, "S64FNE0R801235"),
                new DiskInfo("sda", "ST8000NM017B-2TJ103", 8 * 1_000_000_000_000L, "HDD", "sata", "OK", null, false, "WWZ1A2B3"),
                new DiskInfo("sdb", "ST8000NM017B-2TJ103", 8 * 1_000_000_000_000L, "HDD", "sata", "OK", null, false, "WWZ1A2B4"),
            ],
            [
                new VolumeInfo("/", "rpool/ROOT/pve-1", "zfs", 850 * GiB, 790 * GiB, null),
                new VolumeInfo("/tank", "tank", "zfs", 7200 * GiB, 4100 * GiB, null),
            ],
            [],
            [])
        : new HardwareSection(
            new MachineInfo("QEMU", "Standard PC (Q35 + ICH9, 2009)", null, "4.2025.02-3", Stamp.AddYears(-1), "UEFI"),
            new TpmInfo(false, null, null),
            [new ProcessorInfo("AMD EPYC 7313P 16-Core Processor", 4, 4, null)],
            new MemoryInfo(8 * GiB, []),
            [
                new DiskInfo("vda", "Virtio Block Device", 64 * GiB, "SSD", "virtio", null, null, null),
                new DiskInfo("vdb", "Virtio Block Device", 200 * GiB, "SSD", "virtio", null, null, null),
            ],
            [
                new VolumeInfo("/", null, "ext4", 62 * GiB, 38 * GiB, null),
                new VolumeInfo("/boot/efi", null, "vfat", GiB / 2, GiB / 2 - 6 * 1024 * 1024, null),
                new VolumeInfo("/srv/data", "data", "ext4", 196 * GiB, 77 * GiB, "LUKS"),
            ],
            [],
            []);

    public static NetworkSection Network(bool proxmox = false) => proxmox
        ? new NetworkSection(
            [
                new AdapterInfo("eno1", "Intel Corporation Ethernet Controller X722", "ethernet", "3c:ec:ef:12:34:56", "up", 10_000_000_000, 1500, false, null, null,
                    [], [], [], null, null, null, "i40e", "2.25.7", null, false),
                new AdapterInfo("vmbr0", null, "bridge", "3c:ec:ef:12:34:56", "up", null, 1500, false, null, null, ["10.0.2.5/24"], ["10.0.2.1"],
                    ["10.0.1.10", "10.0.1.11"], "corp.example", null, null, "bridge", null, null, true),
                new AdapterInfo("wg0", null, "wireguard", null, "up", null, 1420, false, null, null, ["10.99.0.5/24"], [], [], null, null, null, "wireguard", null, null, true),
            ],
            [],
            ["vmbr0: eno1, tap100i0, tap101i0"],
            [new RouteInfo("0.0.0.0/0", "10.0.2.1", "vmbr0", 0, true), new RouteInfo("10.99.0.0/24", "0.0.0.0", "wg0", 0, true)],
            [
                new ListenerInfo("tcp", "0.0.0.0", 22, 1022, "sshd", ["ssh.service"]),
                new ListenerInfo("tcp", "::", 8006, 2210, "pveproxy", ["pveproxy.service"]),
                new ListenerInfo("tcp", "::", 3128, 2231, "spiceproxy", ["spiceproxy.service"]),
                new ListenerInfo("udp", "0.0.0.0", 51820, null, null, []),
            ],
            new Dictionary<string, int> { ["ESTAB"] = 61, ["TIME-WAIT"] = 9 },
            null,
            null,
            [])
        : new NetworkSection(
            [
                new AdapterInfo("ens18", "Red Hat, Inc. Virtio network device", "ethernet", "bc:24:11:5e:7a:01", "up", null, 1500, false, null, null,
                    ["10.0.2.21/24", "fe80::be24:11ff:fe5e:7a01/64"], ["10.0.2.1"], ["10.0.1.10", "10.0.1.11"], "corp.example", null, null, "virtio_net", "1.0.0", null, false),
                new AdapterInfo("docker0", null, "bridge", "02:42:6c:1d:9e:01", "down", null, 1500, false, null, null, ["172.17.0.1/16"], [], [], null, null, null,
                    "bridge", null, null, true),
            ],
            [],
            ["docker0"],
            [new RouteInfo("0.0.0.0/0", "10.0.2.1", "ens18", 0, true), new RouteInfo("172.17.0.0/16", "0.0.0.0", "docker0", 0, true)],
            [
                new ListenerInfo("tcp", "0.0.0.0", 22, 612, "sshd", ["ssh.service"]),
                new ListenerInfo("tcp", "0.0.0.0", 80, 1201, "nginx", ["nginx.service"]),
                new ListenerInfo("tcp", "0.0.0.0", 443, 1201, "nginx", ["nginx.service"]),
                new ListenerInfo("tcp", "0.0.0.0", 10050, 833, "zabbix_agent2", ["zabbix-agent2.service"]),
                new ListenerInfo("tcp", "127.0.0.1", 5432, 744, "postgres", ["postgresql@15-main.service"]),
            ],
            new Dictionary<string, int> { ["ESTAB"] = 38, ["TIME-WAIT"] = 12 },
            null,
            null,
            ["10.0.2.30 db-01.corp.example db-01"]);

    // --- services, timers and cron (§5.4) -----------------------------------------------------------------------

    private static LinuxServiceEntry Service(string name, string description, string program, LinuxServiceClass c, string? package, string? origin,
        string? user = null, string state = "running", string? unitFileState = "enabled", int? pid = 1000, string source = "package", bool failed = false) =>
        new(name, description, "loaded", failed ? "failed" : state == "running" ? "active" : "inactive", failed ? "failed" : state, unitFileState, user, pid, program,
            source == "package" ? $"/usr/lib/systemd/system/{name}" : $"/etc/systemd/system/{name}", source, package, origin, null, c,
            unitFileState == "enabled" && (failed || state != "running"), failed, "simple");

    public static LinuxServicesSection Services(bool proxmox = false) => proxmox
        ? new LinuxServicesSection(
            [
                Service("ssh.service", "OpenBSD Secure Shell server", "/usr/sbin/sshd", LinuxServiceClass.Distribution, "openssh-server", "Debian", "root", pid: 1022),
                Service("pveproxy.service", "PVE API Proxy Server", "/usr/bin/pveproxy", LinuxServiceClass.Distribution, "pve-manager", "Proxmox", "www-data", pid: 2210),
                Service("spiceproxy.service", "PVE SPICE Proxy Server", "/usr/bin/spiceproxy", LinuxServiceClass.Distribution, "pve-manager", "Proxmox", "www-data", pid: 2231),
                Service("pve-cluster.service", "The Proxmox VE cluster filesystem", "/usr/bin/pmxcfs", LinuxServiceClass.Distribution, "pve-cluster", "Proxmox", "root", pid: 1503),
                Service("pnmt-agent.service", "PNMT agent", "/usr/local/sbin/pnmt-agent", LinuxServiceClass.Pnmt, null, null, "root", pid: 1803, source: "admin"),
                Service("chrony.service", "chrony, an NTP client/server", "/usr/sbin/chronyd", LinuxServiceClass.Distribution, "chrony", "Debian", "_chrony", pid: 990),
            ],
            [
                new TimerEntry("pve-daily-update.timer", "pve-daily-update.service", Stamp.AddHours(-6), Stamp.AddHours(18), "enabled"),
                new TimerEntry("zfs-scrub-monthly@rpool.timer", "zfs-scrub@rpool.service", Stamp.AddDays(-12), Stamp.AddDays(18), "enabled"),
            ],
            [new CronEntry("/etc/cron.d/vzdump", "root", "0 2 * * 6", "vzdump", null, "a41c2d7e9b0f13c5")],
            841,
            false)
        : new LinuxServicesSection(
            [
                Service("ssh.service", "OpenBSD Secure Shell server", "/usr/sbin/sshd", LinuxServiceClass.Distribution, "openssh-server", "Debian", "root", pid: 612),
                Service("nginx.service", "A high performance web server and a reverse proxy server", "/usr/sbin/nginx", LinuxServiceClass.Distribution, "nginx", "Debian",
                    "root", pid: 1201),
                Service("cron.service", "Regular background program processing daemon", "/usr/sbin/cron", LinuxServiceClass.Distribution, "cron", "Debian", "root", pid: 540),
                Service("unattended-upgrades.service", "Unattended Upgrades Shutdown", "/usr/share/unattended-upgrades/unattended-upgrade-shutdown",
                    LinuxServiceClass.Distribution, "unattended-upgrades", "Debian", "root", pid: 701),
                Service("postgresql@15-main.service", "PostgreSQL Cluster 15-main", "/usr/lib/postgresql/15/bin/postgres", LinuxServiceClass.Distribution,
                    "postgresql-15", "Debian", "postgres", pid: 744),
                Service("docker.service", "Docker Application Container Engine", "/usr/bin/dockerd", LinuxServiceClass.ThirdParty, "docker-ce", "Docker", "root", pid: 802),
                Service("zabbix-agent2.service", "Zabbix Agent 2", "/usr/sbin/zabbix_agent2", LinuxServiceClass.ThirdParty, "zabbix-agent2", "Zabbix", "zabbix", pid: 833),
                Service("pnmt-agent.service", "PNMT agent", "/usr/local/sbin/pnmt-agent", LinuxServiceClass.Pnmt, null, null, "root", pid: 905, source: "admin"),
                Service("sync-daemon.service", "Sync daemon", "/opt/sync/syncd", LinuxServiceClass.Unpackaged, null, null, "deploy", state: "failed", pid: null,
                    source: "admin", failed: true),
            ],
            [
                new TimerEntry("apt-daily.timer", "apt-daily.service", Stamp.AddHours(-7), Stamp.AddHours(5), "enabled"),
                new TimerEntry("apt-daily-upgrade.timer", "apt-daily-upgrade.service", Stamp.AddHours(-2), Stamp.AddHours(22), "enabled"),
                new TimerEntry("certbot.timer", "certbot.service", Stamp.AddHours(-4), Stamp.AddHours(8), "enabled"),
                new TimerEntry("logrotate.timer", "logrotate.service", Stamp.AddHours(-8), Stamp.AddHours(16), "enabled"),
            ],
            [
                new CronEntry("/etc/cron.d/sync", "root", "*/5 * * * *", "/opt/sync/run.sh", null, "5d0b8e1a77c2f4e9"),
                new CronEntry("/var/spool/cron/crontabs/deploy", "deploy", "15 3 * * *", "/home/deploy/bin/cleanup.sh", null, "c3a19f02b6d84e71"),
                new CronEntry("/etc/cron.daily", "root", "@daily", "/etc/cron.daily/logrotate", null, "0e7d2c94a1b35f68"),
            ],
            612,
            false);

    // --- remote access (§5.5) -----------------------------------------------------------------------------------

    public static LinuxRemoteAccessSection RemoteAccess(bool proxmox = false) => proxmox
        ? new LinuxRemoteAccessSection(
            new SshdSettings([22], ["0.0.0.0", "::"], "yes", true, false, true, [], [], [], [], true, "yes", false, 6, "INFO", false, null, false, false,
                "OpenSSH_10.0p2 Debian-7"),
            [
                new LinuxRemoteFeature("openssh", "OpenSSH server", true, true, [22], true, "unknown", ["root: password allowed", "password authentication on"]),
                new LinuxRemoteFeature("proxmox", "Proxmox VE web UI", true, true, [8006], true, "unknown", ["realms: pam, pve", "two-factor: not required"]),
                new LinuxRemoteFeature("spice", "Proxmox VE SPICE proxy", true, true, [3128], true, "unknown", []),
                new LinuxRemoteFeature("wireguard", "WireGuard", true, true, [51820], true, "unknown", ["wg0: 3 peers"]),
            ],
            "SSH (root: password allowed) · Proxmox UI · SPICE · WireGuard",
            "pve-firewall")
        : new LinuxRemoteAccessSection(
            new SshdSettings([22], ["0.0.0.0", "::"], "prohibit-password", false, false, true, [], [], [], [], true, "yes", false, 6, "INFO", false, null, false, false,
                "OpenSSH_9.2p1 Debian-2+deb12u3"),
            [
                new LinuxRemoteFeature("openssh", "OpenSSH server", true, true, [22], true, "open", ["root: key only"]),
                new LinuxRemoteFeature("fail2ban", "fail2ban", true, true, [], false, "unknown", ["jail sshd: 3 banned"]),
                new LinuxRemoteFeature("docker-tcp", "Docker's API over TCP", true, false, [2375, 2376], false, "filtered", []),
                new LinuxRemoteFeature("cockpit", "Cockpit", false, false, [9090], false, "filtered", []),
            ],
            "SSH (root: key only) · fail2ban",
            "nftables");

    // --- access, the journal (§5.6, §5.7) -----------------------------------------------------------------------

    public static LinuxAccessSection Access() => new(
        [new SessionInfo("philipp", "Ssh", "active", "10.0.4.17", Stamp.AddMinutes(-20))],
        new LoggingCheck(true, Stamp.AddDays(-41), 41, 512L * 1024 * 1024, "INFO", true,
            [new LoggingFix("sshd-loglevel", "sshd logs at INFO: the keys of failed attempts are not logged",
                "printf 'LogLevel VERBOSE\\n' > /etc/ssh/sshd_config.d/50-pnmt-logging.conf && systemctl reload ssh")]),
        new AccessCounts(14, 3, 6, 0),
        412,
        9,
        ["sshd logs at INFO: the keys of failed attempts are not logged"]);

    public static JournalSection Journal() => new(
    [
        new JournalEntry(Stamp.AddHours(-2), 3, "err", "nginx.service", "nginx", 1203, "b0f1", "2026/11/01 06:00:12 [error] 1203#1203: *8812 connect() failed "
            + "(111: Connection refused) while connecting to upstream, client: 10.0.4.17, server: web-01.corp.example", "s=b0f1;i=1a2b01"),
        new JournalEntry(Stamp.AddHours(-5), 4, "warning", null, "kernel", null, "b0f1", "EXT4-fs (vdb1): warning: maximal mount count reached, running e2fsck is recommended",
            "s=b0f1;i=1a2a77"),
        new JournalEntry(Stamp.AddHours(-9), 3, "err", "sync-daemon.service", "systemd", 1, "b0f1", "sync-daemon.service: Failed with result 'exit-code'.", "s=b0f1;i=1a2950"),
    ], true);

    // --- SSH keys (§6.1) ----------------------------------------------------------------------------------------

    /// <summary>Philipp's laptop: authorized for root (without from=) and deploy.</summary>
    public static SimulatedKey PhilippLaptop { get; } = Ed25519("philipp-laptop", "philipp@laptop");

    /// <summary>The CI runner's deploy key, limited to its network.</summary>
    public static SimulatedKey CiRunner { get; } = Ed25519("ci-runner", "ci@gitlab-runner");

    /// <summary>The backup server's key, a forced command.</summary>
    public static SimulatedKey Backup { get; } = Ed25519("bkp-01", "root@bkp-01");

    /// <summary>A weak RSA key nobody registered, still in root's file.</summary>
    public static SimulatedKey OldRsa { get; } = Rsa("old-laptop", 1024, "admin@old-laptop");

    /// <summary>Proxmox VE: the other node's root key, which the cluster needs (protected).</summary>
    public static SimulatedKey ClusterNode { get; } = Ed25519("pve-02", "root@pve-02");

    public static SshKeysSection SshKeys(string hostname, bool proxmox = false, bool keyRemoval = false)
    {
        var settings = new SshdKeySettings([".ssh/authorized_keys", ".ssh/authorized_keys2"], null, null, null, null, true, proxmox ? "yes" : "prohibit-password", true,
            false, true);
        AuthorizedKey Key(int line, SimulatedKey key, string account, IReadOnlyList<string> options, bool isProtected = false, string? because = null, string? extra = null)
        {
            var parsed = new SshPublicKey(options, key.Type, key.Key, key.Comment, key.Bits, key.Fingerprint);
            var notes = global::PNMTD.Lib.Agent.SshKeys.Notes(parsed, account, Stamp);
            if (extra is not null)
            {
                notes.Add(extra);
            }
            return new AuthorizedKey(line, key.Type, key.Bits, key.Fingerprint, key.Comment, options, key.Key, notes, isProtected, because);
        }
        AuthorizedKeysFile File(string account, string path, string? target, bool clusterWide, IReadOnlyList<AuthorizedKey> keys, bool removable, string? notRemovable) =>
            new(account, path, target, true, account, account, "0600", 120L * keys.Count + 40, Stamp.AddDays(-30), Hash(AgentJson.ToElement(keys)) + Hash(AgentJson.ToElement(path)),
                clusterWide, [], keys, [], removable, notRemovable);
        var refused = "config.yaml doesn't allow PNMT to remove keys (ssh_keys.remove: false)";
        if (proxmox)
        {
            var root = File("root", "/root/.ssh/authorized_keys", "/etc/pve/priv/authorized_keys", true,
                [
                    Key(1, ClusterNode, "root", [], isProtected: true, because: "a cluster node's key", extra: "a cluster node's"),
                    Key(2, PhilippLaptop, "root", []),
                ], false, keyRemoval ? "config.yaml allows removing keys only for deploy, www" : refused);
            return new SshKeysSection(settings, [root], [], 2, false, ["root's file is the cluster's (/etc/pve/priv/authorized_keys): a removal takes a key from every node"]);
        }
        var rootFile = File("root", "/root/.ssh/authorized_keys", null, false,
            [
                Key(1, PhilippLaptop, "root", []),
                Key(2, Backup, "root", ["from=\"10.0.1.30\"", "command=\"/usr/local/bin/backup-receive\"", "no-pty"]),
                Key(4, OldRsa, "root", []),
            ], false, keyRemoval ? "config.yaml allows removing keys only for deploy, www" : refused);
        var deployFile = File("deploy", "/home/deploy/.ssh/authorized_keys", null, false,
            [
                Key(1, CiRunner, "deploy", ["from=\"10.0.5.0/24\"", "no-port-forwarding", "no-agent-forwarding"]),
                Key(2, PhilippLaptop, "deploy", []),
            ], keyRemoval, keyRemoval ? null : refused);
        return new SshKeysSection(settings, [rootFile, deployFile], [], 5, false, []);
    }

    private static SimulatedKey Ed25519(string seed, string comment)
    {
        var blob = Concat(SshString("ssh-ed25519"u8.ToArray()), SshString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))));
        return new SimulatedKey("ssh-ed25519", Convert.ToBase64String(blob), global::PNMTD.Lib.Agent.SshKeys.Fingerprint(blob), 256, comment);
    }

    private static SimulatedKey Rsa(string seed, int bits, string comment)
    {
        var modulus = new byte[bits / 8];
        for (var i = 0; i < modulus.Length; i += 32)
        {
            SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}/{i}")).AsSpan(0, Math.Min(32, modulus.Length - i)).CopyTo(modulus.AsSpan(i));
        }
        modulus[0] |= 0x80;
        modulus[^1] |= 1;
        var blob = Concat(SshString("ssh-rsa"u8.ToArray()), SshString([0x01, 0x00, 0x01]), SshString([0x00, .. modulus]));
        return new SimulatedKey("ssh-rsa", Convert.ToBase64String(blob), global::PNMTD.Lib.Agent.SshKeys.Fingerprint(blob),
            global::PNMTD.Lib.Agent.SshKeys.Bits("ssh-rsa", blob), comment);
    }

    private static byte[] SshString(byte[] value) => [(byte)(value.Length >> 24), (byte)(value.Length >> 16), (byte)(value.Length >> 8), (byte)value.Length, .. value];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // --- logins (§5.6) ------------------------------------------------------------------------------------------

    /// <summary>
    /// A batch of the journal's logins, new each time (record ids from the clock): root's and deploy's SSH key logins, a sudo, a
    /// file transfer aggregate, wrong passwords for root, and an invalid-user aggregate with the names it tried.
    /// </summary>
    public static AccessBatch Logins(string hostname)
    {
        var id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 4;
        var now = DateTimeOffset.UtcNow;
        const string channel = LinuxLogins.Channel;
        return new AccessBatch(
        [
            new LoginRecord(channel, id, now.AddSeconds(-40), null, 1, LogonOutcome.Success, null, "root", null, true, LogonKind.Ssh, "publickey", "10.0.4.17", 51234,
                null, null, "sshd-51234", null, KeyFingerprint: PhilippLaptop.Fingerprint),
            new LoginRecord(channel, id + 1, now.AddSeconds(-35), null, 1, LogonOutcome.Success, null, "deploy", null, true, LogonKind.Ssh, "publickey", "10.0.5.12", 40022,
                null, null, "sshd-40022", now.AddSeconds(-31), KeyFingerprint: CiRunner.Fingerprint),
            new LoginRecord(channel, id + 2, now.AddSeconds(-30), null, 1, LogonOutcome.Success, null, "philipp", null, true, LogonKind.Sudo, null, null, null, "pts/0",
                true, null, null, TargetUser: "root"),
            new LoginRecord(channel, id + 3, now.AddMinutes(-9), now.AddSeconds(-20), 6, LogonOutcome.Success, null, "bkp", null, false, LogonKind.Sftp, "publickey",
                "10.0.1.30", null, null, null, null, null, KeyFingerprint: Backup.Fingerprint),
            new LoginRecord(channel, id + 4, now.AddMinutes(-3), now.AddSeconds(-10), 4, LogonOutcome.Failure, LinuxLogins.WrongPassword, "root", null, true, LogonKind.Ssh,
                "password", "203.0.113.9", null, null, null, null, null),
            new LoginRecord(channel, id + 5, now.AddMinutes(-8), now.AddSeconds(-5), 37, LogonOutcome.Failure, LinuxLogins.InvalidUser, LinuxLogins.InvalidUser, null, false,
                LogonKind.Ssh, "password", "198.51.100.23", null, null, null, null, null,
                TriedUsers: ["admin", "oracle", "test", "ubuntu", "git", "postgres", "pi", "user", "ftpuser", "guest"]),
        ]);
    }
}
