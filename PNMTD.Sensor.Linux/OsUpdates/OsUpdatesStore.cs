using System.Security.Cryptography;
using System.Text;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Linux.OsUpdates;

/// <summary>
/// A run as the agent keeps it (docs/linux-os-updates-plan.md §4.5): what was asked, its unit, where its output is, how much of the
/// cleaned log exists and was sent, and its result once it ended. Kept until PNMTD acknowledges the result; the newest 10 logs stay
/// for <c>pnmt-agent os-updates log</c>.
/// </summary>
internal sealed record RunRecord
{
    public required Guid RunId { get; init; }

    public required OsUpdateKind Kind { get; init; }

    public OsUpgradeMode? Mode { get; init; }

    public required OsUpdateTrigger Trigger { get; init; }

    public string? RequestedByName { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required string Command { get; init; }

    public required string Unit { get; init; }

    /// <summary>Bytes of the raw output (systemd's file) already read and cleaned.</summary>
    public long RawRead { get; init; }

    /// <summary>Bytes of the cleaned log (UTF-8).</summary>
    public long CleanBytes { get; init; }

    /// <summary>Bytes of the cleaned log sent on the current connection; reset to what PNMTD asks for (<c>os-update-log</c>).</summary>
    public long SentBytes { get; init; }

    public bool StartedSent { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public OsUpdateState State { get; init; } = OsUpdateState.Running;

    public int? ExitCode { get; init; }

    public string? Message { get; init; }

    public OsUpdateSummary? Summary { get; init; }

    public string? LogSha256 { get; init; }

    public bool ResultSent { get; init; }

    /// <summary>PNMTD stored it in full: only the log stays, for the command line.</summary>
    public bool Acknowledged { get; init; }

    public bool Finished => FinishedAt is not null;

    public OsUpdateResultMessage Result() => new(RunId, Kind, Mode, Trigger, RequestedByName, State, ExitCode, Summary ?? OsUpdateSummary.Empty, Message,
        StartedAt, FinishedAt ?? DateTimeOffset.UtcNow, CleanBytes, LogSha256 ?? "", Command);

    public OsUpdateRunRef Ref() => new(RunId, Kind, Mode, StartedAt, State);
}

/// <summary>The schedule's memory: the day the daily update last ran, and the last update of any trigger.</summary>
internal sealed record ScheduleState(DateOnly? LastScheduledDay, DateTimeOffset? LastUpdateAt, OsUpdateState? LastUpdateState, Guid? LastUpdateRunId,
    int RepositoriesFailed = 0);

/// <summary>
/// <c>/var/lib/pnmt-agent/os-updates/</c> (root, 0700): each run's record (<c>&lt;id&gt;.json</c>), systemd's raw output (<c>.log</c>),
/// the exit status the wrapper writes (<c>.exit</c>), the cleaned log (<c>.txt</c>); the wrapper itself; the schedule's state; the
/// changelogs already fetched; the history.log entries already reported; and the flag <c>pnmt-agent os-updates update</c> leaves.
/// Damaged files are renamed <c>.broken</c> (W§3.8 rule 7). Not thread-safe: the feature holds its lock around it.
/// </summary>
internal sealed class OsUpdatesStore(string folder)
{
    public const string DefaultFolder = AgentInfo.DataFolder + "/os-updates";

    public static OsUpdatesStore Default { get; } = new(DefaultFolder);

    /// <summary>The newest logs kept on the server for <c>pnmt-agent os-updates log</c>.</summary>
    public const int KeepLocalLogs = 10;

    public string Folder { get; } = folder;

    public string Wrapper => Path.Combine(Folder, "run-apt.sh");

    public string UpdateNowFlag => Path.Combine(Folder, "update-now");

    public string RawLog(Guid id) => Path.Combine(Folder, $"{id:N}.log");

    public string ExitFile(Guid id) => Path.Combine(Folder, $"{id:N}.exit");

    public string CleanLog(Guid id) => Path.Combine(Folder, $"{id:N}.txt");

    private string RecordFile(Guid id) => Path.Combine(Folder, $"{id:N}.json");

    private string ScheduleFile => Path.Combine(Folder, "schedule.json");

    private string ChangelogsFile => Path.Combine(Folder, "changelogs.json");

    private string HistoryFile => Path.Combine(Folder, "history.json");

    public void Ensure()
    {
        if (!Directory.Exists(Folder))
        {
            Directory.CreateDirectory(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // --- runs ----------------------------------------------------------------------------------------------------------

    public List<RunRecord> Runs()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }
        var runs = new List<RunRecord>();
        foreach (var file in Directory.GetFiles(Folder, "*.json"))
        {
            if (Path.GetFileNameWithoutExtension(file) is { Length: 32 } name && Guid.TryParseExact(name, "N", out _) && Read<RunRecord>(file) is { } run)
            {
                runs.Add(run);
            }
        }
        return runs.OrderBy(r => r.StartedAt).ToList();
    }

    public RunRecord? Current() => Runs().LastOrDefault(r => !r.Finished);

    public void Save(RunRecord run)
    {
        Ensure();
        Write(RecordFile(run.RunId), run);
    }

    /// <summary>
    /// After PNMTD's acknowledgement: the record goes, the cleaned log stays (the newest <see cref="KeepLocalLogs"/>); and at most
    /// <see cref="OsUpdateLimits.MaxPendingRuns"/> unacknowledged runs are kept, oldest first, so a server that never reaches PNMT
    /// doesn't fill its disk.
    /// </summary>
    public void Prune()
    {
        var runs = Runs();
        foreach (var run in runs.Where(r => r.Finished && r.Acknowledged))
        {
            TryDelete(RecordFile(run.RunId));
            TryDelete(RawLog(run.RunId));
            TryDelete(ExitFile(run.RunId));
        }
        var unacknowledged = runs.Where(r => r.Finished && !r.Acknowledged).ToList();
        foreach (var run in unacknowledged.Take(Math.Max(0, unacknowledged.Count - OsUpdateLimits.MaxPendingRuns)))
        {
            TryDelete(RecordFile(run.RunId));
            TryDelete(RawLog(run.RunId));
            TryDelete(ExitFile(run.RunId));
        }
        if (!Directory.Exists(Folder))
        {
            return;
        }
        var logs = Directory.GetFiles(Folder, "*.txt").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var active = Runs().Select(r => r.RunId.ToString("N")).ToHashSet();
        foreach (var old in logs.Skip(KeepLocalLogs).Where(f => !active.Contains(Path.GetFileNameWithoutExtension(f.Name))))
        {
            TryDelete(old.FullName);
        }
    }

    /// <summary>The cleaned logs on the server, newest first: their run id and when they were last written.</summary>
    public List<(Guid RunId, DateTime WrittenAt, long Bytes)> LocalLogs() => !Directory.Exists(Folder) ? []
        : Directory.GetFiles(Folder, "*.txt").Select(f => new FileInfo(f))
            .Where(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "N", out _))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => (Guid.ParseExact(Path.GetFileNameWithoutExtension(f.Name), "N"), f.LastWriteTimeUtc, f.Length)).ToList();

    /// <summary>The cleaned log from <paramref name="offset"/> on, at most <paramref name="max"/> bytes, cut at a whole UTF-8 character.</summary>
    public string ReadLog(Guid id, long offset, int max)
    {
        var path = CleanLog(id);
        if (!File.Exists(path))
        {
            return "";
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset >= stream.Length)
        {
            return "";
        }
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(max, stream.Length - offset)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var length = read;
        // Never end in the middle of a character: back off to the start of the last one if it is incomplete.
        if (length < stream.Length - offset)
        {
            var start = length - 1;
            while (start > 0 && (buffer[start] & 0xC0) == 0x80)
            {
                start--;
            }
            var lead = buffer[start];
            var size = lead < 0x80 ? 1 : lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
            if (start + size > length)
            {
                length = start;
            }
        }
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    /// <summary>Appends cleaned text; answers the new size. Beyond <see cref="OsUpdateLimits.MaxLogBytes"/> nothing more is kept but a marker.</summary>
    public long AppendLog(Guid id, string text, long size)
    {
        if (text.Length == 0)
        {
            return size;
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        if (size + bytes.Length > OsUpdateLimits.MaxLogBytes)
        {
            if (size >= OsUpdateLimits.MaxLogBytes)
            {
                return size;
            }
            bytes = Encoding.UTF8.GetBytes("\n[… the agent stopped keeping this log: it reached 64 MiB …]\n");
        }
        using var stream = new FileStream(CleanLog(id), new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(bytes);
        return size + bytes.Length;
    }

    public static string Sha256(string path)
    {
        if (!File.Exists(path))
        {
            return Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        }
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // --- the schedule, changelogs and history ----------------------------------------------------------------------------

    public ScheduleState Schedule() => Read<ScheduleState>(ScheduleFile) ?? new ScheduleState(null, null, null, null);

    public void Save(ScheduleState state)
    {
        Ensure();
        Write(ScheduleFile, state);
    }

    /// <summary>The source versions whose changelog was fetched (or tried), with the day: <c>openssl=3.0.22-1~deb12u2</c>.</summary>
    public Dictionary<string, DateOnly> Changelogs() => Read<Dictionary<string, DateOnly>>(ChangelogsFile) ?? [];

    public void Save(Dictionary<string, DateOnly> changelogs)
    {
        Ensure();
        // 60 days are enough: a version pending longer was fetched long ago.
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-60);
        Write(ChangelogsFile, changelogs.Where(p => p.Value >= cutoff).ToDictionary());
    }

    /// <summary>The history.log entries already reported (their ids, the newest 2000), and whether the first pass (30 days) was made.</summary>
    public HistoryState History() => Read<HistoryState>(HistoryFile) ?? new HistoryState(false, []);

    public void Save(HistoryState state)
    {
        Ensure();
        Write(HistoryFile, state with { Reported = state.Reported.TakeLast(2000).ToList() });
    }

    // --- files -----------------------------------------------------------------------------------------------------------

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return AgentJson.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or NotSupportedException)
        {
            try
            {
                File.Move(path, path + ".broken", overwrite: true);
            }
            catch (IOException)
            {
            }
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, AgentJson.Serialize(value));
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>What history.log's reader remembers.</summary>
internal sealed record HistoryState(bool FirstPassDone, List<string> Reported);
