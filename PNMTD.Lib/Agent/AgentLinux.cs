using System.Text.Json.Serialization;

namespace PNMTD.Lib.Agent;

// The Linux agent's own sections (docs/linux-sensor-plan.md §5, §6), shared by the agent, PNMTD and
// the API's typed answers. hardware and network are the Windows records (AgentSections.cs); system,
// services, remote-access and access have the same names and these records on a Linux server; journal
// and ssh-keys are Linux's only. A field the agent could not read is null, and the collector's error
// is in the section's errors; a tool that isn't installed leaves its part null, without an error.

// --- system (§5.2) ------------------------------------------------------------------------------------------

public sealed record LinuxSystemSection(
    string Hostname,
    string? Fqdn,
    string? Realm,
    string? MachineId,
    string? Virtualization,
    IReadOnlyList<ContainerRuntime> Containers,
    ProxmoxInfo? Proxmox,
    LinuxOsInfo Os,
    DateTimeOffset BootTime,
    LinuxShutdownInfo? LastShutdown,
    LinuxUpdatesInfo? Updates,
    LinuxTimeInfo? Time,
    LinuxSecurityInfo? Security,
    IReadOnlyList<LinuxAdmin> Administrators,
    IReadOnlyList<SudoRule> SudoRules,
    IReadOnlyList<LinuxAccount> Accounts,
    IReadOnlyList<SessionInfo> Sessions);

/// <summary>
/// The distribution (os-release), its Debian point release, the kernel, and whether it is a supported version (§4.8): Debian 12
/// and 13, Proxmox VE 8 and 9, Ubuntu LTS 22.04, 24.04 and 26.04. Anything else runs anyway, flagged.
/// </summary>
public sealed record LinuxOsInfo(
    string PrettyName,
    string Id,
    IReadOnlyList<string> IdLike,
    string? VersionId,
    string? Codename,
    string? DebianVersion,
    string KernelRelease,
    string? KernelVersion,
    string Architecture,
    bool Supported,
    string? Distribution,
    string? SystemdVersion);

/// <summary>Proxmox VE (§4.8): its version (pveversion), the cluster's name and nodes (/etc/pve), and this node's name.</summary>
public sealed record ProxmoxInfo(string? Version, string? ClusterName, string? Node, IReadOnlyList<string> Nodes);

/// <summary>A container runtime: Docker or Podman, installed and running.</summary>
public sealed record ContainerRuntime(string Name, bool Installed, bool Running);

/// <summary>The previous boot's end: when, and whether it reached the shutdown target (clean) or not.</summary>
public sealed record LinuxShutdownInfo(DateTimeOffset? At, bool? Clean, string? Boot);

/// <summary>
/// Updates, from the package lists as they are (§5.2): <c>apt-get -s dist-upgrade</c>, never <c>apt update</c>. The security
/// ones by name (at most 200), when the lists were refreshed, unattended-upgrades, and a reboot pending with its reasons.
/// </summary>
public sealed record LinuxUpdatesInfo(
    int Upgradable,
    int Security,
    IReadOnlyList<string> SecurityPackages,
    DateTimeOffset? ListsUpdatedAt,
    bool UnattendedInstalled,
    bool UnattendedEnabled,
    DateTimeOffset? UnattendedLastRun,
    bool RebootPending,
    IReadOnlyList<string> RebootReasons,
    bool? EsmEnabled = null);

/// <summary>The time zone, NTP synchronised or not, the service that keeps the time (systemd-timesyncd, chrony, ntpsec).</summary>
public sealed record LinuxTimeInfo(string TimeZone, bool? NtpEnabled, bool? NtpSynchronized, string? Service);

public sealed record LinuxSecurityInfo(
    LinuxFirewallInfo? Firewall,
    AppArmorInfo? AppArmor,
    bool? SecureBoot,
    IReadOnlyList<EncryptedVolume> Encrypted,
    Fail2banInfo? Fail2ban);

/// <summary>The firewall in use (nftables, iptables, ufw, firewalld, or none), whether it is active, and the input chain's default policy.</summary>
public sealed record LinuxFirewallInfo(string Kind, bool Active, string? InputPolicy, IReadOnlyList<string> Notes);

public sealed record AppArmorInfo(bool Enabled, int Enforcing, int Complaining);

/// <summary>A LUKS volume: the device, where it is mounted (through its mapper), and the kind.</summary>
public sealed record EncryptedVolume(string Device, string? Mount, string Kind);

public sealed record Fail2banInfo(bool Running, IReadOnlyList<Fail2banJail> Jails);

public sealed record Fail2banJail(string Name, int Banned, int TotalBanned);

