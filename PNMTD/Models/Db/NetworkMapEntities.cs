using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Audit;
using PNMTD.Lib.Network;
using PNMTD.Models.Enums;

namespace PNMTD.Models.Db;

/// <summary>
/// The network map's settings, one row (docs/network-map-plan.md §7.3, §9.2): the schedule, off until someone turns it on,
/// and how long interfaces no router sees are kept.
/// </summary>
[Table("network_map_settings")]
[Audited]
public class NetworkMapSettingsEntity
{
    public const int SingletonId = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = SingletonId;

    /// <summary>Off at first (§15): the first scan is started by hand.</summary>
    public bool ScanScheduleEnabled { get; set; }

    /// <summary>Kept while the schedule is off, so turning it on gives 14 days.</summary>
    public int ScanIntervalDays { get; set; } = 14;

    /// <summary>The hour, in the server's time zone, when a scheduled run may start.</summary>
    public int ScanHour { get; set; } = 2;

    public int RetentionDays { get; set; } = 90;

    [AuditIgnore]
    public DateTime? ChangedAt { get; set; }

    [AuditIgnore]
    public Guid? ChangedBy { get; set; }

    [AuditIgnore]
    public string? ChangedByName { get; set; }
}

/// <summary>A scan run (§7.1): its scope, its routers fixed when it starts, and what it is doing now.</summary>
[Table("network_scans")]
[NotAudited("the scan's progress; starting and cancelling are audited by their events")]
public class NetworkScanEntity
{
    [Key]
    public Guid Id { get; set; }

    public NetworkScanScope Scope { get; set; }

    public Guid? LocationId { get; set; }

    public Guid? SensorId { get; set; }

    /// <summary>"All routers", the location's path, or the router's name, as it was when the run started.</summary>
    public string ScopeName { get; set; } = "";

    public NetworkScanTrigger Trigger { get; set; }

    public Guid? RequestedBy { get; set; }

    public string? RequestedByName { get; set; }

    public NetworkScanState State { get; set; }

    public int Total { get; set; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    public int Skipped { get; set; }

    public Guid? CurrentSensorId { get; set; }

    public string? CurrentRouterName { get; set; }

    /// <summary>"the MAC table", as the Scans tab shows it.</summary>
    public string? CurrentStep { get; set; }

    public int? CurrentStepNumber { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }
}

/// <summary>One router of a run (§7.2): its state, step, error, counts and duration.</summary>
[Table("network_scan_items")]
[NotAudited("the scan's progress")]
public class NetworkScanItemEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid ScanId { get; set; }

    public Guid? SensorId { get; set; }

    public string RouterName { get; set; } = "";

    public int Position { get; set; }

    public NetworkScanItemState State { get; set; }

    public string? Step { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string? Error { get; set; }

    /// <summary>The tables that failed, with their errors: <c>{"MacTable":"Output over 8 MiB"}</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string? TableErrors { get; set; }

    /// <summary>What was read: <c>{"ports":12,"macs":230,…}</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string? Counts { get; set; }

    public int? DurationMs { get; set; }
}

/// <summary>A router's latest scan, raw (§4.3): the analysis can run again from it after a change, without a new scan.</summary>
[Table("network_router_snapshots")]
[NotAudited("written by the scan")]
public class NetworkRouterSnapshotEntity
{
    [Key]
    public Guid SensorId { get; set; }

    public DateTime ScannedAt { get; set; }

    /// <summary>The scans of this router so far: a connection is unconfirmed only after two scans without it (§5.5).</summary>
    public int ScanNumber { get; set; }

    public string? RouterOsVersion { get; set; }

    /// <summary>Each table's output, as a gzip'd JSON object by table.</summary>
    public byte[] Raw { get; set; } = [];

    [Column(TypeName = "jsonb")]
    public string? DiscoverySettings { get; set; }

