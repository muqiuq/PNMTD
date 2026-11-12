using PNMTD.Lib.Udp;
using PNMTD.Models.Enums;

namespace PNMTD.Api.V1.Contracts;

// UDP nodes (docs/udp-iot-plan.md §5). Bytes travel as Base64; the UI renders hex and ASCII. Every
// datagram comes with its fields (offset, length, name, value), named by the server's own codec.

/// <summary>
/// <c>SensorRequest.udp</c> for a UDP_NODE. On create, a missing <see cref="Key"/> or <see cref="Token"/>
/// is generated and a given one stored as manual; on update, a missing one stays as it is.
/// </summary>
public sealed class UdpNodeRequest
{
    /// <summary>Null: a random free id on create (Udp:DeviceIdBytes, 2 by default), unchanged on update.</summary>
    public long? DeviceId { get; init; }

    public KeyFamily KeyFamily { get; init; } = KeyFamily.HmacSha256;

    /// <summary>NONE, TOKEN32 and suites of the family. Default: the family's 32-bit MAC suite.</summary>
    public List<SecuritySuite>? AllowedSuites { get; init; }

    public UdpSessionMode SessionMode { get; init; } = UdpSessionMode.None;

    public UdpAckMode AckMode { get; init; } = UdpAckMode.OnRequest;

    public bool ReplayProtection { get; init; } = true;

    /// <summary>Null: answer in the suite the uplink came with (§3.5).</summary>
    public SecuritySuite? ResponseSuite { get; init; }

    /// <summary>32 hex digits (spaces, colons and 0x allowed).</summary>
    public string? Key { get; init; }

    /// <summary>8 hex digits.</summary>
    public string? Token { get; init; }
}

/// <summary>A count per reject reason.</summary>
public sealed record UdpReasonCount(UdpRejectReason Reason, long Count);

/// <summary>A node's numbers over the last 24 hours, from memory (they start again after a restart).</summary>
public sealed record UdpNumbers(long Accepted, long Rejected, long BytesIn, long BytesOut, double? Loss, IReadOnlyList<UdpReasonCount> RejectedByReason);

/// <summary>An update's progress, for lists.</summary>
public sealed record UdpOtaProgress(Guid JobId, string FirmwareName, string FirmwareVersion, UdpOtaState State, long HighestOffset, int Size, double Progress);

/// <summary><c>SensorDetail.udp</c>: the node's settings and state. The key and the token are in <c>integration.udp</c>.</summary>
public sealed record UdpNodeDetail(
    long DeviceId,
    string DeviceIdBytes,
    KeyFamily KeyFamily,
    IReadOnlyList<SecuritySuite> AllowedSuites,
    UdpSessionMode SessionMode,
    bool SessionActive,
    bool SessionPending,
    DateTime? SessionStartedAt,
    UdpAckMode AckMode,
    bool ReplayProtection,
    SecuritySuite? ResponseSuite,
    UdpSecretSource KeySource,
    DateTime KeyChangedAt,
    string? KeyChangedBy,
    UdpSecretSource TokenSource,
    DateTime TokenChangedAt,
    string? TokenChangedBy,
    long? Fcnt,
    DateTime? LastSeenAt,
    string? LastEndpoint,
    SecuritySuite? LastSuite,
    DateTime? LastBootAt,
    string? LastBootReason,
    string? FirmwareVersion,
    UdpNumbers Last24Hours,
    bool BadTagWarning,
    int OpenCommands,
    UdpOtaProgress? OpenUpdate,
    bool ListenerEnabled,
    string Endpoint,
    IReadOnlyList<string> Warnings);

/// <summary>A node's secrets, as hex: for User and Admin only.</summary>
public sealed record UdpSecrets(string Key, string Token);

/// <summary><c>PUT /sensors/{id}/udp/key</c>: values to set manually, as hex. A missing one stays.</summary>
public sealed class UdpKeyRequest
{
    public string? Key { get; init; }

    public string? Token { get; init; }
}

/// <summary><c>POST /sensors/{id}/udp/regenerate-key</c>: which values get new random ones.</summary>
public sealed class UdpRegenerateRequest
{
    public bool Key { get; init; } = true;

    public bool Token { get; init; }
}

/// <summary>New secrets, and warnings (another node uses the same key).</summary>
public sealed record UdpSecretsChanged(UdpSecrets Secrets, IReadOnlyList<string> Warnings);

/// <summary>A packet as stored, with its fields for the packet inspector.</summary>
public sealed record UdpPacketView(
    PacketType Type,
    DateTime ReceivedAt,
    string Endpoint,
    SecuritySuite Suite,
    long Fcnt,
    byte[] Datagram,
    byte[]? Payload,
    IReadOnlyList<PacketField> Fields,
    byte[]? Response,
    IReadOnlyList<PacketField>? ResponseFields);

