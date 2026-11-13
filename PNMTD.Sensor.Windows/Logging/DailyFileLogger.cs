using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PNMTD.Sensor.Windows.Logging;

/// <summary>
/// The log file (docs/windows-sensor-plan.md §3.8): one file per day (the server's local date), the last 10 kept, at most
/// 50 MB each (past it, lines are dropped with one note saying how many). Each line: the time with its offset, the level,
/// the component, the message, and on errors the exception with its stack. No third-party library.
/// </summary>
internal sealed class DailyFileLoggerProvider : ILoggerProvider
{
    public const int KeepFiles = 10;
    public const long MaxBytes = 50L * 1024 * 1024;

    private readonly string folder;
    private readonly string prefix;
    private readonly Lock gate = new();
    private readonly ConcurrentDictionary<string, DailyFileLogger> loggers = new(StringComparer.Ordinal);
    private DateOnly day;
    private StreamWriter? writer;
    private long written;
    private int dropped;

    public DailyFileLoggerProvider(string folder, string prefix)
    {
        this.folder = folder;
        this.prefix = prefix;
        Directory.CreateDirectory(folder);
        Cleanup();
    }

    /// <summary>Debug or Information; switched without a restart (the window, agent.json's logLevel).</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    public ILogger CreateLogger(string categoryName) => loggers.GetOrAdd(categoryName, name => new DailyFileLogger(this, Short(name)));

    private static string Short(string category) => category.StartsWith("PNMTD.Sensor.Windows.", StringComparison.Ordinal)
        ? category["PNMTD.Sensor.Windows.".Length..]
        : category;

    public string CurrentFile => Path.Combine(folder, $"{prefix}-{DateTime.Now:yyyy-MM-dd}.log");

    internal void Write(LogLevel level, string component, string message, Exception? exception)
    {
        var now = DateTimeOffset.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(Level(level)).Append(' ').Append(component).Append(": ").Append(message);
        if (exception is not null)
        {
            line.Append(Environment.NewLine).Append(exception);
        }
        lock (gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(now.LocalDateTime);
                if (writer is null || today != day)
                {
                    Open(today);
                }
                if (written >= MaxBytes)
                {
                    dropped++;
                    return;
                }
                var text = line.ToString();
                writer!.WriteLine(text);
                written += Encoding.UTF8.GetByteCount(text) + 2;
                if (written >= MaxBytes)
                {
                    writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [WRN] logging: the file reached 50 MB: further lines today are dropped and counted.");
                }
            }
            catch (IOException)
            {
                // The log must never take the agent down: a line that can't be written is lost.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Open(DateOnly today)
    {
        if (writer is not null && dropped > 0)
        {
            writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [WRN] logging: {dropped} lines were dropped above 50 MB.");
        }
        writer?.Dispose();
        day = today;
        dropped = 0;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{prefix}-{today:yyyy-MM-dd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        written = stream.Length;
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        Cleanup();
    }

    /// <summary>Keeps the newest 10 files of this prefix.</summary>
    private void Cleanup()
    {
        try
        {
            foreach (var old in Directory.GetFiles(folder, $"{prefix}-????-??-??.log").OrderDescending().Skip(KeepFiles))
            {
                File.Delete(old);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The last lines of today's file and the days before, for PNMT's Send log.</summary>
    public IReadOnlyList<string> Tail(int lines)
    {
        var result = new List<string>();
        try
        {
            foreach (var file in Directory.GetFiles(folder, $"{prefix}-????-??-??.log").OrderDescending())
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var all = new List<string>();
                while (reader.ReadLine() is { } line)
                {
                    all.Add(line);
                }
                result.InsertRange(0, all.TakeLast(lines - result.Count));
                if (result.Count >= lines)
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
        }
        return result;
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "[TRC]",
        LogLevel.Debug => "[DBG]",
        LogLevel.Information => "[INF]",
        LogLevel.Warning => "[WRN]",
        LogLevel.Error => "[ERR]",
        LogLevel.Critical => "[FTL]",
        _ => "[---]",
    };

    public void Dispose()
    {
        lock (gate)
        {
            writer?.Dispose();
            writer = null;
        }
    }

    private sealed class DailyFileLogger(DailyFileLoggerProvider provider, string component) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider.MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, component, formatter(state, exception), exception);
            }
        }
    }
}
