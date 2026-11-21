namespace PNMTD.Lib.Agent;

// The sections an agent reports (docs/windows-sensor-plan.md §4; Linux: docs/linux-sensor-plan.md §5
// and AgentLinux.cs), shared by the agents, PNMTD and the API's typed answers. A field the agent could
// not read is null, and the collector's error is in the section's errors. Lists are capped on PNMTD's
// side when they are stored. hardware and network are the same records on both platforms; system,
// services, remote-access and access are named the same and typed by the agent's platform.

/// <summary>The sections' names and the schema version they are sent with.</summary>
public static class SectionNames
{
    public const string System = "system";
    public const string Hardware = "hardware";
    public const string Network = "network";
    public const string Services = "services";
    public const string RemoteAccess = "remote-access";
    public const string Access = "access";
    public const string EventLog = "event-log";
    public const string Scripts = "scripts";
    public const string Wmi = "wmi";

    /// <summary>Linux: the newest journal entries at warning or above (docs/linux-sensor-plan.md §5.7).</summary>
    public const string Journal = "journal";

    /// <summary>Linux: sshd's key settings, every authorized_keys file and its keys (docs/linux-sensor-plan.md §6.1).</summary>
    public const string SshKeys = "ssh-keys";

    /// <summary>Linux: the pending packages and what an upgrade would do (docs/linux-os-updates-plan.md §4.1).</summary>
    public const string OsUpdates = "os-updates";

    public const int SchemaVersion = 1;

    /// <summary>What a Windows agent sends.</summary>
    public static readonly IReadOnlyList<string> Windows = [System, Hardware, Network, Services, RemoteAccess, Access, EventLog, Scripts, Wmi];

    /// <summary>What a Linux agent sends.</summary>
    public static readonly IReadOnlyList<string> Linux = [System, Hardware, Network, Services, RemoteAccess, Access, Journal, SshKeys, OsUpdates];

    public static readonly IReadOnlyList<string> All = [.. Windows, Journal, SshKeys, OsUpdates];

    public static bool IsKnown(string? name) => name is not null && All.Contains(name);

    public static IReadOnlyList<string> For(AgentPlatform platform) => platform == AgentPlatform.Linux ? Linux : Windows;

    public static bool IsKnown(AgentPlatform platform, string? name) => name is not null && For(platform).Contains(name);
}

// --- system (§4.2) -----------------------------------------------------------------------------------------

public sealed record SystemSection(
    string Hostname,
    string? Fqdn,
    string? Domain,
    bool PartOfDomain,
    string? DomainRole,
    string? MachineGuid,
    OsInfo Os,
    DateTimeOffset BootTime,
    ShutdownInfo? LastShutdown,
    UpdatesInfo? Updates,
    TimeInfo? Time,
    SecurityInfo? Security,
    IReadOnlyList<LocalAdmin> LocalAdministrators,
    IReadOnlyList<SessionInfo> Sessions,
    HypervisorInfo? Hypervisor);

public sealed record OsInfo(string Caption, string? Edition, string? Version, string Build, int? Ubr, string? DisplayVersion, DateTimeOffset? InstallDate,
    string? Architecture, bool? Activated, bool Supported, string? ProductType, ActivationInfo? Activation = null);

/// <summary>
/// Windows' activation, as the Software Licensing service reports it: the status (Licensed, a grace period, Notification, …),
/// the installed key's channel (Retail, OEM, Volume:MAK, Volume:GVLK for KMS) and its last five characters, the KMS host, and
/// whether the firmware holds an OEM key (its last five; the key itself never leaves the server). <c>Reason</c> is the
/// licensing error (0xC004F034, …) while not activated.
/// </summary>
public sealed record ActivationInfo(
    string Status,
    string? Product,
    string? Description,
    string? Channel,
    string? PartialKey,
    int? GraceMinutes,
    DateTimeOffset? EvaluationEnds,
    string? KmsHost,
    string? FirmwareKey,
    string? Reason);

/// <summary>The last shutdown: planned (1074, with the user and the reason) or unexpected (6008).</summary>
public sealed record ShutdownInfo(DateTimeOffset At, bool Unexpected, string? Reason, string? User);

