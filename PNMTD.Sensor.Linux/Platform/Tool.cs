using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PNMTD.Sensor.Core.Collectors;

namespace PNMTD.Sensor.Linux.Platform;

/// <summary>What a tool answered: its exit code, its output and its errors (each capped).</summary>
internal sealed record ToolResult(int ExitCode, string Output, string Errors)
{
    public bool Ok => ExitCode == 0;

    /// <summary>The first line of the errors, or of the output, for a message.</summary>
    public string Reason => (Errors.Trim().Length > 0 ? Errors : Output).Trim().Split('\n')[0].Trim();
}

/// <summary>
/// The tools the agent calls (docs/linux-sensor-plan.md §4.1; W§3.8 rules 1 and 2): each with a timeout, killed with its children
/// when it runs out; <see cref="NotAvailableException"/> when it isn't installed or the system lacks what it needs (no D-Bus, no
/// systemd). In the C locale, without colours or a pager, its arguments as a list (never a shell), stdin closed.
/// </summary>
internal static class Tool
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Each call with its exit code, size and time, at debug level.</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    private const int MaxChars = 16 * 1024 * 1024;

    /// <summary>Where tools are looked for: the unit's PATH and sbin, which a user's sudo may lack.</summary>
    private static readonly string[] Folders = ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    /// <summary>What systemd's tools say when there is no bus or no systemd: not available, not an error (L0, minimal containers).</summary>
    private static readonly string[] Unavailable =
    [
        "Failed to connect to bus", "System has not been booted with systemd", "Failed to get D-Bus connection", "Host is down",
        "Failed to create bus connection",
    ];

    public static string? Find(string name)
    {
        if (name.Contains('/'))
        {
            return File.Exists(name) ? name : null;
        }
        return Folders.Select(f => Path.Combine(f, name)).FirstOrDefault(File.Exists);
    }

    public static bool Exists(string name) => Find(name) is not null;

    /// <summary>
    /// Runs a tool and answers what it said, whatever its exit code; throws only when it is missing or runs out of time. <paramref name="input"/>
    /// is written to its stdin (closed after it), <paramref name="environment"/> added to its environment (<c>TZ=UTC</c>).
    /// </summary>
    public static ToolResult Run(string name, IReadOnlyList<string> arguments, TimeSpan? timeout = null, string? input = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var path = Find(name) ?? throw new NotAvailableException($"{name} is not installed");
        var limit = timeout ?? DefaultTimeout;
        var info = StartInfo(path, arguments, environment);
        var started = Stopwatch.GetTimestamp();
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{name} did not start.");
        // Read before writing: a tool that answers before it read its input all would otherwise block on a full pipe.
        var output = ReadAsync(process.StandardOutput);
        var errors = ReadAsync(process.StandardError);
        if (input is not null)
        {
            try
            {
                process.StandardInput.Write(input);
            }
            catch (IOException)
            {
                // The tool ended without reading it all: its exit code says why.
            }
        }
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        if (!process.WaitForExit(limit))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            Logger.LogDebug("{Tool} {Arguments}: timed out after {Seconds:0} s", name, string.Join(' ', arguments), limit.TotalSeconds);
            throw new TimeoutException($"{name} did not finish within {limit.TotalSeconds:0} s");
        }
        process.WaitForExit();
        var result = new ToolResult(process.ExitCode, output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult());
        Logger.LogDebug("{Tool} {Arguments}: exit {Code}, {Chars} characters in {Ms:0} ms", name, string.Join(' ', arguments), result.ExitCode,
            result.Output.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (!result.Ok && Unavailable.Any(u => result.Errors.Contains(u, StringComparison.Ordinal)))
        {
            throw new NotAvailableException($"{name}: {result.Reason}");
        }
        return result;
    }

    /// <summary>
    /// Runs a tool and hands its output to <paramref name="line"/> line by line as it comes, until that answers false (the tool is then
    /// killed) or the output ends: <c>journalctl</c>'s entries after a cursor, which <c>-n</c> can't limit from the front. Answers the exit
    /// code (null when it was stopped) and the first line of its errors.
    /// </summary>
    public static (int? ExitCode, string Errors) Stream(string name, IReadOnlyList<string> arguments, TimeSpan timeout, Func<string, bool> line,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var path = Find(name) ?? throw new NotAvailableException($"{name} is not installed");
        var info = StartInfo(path, arguments, environment);
        var started = Stopwatch.GetTimestamp();
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{name} did not start.");
        process.StandardInput.Close();
        var errors = ReadAsync(process.StandardError);
        using var limit = new CancellationTokenSource(timeout);
        var stopped = false;
        var lines = 0;
        try
        {
            while (!stopped)
            {
                var next = process.StandardOutput.ReadLineAsync(limit.Token).AsTask().GetAwaiter().GetResult();
                if (next is null)
                {
                    break;
                }
                lines++;
                stopped = !line(next);
            }
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            Logger.LogDebug("{Tool} {Arguments}: timed out after {Seconds:0} s ({Lines} lines)", name, string.Join(' ', arguments), timeout.TotalSeconds, lines);
            throw new TimeoutException($"{name} did not finish within {timeout.TotalSeconds:0} s");
        }
        if (stopped)
        {
            Kill(process);
        }
        process.WaitForExit();
        var text = errors.GetAwaiter().GetResult();
        Logger.LogDebug("{Tool} {Arguments}: {Lines} lines{Stopped} in {Ms:0} ms", name, string.Join(' ', arguments), lines, stopped ? " (stopped there)" : "",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (!stopped && process.ExitCode != 0 && Unavailable.Any(u => text.Contains(u, StringComparison.Ordinal)))
        {
            throw new NotAvailableException($"{name}: {text.Trim().Split('\n')[0]}");
        }
        return (stopped ? null : process.ExitCode, text.Trim().Split('\n')[0].Trim());
    }

    private static ProcessStartInfo StartInfo(string path, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        // systemd's notification socket and watchdog are the service's own: a tool that inherits them may write to the socket
        // (L3 in the lab: "Got notification message from PID …, but reception only permitted for main PID").
        foreach (var inherited in new[] { "NOTIFY_SOCKET", "WATCHDOG_PID", "WATCHDOG_USEC", "LISTEN_FDS", "LISTEN_PID", "LISTEN_FDNAMES" })
        {
            info.Environment.Remove(inherited);
        }
        info.Environment["LC_ALL"] = "C";
        info.Environment["LANG"] = "C";
        info.Environment["SYSTEMD_COLORS"] = "0";
        info.Environment["SYSTEMD_PAGER"] = "cat";
        info.Environment["PAGER"] = "cat";
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }
        return info;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>The output of a tool that must succeed: a failure is an error with its reason.</summary>
    public static string Text(string name, IReadOnlyList<string> arguments, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = Run(name, arguments, timeout, environment: environment);
        return result.Ok ? result.Output : throw new InvalidOperationException($"{name} failed ({result.ExitCode}): {result.Reason}");
    }

    /// <summary>A file's text, or null when it doesn't exist (or can't be read where that just means "not here").</summary>
    public static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A one-line file (/sys, /proc), trimmed, or null.</summary>
    public static string? ReadValue(string path) => ReadFile(path)?.Trim() is { Length: > 0 } value ? value : null;

    private static async Task<string> ReadAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[16 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            // Past the cap the rest is read and dropped, so the tool never blocks on a full pipe.
            if (text.Length < MaxChars)
            {
                text.Append(buffer, 0, Math.Min(read, MaxChars - text.Length));
            }
        }
        return text.ToString();
    }
}
