using System.Globalization;
using System.Text.Json;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;

namespace PNMTD.Sensor.Linux.Platform;

/// <summary>A mounted file system: the device, where, and its type (from /proc/self/mounts).</summary>
internal sealed record MountEntry(string Device, string MountPoint, string Type, string Options);

/// <summary>A logind session, as <c>loginctl</c> knows it.</summary>
internal sealed record LoginSession(string Id, string User, string? Tty, string? Seat, string? Type, string? Class, string? Service, bool Remote,
    string? RemoteHost, string? State, long? StartedMicroseconds);

/// <summary>
/// What the heartbeat and the collectors read alike (docs/linux-sensor-plan.md §5.2, §5.3): <c>/proc/stat</c>, <c>/proc/meminfo</c>,
/// the real file systems, logind's sessions, a pending reboot, and the last shutdown. The parsing is in pure static methods.
/// </summary>
internal static class LinuxFacts
{
    /// <summary>Real file systems (§5.3): not tmpfs, overlay, squashfs, proc and the other pseudo ones.</summary>
    public static readonly IReadOnlySet<string> RealFileSystems = new HashSet<string>(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "jfs", "reiserfs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "bcachefs", "nilfs2",
    };

    // --- /proc --------------------------------------------------------------------------------------------------

    /// <summary>The boot time: /proc/stat's <c>btime</c>.</summary>
    public static DateTimeOffset BootTime() =>
        ParseBootTime(File.ReadAllText("/proc/stat")) is { } boot ? boot : DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static DateTimeOffset? ParseBootTime(string stat) =>
        stat.Split('\n').FirstOrDefault(l => l.StartsWith("btime ", StringComparison.Ordinal)) is { } line
        && long.TryParse(line[6..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>
    /// The first <c>cpu</c> line of /proc/stat as (idle, total) jiffies: idle is idle and iowait, total the first eight fields (guest
    /// time is in user already).
    /// </summary>
    public static (long Idle, long Total)? ParseCpu(string stat)
    {
        var line = stat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(f => long.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0).ToList();
        if (fields.Count < 4)
        {
            return null;
        }
        return (fields[3] + (fields.Count > 4 ? fields[4] : 0), fields.Sum());
    }

    /// <summary>/proc/meminfo's values in bytes (<c>MemTotal: 16316416 kB</c>).</summary>
    public static IReadOnlyDictionary<string, long> ParseMeminfo(string text)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var parts = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                values[line[..colon]] = parts.Length > 1 && parts[1] == "kB" ? value * 1024 : value;
            }
        }
        return values;
    }

    /// <summary>Memory used, as <c>free</c> counts it: everything but <c>MemAvailable</c>.</summary>
    public static double? MemoryPercent(IReadOnlyDictionary<string, long> meminfo) =>
        meminfo.TryGetValue("MemTotal", out var total) && total > 0 && meminfo.TryGetValue("MemAvailable", out var available)
            ? Math.Round(100.0 * (total - available) / total, 1)
            : null;

    // --- file systems -------------------------------------------------------------------------------------------

    /// <summary>/proc/self/mounts' lines; octal escapes (<c>\040</c> for a space) decoded.</summary>
    public static List<MountEntry> ParseMounts(string text) => text.Split('\n')
        .Select(l => l.Split(' '))
        .Where(f => f.Length >= 4)
        .Select(f => new MountEntry(Unescape(f[0]), Unescape(f[1]), f[2], f[3]))
        .ToList();

    /// <summary>
    /// The real file systems once each: a device mounted twice (bind mounts) counts at its shortest mount point, ZFS datasets by their
    /// pool (§5.3), and mounts of files (a container's /etc/hosts) not at all.
    /// </summary>
    public static List<MountEntry> RealMounts(IEnumerable<MountEntry> mounts, Func<string, bool>? isDirectory = null)
    {
        isDirectory ??= Directory.Exists;
        return mounts.Where(m => RealFileSystems.Contains(m.Type) && isDirectory(m.MountPoint))
            .GroupBy(m => m.Type == "zfs" ? "zfs:" + m.Device.Split('/')[0] : m.Device, StringComparer.Ordinal)
            .Select(g => g.OrderBy(m => m.MountPoint.Length).ThenBy(m => m.MountPoint, StringComparer.Ordinal).First())
            .OrderBy(m => m.MountPoint, StringComparer.Ordinal)
            .ToList();
    }

    public static List<MountEntry> RealMounts() => RealMounts(ParseMounts(File.ReadAllText("/proc/self/mounts")));

    /// <summary>Size, available and free percentage as <c>df</c> counts them (what non-root users can still write).</summary>
    public static (long Size, long Available, double FreePercent)? Usage(string mountPoint)
    {
        try
        {
            var drive = new DriveInfo(mountPoint);
            var size = drive.TotalSize;
            var available = drive.AvailableFreeSpace;
            var usable = size - drive.TotalFreeSpace + available;
            return size <= 0 || usable <= 0 ? null : (size, available, Math.Round(100.0 * available / usable, 1));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string Unescape(string field)
    {
        if (!field.Contains('\\'))
        {
            return field;
        }
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length && field.Substring(i + 1, 3).All(c => c is >= '0' and <= '7'))
            {
                result.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                result.Append(field[i]);
            }
        }
        return result.ToString();
    }

    // --- sessions (logind) --------------------------------------------------------------------------------------

    /// <summary>
    /// The user sessions now, from logind (§5.2, the heartbeat): the list with <c>--json=short</c> (systemd 250 on), else
    /// <c>--output=json</c> (249), else the text table (L0: 257 prints a table for <c>--output=json</c>), then one <c>show-session</c>
    /// for the details of all of them. No D-Bus or no logind is <see cref="NotAvailableException"/>: no sessions, not an error.
    /// </summary>
    public static List<SessionInfo> Sessions(DateTimeOffset bootTime)
    {
        var ids = SessionIds();
        if (ids.Count == 0)
        {
            return [];
        }
        var properties = Tool.Run("loginctl", ["show-session", .. ids.Take(50), "-p", "Id", "-p", "Name", "-p", "TTY", "-p", "Seat", "-p", "Type",
            "-p", "Class", "-p", "Service", "-p", "Remote", "-p", "RemoteHost", "-p", "State", "-p", "TimestampMonotonic"], TimeSpan.FromSeconds(10));
        return ParseShowSessions(properties.Output)
            // user, and systemd 256's user-early and user-incomplete; not the greeter, the lock screen or user@'s manager.
            .Where(s => s.Class is null || s.Class.StartsWith("user", StringComparison.Ordinal))
            .Select(s => ToSessionInfo(s, bootTime))
            .OrderBy(s => s.LogonAt).ThenBy(s => s.User, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> SessionIds()
    {
        foreach (var arguments in new[] { new[] { "list-sessions", "--no-legend", "--json=short" }, ["list-sessions", "--no-legend", "--output=json"] })
        {
            var result = Tool.Run("loginctl", arguments, TimeSpan.FromSeconds(10));
            if (result.Ok && result.Output.TrimStart().StartsWith('[') && ParseSessionIdsJson(result.Output) is { } ids)
            {
                return ids;
            }
        }
        var table = Tool.Run("loginctl", ["list-sessions", "--no-legend"], TimeSpan.FromSeconds(10));
        return table.Ok ? ParseSessionIdsTable(table.Output) : throw new InvalidOperationException($"loginctl failed ({table.ExitCode}): {table.Reason}");
    }

    /// <summary>The session ids of <c>loginctl list-sessions</c>' JSON (<c>[{"session":"3","uid":0,"user":"root",…}]</c>).</summary>
    public static List<string>? ParseSessionIdsJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind != JsonValueKind.Array ? null
                : document.RootElement.EnumerateArray()
                    .Select(e => e.TryGetProperty("session", out var s) ? s.ToString() : null)
                    .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first column of the text table (<c>SESSION UID USER SEAT …</c>, without the legend).</summary>
    public static List<string> ParseSessionIdsTable(string text) => text.Split('\n')
        .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(f => f.Length >= 3 && f[1].All(char.IsDigit))
        .Select(f => f[0]).ToList();

    /// <summary><c>loginctl show-session a b -p …</c>: <c>Key=value</c> blocks, one per session, separated by a blank line.</summary>
    public static List<LoginSession> ParseShowSessions(string text)
    {
        var sessions = new List<LoginSession>();
        foreach (var block in text.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var values = block.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            if (!values.TryGetValue("Id", out var id) || !values.TryGetValue("Name", out var user))
            {
                continue;
            }
            string? Value(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
            sessions.Add(new LoginSession(id, user, Value("TTY"), Value("Seat"), Value("Type"), Value("Class"), Value("Service"), Value("Remote") == "yes",
                Value("RemoteHost"), Value("State"),
                long.TryParse(Value("TimestampMonotonic"), NumberStyles.None, CultureInfo.InvariantCulture, out var us) && us > 0 ? us : null));
        }
        return sessions;
    }

    /// <summary>A session as PNMT shows it: who, how (SSH, Console, Graphical, or the service), its state, from where, since when.</summary>
    public static SessionInfo ToSessionInfo(LoginSession s, DateTimeOffset bootTime)
    {
        var kind = s.Service is "sshd" or "ssh" || (s.Remote && s.Service is null) ? "SSH"
            : s.Type is "x11" or "wayland" or "mir" ? "Graphical"
            : s.Tty?.StartsWith("tty", StringComparison.Ordinal) == true || s.Seat is not null ? "Console"
            : s.Remote ? "Remote"
            : s.Service ?? s.Type ?? "Session";
        return new SessionInfo(s.User, kind, s.State ?? "unknown", s.Remote ? s.RemoteHost : null,
            s.StartedMicroseconds is { } us ? bootTime + TimeSpan.FromMicroseconds(us) : null);
    }

    // --- a pending reboot ----------------------------------------------------------------------------------------

    /// <summary>
    /// Why a reboot is pending (§5.2, the heartbeat): <c>/run/reboot-required</c> (with the packages that asked), and a newer kernel
    /// installed than the one running.
    /// </summary>
    public static List<string> RebootReasons()
    {
        var reasons = new List<string>();
        if (File.Exists("/run/reboot-required"))
        {
            var packages = Tool.ReadFile("/run/reboot-required.pkgs")?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct().Take(10).ToList() ?? [];
            reasons.Add(packages.Count > 0 ? $"/run/reboot-required ({string.Join(", ", packages)})" : "/run/reboot-required");
        }
        var running = Tool.ReadValue("/proc/sys/kernel/osrelease");
        var installed = Directory.Exists("/boot") ? Directory.GetFiles("/boot", "vmlinuz-*").Select(f => Path.GetFileName(f)["vmlinuz-".Length..]).ToList() : [];
        if (running is not null && NewerKernel(running, installed) is { } newer)
        {
            reasons.Add($"kernel {newer} is installed, {running} runs");
        }
        return reasons;
    }

    /// <summary>The newest installed kernel when it is newer than the running one (by version, digit groups compared as numbers), or null.</summary>
    public static string? NewerKernel(string running, IReadOnlyCollection<string> installed)
    {
        if (installed.Count == 0 || installed.Contains(running) && installed.Count == 1)
        {
            return null;
        }
        var newest = installed.OrderBy(k => k, KernelOrder.Instance).Last();
        return KernelOrder.Instance.Compare(newest, running) > 0 ? newest : null;
    }

    /// <summary>Kernel releases in version order: <c>6.1.0-27-arm64</c> before <c>6.1.0-28-arm64</c>, <c>6.8.12-4-pve</c> before <c>6.11.0-1-pve</c>.</summary>
    private sealed class KernelOrder : IComparer<string>
    {
        public static KernelOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            var a = Parts(x ?? "");
            var b = Parts(y ?? "");
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                var c = a[i] is long ai && b[i] is long bi ? ai.CompareTo(bi) : string.CompareOrdinal(a[i].ToString(), b[i].ToString());
                if (c != 0)
                {
                    return c;
                }
            }
            return a.Count.CompareTo(b.Count);
        }

        private static List<object> Parts(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\d+|[^\d.\-+~_]+")
            .Select(m => long.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? (object)n : m.Value).ToList();
    }

    // --- the last shutdown ---------------------------------------------------------------------------------------

    /// <summary>
    /// The previous boot's end (§5.2): its last entry in the journal, and whether it reached the shutdown target (clean) or not. Null
    /// when the journal has no previous boot (a volatile journal, a container's first boot). At most 200 entries are read.
    /// </summary>
    public static LinuxShutdownInfo? LastShutdown()
    {
        var result = Tool.Run("journalctl", ["-b", "-1", "-n", "200", "-o", "json", "--no-pager",
            "--output-fields=MESSAGE,UNIT,JOB_TYPE,JOB_RESULT,_BOOT_ID,__REALTIME_TIMESTAMP"], TimeSpan.FromSeconds(20));
        return result.Ok ? ParseLastShutdown(result.Output) : null;
    }

    /// <summary>The previous boot's tail, as <c>journalctl -o json</c> lines: its last time, its boot id, and clean or not.</summary>
    public static LinuxShutdownInfo? ParseLastShutdown(string jsonLines)
    {
        DateTimeOffset? last = null;
        string? boot = null;
        var clean = false;
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var e = document.RootElement;
                string? Field(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (long.TryParse(Field("__REALTIME_TIMESTAMP"), NumberStyles.None, CultureInfo.InvariantCulture, out var us))
                {
                    var at = DateTimeOffset.UnixEpoch + TimeSpan.FromMicroseconds(us);
                    last = last is null || at > last ? at : last;
                }
                boot ??= Field("_BOOT_ID");
                var message = Field("MESSAGE") ?? "";
                if ((Field("UNIT") is "shutdown.target" or "final.target" or "poweroff.target" or "reboot.target" or "halt.target" or "kexec.target"
                        && Field("JOB_RESULT") == "done")
                    || message.StartsWith("Reached target", StringComparison.Ordinal) && (message.Contains("Shutdown", StringComparison.Ordinal)
                        || message.Contains("shutdown.target", StringComparison.Ordinal) || message.Contains("Power-Off", StringComparison.Ordinal)
                        || message.Contains("Reboot", StringComparison.Ordinal))
                    || message.StartsWith("Journal stopped", StringComparison.Ordinal))
                {
                    clean = true;
                }
            }
            catch (JsonException)
            {
            }
        }
        return last is null ? null : new LinuxShutdownInfo(last, clean, boot);
    }

    /// <summary>The heartbeat's words for it, as Windows has them: "clean shutdown at …" or "unexpected shutdown before …".</summary>
    public static string? Describe(LinuxShutdownInfo? shutdown) => shutdown?.At is not { } at ? null
        : shutdown.Clean == true ? $"clean shutdown at {at.ToLocalTime():yyyy-MM-dd HH:mm}"
        : $"unexpected shutdown before {at.ToLocalTime():yyyy-MM-dd HH:mm}";
}
