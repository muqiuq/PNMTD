using System.Runtime.InteropServices;

namespace PNMTD.Sensor.Linux.Platform;

/// <summary>
/// The file calls of the SSH keys (docs/linux-sensor-plan.md §6.1, §6.5, §6.6) that .NET's own file API doesn't offer: opening without
/// following a symlink (<c>O_NOFOLLOW</c>) and without blocking on a FIFO, the facts of what was opened (<c>statx</c> on the descriptor),
/// and writing through a folder's descriptor (<c>openat</c>, <c>renameat</c>), so a folder swapped for a symlink between a check and a
/// write can't send the write elsewhere. <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> differ between x64 and arm64; the rest is the same.
/// </summary>
internal static unsafe partial class Native
{
    public const int ReadOnly = 0;
    public const int WriteOnly = 1;
    public const int Create = 0x40;
    public const int Exclusive = 0x80;
    public const int NoControllingTerminal = 0x100;
    public const int NonBlocking = 0x800;
    public const int CloseOnExec = 0x80000;

    public static readonly int NoFollow = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

    public static readonly int DirectoryOnly = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;

    private const int AtEmptyPath = 0x1000;

    public const int ENOENT = 2;
    public const int EACCES = 13;
    public const int EEXIST = 17;
    public const int ENOSPC = 28;
    public const int ELOOP = 40;
    public const int EAGAIN = 11;
    public const int EINTR = 4;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenNative(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAtNative(int directory, string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseNative(int fd);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint ReadNative(int fd, byte* buffer, nint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint WriteNative(int fd, byte* buffer, nint count);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int FsyncNative(int fd);

    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int FchmodNative(int fd, uint mode);

    [LibraryImport("libc", EntryPoint = "fchown", SetLastError = true)]
    private static partial int FchownNative(int fd, uint owner, uint group);

    [LibraryImport("libc", EntryPoint = "renameat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAtNative(int fromDirectory, string from, int toDirectory, string to);

    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAtNative(int directory, string path, int flags);

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int StatxAt(int directory, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();

    [LibraryImport("libc", EntryPoint = "getgid")]
    private static partial uint GetGid();

    public static uint Uid => GetUid();

    public static uint Gid => GetGid();

    public static int LastError => Marshal.GetLastPInvokeError();

    public static string Describe(int errno) => Marshal.GetPInvokeErrorMessage(errno);

    /// <summary>open(2): a descriptor, or -1 and <see cref="LastError"/>.</summary>
    public static int Open(string path, int flags, uint mode = 0) => OpenNative(path, flags | CloseOnExec, mode);

    public static int OpenAt(int directory, string path, int flags, uint mode = 0) => OpenAtNative(directory, path, flags | CloseOnExec, mode);

    public static void Close(int fd)
    {
        if (fd >= 0)
        {
            CloseNative(fd);
        }
    }

    /// <summary>What a descriptor refers to: owner, mode, kind, size, inode and modification time.</summary>
    public static FileFacts? StatFd(int fd)
    {
        var buffer = stackalloc byte[256];
        return StatxAt(fd, "", AtEmptyPath, 0x7ff, buffer) == 0 ? Facts(buffer) : null;
    }

    /// <summary>Reads at most <paramref name="max"/> bytes; null when the descriptor fails (<see cref="LastError"/>).</summary>
    public static byte[]? ReadAll(int fd, int max)
    {
        var result = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (result.Length < max)
        {
            nint read;
            fixed (byte* p = buffer)
            {
                read = ReadNative(fd, p, Math.Min(buffer.Length, max - (int)result.Length));
            }
            if (read < 0)
            {
                if (LastError == EINTR)
                {
                    continue;
                }
                return null;
            }
            if (read == 0)
            {
                break;
            }
            result.Write(buffer, 0, (int)read);
        }
        return result.ToArray();
    }

    /// <summary>Writes everything, or throws with the error.</summary>
    public static void WriteAll(int fd, ReadOnlySpan<byte> data)
    {
        fixed (byte* start = data)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var written = WriteNative(fd, start + offset, data.Length - offset);
                if (written < 0)
                {
                    if (LastError == EINTR)
                    {
                        continue;
                    }
                    throw new IOException($"write: {Describe(LastError)}");
                }
                offset += (int)written;
            }
        }
    }

    public static void Fsync(int fd)
    {
        if (FsyncNative(fd) != 0)
        {
            throw new IOException($"fsync: {Describe(LastError)}");
        }
    }

    public static void Fchmod(int fd, uint mode)
    {
        if (FchmodNative(fd, mode) != 0)
        {
            throw new IOException($"fchmod: {Describe(LastError)}");
        }
    }

    public static void Fchown(int fd, uint owner, uint group)
    {
        if (FchownNative(fd, owner, group) != 0)
        {
            throw new IOException($"fchown: {Describe(LastError)}");
        }
    }

    public static void RenameAt(int directory, string from, string to)
    {
        if (RenameAtNative(directory, from, directory, to) != 0)
        {
            throw new IOException($"rename: {Describe(LastError)}");
        }
    }

    public static void UnlinkAt(int directory, string name) => UnlinkAtNative(directory, name, 0);

    private static FileFacts Facts(byte* buffer)
    {
        // struct statx: uid at 20, gid at 24, mode at 28, ino at 32, size at 40, mtime (s64 seconds, u32 nanoseconds) at 112, nlink at 16.
        var mode = *(ushort*)(buffer + 28);
        var type = mode & 0xF000;
        var seconds = *(long*)(buffer + 112);
        return new FileFacts(*(uint*)(buffer + 20), *(uint*)(buffer + 24), (UnixFileMode)(mode & 0xFFF), type == 0x4000, type == 0xA000, *(long*)(buffer + 40),
            type == 0x8000, *(ulong*)(buffer + 32), DateTimeOffset.FromUnixTimeSeconds(seconds), *(uint*)(buffer + 16), mode & 0xFFF);
    }

    /// <summary>A path's facts with the inode, the time and the kind (<see cref="Stat"/> answers the same).</summary>
    public static FileFacts? StatFull(string path, bool followLinks)
    {
        var buffer = stackalloc byte[256];
        return StatxAt(-100, path, followLinks ? 0 : 0x100, 0x7ff, buffer) == 0 ? Facts(buffer) : null;
    }

    // --- a thread's own file rights (§6.1: "files in a user's home are read with that user's rights") -------------------

    [LibraryImport("libc", EntryPoint = "setfsuid")]
    private static partial int SetFsUid(uint uid);

    [LibraryImport("libc", EntryPoint = "setfsgid")]
    private static partial int SetFsGid(uint gid);

    [LibraryImport("libc", EntryPoint = "getgroups", SetLastError = true)]
    private static partial int GetGroups(int size, uint* list);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial nint Syscall(nint number, nint a, nint b);

    /// <summary>setgroups(2) as the raw system call: glibc's wrapper changes every thread of the process, the kernel's only the caller.</summary>
    private static readonly nint SetGroupsCall = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 159 : 116;

    /// <summary>
    /// Runs <paramref name="work"/> on a thread of its own whose file system rights are the account's (fsuid, fsgid and its groups), so the
    /// kernel checks every open as it would for that user; root's are back before the thread ends. Nothing in <paramref name="work"/> may log
    /// or touch the agent's own files. Linux keeps credentials per thread: setfsuid and setfsgid are per thread already (glibc doesn't
    /// broadcast them), and the groups are set with the raw system call. Throws when the rights can't be switched.
    /// </summary>
    public static T AsUser<T>(uint uid, uint gid, IReadOnlyList<uint> groups, Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var saved = new uint[256];
            int count;
            fixed (uint* p = saved)
            {
                count = GetGroups(saved.Length, p);
            }
            if (count < 0)
            {
                failure = new IOException($"getgroups: {Describe(LastError)}");
                return;
            }
            var switched = false;
            try
            {
                var wanted = groups.Distinct().Take(256).ToArray();
                fixed (uint* g = wanted)
                {
                    if (Syscall(SetGroupsCall, wanted.Length, (nint)g) != 0)
                    {
                        throw new IOException($"setgroups: {Describe(LastError)}");
                    }
                }
                switched = true;
                SetFsGid(gid);
                SetFsUid(uid);
                // setfsuid answers the previous value and never an error: asking again answers the current one.
                if ((uint)SetFsUid(uid) != uid || (uint)SetFsGid(gid) != gid)
                {
                    throw new IOException($"could not take uid {uid}'s rights");
                }
                result = work();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                SetFsUid(0);
                SetFsGid(0);
                if (switched)
                {
                    fixed (uint* p = saved)
                    {
                        Syscall(SetGroupsCall, count, (nint)p);
                    }
                }
                // The thread ends here: whatever it held goes with it.
            }
        })
        {
            IsBackground = true,
            Name = "pnmt as user",
        };
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure as IOException ?? new IOException(failure.Message, failure);
        }
        return result;
    }

    // --- inotify (§6.2) -------------------------------------------------------------------------------------------

    [LibraryImport("libc", EntryPoint = "inotify_init1", SetLastError = true)]
    public static partial int InotifyInit(int flags);

    [LibraryImport("libc", EntryPoint = "inotify_add_watch", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int InotifyAddWatch(int fd, string path, uint mask);

    [LibraryImport("libc", EntryPoint = "inotify_rm_watch", SetLastError = true)]
    public static partial int InotifyRemoveWatch(int fd, int watch);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int PollNative(PollFd* fds, nuint count, int timeout);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    /// <summary>Whether the descriptor has something to read within the time (poll(2), POLLIN).</summary>
    public static bool Poll(int fd, int timeoutMs)
    {
        var item = new PollFd { Fd = fd, Events = 1 };
        var ready = PollNative(&item, 1, timeoutMs);
        return ready > 0 && (item.Revents & 1) != 0;
    }

    public static int Read(int fd, byte[] buffer)
    {
        fixed (byte* p = buffer)
        {
            return (int)ReadNative(fd, p, buffer.Length);
        }
    }
}