/// <summary>A rejected packet, from memory.</summary>
public sealed record UdpRejectionView(DateTime At, UdpRejectReason Reason, string Endpoint, byte[] Datagram, string? Detail, IReadOnlyList<PacketField>? Fields);

/// <summary><c>POST /sensors/{id}/udp/commands</c>.</summary>
public sealed class UdpCommandRequest
{
    public byte[]? Payload { get; init; }

    public UdpCommandDelivery Delivery { get; init; } = UdpCommandDelivery.ResultRequired;

    /// <summary>Null: the node's response suite, by default the uplink's (§3.10).</summary>
    public SecuritySuite? Suite { get; init; }

    /// <summary>Default 24; 0: never.</summary>
    public int? ExpiresInHours { get; init; }

    public int? MaxAttempts { get; init; }
}

public sealed record UdpCommandItem(
    Guid Id,
    Guid? SensorId,
    long DeviceId,
    string NodeName,
    short CmdId,
    byte[] Payload,
    UdpCommandState State,
    UdpCommandDelivery Delivery,
    SecuritySuite? Suite,
    short Attempts,
    short MaxAttempts,
    DateTime Created,
    string? CreatedBy,
    DateTime? ExpiresAt,
    DateTime? LastSentAt,
    DateTime? CompletedAt,
    short? ResultStatus,
    string? ResultStatusText,
    byte[]? Result,
    string? Note);

public sealed record UdpCommandCreated(UdpCommandItem Command, IReadOnlyList<string> Warnings);

/// <summary><c>GET /sensors/{id}/udp/commands</c>. Sort: created (default -created).</summary>
public sealed class UdpCommandQuery : PageQuery
{
    public UdpCommandState? State { get; set; }
}

/// <summary>A row of the permanent command log, with the fields of its datagram.</summary>
public sealed record UdpLogItem(
    long Id,
    DateTime Created,
    UdpLogKind Kind,
    UdpDirection? Direction,
    Guid? CommandId,
    Guid? OtaJobId,
    Guid? SensorId,
    long DeviceId,
    string NodeName,
    short? CmdId,
    short? Attempt,
    string? Endpoint,
    SecuritySuite? Suite,
    long? Fcnt,
    byte[]? Datagram,
    byte[]? Payload,
    short? ResultStatus,
    string? UserName,
    string? Note,
    IReadOnlyList<PacketField>? Fields);

/// <summary><c>GET /udp/command-log</c>: newest first.</summary>
public sealed class UdpLogQuery : PageQuery
{
    public Guid? SensorId { get; set; }

    public long? DeviceId { get; set; }

