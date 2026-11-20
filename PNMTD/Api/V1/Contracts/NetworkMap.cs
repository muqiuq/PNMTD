using System.Text.Json.Serialization;
using PNMTD.Lib.Network;
using PNMTD.Models.Enums;

namespace PNMTD.Api.V1.Contracts;

// The network map (docs/network-map-plan.md §10.1).

[JsonConverter(typeof(JsonStringEnumConverter<NetworkDeviceKind>))]
public enum NetworkDeviceKind { Router, Host }

/// <summary>A connection's scan state (§5.5): seen by the latest scans, seen before but no longer, or never seen (made by hand).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetworkScanEvidence>))]
public enum NetworkScanEvidence { Confirmed, Unconfirmed, Manual }

public sealed record NetworkConnectionTypeItem(string Code, string Label, ConnectionMedium Medium, int? SpeedMbps, bool Physical);

/// <summary>One end as the UI shows it: the device (a router or a host), its name, the port, and its top-level location (the site).</summary>
public sealed record NetworkEndItem(NetworkDeviceKind Kind, Guid HostId, Guid? SensorId, string Name, string? Interface, Guid? SiteId, string? Site);

/// <summary>An end to write: a router (its sensor; its host follows) or a host, and optionally the interface.</summary>
public sealed class NetworkEndRequest
{
    public Guid? SensorId { get; set; }

    public Guid? HostId { get; set; }

    public string? Interface { get; set; }
}

// --- the map --------------------------------------------------------------------------------------------------------------

public sealed class NetworkGraphQuery
{
    /// <summary>All (the default), Locations (those of <see cref="RouterLocationIds"/>), or Problems (failing only).</summary>
    public string? Routers { get; set; }

    public List<Guid>? RouterLocationIds { get; set; }

    /// <summary>The hosts of these locations and their sub-locations; none by default.</summary>
    public List<Guid>? HostLocationIds { get; set; }

    /// <summary>Only hosts with a connection (true by default).</summary>
    public bool? OnlyConnected { get; set; }

    /// <summary>The open suggestions as edges too.</summary>
    public bool? Suggestions { get; set; }

    /// <summary>Only physical (true) or only virtual (false) connections.</summary>
    public bool? Physical { get; set; }

    /// <summary>Only these media (BaseT, Dac, FiberSmf, …).</summary>
    public List<ConnectionMedium>? Media { get; set; }
}

/// <summary>A node: "router:{sensorId}" or "host:{hostId}", with its state and site (§8.2).</summary>
/// <summary>A node; a router's saved position in <see cref="X"/> and <see cref="Y"/>, both null when it is placed automatically.</summary>
public sealed record NetworkNode(string Id, NetworkDeviceKind Kind, Guid HostId, Guid? SensorId, string Name, Guid? LocationId, Guid? SiteId,
    string State, string? Os, int HiddenConnections, double? X = null, double? Y = null);

/// <summary>A dashed circle: a top-level location with nodes on the map (decision 1).</summary>
public sealed record NetworkGroup(Guid LocationId, string Name);

public sealed record NetworkEdge(Guid Id, string Source, string Target, string? SourcePort, string? TargetPort, string? Type, string? TypeLabel,
    bool Physical, int? SpeedMbps, NetworkScanEvidence Scan, bool Suggestion, Confidence? Confidence);

/// <summary>The map; <see cref="SavedPositions"/>: the routers with a saved position, shown or not.</summary>
public sealed record NetworkGraph(IReadOnlyList<NetworkNode> Nodes, IReadOnlyList<NetworkGroup> Groups, IReadOnlyList<NetworkEdge> Edges,
    DateTime? LastFullScanAt, int SavedPositions = 0);

// --- a device's panel -----------------------------------------------------------------------------------------------------

public sealed record NetworkPortItem(string Name, string? DefaultName, string? Type, string? Mac, bool Running, bool Disabled, string? Comment,
    string? Bridge, string? VlanParent, int? VlanId, string? Bond, int? RateMbps, bool? FullDuplex, string? DetectedType, string? DetectedTypeLabel,
    PortRole Role, int MacCount, PortFacts? Sfp, Guid? ConnectionId, string? ConnectedTo, Guid? SuggestionId);

public sealed record NetworkAdapterItem(string Adapter, string Mac, string? Kind, long? SpeedBps, bool Virtual);

public sealed record NetworkDeviceScan(DateTime? ScannedAt, NetworkScanItemState? State, string? Error, IReadOnlyDictionary<string, string>? TableErrors,
    string? DiscoverySettings);