/// <summary>A local administrator and why: <c>uid 0</c>, <c>group sudo</c> (admin, wheel), or <c>sudoers</c> (a rule that gives ALL).</summary>
public sealed record LinuxAdmin(string Name, string Via);

/// <summary>A sudoers rule that gives ALL: who (a user, or %group), the file and line, and NOPASSWD.</summary>
public sealed record SudoRule(string Who, string File, int Line, bool NoPassword);

/// <summary>
/// A local account (§5.2): its ids, home, shell, groups, and from /etc/shadow only whether a password is set and whether it is
/// locked, never the hash. <see cref="LoginShell"/>: not nologin or false.
/// </summary>
public sealed record LinuxAccount(
    string Name,
    int Uid,
    int Gid,
    string? Home,
    string? Shell,
    bool LoginShell,
    IReadOnlyList<string> Groups,
    bool? PasswordSet,
    bool? Locked,
    bool SystemAccount);

// --- services (§5.4) ----------------------------------------------------------------------------------------

/// <summary>
/// Whose program a service runs (§5.4): a package of the distribution's archive (or a snap by Canonical), another archive's or a
/// .deb installed by hand, no package at all (<c>/usr/local</c>, <c>/opt</c>, a home), or the PNMT agent itself.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LinuxServiceClass>))]
public enum LinuxServiceClass { Distribution, ThirdParty, Unpackaged, Pnmt, Unknown }

public sealed record LinuxServicesSection(
    IReadOnlyList<LinuxServiceEntry> Services,
    IReadOnlyList<TimerEntry> Timers,
    IReadOnlyList<CronEntry> Cron,
    int Packages,
    bool CronCommands);

/// <summary>
/// A systemd service (§5.4): its states, unit file state (enabled, disabled, static, masked, generated), account, program, unit
/// file and where it comes from (<c>package</c>, <c>admin</c>, <c>runtime</c>), the package and its origin, and its class.
/// <see cref="EnabledNotRunning"/>: enabled, not one-shot, not socket- or timer-activated, and not active.
/// </summary>
public sealed record LinuxServiceEntry(
    string Name,
    string? Description,
    string LoadState,
    string ActiveState,
    string SubState,
    string? UnitFileState,
    string? User,
    int? Pid,
    string? Program,
    string? UnitFile,
    string? UnitSource,
    string? Package,
    string? Origin,
    string? Snap,
    LinuxServiceClass Class,
    bool EnabledNotRunning,
    bool Failed,
    string? Type = null);

/// <summary>A systemd timer: the unit it starts, its last and next run.</summary>
public sealed record TimerEntry(string Name, string? Unit, DateTimeOffset? LastRun, DateTimeOffset? NextRun, string? UnitFileState);

/// <summary>
/// A cron entry (§5.4): its file, user, schedule, and the program it runs. The command line only with
/// <c>collect.cron_commands</c> (command lines can hold passwords); <see cref="LineHash"/> for the changes either way.
/// </summary>
public sealed record CronEntry(string File, string User, string Schedule, string Program, string? Command, string LineHash);

// --- remote access (§5.5) -----------------------------------------------------------------------------------

public sealed record LinuxRemoteAccessSection(SshdSettings? Sshd, IReadOnlyList<LinuxRemoteFeature> Features, string Summary, string? FirewallKind);

/// <summary>
/// A way in (§5.5): installed, active, its ports and whether they listen, the firewall's verdict (<c>open</c>, <c>filtered</c> or
/// <c>unknown</c>, which is said rather than guessed for complex rule sets), and notes. <see cref="Highlight"/>: Telnet and the like,
/// Docker's API without TLS, a remote tool.
/// </summary>
public sealed record LinuxRemoteFeature(
    string Key,
    string Name,
    bool Installed,
    bool Active,
    IReadOnlyList<int> Ports,
    bool Listening,
    string Firewall,
    IReadOnlyList<string> Notes,
    bool Highlight = false);

/// <summary>sshd's effective settings (<c>sshd -T</c>), as far as they matter for who gets in.</summary>
public sealed record SshdSettings(
    IReadOnlyList<int> Ports,
    IReadOnlyList<string> ListenAddresses,
    string? PermitRootLogin,
    bool PasswordAuthentication,
    bool KbdInteractiveAuthentication,
    bool PubkeyAuthentication,
    IReadOnlyList<string> AllowUsers,
    IReadOnlyList<string> AllowGroups,
    IReadOnlyList<string> DenyUsers,
    IReadOnlyList<string> DenyGroups,
    bool AllowAgentForwarding,
    string? AllowTcpForwarding,
    bool X11Forwarding,
    int? MaxAuthTries,
    string? LogLevel,
    bool MatchBlocks,
    string? AuthorizedKeysCommand,
    bool TrustedUserCaKeys,
    bool SocketActivated,
    string? Version);

