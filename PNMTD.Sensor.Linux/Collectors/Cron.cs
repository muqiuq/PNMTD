using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>
/// Cron (docs/linux-sensor-plan.md §5.4): <c>/etc/crontab</c> and <c>/etc/cron.d/</c> (with a user field), the users' crontabs in
/// <c>/var/spool/cron/crontabs/</c> (the file's name is the user), and the scripts in <c>/etc/cron.{hourly,daily,weekly,monthly}</c>. Per entry
/// the schedule, the user and <b>only the program</b> it runs, because command lines can hold passwords; the command too with config.yaml's
/// <c>collect.cron_commands</c>. <see cref="CronEntry.LineHash"/> (a SHA-256 prefix of the line, or of a script's text) says when an entry
/// changed either way. The parsing is in pure static methods.
/// </summary>
internal static partial class Cron
{
    public const int MaxEntries = 500;

    private static readonly string[] Periods = ["hourly", "daily", "weekly", "monthly"];

    /// <summary>cron and run-parts ignore other names in cron.d and the period folders (Debian's cron(8)): no dots, no backups.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex RunPartsName();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\s*=")]
    private static partial Regex Assignment();

    /// <summary>Words before the program that only change how it runs.</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "nice", "ionice", "nohup", "timeout", "flock", "chronic", "cronic", "env", "exec", "time", "setsid", "sudo",
    };

    /// <summary>Parts that aren't the program: tests, cd, the end of an if or a loop.</summary>
    private static readonly HashSet<string> NotPrograms = new(StringComparer.Ordinal)
    {
        "[", "[[", "test", "cd", "fi", "true", ":", "}", ")", "export", "umask", "sleep", "done", "while", "for", "until", "esac",
    };

    /// <summary>Words before a command that are the shell's: the program follows them.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal) { "if", "then", "else", "elif", "do", "!", "{", "(" };

    public static List<CronEntry> Read(bool commands)
    {
        var entries = new List<CronEntry>();
        if (Tool.ReadFile("/etc/crontab") is { } crontab)
        {
            entries.AddRange(ParseFile("/etc/crontab", crontab, null, commands));
        }
        foreach (var file in Files("/etc/cron.d"))
        {
            if (Tool.ReadFile(file) is { } text)
            {
                entries.AddRange(ParseFile(file, text, null, commands));
            }
        }
        foreach (var file in Files("/var/spool/cron/crontabs", runParts: false))
        {
            if (Tool.ReadFile(file) is { } text)
            {
                entries.AddRange(ParseFile(file, text, Path.GetFileName(file), commands));
            }
        }
        foreach (var period in Periods)
        {
            foreach (var script in Files($"/etc/cron.{period}"))
            {
                var facts = Native.Stat(script);
                if (facts is null || facts.Directory || (facts.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                {
                    continue;
                }
                var content = facts.Size <= 1024 * 1024 ? ReadBytes(script) : null;
                entries.Add(new CronEntry($"/etc/cron.{period}", "root", period, script, commands ? script : null,
                    Hash(content ?? Encoding.UTF8.GetBytes(script))));
            }
        }
        return entries.Take(MaxEntries).ToList();
    }

    private static IEnumerable<string> Files(string folder, bool runParts = true) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder).Where(f => !runParts || RunPartsName().IsMatch(Path.GetFileName(f))).Order(StringComparer.Ordinal)
            : [];

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A crontab's entries: with a user field (<paramref name="user"/> null: /etc/crontab, cron.d) or without (a user's crontab). Comments,
    /// blank lines and variable assignments (<c>MAILTO=…</c>) are skipped; <c>@reboot</c> and the other nicknames are schedules.
    /// </summary>
    public static List<CronEntry> ParseFile(string file, string text, string? user, bool commands)
    {
        var entries = new List<CronEntry>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || Assignment().IsMatch(line))
            {
                continue;
            }
            var fields = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var scheduleFields = fields[0].StartsWith('@') ? 1 : 5;
            var needed = scheduleFields + (user is null ? 1 : 0) + 1;
            if (fields.Length < needed)
            {
                continue;
            }
            var schedule = string.Join(' ', fields.Take(scheduleFields));
            var owner = user ?? fields[scheduleFields];
            var command = Rest(line, scheduleFields + (user is null ? 1 : 0));
            // cron turns an unescaped % into a newline: what follows is the command's input, not part of it.
            var percent = UnescapedPercent(command);
            var runs = percent >= 0 ? command[..percent] : command;
            entries.Add(new CronEntry(file, owner, schedule, Program(runs) ?? "?", commands ? command : null, Hash(Encoding.UTF8.GetBytes(line))));
        }
        return entries;
    }

    /// <summary>The text after the first <paramref name="words"/> words, as written (its spacing kept).</summary>
    private static string Rest(string line, int words)
    {
        var index = 0;
        for (var w = 0; w < words; w++)
        {
            while (index < line.Length && line[index] is ' ' or '\t')
            {
                index++;
            }
            while (index < line.Length && line[index] is not (' ' or '\t'))
            {
                index++;
            }
        }
        return line[index..].Trim();
    }

    private static int UnescapedPercent(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && (i == 0 || text[i - 1] != '\\'))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The program a command runs, never its arguments: the first word of the first part (split at <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and
    /// <c>|</c>) that isn't a test, a cd or a variable, after the wrappers that only change how it runs (<c>nice</c>, <c>flock -n f</c>,
    /// <c>timeout 60</c>, <c>sudo -u x</c>). <c>cd / &amp;&amp; run-parts --report /etc/cron.hourly</c> → <c>run-parts</c>.
    /// </summary>
    public static string? Program(string command)
    {
        foreach (var part in Regex.Split(command, @"&&|\|\||;|\|"))
        {
            var words = part.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
            var i = 0;
            while (i < words.Count)
            {
                var word = words[i].Trim('(', ')', '`', '"', '\'');
                if (word.Length == 0 || Keywords.Contains(words[i]) || Assignment().IsMatch(word) || word.StartsWith('>') || word.StartsWith('<')
                    || word.StartsWith("2>", StringComparison.Ordinal))
                {
                    i++;
                    continue;
                }
                if (NotPrograms.Contains(word))
                {
                    // A test or a cd: the part isn't the program (its arguments neither).
                    i = words.Count;
                    break;
                }
                if (Wrappers.Contains(word) || Wrappers.Contains(Path.GetFileName(word)))
                {
                    var wrapper = Path.GetFileName(word);
                    i++;
                    // The wrapper's options and their values (-n 10, -c3, sudo's -u user), then its own arguments (timeout's
                    // duration, flock's lock file).
                    while (i < words.Count && words[i].StartsWith('-'))
                    {
                        var option = words[i++];
                        if (i < words.Count && OptionTakesValue(wrapper, option))
                        {
                            i++;
                        }
                    }
                    if (wrapper is "timeout" or "flock" && i < words.Count)
                    {
                        i++;
                    }
                    continue;
                }
                return word;
            }
        }
        return null;
    }

    private static bool OptionTakesValue(string wrapper, string option) => (wrapper, option) switch
    {
        ("sudo", "-u" or "-g" or "-C" or "-h" or "-p" or "-D") => true,
        ("ionice", "-c" or "-n" or "-p" or "-P" or "-u") => true,
        ("nice", "-n") => true,
        ("timeout", "-s" or "-k") => true,
        ("flock", "-w" or "-E" or "-c") => true,
        ("env", "-u" or "-C" or "-S") => true,
        _ => false,
    };

    /// <summary>The first 16 hex digits of the SHA-256: enough to tell lines apart, nothing of what they say.</summary>
    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant();
}
