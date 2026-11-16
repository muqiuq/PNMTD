using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Lib.Udp;
using PNMTD.Models.Enums;

namespace PNMTD.Models.Db;

/// <summary>
/// The protocol settings and state of a UDP_NODE sensor, 1:1, deleted with it (docs/udp-iot-plan.md §4.3).
/// Key and token are in plaintext, like the sensor tokens (review decision 3).
/// </summary>
[Table("udp_nodes")]
[Audited]
public class UdpNodeEntity
{
    [Key]
    public Guid SensorId { get; set; }

    public virtual SensorEntity Sensor { get; set; } = null!;

    /// <summary>1 … 4,294,967,295; unique.</summary>
    public long DeviceId { get; set; }

    [AuditPlain("the kind of key")]

    public KeyFamily KeyFamily { get; set; }

    /// <summary>A bitmask of SUITE values (<see cref="SecuritySuites.Mask"/>).</summary>
    public int AllowedSuites { get; set; }

    [AuditSecret]

    public byte[] Key { get; set; } = [];

    [AuditSecret]

    public byte[] Token { get; set; } = [];

    [AuditPlain("where the key came from")]

    public UdpSecretSource KeySource { get; set; }

    [AuditPlain("where the token came from")]

    public UdpSecretSource TokenSource { get; set; }

    [AuditIgnore]

    public DateTime KeyChangedAt { get; set; }

    [AuditIgnore]

    public Guid? KeyChangedBy { get; set; }

    [AuditIgnore]

    public DateTime TokenChangedAt { get; set; }

    [AuditIgnore]

    public Guid? TokenChangedBy { get; set; }

    public UdpSessionMode SessionMode { get; set; }

    [AuditSecret]

    public byte[]? SessionKey { get; set; }

    public DateTime? SessionStartedAt { get; set; }

    [AuditSecret]

    public byte[]? PendingSessionKey { get; set; }

    public DateTime? PendingSessionAt { get; set; }

    public UdpAckMode AckMode { get; set; } = UdpAckMode.OnRequest;

    public bool ReplayProtection { get; set; } = true;

    /// <summary>Null: answer in the suite the uplink came with (§3.5).</summary>
    public SecuritySuite? ResponseSuite { get; set; }

    /// <summary>The counter window of the keyed suites (§3.7): the highest accepted FCNT32, and the 64-bit bitmap below it.</summary>
    public long? FcntHigh { get; set; }

    public long FcntWindow { get; set; }

    /// <summary>
    /// The counter window of NONE and TOKEN32 uplinks (§3.7), kept apart: anyone can send one, so it must never move the
    /// window the keyed suites are checked against.
    /// </summary>
    public long? PlainFcntHigh { get; set; }

    public long PlainFcntWindow { get; set; }

    /// <summary>The last accepted uplink, for retransmissions (§3.9): its counter, a SHA-256 of its bytes, and what PNMTD answered.</summary>
    public long? LastUplinkFcnt { get; set; }

    public byte[]? LastUplinkDigest { get; set; }

    public DateTime? LastUplinkAt { get; set; }

    public byte[]? LastResponse { get; set; }

    public string? LastEndpoint { get; set; }

    public SecuritySuite? LastSuite { get; set; }

    public DateTime? LastSeenAt { get; set; }

    public DateTime? LastBootAt { get; set; }

    public byte? LastBootReason { get; set; }

    public short NextCmdId { get; set; }

    public short NextUpdateId { get; set; }

    /// <summary>The running version, from BOOT's INFO or OTA APPLIED.</summary>
    public string? FirmwareVersion { get; set; }

    public DateTime Created { get; set; }

    [NotMapped]
    public CounterState Counter
    {
        get => new(FcntHigh, unchecked((ulong)FcntWindow));
        set
        {
            FcntHigh = value.High;
            FcntWindow = unchecked((long)value.Window);
        }
    }

    [NotMapped]
    public CounterState PlainCounter
    {
        get => new(PlainFcntHigh, unchecked((ulong)PlainFcntWindow));
        set
        {
            PlainFcntHigh = value.High;
            PlainFcntWindow = unchecked((long)value.Window);
        }
    }
}

/// <summary>The last packet of each uplink type per node, overwritten by the next one (§4.7).</summary>
[Table("udp_last_packets")]
[NotAudited("packets received")]
public class UdpLastPacketEntity
{
    public Guid SensorId { get; set; }

