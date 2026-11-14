using System.Buffers.Binary;
using System.Text;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Keys;

/// <summary>
/// <b>One inotify instance</b> for every key folder (docs/linux-sensor-plan.md §6.2): P/Invoke, not a FileSystemWatcher per folder, which
/// would take an instance each (128 per user by default). <see cref="Update"/> sets the folders after each scan; <see cref="Wait"/> answers
/// whether something relevant changed: anything in a key folder, a <c>.ssh</c> created in a home that had none, <c>passwd</c> or <c>group</c>
/// in /etc. A full event queue counts as a change. A watch limit (<c>max_user_watches</c>) is said once; the 5-minute rescan covers the rest.
/// </summary>
internal sealed class KeyWatch : IDisposable
{
    private const uint Modify = 0x2;
    private const uint Attrib = 0x4;
    private const uint CloseWrite = 0x8;
    private const uint MovedFrom = 0x40;
    private const uint MovedTo = 0x80;
    private const uint Create = 0x100;
    private const uint Delete = 0x200;
    private const uint DeleteSelf = 0x400;
    private const uint MoveSelf = 0x800;
    private const uint Overflow = 0x4000;
    private const uint Ignored = 0x8000;
    private const uint OnlyDir = 0x01000000;
    private const uint DontFollow = 0x02000000;

    private const uint KeyFolderMask = Modify | Attrib | CloseWrite | MovedFrom | MovedTo | Create | Delete | DeleteSelf | MoveSelf | OnlyDir | DontFollow;
    private const uint NamesMask = CloseWrite | MovedTo | Create | Delete | Attrib | OnlyDir | DontFollow;

    private readonly int fd;
    private readonly Dictionary<string, int> watches = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (string Folder, Func<string?, bool> Relevant)> byWatch = [];
    private readonly byte[] buffer = new byte[64 * 1024];

    public KeyWatch()
    {
        fd = Native.InotifyInit(Native.NonBlocking | Native.CloseOnExec);
        if (fd < 0)
        {
            throw new IOException($"inotify_init1: {Native.Describe(Native.LastError)}");
        }
    }

    /// <summary>Set when a watch couldn't be added because the kernel's limit was reached (ENOSPC), until it is taken.</summary>
    public string? LimitReached { get; private set; }

    public int Count => watches.Count;

    /// <summary>
    /// Watches these folders and no others: <paramref name="keyFolders"/> for anything, <paramref name="homes"/> (without a .ssh yet) for a
    /// .ssh only, /etc for passwd and group only.
    /// </summary>
    public void Update(IReadOnlyCollection<string> keyFolders, IReadOnlyCollection<string> homes)
    {
        var wanted = new Dictionary<string, (uint Mask, Func<string?, bool> Relevant)>(StringComparer.Ordinal);
        foreach (var folder in keyFolders)
        {
            wanted[folder] = folder == "/etc"
                ? (NamesMask, name => name is "passwd" or "group")
                : (KeyFolderMask, _ => true);
        }
        foreach (var home in homes.Where(h => !wanted.ContainsKey(h)))
        {
            wanted[home] = (Create | MovedTo | OnlyDir | DontFollow, name => name == ".ssh");
        }
        foreach (var (folder, watch) in watches.Where(w => !wanted.ContainsKey(w.Key)).ToList())
        {
            Native.InotifyRemoveWatch(fd, watch);
            watches.Remove(folder);
            byWatch.Remove(watch);
        }
        foreach (var (folder, (mask, relevant)) in wanted)
        {
            // Added again when it is there already: the kernel answers the same watch, with the new mask.
            var watch = Native.InotifyAddWatch(fd, folder, mask);
            if (watch < 0)
            {
                var errno = Native.LastError;
                if (errno == Native.ENOSPC)
                {
                    LimitReached ??= $"the kernel's inotify watch limit (fs.inotify.max_user_watches) is reached at {watches.Count} watches: changes in {folder} and beyond are seen by the 5-minute rescan only";
                }
                continue;
            }
            if (watches.TryGetValue(folder, out var old) && old != watch)
            {
                byWatch.Remove(old);
            }
            watches[folder] = watch;
            byWatch[watch] = (folder, relevant);
        }
    }

    public string? TakeLimit()
    {
        var limit = LimitReached;
        LimitReached = null;
        return limit;
    }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for events; true when one of them is relevant (the folder that changed in <paramref name="changed"/>).</summary>
    public bool Wait(int timeoutMs, out string? changed)
    {
        changed = null;
        if (!Native.Poll(fd, timeoutMs))
        {
            return false;
        }
        var relevant = false;
        while (true)
        {
            var read = Native.Read(fd, buffer);
            if (read <= 0)
            {
                break;
            }
            for (var offset = 0; offset + 16 <= read;)
            {
                var watch = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
                var mask = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4));
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 12));
                var name = length > 0 ? Encoding.UTF8.GetString(buffer, offset + 16, length).TrimEnd('\0') : null;
                offset += 16 + length;
                if ((mask & Overflow) != 0)
                {
                    relevant = true;
                    changed = "(the event queue overflowed)";
                    continue;
                }
                if ((mask & Ignored) != 0)
                {
                    if (byWatch.Remove(watch, out var gone))
                    {
                        watches.Remove(gone.Folder);
                    }
                    continue;
                }
                if (byWatch.TryGetValue(watch, out var entry) && entry.Relevant(name))
                {
                    relevant = true;
                    changed = name is null ? entry.Folder : Path.Combine(entry.Folder, name);
                }
            }
        }
        return relevant;
    }

    public void Dispose() => Native.Close(fd);
}
