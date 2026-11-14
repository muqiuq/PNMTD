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

    /// <summary>Runs a tool and answers what it said, whatever its exit code; throws only when it is missing or runs out of time.</summary>
    public static ToolResult Run(string name, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var path = Find(name) ?? throw new NotAvailableException($"{name} is not installed");
        var limit = timeout ?? DefaultTimeout;
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
        var started = Stopwatch.GetTimestamp();
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{name} did not start.");
        process.StandardInput.Close();
        var output = ReadAsync(process.StandardOutput);
        var errors = ReadAsync(process.StandardError);
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

    /// <summary>The output of a tool that must succeed: a failure is an error with its reason.</summary>
    public static string Text(string name, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var result = Run(name, arguments, timeout);
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
