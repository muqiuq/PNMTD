using System.Runtime.InteropServices;

namespace PNMTD.Sensor.Linux.Platform;

/// <summary>
/// A file's owner, group, permission bits, kind and size, as <c>statx</c> answers them; <see cref="Native.StatFull"/> and
/// <see cref="Native.StatFd"/> add whether it is a regular file, the inode, the modification time, the links and the raw bits (with setuid
/// and sticky).
/// </summary>
internal sealed record FileFacts(uint Uid, uint Gid, UnixFileMode Mode, bool Directory, bool Symlink, long Size, bool Regular = false, ulong Inode = 0,
    DateTimeOffset? ModifiedAt = null, uint Links = 0, int Bits = 0)
{
    /// <summary>Writable by its group or others (sshd's StrictModes refuses that).</summary>
    public bool WritableByOthers => (Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;

    /// <summary>The mode as <c>ls</c>'s octal: <c>0600</c>.</summary>
    public string Octal => Convert.ToString((int)Mode & 0xFFF, 8).PadLeft(4, '0');
}

/// <summary>
/// The few libc calls the agent needs (docs/linux-sensor-plan.md §4.1, §4.6): who runs it (<c>geteuid</c>), a file's owner
/// (<c>statx</c>, whose layout is the same on every architecture), and making a folder root's (<c>chown</c>).
/// </summary>
internal static unsafe partial class Native
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;

    /// <summary>STATX_BASIC_STATS: type, mode, owner, size and the rest of what <c>stat</c> answers.</summary>
    private const uint StatxBasicStats = 0x7ff;

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();

    [LibraryImport("libc", EntryPoint = "chown", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Chown(string path, uint owner, uint group);

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directory, string path, int flags, uint mask, byte* buffer);

    /// <summary>Every command but <c>version</c> and <c>help</c> needs root (§4.1).</summary>
    public static bool IsRoot => GetEuid() == 0;

    /// <summary>The file's facts, or null when it doesn't exist or can't be read. With <paramref name="followLinks"/> false, a symlink's own.</summary>
    public static FileFacts? Stat(string path, bool followLinks = true)
    {
        // struct statx: stx_uid at 20, stx_gid at 24, stx_mode (16 bits) at 28, stx_size at 40; 256 bytes in all.
        var buffer = stackalloc byte[256];
        if (Statx(AtFdCwd, path, followLinks ? 0 : AtSymlinkNoFollow, StatxBasicStats, buffer) != 0)
        {
            return null;
        }
        var mode = *(ushort*)(buffer + 28);
        var type = mode & 0xF000;
        return new FileFacts(*(uint*)(buffer + 20), *(uint*)(buffer + 24), (UnixFileMode)(mode & 0xFFF), type == 0x4000, type == 0xA000, *(long*)(buffer + 40));
    }

    /// <summary>Makes a file or folder root's (uid 0, gid 0).</summary>
    public static void ChownToRoot(string path)
    {
        if (Chown(path, 0, 0) != 0)
        {
            throw new IOException($"chown root:root {path} failed (errno {Marshal.GetLastPInvokeError()}).");
        }
    }
}