    [Column(TypeName = "jsonb")]
    public string? TableErrors { get; set; }
}

/// <summary>One of a router's interfaces from its latest scan (§9.2).</summary>
[Table("network_router_ports")]
[NotAudited("written by the scan")]
public class NetworkRouterPortEntity
{
    public Guid SensorId { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    public string? DefaultName { get; set; }

    public string? Type { get; set; }

    [MaxLength(17)]
    public string? Mac { get; set; }

    public int? Mtu { get; set; }

    public bool Running { get; set; }

    public bool Disabled { get; set; }

    public string? Comment { get; set; }

    public string? Bridge { get; set; }

    public string? VlanParent { get; set; }

    public int? VlanId { get; set; }

    public string? Bond { get; set; }

    public int? RateMbps { get; set; }

    public bool? FullDuplex { get; set; }

    /// <summary>The module, as the monitor showed it: <see cref="PortFacts"/> as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string? Sfp { get; set; }

    public string? DetectedType { get; set; }

    public PortRole Role { get; set; }

    public int MacCount { get; set; }

    public DateTime SeenAt { get; set; }
}

/// <summary>
/// One row a router showed (§4.3): a MAC table entry, ARP, a lease, a neighbor, a tunnel, a Wi-Fi client, an address. Replaced
/// per router and table when that table succeeds.
/// </summary>
[Table("network_observations")]
[NotAudited("written by the scan")]
public class NetworkObservationEntity
{
    [Key]
    public long Id { get; set; }

    public Guid SensorId { get; set; }

    public ObservationSource Source { get; set; }

    public string? Interface { get; set; }

    public string? Bridge { get; set; }

    public int? VlanId { get; set; }

    [MaxLength(17)]
    public string? Mac { get; set; }

    public string? Ip { get; set; }

    public string? HostName { get; set; }

    public string? Identity { get; set; }

    public string? Platform { get; set; }

    public string? Board { get; set; }

    public string? Version { get; set; }

    public string? RemoteInterface { get; set; }

    public string? DiscoveredBy { get; set; }

    public string? RemoteKey { get; set; }

    public string? Note { get; set; }

    public DateTime SeenAt { get; set; }
}

/// <summary>
/// A network interface (§6): one MAC that any router sees, where it is attached, and its match. A person's assignment
/// (<see cref="AssignedHostId"/>) wins over the automatic match and stays until it is cleared.
/// </summary>
[Table("network_interfaces")]
[NotAudited("written by the scan; assignments and ignoring are audited by their events")]
public class NetworkInterfaceEntity
{
    [Key]
    [MaxLength(17)]
    public string Mac { get; set; } = "";

    public List<string> Ips { get; set; } = [];

    public string? HostName { get; set; }

    public bool Random { get; set; }

    public bool Ignored { get; set; }

    public InterfaceSources Sources { get; set; }

    public Guid? AttachedSensorId { get; set; }

    public string? AttachedPort { get; set; }

    public int? AttachedVlan { get; set; }

    public string? AttachedNote { get; set; }

    public string? NeighborIdentity { get; set; }

    public string? NeighborPlatform { get; set; }

    public string? NeighborBoard { get; set; }

    public string? NeighborPort { get; set; }

    public MatchKind MatchKind { get; set; }

    public Guid? MatchHostId { get; set; }

    /// <summary>The router, or the server's (WINDOWS or LINUX) sensor.</summary>
    public Guid? MatchSensorId { get; set; }

    public string? MatchInterface { get; set; }

    public Guid? HintHostId { get; set; }

    public Guid? AssignedHostId { get; set; }

    public string? AssignedInterface { get; set; }

    public Guid? AssignedBy { get; set; }

    public string? AssignedByName { get; set; }

    public DateTime? AssignedAt { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }
}

/// <summary>
/// A server's MACs, from its agent's <c>network</c> section (§6.2), as a table that can be queried: written with each
/// section, so a server that reports later matches at once.
/// </summary>
[Table("agent_adapter_macs")]
[NotAudited("copied from the agent's network section")]
public class AgentAdapterMacEntity
{
    public Guid SensorId { get; set; }

    [MaxLength(17)]
    public string Mac { get; set; } = "";

    [MaxLength(200)]
    public string Adapter { get; set; } = "";

    public string? Kind { get; set; }

    public long? SpeedBps { get; set; }

    public bool Virtual { get; set; }
}

/// <summary>
/// A connection (§3.1): two ends, each a host and for a router its sensor, optionally with the interface; one type. Its ends
/// are stored in order (<see cref="ConnectionEnd.Ordered"/>) and <see cref="Key"/> is unique, so a pair of ends exists once.
/// </summary>
[Table("network_connections")]
[Audited]
public class NetworkConnectionEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Both ends' keys (<see cref="ConnectionEnd.PairKey"/>): unbounded, as two ports of 200 characters make it long.</summary>
    [AuditIgnore]
    public string Key { get; set; } = "";

