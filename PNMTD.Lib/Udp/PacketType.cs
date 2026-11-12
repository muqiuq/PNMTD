namespace PNMTD.Lib.Udp;

/// <summary>
/// The 4-bit TYPE of a PNMT-UDP packet (docs/udp-protocol.md §3.4). The top bit is the direction:
/// 0x0–0x7 node → server (uplink), 0x8–0xF server → node (downlink).
/// </summary>
public enum PacketType : byte
{
    Heartbeat = 0x0,
    Data = 0x1,
    Event = 0x2,
    CmdResult = 0x3,
    Boot = 0x4,
    Join = 0x5,
    Ota = 0x6,
    Ack = 0x8,
    Command = 0x9,
    JoinAccept = 0xA,
    OtaOffer = 0xB,
    OtaChunk = 0xC,
}

public static class PacketTypes
{
    public static bool IsDownlink(this PacketType type) => ((byte)type & 0x8) != 0;

    public static bool IsDefined(byte value) => value is <= 0x6 or (>= 0x8 and <= 0xC);

    /// <summary>The name in the specification: HEARTBEAT, CMD_RESULT, JOIN_ACCEPT, …</summary>
    public static string Name(this PacketType type) => type switch
    {
        PacketType.Heartbeat => "HEARTBEAT",
        PacketType.Data => "DATA",
        PacketType.Event => "EVENT",
        PacketType.CmdResult => "CMD_RESULT",
        PacketType.Boot => "BOOT",
        PacketType.Join => "JOIN",
        PacketType.Ota => "OTA",
        PacketType.Ack => "ACK",
        PacketType.Command => "COMMAND",
        PacketType.JoinAccept => "JOIN_ACCEPT",
        PacketType.OtaOffer => "OTA_OFFER",
        PacketType.OtaChunk => "OTA_CHUNK",
        _ => $"0x{(byte)type:X}",
    };
}