// --- access (§5.6) ------------------------------------------------------------------------------------------

/// <summary>
/// The access section: the sessions now, the logging check with what is missing and the command that fixes it, and the last 24
/// hours in numbers (the internet's scanners counted apart). The logins and elevations themselves are a stream (<see cref="AccessBatch"/>).
/// </summary>
public sealed record LinuxAccessSection(
    IReadOnlyList<SessionInfo> Sessions,
    LoggingCheck Logging,
    AccessCounts? Last24Hours,
    int InvalidUserFailures24Hours,
    int Elevations24Hours,
    IReadOnlyList<string> Gaps);

/// <summary>
/// Whether what §5.6 reads is there (§5.6): a persistent journal, its span and size limit, sshd's LogLevel (VERBOSE logs the keys
/// of failed attempts too), and sudo's logging. <see cref="Fixes"/> are the commands, one per gap; the agent changes nothing.
/// </summary>
public sealed record LoggingCheck(
    bool JournalPersistent,
    DateTimeOffset? OldestEntryAt,
    double? SpanDays,
    long? JournalBytes,
    string? SshdLogLevel,
    bool SudoLogging,
    IReadOnlyList<LoggingFix> Fixes);

public sealed record LoggingFix(string Key, string Problem, string? Fix);

// --- the journal (§5.7) -------------------------------------------------------------------------------------

public sealed record JournalSection(IReadOnlyList<JournalEntry> Entries, bool Persistent);

/// <summary>An entry at warning or above: its time, priority (0 emerg … 4 warning), unit, identifier, PID, boot, and message (at most 2 KB).</summary>
public sealed record JournalEntry(DateTimeOffset At, int Priority, string Level, string? Unit, string? Identifier, int? Pid, string? Boot, string Message, string Cursor);

// --- SSH keys (§6.1) ----------------------------------------------------------------------------------------

/// <summary>
/// Every authorized_keys file sshd uses, for every account (§6.1), with sshd's key settings and the CA keys it trusts. Capped at
/// 1 MiB and 1,000 keys per file and 20,000 keys per server; <see cref="Truncated"/> says when the cap was reached.
/// </summary>
public sealed record SshKeysSection(
    SshdKeySettings Sshd,
    IReadOnlyList<AuthorizedKeysFile> Files,
    IReadOnlyList<CaKey> CaKeys,
    int TotalKeys,
    bool Truncated,
    IReadOnlyList<string> Notes);

/// <summary>sshd's settings for keys: the AuthorizedKeysFile patterns, the command and principals that bring keys from elsewhere, and StrictModes.</summary>
public sealed record SshdKeySettings(
    IReadOnlyList<string> AuthorizedKeysFiles,
    string? AuthorizedKeysCommand,
    string? AuthorizedKeysCommandUser,
    string? TrustedUserCaKeys,
    string? AuthorizedPrincipalsFile,
    bool PubkeyAuthentication,
    string? PermitRootLogin,
    bool StrictModes,
    bool MatchBlocks,
    bool Available);

/// <summary>
/// A key file (§6.1): the account, the path sshd reads, the target of a root-owned symlink that was followed, its owner, mode and
/// hash, whether it is Proxmox VE's cluster-wide file, its problems, its keys, and the numbers of the lines that are not keys
/// (never their text). <see cref="Removable"/>: whether this agent's config.yaml lets PNMT remove keys here, with the reason when not.
/// </summary>
public sealed record AuthorizedKeysFile(
    string Account,
    string Path,
    string? Target,
    bool Exists,
    string? Owner,
    string? Group,
    string? Mode,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Sha256,
    bool ClusterWide,
    IReadOnlyList<string> Problems,
    IReadOnlyList<AuthorizedKey> Keys,
    IReadOnlyList<int> InvalidLines,
    bool Removable,
    string? NotRemovable);

/// <summary>
/// A key in a file (§6.1): its line, type and bits, fingerprint (<c>SHA256:…</c>, as <c>ssh-keygen -l</c> prints it), comment,
/// options, the public key itself (Base64), notes (weak, an expiry passed, root without from=, twice in the file, a CA, a cluster
/// node's), and whether it is protected on this server (config.yaml's ssh_keys.protect, or Proxmox's cluster keys).
/// </summary>
public sealed record AuthorizedKey(
    int Line,
    string Type,
    int? Bits,
    string Fingerprint,
    string? Comment,
    IReadOnlyList<string> Options,
    string Key,
    IReadOnlyList<string> Notes,
    bool Protected,
    string? ProtectedBecause = null);

/// <summary>A CA key sshd trusts (TrustedUserCAKeys): whoever it signs a certificate for gets in.</summary>
public sealed record CaKey(string Source, string Type, int? Bits, string Fingerprint, string? Comment);