    public Guid AHostId { get; set; }

    public Guid? ASensorId { get; set; }

    [MaxLength(200)]
    public string? AInterface { get; set; }

    public Guid BHostId { get; set; }

    public Guid? BSensorId { get; set; }

    [MaxLength(200)]
    public string? BInterface { get; set; }

    [MaxLength(40)]
    public string Type { get; set; } = "";

    [AuditText]
    public string? Notes { get; set; }

    public NetworkConnectionSource Source { get; set; }

    /// <summary>The suggestion's evidence when it was accepted.</summary>
    [AuditIgnore]
    [Column(TypeName = "jsonb")]
    public string? Evidence { get; set; }

    /// <summary>When an analysis last found it in the routers' tables (§5.5).</summary>
    [AuditIgnore]
    public DateTime? ConfirmedAt { get; set; }

    [AuditIgnore]
    public DateTime? UnconfirmedSince { get; set; }

    /// <summary>Scans of its routers in a row without it.</summary>
    [AuditIgnore]
    public int Misses { get; set; }

    /// <summary>The sum of its routers' scan numbers when it was last checked: an analysis after a new scan of either is one look.</summary>
    [AuditIgnore]
    public int CheckedScans { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [AuditIgnore]
    public Guid? CreatedBy { get; set; }

    [AuditIgnore]
    public string? CreatedByName { get; set; }

    [AuditIgnore]
    public DateTime? UpdatedAt { get; set; }

    [AuditIgnore]
    public Guid? UpdatedBy { get; set; }

    [AuditIgnore]
    public string? UpdatedByName { get; set; }

    [NotMapped]
    public ConnectionEnd A => new(AHostId, ASensorId, AInterface);

    [NotMapped]
    public ConnectionEnd B => new(BHostId, BSensorId, BInterface);
}

/// <summary>A suggestion (§5.3, §5.4): the analysis' proposal, with its evidence, until a person accepts or dismisses it.</summary>
[Table("network_suggestions")]
[NotAudited("the analysis' output; accepting, dismissing and restoring are audited by their events")]
public class NetworkSuggestionEntity
{
    [Key]
    public Guid Id { get; set; }

    public string Key { get; set; } = "";

    public SuggestionKind Kind { get; set; }

    public Guid AHostId { get; set; }

    public Guid? ASensorId { get; set; }

    public string? AInterface { get; set; }

    public Guid BHostId { get; set; }

    public Guid? BSensorId { get; set; }

    public string? BInterface { get; set; }

    public string? Type { get; set; }

    public Confidence Confidence { get; set; }

    /// <summary>The evidence: <c>[{"rule":"neighbor","confidence":"High","text":"…"}]</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string Evidence { get; set; } = "[]";

    /// <summary>For <see cref="SuggestionKind.Differs"/>: the connection it would change.</summary>
    public Guid? DiffersFrom { get; set; }

    public NetworkSuggestionState State { get; set; }

    public Guid? ConnectionId { get; set; }

    public Guid? DecidedBy { get; set; }

    public string? DecidedByName { get; set; }

    public DateTime? DecidedAt { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    [NotMapped]
    public ConnectionEnd A => new(AHostId, ASensorId, AInterface);

    [NotMapped]
    public ConnectionEnd B => new(BHostId, BSensorId, BInterface);
}

/// <summary>
/// A router's place on the map, saved by hand (docs/network-map-positions-plan.md §3): one layout for everyone, in the
/// map's own units (the layout's space, before zoom and pan). No row: the router is placed automatically.
/// </summary>
[Table("network_router_positions")]
[Audited]
public class NetworkRouterPositionEntity
{
    /// <summary>The router's <c>MIKROTIK</c> sensor; the row goes with it.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid SensorId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    [AuditIgnore]
    public DateTime UpdatedAt { get; set; }

    [AuditIgnore]
    public Guid? UpdatedBy { get; set; }

    [AuditIgnore]
    public string? UpdatedByName { get; set; }
}
