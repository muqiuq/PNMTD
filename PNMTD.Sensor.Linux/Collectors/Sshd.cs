using System.Globalization;
using System.Text.RegularExpressions;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>
/// sshd's effective settings, as <c>sshd -T</c> prints them (docs/linux-sensor-plan.md §5.5, §5.6, §6.1): lower-case keywords, one per line,
/// repeated keywords (port, listenaddress, allowusers …) collected in order. <c>-C user=…,host=,addr=</c> evaluates the Match blocks for one
/// account. The Match blocks themselves come from the configuration files, which <c>-T</c> doesn't show.
/// </summary>
internal static partial class Sshd
{
    public const string ConfigFile = "/etc/ssh/sshd_config";

    [GeneratedRegex(@"OpenSSH_[0-9][^\s,]*")]
    private static partial Regex VersionPattern();

    public static bool Installed => Tool.Exists("sshd");

    /// <summary><c>sshd -T</c> (or with <c>-C</c> for one account): every keyword's values, or an error with sshd's reason.</summary>
    public static Dictionary<string, List<string>> Effective(string? connection = null)
    {
        if (!Installed)
        {
            throw new NotAvailableException("sshd (openssh-server) is not installed");
        }
        var result = Tool.Run("sshd", connection is null ? ["-T"] : ["-T", "-C", connection], TimeSpan.FromSeconds(15));
        if (!result.Ok)
        {
            throw new InvalidOperationException($"sshd -T failed ({result.ExitCode}): {result.Reason}");
        }
        return Parse(result.Output);
    }

    public static Dictionary<string, List<string>> Parse(string output)
    {
        var settings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }
            var key = line[..space].ToLowerInvariant();
            var value = line[(space + 1)..].Trim();
            if (!settings.TryGetValue(key, out var values))
            {
                settings[key] = values = [];
            }
            values.Add(value);
        }
        return settings;
    }

    public static string? One(IReadOnlyDictionary<string, List<string>> settings, string key) =>
        settings.TryGetValue(key, out var values) && values.Count > 0 ? values[0] : null;

    /// <summary>A keyword's values split into words (<c>allowusers a b</c> or one line each), without <c>none</c>.</summary>
    public static List<string> Words(IReadOnlyDictionary<string, List<string>> settings, string key) =>
        settings.TryGetValue(key, out var values)
            ? values.SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(w => w != "none").Distinct(StringComparer.Ordinal).ToList()
            : [];

    public static bool Yes(IReadOnlyDictionary<string, List<string>> settings, string key, bool fallback = false) =>
        One(settings, key) is { } value ? value.Equals("yes", StringComparison.OrdinalIgnoreCase) : fallback;

    /// <summary>A path-like value, null for <c>none</c>.</summary>
    public static string? Path(IReadOnlyDictionary<string, List<string>> settings, string key) =>
        One(settings, key) is { } value && !value.Equals("none", StringComparison.OrdinalIgnoreCase) && value.Length > 0 ? value : null;

    public static List<int> Ports(IReadOnlyDictionary<string, List<string>> settings) =>
        Words(settings, "port").Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).Where(p => p > 0).Distinct().ToList();

    /// <summary>The configuration files sshd reads: sshd_config and what it includes (globs in the last part, relative to /etc/ssh).</summary>
    public static List<(string File, string Text)> ConfigFiles()
    {
        var files = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Read(string file, int depth)
        {
            if (depth > 8 || !seen.Add(file) || Tool.ReadFile(file) is not { } text)
            {
                return;
            }
            files.Add((file, text));
            foreach (var include in Includes(text))
            {
                foreach (var path in Expand(include))
                {
                    Read(path, depth + 1);
                }
            }
        }
        Read(ConfigFile, 0);
        return files;
    }

    public static IEnumerable<string> Includes(string text) => Lines(text)
        .Where(l => l.Keyword.Equals("include", StringComparison.OrdinalIgnoreCase))
        .SelectMany(l => l.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static IEnumerable<string> Expand(string pattern)
    {
        var path = pattern.StartsWith('/') ? pattern : System.IO.Path.Combine("/etc/ssh", pattern);
        var folder = System.IO.Path.GetDirectoryName(path) ?? "/etc/ssh";
        var name = System.IO.Path.GetFileName(path);
        if (!name.Contains('*') && !name.Contains('?'))
        {
            return [path];
        }
        return Directory.Exists(folder) ? Directory.GetFiles(folder, name).Order(StringComparer.Ordinal) : [];
    }

    /// <summary>Keyword and value per line, without comments.</summary>
    public static IEnumerable<(string Keyword, string Value, int Line)> Lines(string text)
    {
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var split = line.IndexOfAny([' ', '\t', '=']);
            if (split <= 0)
            {
                yield return (line, "", number);
                continue;
            }
            yield return (line[..split], line[(split + 1)..].Trim().TrimStart('=').Trim(), number);
        }
    }

    /// <summary>Whether any configuration file has a Match block (§5.5, §6.1).</summary>
    public static bool HasMatchBlocks(IEnumerable<(string File, string Text)> files) =>
        files.Any(f => Lines(f.Text).Any(l => l.Keyword.Equals("match", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The AuthorizedKeysFile patterns inside Match blocks, whatever their conditions: candidates for every account (§6.1).</summary>
    public static List<string> MatchKeyFiles(IEnumerable<(string File, string Text)> files)
    {
        var patterns = new List<string>();
        foreach (var (_, text) in files)
        {
            var inMatch = false;
            foreach (var (keyword, value, _) in Lines(text))
            {
                if (keyword.Equals("match", StringComparison.OrdinalIgnoreCase))
                {
                    inMatch = !value.Equals("all", StringComparison.OrdinalIgnoreCase);
                }
                else if (inMatch && keyword.Equals("authorizedkeysfile", StringComparison.OrdinalIgnoreCase))
                {
                    patterns.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p != "none"));
                }
            }
        }
        return patterns.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>OpenSSH's version: <c>sshd -V</c> (9.x on), or the usage text older ones print for it, or <c>ssh -V</c>.</summary>
    public static string? Version()
    {
        foreach (var (tool, arguments) in new[] { ("sshd", new[] { "-V" }), ("ssh", new[] { "-V" }) })
        {
            if (!Tool.Exists(tool))
            {
                continue;
            }
            var result = Tool.Run(tool, arguments, TimeSpan.FromSeconds(5));
            if (VersionPattern().Match(result.Errors + "\n" + result.Output) is { Success: true } match)
            {
                return match.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Ubuntu's <c>ssh.socket</c> (22.10 on, §4.8): whether it is active or enabled, and the ports of its <c>Listen=</c>
    /// (<c>[::]:22 (Stream)</c>). Null when there is no such unit.
    /// </summary>
    public static (bool Active, bool Enabled, List<int> Ports)? Socket()
    {
        var show = Tool.Run("systemctl", ["show", "-p", "LoadState,ActiveState,UnitFileState,Listen", "ssh.socket"], TimeSpan.FromSeconds(10));
        if (!show.Ok)
        {
            return null;
        }
        var values = SystemCollector.KeyValues(show.Output);
        if (values.GetValueOrDefault("LoadState") is not "loaded")
        {
            return null;
        }
        var ports = Regex.Matches(show.Output, @"Listen=.*?:(\d+) \(Stream\)").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().ToList();
        return (values.GetValueOrDefault("ActiveState") is "active" or "activating", values.GetValueOrDefault("UnitFileState") is "enabled" or "enabled-runtime", ports);
    }
}