public sealed record UpdatesInfo(string? LastHotfix, DateTimeOffset? LastHotfixAt, bool RebootPending, IReadOnlyList<string> RebootReasons);

public sealed record TimeInfo(string TimeZone, string? Source, DateTimeOffset? LastSync);

public sealed record SecurityInfo(DefenderInfo? Defender, IReadOnlyList<FirewallProfileState> Firewall, IReadOnlyList<BitLockerVolume> BitLocker, bool? SecureBoot);

public sealed record DefenderInfo(bool? RealTimeProtection, int? SignatureAgeDays, DateTimeOffset? LastQuickScan, DateTimeOffset? LastFullScan, string? ProductVersion);

/// <summary>A firewall profile (Domain, Private, Public): on or off, and its default for inbound connections.</summary>
public sealed record FirewallProfileState(string Profile, bool Enabled, string? DefaultInbound, bool Active);

/// <summary>BitLocker on a volume: protection on or off, and the conversion's state.</summary>
public sealed record BitLockerVolume(string Volume, string Protection, string? Conversion);

/// <summary>A member of the local Administrators group: a user or a group, local or of the domain.</summary>
public sealed record LocalAdmin(string Name, string Kind, IReadOnlyList<string>? Members = null);

public sealed record HypervisorInfo(bool Present, string? Vendor, string? Model);

// --- hardware (§4.3) ----------------------------------------------------------------------------------------

public sealed record HardwareSection(
    MachineInfo Machine,
    TpmInfo? Tpm,
    IReadOnlyList<ProcessorInfo> Processors,
    MemoryInfo Memory,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<BatteryInfo> Batteries);

public sealed record MachineInfo(string? Manufacturer, string? Model, string? SerialNumber, string? BiosVersion, DateTimeOffset? BiosDate, string? Firmware);

public sealed record TpmInfo(bool Present, string? Version, bool? Ready);

public sealed record ProcessorInfo(string Name, int? Cores, int? Threads, int? MaxClockMhz);

public sealed record MemoryInfo(long TotalBytes, IReadOnlyList<MemoryModule> Modules);

public sealed record MemoryModule(string? Slot, long SizeBytes, int? SpeedMhz, string? Manufacturer, string? PartNumber);

/// <summary>A physical disk: its media (HDD, SSD, NVMe), bus, health, and SMART's prediction.</summary>
public sealed record DiskInfo(string? Number, string Model, long? SizeBytes, string? Media, string? Bus, string? Health, string? OperationalStatus,
    bool? PredictFailure, string? SerialNumber = null);

/// <summary>
/// A volume: its mount (a drive letter, <c>C:</c>, on Windows; the mount point, <c>/var</c>, on Linux), label, file system, size,
/// free space, and its encryption (BitLocker's protection on Windows, <c>LUKS</c> on Linux).
/// </summary>
public sealed record VolumeInfo(string Mount, string? Label, string? FileSystem, long? SizeBytes, long? FreeBytes, string? Encryption);

public sealed record GpuInfo(string Name, string? DriverVersion, long? MemoryBytes);

public sealed record BatteryInfo(string Name, int? ChargePercent, string? Status);

// --- network (§4.4) -----------------------------------------------------------------------------------------

public sealed record NetworkSection(
    IReadOnlyList<AdapterInfo> Adapters,
    IReadOnlyList<TeamInfo> Teams,
    IReadOnlyList<string> VirtualSwitches,
    IReadOnlyList<RouteInfo> Routes,
    IReadOnlyList<ListenerInfo> Listeners,
    IReadOnlyDictionary<string, int> ConnectionsByState,
    FirewallInfo? Firewall,
    string? Proxy,
    IReadOnlyList<string> HostsEntries);

