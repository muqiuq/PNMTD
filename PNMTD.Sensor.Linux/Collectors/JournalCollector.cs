using System.Globalization;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Linux.Access;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>
/// <c>journal</c> (docs/linux-sensor-plan.md §5.7 → <see cref="JournalSection"/>): the newest 100 entries at priority warning or above (emerg,
/// alert, crit, err, warning) from this boot and the one before, from every unit but the login sources of §5.6, each message capped at 2 KB,
/// and whether the journal is persistent. The login stream's 15 s poll notices new ones and asks for it again; every 6 hours anyway.
/// </summary>
internal sealed class JournalCollector : Collector
{
    public const int MaxEntries = 100;
    public const int MaxMessage = 2048;

    private static readonly string[] Levels = ["emerg", "alert", "crit", "err", "warning"];

    public override string Name => SectionNames.Journal;

    public override TimeSpan Interval => TimeSpan.FromHours(6);

    public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        if (!Tool.Exists("journalctl"))
        {
            throw new NotAvailableException("journalctl is not installed");
        }
        var entries = Read("0");
        if (entries.Count < MaxEntries)
        {
            entries.AddRange(Isolated.Part(errors, "the boot before", () => Read("-1")) ?? []);
        }
        var persistent = Isolated.Part(errors, "persistence", () => (bool?)JournalFacts.Persistent()) ?? false;
        return new JournalSection(entries.OrderByDescending(e => e.At).Take(MaxEntries).ToList(), persistent);
    }

    /// <summary>
    /// One boot's newest entries at warning or above, without the login sources: journalctl can't leave identifiers out, so ten times as many are
    /// read (only the fields needed) and filtered.
    /// </summary>
    private static List<JournalEntry> Read(string boot)
    {
        var result = Tool.Run("journalctl", ["-o", "json", "-p", "0..4", "-b", boot, "-n", "1000", "--no-pager", "-q",
            "--output-fields=MESSAGE,PRIORITY,SYSLOG_IDENTIFIER,_PID,_SYSTEMD_UNIT,UNIT"], TimeSpan.FromSeconds(30));
        if (!result.Ok)
        {
            // "Data from the specified boot (-1) is not available" (252), "No journal boot entry found for the specified boot" (257 on).
            if (boot != "0" && Regex.IsMatch(result.Errors, "not available|No such boot|No journal boot entry|boot ID", RegexOptions.IgnoreCase))
            {
                return [];
            }
            throw new InvalidOperationException($"journalctl -b {boot} failed ({result.ExitCode}): {result.Reason}");
        }
        return Parse(result.Output);
    }

    public static List<JournalEntry> Parse(string output) => output.Split('\n')
        .Select(JournalLines.ParseJson)
        .Where(r => r is not null && !(r.Identifier is { } i && JournalLines.Identifiers.Contains(i)) && r.Priority is >= 0 and <= 4)
        .Select(r => new JournalEntry(r!.At, r.Priority!.Value, Levels[r.Priority.Value], r.Unit, r.Identifier, r.Pid, r.Boot,
            r.Message.Length > MaxMessage ? r.Message[..MaxMessage] + "…" : r.Message, r.Cursor))
        .TakeLast(MaxEntries)
        .ToList();
}

/// <summary>The journal's own facts (§5.6's logging check, §5.7): persistent or not, its oldest entry and its size.</summary>
internal static partial class JournalFacts
{
    [GeneratedRegex(@"^Head (?:realtime )?timestamp:.*\(([0-9a-f]+)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HeadTimestamp();

    [GeneratedRegex(@"take up ([0-9.]+)([BKMGTP]?)", RegexOptions.IgnoreCase)]
    private static partial Regex DiskUsage();

    /// <summary>
    /// journald's <c>Storage=</c> as its files set it (journald.conf, then the drop-ins in /usr/lib, /run and /etc, the last one wins), and
    /// the file that set it; <c>auto</c> when none does.
    /// </summary>
    public static (string Storage, string? File) Storage()
    {
        var files = new List<string> { "/etc/systemd/journald.conf" };
        var dropIns = new[] { "/usr/lib/systemd/journald.conf.d", "/usr/local/lib/systemd/journald.conf.d", "/run/systemd/journald.conf.d", "/etc/systemd/journald.conf.d" }
            .Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.conf"))
            .GroupBy(Path.GetFileName).Select(g => g.Last()).OrderBy(Path.GetFileName, StringComparer.Ordinal);
        files.AddRange(dropIns);
        var storage = "auto";
        string? from = null;
        foreach (var file in files)
        {
            if (Tool.ReadFile(file) is not { } text)
            {
                continue;
            }
            if (Regex.Matches(text, @"^\s*Storage\s*=\s*(\S+)\s*$", RegexOptions.Multiline) is { Count: > 0 } matches)
            {
                storage = matches[^1].Groups[1].Value.ToLowerInvariant();
                from = file;
            }
        }
        return (storage, from);
    }

    /// <summary>Persistent: Storage=persistent, or auto with /var/log/journal there (§5.6).</summary>
    public static bool Persistent()
    {
        var (storage, _) = Storage();
        return storage == "persistent" || storage == "auto" && Directory.Exists("/var/log/journal");
    }

    /// <summary>The oldest entry: the earliest head of the journal's files (<c>journalctl --header</c>'s realtime timestamps, in µs).</summary>
    public static DateTimeOffset? OldestEntry()
    {
        var header = Tool.Run("journalctl", ["--header", "--no-pager"], TimeSpan.FromSeconds(30));
        if (!header.Ok && header.Output.Length == 0)
        {
            throw new InvalidOperationException($"journalctl --header failed ({header.ExitCode}): {header.Reason}");
        }
        return ParseOldest(header.Output);
    }

    public static DateTimeOffset? ParseOldest(string header)
    {
        DateTimeOffset? oldest = null;
        foreach (Match m in HeadTimestamp().Matches(header))
        {
            if (long.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var us) && us > 0)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(us / 1000);
                if (oldest is null || at < oldest)
                {
                    oldest = at;
                }
            }
        }
        return oldest;
    }

    /// <summary><c>journalctl --disk-usage</c>: "Archived and active journals take up 16.0M in the file system."</summary>
    public static long? Bytes()
    {
        var usage = Tool.Run("journalctl", ["--disk-usage"], TimeSpan.FromSeconds(30));
        return ParseBytes(usage.Output + usage.Errors);
    }

    public static long? ParseBytes(string text)
    {
        if (DiskUsage().Match(text) is not { Success: true } m || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }
        var factor = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => 1024d,
            "M" => 1024d * 1024,
            "G" => 1024d * 1024 * 1024,
            "T" => 1024d * 1024 * 1024 * 1024,
            "P" => 1024d * 1024 * 1024 * 1024 * 1024,
            _ => 1d,
        };
        return (long)(number * factor);
    }
}