public sealed record NetworkDeviceDetail(
    string Id,
    NetworkDeviceKind Kind,
    Guid HostId,
    Guid? SensorId,
    string Name,
    string HostName,
    Guid? LocationId,
    string? LocationPath,
    string State,
    string? StateMessage,
    string? Model,
    string? Version,
    string? Identity,
    string? Os,
    NetworkDeviceScan? Scan,
    IReadOnlyList<NetworkConnectionItem> Connections,
    IReadOnlyList<NetworkPortItem> Ports,
    IReadOnlyList<NetworkAdapterItem> Adapters,
    IReadOnlyList<NetworkInterfaceItem>? Interfaces,
    int InterfacesSeen,
    int InterfacesUnassigned,
    int OpenSuggestions,
    NetworkRouterPosition? Position = null);

/// <summary>A router's saved place on the map (docs/network-map-positions-plan.md §5.1).</summary>
public sealed record NetworkRouterPosition(double X, double Y, DateTime UpdatedAt, string? UpdatedByName);

/// <summary>A router's place to save: in the map's own units.</summary>
public sealed record NetworkPositionInput
{
    public Guid SensorId { get; init; }

    public double X { get; init; }

    public double Y { get; init; }
}

public sealed record NetworkPositionsRequest
{
    public List<NetworkPositionInput>? Positions { get; init; }
}

/// <summary>Saved; skipped: routers deleted between the drag and the save.</summary>
public sealed record NetworkPositionsResult(int Saved, int Skipped);

// --- connections ----------------------------------------------------------------------------------------------------------

public sealed record NetworkConnectionItem(
    Guid Id,
    NetworkEndItem A,
    NetworkEndItem B,
    string Type,
    string TypeLabel,
    bool Physical,
    int? SpeedMbps,
    NetworkScanEvidence Scan,
    DateTime? ConfirmedAt,
    DateTime? UnconfirmedSince,
    string? Notes,
    NetworkConnectionSource Source,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime? UpdatedAt,
    string? UpdatedByName);

public sealed class NetworkConnectionQuery : PageQuery
{
    public Guid? LocationId { get; set; }

    /// <summary>A router's sensor or a host: its connections.</summary>
    public Guid? DeviceId { get; set; }

    public string? Type { get; set; }

    public bool? Physical { get; set; }

    /// <summary>Only router ↔ router: how the routers are connected.</summary>
    public bool? RoutersOnly { get; set; }

    public bool? Unconfirmed { get; set; }
}

public sealed class NetworkConnectionRequest
{
    public NetworkEndRequest A { get; set; } = new();

    public NetworkEndRequest B { get; set; } = new();

    public string Type { get; set; } = "";

    public string? Notes { get; set; }
}

/// <summary>A patch: the ports, the type, the notes. The ends' devices don't change; a different pair is a new connection.</summary>
public sealed class NetworkConnectionUpdate
{
    public string? AInterface { get; set; }

    public string? BInterface { get; set; }

    public string? Type { get; set; }

    public string? Notes { get; set; }
}

// --- suggestions ----------------------------------------------------------------------------------------------------------

public sealed record NetworkSuggestionItem(
    Guid Id,
    SuggestionKind Kind,
    NetworkEndItem A,
    NetworkEndItem B,
    string? Type,
    string? TypeLabel,
    Confidence Confidence,
    IReadOnlyList<Evidence> Evidence,
    Guid? DiffersFrom,
    NetworkSuggestionState State,
    Guid? ConnectionId,
    string? DecidedByName,
    DateTime? DecidedAt,
    DateTime FirstSeenAt,
    DateTime LastSeenAt);

public sealed class NetworkSuggestionQuery : PageQuery
{
    /// <summary>Open by default.</summary>
    public NetworkSuggestionState? State { get; set; }

    public Confidence? Confidence { get; set; }

    public SuggestionKind? Kind { get; set; }

    public Guid? DeviceId { get; set; }

    public Guid? LocationId { get; set; }
}

/// <summary>Accepting: the proposal as it is, or with another type, ports or notes.</summary>
public sealed class NetworkSuggestionAccept
{
    public string? Type { get; set; }

    public string? AInterface { get; set; }

    public string? BInterface { get; set; }

    public string? Notes { get; set; }
}

public sealed class NetworkSuggestionIds
{
    public List<Guid> Ids { get; set; } = [];
}

public sealed record NetworkSuggestionsDecided(int Done, IReadOnlyList<NetworkSuggestionRefusal> Refused);

public sealed record NetworkSuggestionRefusal(Guid Id, string Reason);

// --- network interfaces ---------------------------------------------------------------------------------------------------