/// <summary>An adapter: what it is, its link, and its addresses (with prefixes), gateways and DNS.</summary>
public sealed record AdapterInfo(
    string Name,
    string? Description,
    string Kind,
    string? Mac,
    string Status,
    long? SpeedBps,
    int? Mtu,
    bool Dhcp,
    string? DhcpServer,
    DateTimeOffset? LeaseExpires,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    string? DnsSuffix,
    bool? RegisterInDns,
    int? VlanId,
    string? Driver,
    string? DriverVersion,
    string? Profile,
    bool Virtual);

public sealed record TeamInfo(string Name, IReadOnlyList<string> Members, string? Mode);

public sealed record RouteInfo(string Destination, string Gateway, string? Interface, int? Metric, bool Persistent);

/// <summary>A listening port and who owns it: the process, and the services in it.</summary>
public sealed record ListenerInfo(string Protocol, string Address, int Port, int? Pid, string? Process, IReadOnlyList<string> Services);

public sealed record FirewallInfo(IReadOnlyList<FirewallProfileState> Profiles, IReadOnlyList<FirewallRuleInfo> InboundRules);

/// <summary>An enabled inbound allow rule for a port that listens.</summary>
public sealed record FirewallRuleInfo(string Name, string? Group, string Protocol, string Ports, string Profiles, string? Program, string? RemoteAddresses);

// --- services (§4.5) ----------------------------------------------------------------------------------------

/// <summary>Who signed a service's binary: Windows' own, Microsoft's, someone else's, nobody's, or the PNMT sensor itself.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ServiceClass>))]
public enum ServiceClass { Windows, Microsoft, ThirdParty, Unsigned, Pnmt }

public sealed record ServicesSection(IReadOnlyList<ServiceEntry> Services);

/// <summary>
/// A service (§4.5): its state, start type (Automatic, Delayed, Manual, Trigger, Disabled), account, binary (the ServiceDll for
/// svchost services), version, company, signer, and its class. <see cref="AutoNotRunning"/>: automatic, not trigger-started,
/// and stopped.
/// </summary>
public sealed record ServiceEntry(
    string Name,
    string DisplayName,
    string State,
    string StartType,
    string? Account,
    string? Path,
    string? ServiceDll,
    string? Version,
    string? Company,
    string? Signer,
    ServiceClass Class,
    bool AutoNotRunning,
    int? Pid = null);

// --- remote access (§4.6) -----------------------------------------------------------------------------------

public sealed record RemoteAccessSection(IReadOnlyList<RemoteFeature> Features, string Summary);

/// <summary>
/// A way in (§4.6): installed or not, active or not, its ports and whether they listen, whether the firewall lets them in and on
/// which profiles, and notes such as "NLA off" or "SMB1 on".
/// </summary>
public sealed record RemoteFeature(
    string Key,
    string Name,
    bool Installed,
    bool Active,
    IReadOnlyList<int> Ports,
    bool Listening,
    bool FirewallOpen,
    IReadOnlyList<string> FirewallProfiles,
    IReadOnlyList<string> Notes);

// --- access (§4.7) ------------------------------------------------------------------------------------------

/// <summary>
/// The access section: the sessions now, the audit policy and the operational logs with what is missing and the command that
/// fixes it, the Security log's span, and the last 24 hours in numbers. The logins themselves are a stream (<see cref="AccessBatch"/>).
/// </summary>
public sealed record AccessSection(
    IReadOnlyList<SessionInfo> Sessions,
    IReadOnlyList<AuditSetting> AuditPolicy,
    IReadOnlyList<OperationalLog> Logs,
    SecurityLogInfo? SecurityLog,
    AccessCounts? Last24Hours,
    IReadOnlyList<string> Gaps);

/// <summary>An audit subcategory: what is on, what is needed, and the auditpol line for the rest.</summary>
public sealed record AuditSetting(string Subcategory, string Guid, bool Success, bool Failure, bool NeedsSuccess, bool NeedsFailure, string? Fix);

public sealed record OperationalLog(string Name, string Purpose, bool Exists, bool Enabled, string? Fix);

public sealed record SecurityLogInfo(long MaxBytes, DateTimeOffset? OldestAt, double? SpanHours);

public sealed record AccessCounts(int Successes, int Failures, int AdminLogins, int Lockouts);

