using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace PNMTD.Sensor.Linux.Platform;

/// <summary>
/// The journal instead of Windows' event log (docs/linux-sensor-plan.md §4.2, §4.7): starts, stops, joins, crashes, rights changes,
/// updates and uninstalls, with the identifier <c>pnmt-agent</c> (<c>journalctl -t pnmt-agent</c>). Written to journald's native
/// socket, so it works from the service and the command line alike, and the entry carries this process's own PID. Best effort,
/// like the event log on Windows: the log file has everything anyway.
/// </summary>
internal static class Journal
{
    private const string Socket = "/run/systemd/journal/socket";
    private const int MaxMessage = 8 * 1024;

    public static void Write(string message, bool warning = false)
    {
        try
        {
            if (!File.Exists(Socket))
            {
                return;
            }
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            socket.SendTo(Datagram(message.Length > MaxMessage ? message[..MaxMessage] : message, warning ? 4 : 6),
                new UnixDomainSocketEndPoint(Socket));
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The native protocol: <c>NAME=value\n</c> per field; a value with a newline as <c>NAME\n</c>, its length (64 bits, little
    /// endian) and the value. Priority 6 is info, 4 warning.
    /// </summary>
    public static byte[] Datagram(string message, int priority)
    {
        using var stream = new MemoryStream();
        Field(stream, "SYSLOG_IDENTIFIER", AgentInfo.Name);
        Field(stream, "PRIORITY", priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field(stream, "MESSAGE", message);
        return stream.ToArray();
    }

    private static void Field(Stream stream, string name, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(Encoding.ASCII.GetBytes(name));
        if (value.Contains('\n'))
        {
            stream.WriteByte((byte)'\n');
            Span<byte> length = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)bytes.Length);
            stream.Write(length);
        }
        else
        {
            stream.WriteByte((byte)'=');
        }
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
    }
}