public sealed record NetworkInterfaceItem(
    string Mac,
    bool Random,
    bool Ignored,
    IReadOnlyList<string> Ips,
    string? HostName,
    IReadOnlyList<string> Sources,
    Guid? AttachedSensorId,
    string? AttachedRouter,
    string? AttachedPort,
    int? AttachedVlan,
    string? AttachedNote,
    string? NeighborIdentity,
    string? NeighborPlatform,
    string? NeighborBoard,
    string? NeighborPort,
    MatchKind Match,
    Guid? MatchHostId,
    Guid? MatchSensorId,
    string? MatchName,
    string? MatchInterface,
    Guid? HintHostId,
    string? HintHostName,
    string? AssignedByName,
    DateTime? AssignedAt,
    DateTime FirstSeenAt,
    DateTime LastSeenAt);

public sealed class NetworkInterfaceQuery : PageQuery
{
    public bool? Unassigned { get; set; }

    public Guid? RouterId { get; set; }

    public Guid? LocationId { get; set; }

    /// <summary>MacTable, Arp, Dhcp, Neighbor, Wifi.</summary>
    public string? Source { get; set; }

    public int? Vlan { get; set; }

    /// <summary>Seen in its router's latest scan.</summary>
    public bool? SeenInLastScan { get; set; }

    public bool? ShowIgnored { get; set; }

    /// <summary>The host it is matched or assigned to.</summary>
    public Guid? HostId { get; set; }
}

public sealed class NetworkInterfaceAssignment
{
    public Guid HostId { get; set; }

    public string? Interface { get; set; }
}

// --- scans ----------------------------------------------------------------------------------------------------------------

public sealed record NetworkScanItemRow(Guid Id, Guid? SensorId, string RouterName, int Position, NetworkScanItemState State, string? Step,
    DateTime? StartedAt, DateTime? FinishedAt, string? Error, IReadOnlyDictionary<string, string>? TableErrors, IReadOnlyDictionary<string, int>? Counts,
    int? DurationMs);

public sealed record NetworkScanRow(
    Guid Id,
    NetworkScanScope Scope,
    string ScopeName,
    Guid? LocationId,
    Guid? SensorId,
    NetworkScanTrigger Trigger,
    string? RequestedByName,
    NetworkScanState State,
    int Total,
    int Succeeded,
    int Failed,
    int Skipped,
    string? CurrentRouterName,
    string? CurrentStep,
    int? CurrentStepNumber,
    int StepCount,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int? EstimatedSecondsLeft);

public sealed record NetworkScanDetail(NetworkScanRow Scan, IReadOnlyList<NetworkScanItemRow> Routers);

public sealed record NetworkScanStatus(NetworkScanRow? Running, IReadOnlyList<NetworkScanRow> Queued, NetworkScanRow? LastFull, DateTime? NextScheduledAt,
    bool ScheduleEnabled, bool Enabled);

public sealed class NetworkScanRequest
{
    public NetworkScanScope Scope { get; set; }

    public Guid? LocationId { get; set; }

    public Guid? SensorId { get; set; }
}

public sealed class NetworkScanQuery : PageQuery;

public sealed record NetworkMapSettings(bool ScanScheduleEnabled, int ScanIntervalDays, int ScanHour, int RetentionDays, string TimeZone,
    DateTime? ChangedAt, string? ChangedByName);

public sealed class NetworkMapSettingsRequest
{
    public bool ScanScheduleEnabled { get; set; }

    public int ScanIntervalDays { get; set; } = 14;

    public int ScanHour { get; set; } = 2;

    public int RetentionDays { get; set; } = 90;
}

// --- summary and paths (MCP, §10.2) ---------------------------------------------------------------------------------------

public sealed record NetworkMapSummary(
    int Routers,
    int HostsWithConnections,
    int Connections,
    int Physical,
    int Virtual,
    int Unconfirmed,
    IReadOnlyDictionary<string, int> ByMedium,
    IReadOnlyDictionary<string, int> OpenSuggestions,
    int UnassignedInterfaces,
    NetworkScanStatus Scans);

public sealed record NetworkPathHop(Guid ConnectionId, NetworkEndItem From, NetworkEndItem To, string Type, string TypeLabel, bool Physical,
    NetworkScanEvidence Scan, DateTime? ConfirmedAt);

public sealed record NetworkPath(NetworkEndItem From, NetworkEndItem To, IReadOnlyList<NetworkPathHop> Hops, string? Slowest, int VirtualHops,
    IReadOnlyList<Guid> SinglePoints, DateTime? LastFullScanAt);
