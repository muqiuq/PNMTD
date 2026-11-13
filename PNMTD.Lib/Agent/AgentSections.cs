namespace PNMTD.Lib.Agent;

// The sections an agent reports (docs/windows-sensor-plan.md §4), shared by the agent, PNMTD and
// the API's typed answers. A field the agent could not read is null, and the collector's error is
// in the section's errors. Lists are capped on PNMTD's side when they are stored.

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

    public const int SchemaVersion = 1;

    public static readonly IReadOnlyList<string> All = [System, Hardware, Network, Services, RemoteAccess, Access, EventLog];

    public static bool IsKnown(string? name) => name is not null && All.Contains(name);
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
    string? Architecture, bool? Activated, bool Supported, string? ProductType);

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

public sealed record VolumeInfo(string Letter, string? Label, string? FileSystem, long? SizeBytes, long? FreeBytes, string? BitLocker);

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