/// <summary>
/// How a login came. The last five are a MikroTik router's ways (docs/cookie-access-plan.md §4.2): WinBox, WebFig, the API,
/// Telnet and FTP; a router's SSH is <see cref="Ssh"/> and its console <see cref="Console"/>. Stored as numbers: new ones go last.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<LogonKind>))]
public enum LogonKind { Console, Rdp, RdpConnection, Ssh, WinRm, Network, Cached, Unlock, Explicit, Batch, Lockout, Sudo, Su, Sftp, Winbox, Web, Api, Telnet, Ftp }

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<LogonOutcome>))]
public enum LogonOutcome { Success, Failure }

/// <summary>
/// A login or connection (§4.7), or an aggregate of identical ones within 10 minutes (<see cref="Count"/> above 1). Identified by
/// its log and the record id of its first event: sending it again (a higher count, a logoff) updates it. On Linux
/// (docs/linux-sensor-plan.md §5.6) the channel is the journal (<c>journal</c>), the record id a 64-bit hash of the first entry's
/// cursor, <see cref="KeyFingerprint"/> the key of an SSH key login, <see cref="TargetUser"/> whom sudo or su became, <see cref="Command"/>
/// sudo's command (only with config.yaml's collect.sudo_commands), and for the internet's "invalid user" noise, aggregated per source,
/// <see cref="TriedUsers"/> the first 10 names tried (<see cref="User"/> is then <see cref="LinuxLogins.InvalidUser"/>).
/// </summary>
public sealed record LoginRecord(
    string Channel,
    long RecordId,
    DateTimeOffset At,
    DateTimeOffset? LastAt,
    int Count,
    LogonOutcome Outcome,
    string? FailureReason,
    string User,
    string? Domain,
    bool IsAdmin,
    LogonKind Kind,
    string? AuthPackage,
    string? SourceAddress,
    int? SourcePort,
    string? Workstation,
    bool? Elevated,
    string? LogonId,
    DateTimeOffset? LoggedOffAt,
    string? KeyFingerprint = null,
    string? TargetUser = null,
    string? Command = null,
    IReadOnlyList<string>? TriedUsers = null);

/// <summary>Agent → PNMTD: logins and connections, read from the logs every 15 seconds, or buffered while offline.</summary>
public sealed record AccessBatch(IReadOnlyList<LoginRecord> Entries);

// --- the event log (§4.8) -------------------------------------------------------------------------------------

public sealed record EventLogSection(IReadOnlyList<EventEntry> Entries);

/// <summary>An entry of System or Application at Critical, Error or Warning; the message rendered and capped at 2 KB.</summary>
public sealed record EventEntry(DateTimeOffset At, string Log, string Level, string Source, int EventId, string? Category, string Message, string? User, long RecordId);

// --- scripts registered on the server (§7.2) ---------------------------------------------------------------

/// <summary>
/// The scripts an administrator registered in the agent's window (§7.2), and whether PNMT may run scripts there (only these, or
/// any script as the named accounts): PNMT lists them on the server's Scripts tab and runs them as their own account.
/// </summary>
public sealed record ScriptsSection(bool Allowed, bool AnyScript, IReadOnlyList<string> RunAs, IReadOnlyList<RegisteredScript> Scripts);

/// <summary>
/// A registered script: its SHA-256 and whether the copy still matches it, the account it runs as, its schedule and scheduled
/// task, who added it. The text and the parser's view of it only while the copy matches (a changed copy is refused anyway).
/// </summary>
public sealed record RegisteredScript(
    string Name,
    string Sha256,
    bool Matches,
    RunAsKind RunAs,
    string? Account,
    string? Schedule,
    ScheduledTaskState? Task,
    DateTimeOffset AddedAt,
    string AddedBy,
    DateTimeOffset UpdatedAt,
    string? Text,
    ParsedScript? Parsed,
    string? ParseError);

/// <summary>A registered script's scheduled task, as the Task Scheduler reports it; the result is Windows' (0 is success).</summary>
public sealed record ScheduledTaskState(string State, DateTimeOffset? LastRun, long? LastResult, DateTimeOffset? NextRun);