    public UdpLogKind? Kind { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

/// <summary>A row of the admin page's node list.</summary>
public sealed record UdpNodeListItem(
    Guid SensorId,
    string Name,
    Ref Host,
    long DeviceId,
    string DeviceIdBytes,
    bool Enabled,
    SensorState State,
    KeyFamily KeyFamily,
    IReadOnlyList<SecuritySuite> AllowedSuites,
    UdpSecretSource KeySource,
    DateTime KeyChangedAt,
    string? KeyChangedBy,
    UdpSecretSource TokenSource,
    UdpSessionMode SessionMode,
    bool SessionActive,
    UdpAckMode AckMode,
    SecuritySuite? ResponseSuite,
    DateTime? LastSeenAt,
    string? LastEndpoint,
    SecuritySuite? LastSuite,
    long? Fcnt,
    int OpenCommands,
    string? FirmwareVersion,
    UdpOtaProgress? OpenUpdate,
    long Packets24Hours,
    double? Loss24Hours,
    long Rejected24Hours,
    bool BadTagWarning,
    string? Notes);

/// <summary><c>GET /udp/nodes</c>. Sort: name (default), host, deviceId, lastSeen, state. Search: name, host, device id, notes.</summary>
public sealed class UdpNodeQuery : PageQuery
{
    public SensorState? State { get; set; }

    public KeyFamily? KeyFamily { get; set; }

    public bool? Enabled { get; set; }

    public string? FirmwareVersion { get; set; }
}

public sealed record UdpWarning(Guid SensorId, string NodeName, string Message);

/// <summary><c>GET /udp/status</c>: the listener and its counters since the start.</summary>
public sealed record UdpStatus(
    bool Enabled,
    bool Listening,
    int? Port,
    string Endpoint,
    string? Error,
    DateTime? Since,
    int QueueDepth,
    long Received,
    long Accepted,
    long Responses,
    long BytesIn,
    long BytesOut,
    int PacketsLastMinute,
    IReadOnlyList<UdpReasonCount> Rejected,
    int Nodes,
    IReadOnlyList<UdpWarning> Warnings,
    int FirmwareCached,
    long FirmwareCachedBytes);

public sealed record UdpUnknownSenderItem(long? DeviceId, string Address, DateTime FirstSeen, DateTime LastSeen, long Count,
    SecuritySuite? LastSuite, byte[] LastDatagram, string? Problem, bool NodeExists);

// --- firmware and updates (§4.11) ---------------------------------------------------------

public sealed record UdpFirmwareItem(
    Guid Id,
    string Name,
    string Version,
    string FileName,
    int Size,
    string Sha256,
    string? Notes,
    DateTime UploadedAt,
    string? UploadedBy,
    int Jobs,
    int OpenJobs,
    int NodesRunning,
    string? Magic);

public sealed record UdpFirmwareUploaded(UdpFirmwareItem Firmware, IReadOnlyList<string> Warnings);

/// <summary><c>PUT /udp/firmware/{id}</c>.</summary>
public sealed class UdpFirmwareRequest
{
    public string? Name { get; init; }

    public string? Version { get; init; }

    public string? Notes { get; init; }
}

/// <summary>The cost of an update on the wire (§3.13): IPv4, the chunk size, an 8-byte tag.</summary>
public sealed record UdpOtaEstimate(int RoundTrips, long BytesOnTheWire, double SecondsAt100Ms, double SecondsAt2s);

public sealed record UdpOtaJobItem(
    Guid Id,
    Guid? SensorId,
    long DeviceId,
    string NodeName,
    Guid? FirmwareId,
    string FirmwareName,
    string FirmwareVersion,
    int FirmwareSize,
    string FirmwareSha256,
    short UpdateId,
    short ChunkSize,
    UdpOtaState State,
    short? Reason,
    string? ReasonText,
    short OfferAttempts,
    short MaxOfferAttempts,
    long BytesServed,
    int ChunksServed,
    int ChunkRetransmissions,
    long HighestOffset,
    double Progress,
    DateTime Created,
    string? CreatedBy,
    DateTime? OfferedAt,
    DateTime? StartedAt,
    DateTime? LastRequestAt,
    DateTime? FinishedAt,
    DateTime ExpiresAt,
    string? AppliedVersion,
    UdpOtaEstimate Estimate);

/// <summary>A node's update with its command-log rows.</summary>
public sealed record UdpOtaJobDetail(UdpOtaJobItem Job, IReadOnlyList<UdpLogItem> Log);

/// <summary><c>POST /udp/ota-jobs</c>: one job per node.</summary>
public sealed class UdpOtaJobRequest
{
    public Guid FirmwareId { get; init; }

    public List<Guid>? SensorIds { get; init; }

    public int? ChunkSize { get; init; }

    public int? ExpiresInDays { get; init; }

    public int? MaxOfferAttempts { get; init; }
}

public sealed record UdpOtaRefusal(Guid SensorId, string Name, string Reason);

public sealed record UdpOtaJobsCreated(IReadOnlyList<UdpOtaJobItem> Created, IReadOnlyList<UdpOtaRefusal> Refused);

/// <summary><c>GET /udp/ota-jobs</c>: newest first.</summary>
public sealed class UdpOtaJobQuery : PageQuery
{
    public UdpOtaState? State { get; set; }

    public Guid? FirmwareId { get; set; }

    public Guid? SensorId { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

// --- the Protocol subpage (§6.5) ----------------------------------------------------------

public sealed record UdpSuiteInfo(SecuritySuite Suite, int Code, string Name, int TagLength, KeyFamily? Family, bool Keyed, bool Encrypted, bool Legacy);

public sealed record UdpTypeInfo(PacketType Type, int Code, string Name, bool Downlink);

/// <summary>A worked example: the datagram, its fields, and ways to send it.</summary>
public sealed record UdpExample(string Title, string Description, byte[] Datagram, IReadOnlyList<PacketField> Fields, string Hex, string CArray, string? Shell);

/// <summary>
/// <c>GET /udp/protocol/examples</c>: the suites and types, and examples built with the node's id,
/// suites and key (the reference key 00 01 … 0F without a node, or for a Viewer).
/// </summary>
public sealed record UdpProtocolExamples(
    bool ReferenceKey,
    string? KeyHex,
    string? TokenHex,
    long DeviceId,
    KeyFamily KeyFamily,
    IReadOnlyList<SecuritySuite> AllowedSuites,
    string Endpoint,
    int MaxDatagram,
    IReadOnlyList<UdpSuiteInfo> Suites,
    IReadOnlyList<UdpTypeInfo> Types,
    IReadOnlyList<UdpExample> Examples,
    IReadOnlyList<UdpExample> Vectors);

/// <summary><c>POST /udp/protocol/build</c>: builds, never sends.</summary>
public sealed class UdpBuildRequest
{
    public Guid? SensorId { get; init; }

    public PacketType Type { get; init; } = PacketType.Heartbeat;

    public SecuritySuite Suite { get; init; } = SecuritySuite.HmacSha256_32;

    public long Fcnt { get; init; } = 1;

    public bool Ack { get; init; }

    public byte[]? Payload { get; init; }
}

public sealed record UdpBuildResult(bool ReferenceKey, byte[] Datagram, IReadOnlyList<PacketField> Fields, string Hex, string CArray, string Send);