    public PacketType Type { get; set; }

    public DateTime ReceivedAt { get; set; }

    public string Endpoint { get; set; } = "";

    public SecuritySuite Suite { get; set; }

    public long Fcnt { get; set; }

    public byte[] Datagram { get; set; } = [];

    public byte[]? Payload { get; set; }

    /// <summary>What PNMTD answered, if anything.</summary>
    public byte[]? Response { get; set; }
}

/// <summary>A command and its current state. Permanent (§4.3, §4.6).</summary>
[Table("udp_commands")]
[NotAudited("commands at work; queuing and cancelling are entries of their own")]
public class UdpCommandEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Null once the node is deleted; <see cref="DeviceId"/> and <see cref="NodeName"/> keep it readable.</summary>
    public Guid? SensorId { get; set; }

    public long DeviceId { get; set; }

    public string NodeName { get; set; } = "";

    public short CmdId { get; set; }

    public byte[] Payload { get; set; } = [];

    public UdpCommandState State { get; set; }

    public UdpCommandDelivery Delivery { get; set; }

    /// <summary>Null: the node's response suite.</summary>
    public SecuritySuite? Suite { get; set; }

    public short MaxAttempts { get; set; }

    public short Attempts { get; set; }

    public DateTime Created { get; set; }

    public Guid? CreatedBy { get; set; }

    public string? CreatedByName { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? LastSentAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public short? ResultStatus { get; set; }

    public byte[]? Result { get; set; }

    public string? Note { get; set; }
}

/// <summary>Every step of every command and update, with the bytes on the wire. Permanent (§4.3).</summary>
[Table("udp_command_log")]
[NotAudited("a command's log")]
public class UdpCommandLogEntity
{
    [Key]
    public long Id { get; set; }

    public DateTime Created { get; set; }

    public Guid? CommandId { get; set; }

    public Guid? OtaJobId { get; set; }

    public Guid? SensorId { get; set; }

    public long DeviceId { get; set; }

    public string NodeName { get; set; } = "";

    public UdpLogKind Kind { get; set; }

    public UdpDirection? Direction { get; set; }

    public short? CmdId { get; set; }

    public short? Attempt { get; set; }

    public string? Endpoint { get; set; }

    public SecuritySuite? Suite { get; set; }

    public long? Fcnt { get; set; }

    public byte[]? Datagram { get; set; }

    public byte[]? Payload { get; set; }

    public short? ResultStatus { get; set; }

    public Guid? UserId { get; set; }

    public string? UserName { get; set; }

    public string? Note { get; set; }
}

/// <summary>An uploaded firmware image (§4.11). Content is stored uncompressed out of line (STORAGE EXTERNAL).</summary>
[Table("udp_firmware")]
[Audited]
public class UdpFirmwareEntity
{
    [Key]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string Version { get; set; } = "";

    public string FileName { get; set; } = "";

    public int Size { get; set; }

    public byte[] Sha256 { get; set; } = [];

    public byte[] Content { get; set; } = [];

    public string? Notes { get; set; }

    public DateTime UploadedAt { get; set; }

    public Guid? UploadedBy { get; set; }

    public string? UploadedByName { get; set; }
}

/// <summary>One firmware update of one node. Permanent, like the commands (§4.3, §4.11).</summary>
[Table("udp_ota_jobs")]
[NotAudited("updates at work; starting and cancelling are entries of their own")]
public class UdpOtaJobEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid? SensorId { get; set; }

    public long DeviceId { get; set; }

    public string NodeName { get; set; } = "";

    public Guid? FirmwareId { get; set; }

    public string FirmwareName { get; set; } = "";

    public string FirmwareVersion { get; set; } = "";

    public int FirmwareSize { get; set; }

    public byte[] FirmwareSha256 { get; set; } = [];

    public short UpdateId { get; set; }

    public short ChunkSize { get; set; }

    public UdpOtaState State { get; set; }

    public short? Reason { get; set; }

    public short OfferAttempts { get; set; }

    public short MaxOfferAttempts { get; set; }

    public long BytesServed { get; set; }

    public int ChunksServed { get; set; }

    public int ChunkRetransmissions { get; set; }

    public long HighestOffset { get; set; }

    public DateTime Created { get; set; }

    public Guid? CreatedBy { get; set; }

    public string? CreatedByName { get; set; }

    public DateTime? OfferedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? LastRequestAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public string? AppliedVersion { get; set; }
}
