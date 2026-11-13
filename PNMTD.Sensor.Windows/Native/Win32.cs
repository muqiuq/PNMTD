using System.Net;
using System.Runtime.InteropServices;

namespace PNMTD.Sensor.Windows.Native;

/// <summary>The Windows APIs without a managed counterpart (docs/windows-sensor-plan.md §3.1).</summary>
internal static partial class Win32
{
    // --- CPU and memory (the heartbeat) -------------------------------------------------------------------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public static MemoryStatusEx Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status : default;
    }

    /// <summary>1 BIOS, 2 UEFI.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFirmwareType(out int type);

    // --- sessions (wtsapi32) -----------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;
    private const int WTSClientAddress = 14;
    private const int WTSSessionInfo = 24;

    private static readonly string[] States = ["Active", "Connected", "ConnectQuery", "Shadow", "Disconnected", "Idle", "Listen", "Reset", "Down", "Init"];

    public sealed record Session(int Id, string Station, string User, string State, string? ClientAddress, DateTimeOffset? LogonAt);

    /// <summary>The sessions with a user: who, the station (Console, RDP-Tcp#3), the state, where from.</summary>
    public static List<Session> Sessions()
    {
        var result = new List<Session>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var list, out var count))
        {
            return result;
        }
        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(list + i * size);
                var user = QueryString(info.SessionId, WTSUserName);
                if (string.IsNullOrEmpty(user))
                {
                    continue;
                }
                var domain = QueryString(info.SessionId, WTSDomainName);
                result.Add(new Session(info.SessionId, info.WinStationName ?? "", string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}",
                    info.State >= 0 && info.State < States.Length ? States[info.State] : info.State.ToString(), ClientAddress(info.SessionId), LogonTime(info.SessionId)));
            }
        }
        finally
        {
            WTSFreeMemory(list);
        }
        return result;
    }

    private static string? QueryString(int session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buffer, out _))
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string? ClientAddress(int session)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, WTSClientAddress, out var buffer, out _))
        {
            return null;
        }
        try
        {
            // WTS_CLIENT_ADDRESS: AddressFamily (int), then 20 bytes; IPv4 at offset 2 of the bytes.
            var family = Marshal.ReadInt32(buffer);
            var bytes = new byte[20];
            Marshal.Copy(buffer + 4, bytes, 0, 20);
            return family switch
            {
                2 => new IPAddress(bytes.AsSpan(2, 4)).ToString(),
                23 => new IPAddress(bytes.AsSpan(0, 16)).ToString(),
                _ => null,
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static DateTimeOffset? LogonTime(int session)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, WTSSessionInfo, out var buffer, out var bytes) || bytes < 120)
        {
            return null;
        }
        try
        {
            // WTSINFOW: State, SessionId, six byte and frame counters, WinStationName[32], Domain[17], UserName[21], then
            // ConnectTime, DisconnectTime, LastInputTime and LogonTime (8-byte aligned): LogonTime is the fourth.
            const int timesOffset = 4 + 4 + 6 * 4 + 32 * 2 + 17 * 2 + 21 * 2;
            var aligned = (timesOffset + 7) & ~7;
            var logon = Marshal.ReadInt64(buffer + aligned + 24);
            return logon > 0 ? DateTimeOffset.FromFileTime(logon) : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // --- listeners (iphlpapi) -----------------------------------------------------------------------------

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int UdpTableOwnerPid = 1;

    public sealed record Listener(string Protocol, IPAddress Address, int Port, int Pid);

    /// <summary>Every listening TCP port and bound UDP port, IPv4 and IPv6, with the owning process id.</summary>
    public static List<Listener> Listeners()
    {
        var result = new List<Listener>();
        foreach (var family in new[] { AfInet, AfInet6 })
        {
            result.AddRange(Table("TCP", family, (IntPtr t, ref int s) => GetExtendedTcpTable(t, ref s, true, family, TcpTableOwnerPidListener, 0)));
            result.AddRange(Table("UDP", family, (IntPtr t, ref int s) => GetExtendedUdpTable(t, ref s, true, family, UdpTableOwnerPid, 0)));
        }
        return result;
    }

    private delegate uint TableReader(IntPtr table, ref int size);

    private static IEnumerable<Listener> Table(string protocol, int family, TableReader read)
    {
        var size = 0;
        read(IntPtr.Zero, ref size);
        var buffer = Marshal.AllocHGlobal(size);
        var rows = new List<Listener>();
        try
        {
            if (read(buffer, ref size) != 0)
            {
                return rows;
            }
            var count = Marshal.ReadInt32(buffer);
            var tcp = protocol == "TCP";
            // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid (24 bytes).
            // MIB_TCP6ROW_OWNER_PID: local addr[16], scope, port, remote[16], scope, port, state, pid (56 bytes).
            // MIB_UDPROW_OWNER_PID: local addr, port, pid (12); MIB_UDP6ROW_OWNER_PID: addr[16], scope, port, pid (28).
            var rowSize = (tcp, family) switch
            {
                (true, AfInet) => 24,
                (true, _) => 56,
                (false, AfInet) => 12,
                _ => 28,
            };
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * rowSize;
                IPAddress address;
                int port;
                int pid;
                if (family == AfInet)
                {
                    var offset = tcp ? 4 : 0;
                    address = new IPAddress((uint)Marshal.ReadInt32(row + offset));
                    port = Port(row + offset + 4);
                    pid = Marshal.ReadInt32(row + (tcp ? 20 : 8));
                }
                else
                {
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, 16);
                    address = new IPAddress(bytes);
                    port = Port(row + 20);
                    pid = Marshal.ReadInt32(row + (tcp ? 52 : 24));
                }
                rows.Add(new Listener(protocol, address, port, pid));
            }
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Ports are in network order in the low 16 bits.</summary>
    private static int Port(IntPtr at)
    {
        var raw = Marshal.ReadInt32(at);
        return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    }

    // --- local groups (netapi32) ----------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo2
    {
        public IntPtr Sid;
        public int SidUsage;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string DomainAndName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer, int max, out int read, out int total, IntPtr resume);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>The members of a local group: domain\name and whether each is a user or a group (SID_NAME_USE).</summary>
    public static List<(string Name, string Kind)> LocalGroupMembers(string group)
    {
        var result = new List<(string, string)>();
        if (NetLocalGroupGetMembers(null, group, 2, out var buffer, -1, out var read, out _, IntPtr.Zero) != 0)
        {
            throw new InvalidOperationException($"NetLocalGroupGetMembers({group}) failed.");
        }
        try
        {
            var size = Marshal.SizeOf<LocalGroupMembersInfo2>();
            for (var i = 0; i < read; i++)
            {
                var member = Marshal.PtrToStructure<LocalGroupMembersInfo2>(buffer + i * size);
                var kind = member.SidUsage switch
                {
                    1 => "User",
                    2 or 4 or 5 => "Group",
                    6 => "Deleted",
                    9 => "Computer",
                    _ => "Unknown",
                };
                result.Add((member.DomainAndName, kind));
            }
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
        return result;
    }

    // --- deleting a file at the next reboot (uninstall, §10.4) ------------------------------------------------

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string existing, string? replacement, int flags);

    public const int MoveFileDelayUntilReboot = 0x4;
}
